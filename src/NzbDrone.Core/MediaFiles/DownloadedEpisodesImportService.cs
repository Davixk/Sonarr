using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Download;
using NzbDrone.Core.MediaFiles.EpisodeImport;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Tv;

namespace NzbDrone.Core.MediaFiles
{
    public interface IDownloadedEpisodesImportService
    {
        List<ImportResult> ProcessRootFolder(DirectoryInfo directoryInfo);
        List<ImportResult> ProcessPath(string path, ImportMode importMode = ImportMode.Auto, Series series = null, DownloadClientItem downloadClientItem = null);

        // Read-only DECIDE phase: computes the import decisions (the ffprobe/media-info/decision work) for
        // a path without importing anything. Safe to run concurrently across downloads.
        DownloadedEpisodesImportBatch DecidePath(string path, ImportMode importMode = ImportMode.Auto, Series series = null, DownloadClientItem downloadClientItem = null);

        // Mutating COMMIT phase: imports the (already decided) batch. Must run serially and in order.
        List<ImportResult> ImportDecidedBatch(DownloadedEpisodesImportBatch batch);

        bool ShouldDeleteFolder(DirectoryInfo directoryInfo, Series series);
    }

    public class DownloadedEpisodesImportService : IDownloadedEpisodesImportService
    {
        private readonly IDiskProvider _diskProvider;
        private readonly IDiskScanService _diskScanService;
        private readonly ISeriesService _seriesService;
        private readonly IParsingService _parsingService;
        private readonly IMakeImportDecision _importDecisionMaker;
        private readonly IImportApprovedEpisodes _importApprovedEpisodes;
        private readonly IDetectSample _detectSample;
        private readonly IRuntimeInfo _runtimeInfo;
        private readonly Logger _logger;

        public DownloadedEpisodesImportService(IDiskProvider diskProvider,
                                               IDiskScanService diskScanService,
                                               ISeriesService seriesService,
                                               IParsingService parsingService,
                                               IMakeImportDecision importDecisionMaker,
                                               IImportApprovedEpisodes importApprovedEpisodes,
                                               IDetectSample detectSample,
                                               IRuntimeInfo runtimeInfo,
                                               Logger logger)
        {
            _diskProvider = diskProvider;
            _diskScanService = diskScanService;
            _seriesService = seriesService;
            _parsingService = parsingService;
            _importDecisionMaker = importDecisionMaker;
            _importApprovedEpisodes = importApprovedEpisodes;
            _detectSample = detectSample;
            _runtimeInfo = runtimeInfo;
            _logger = logger;
        }

        public List<ImportResult> ProcessRootFolder(DirectoryInfo directoryInfo)
        {
            var results = new List<ImportResult>();

            foreach (var subFolder in _diskProvider.GetDirectories(directoryInfo.FullName))
            {
                var folderResults = ProcessFolder(new DirectoryInfo(subFolder), ImportMode.Auto, null);
                results.AddRange(folderResults);
            }

            foreach (var videoFile in _diskScanService.GetVideoFiles(directoryInfo.FullName, false))
            {
                var fileResults = ProcessFile(new FileInfo(videoFile), ImportMode.Auto, null);
                results.AddRange(fileResults);
            }

            return results;
        }

        public List<ImportResult> ProcessPath(string path, ImportMode importMode = ImportMode.Auto, Series series = null, DownloadClientItem downloadClientItem = null)
        {
            return ImportDecidedBatch(DecidePath(path, importMode, series, downloadClientItem));
        }

        public DownloadedEpisodesImportBatch DecidePath(string path, ImportMode importMode = ImportMode.Auto, Series series = null, DownloadClientItem downloadClientItem = null)
        {
            _logger.Debug("Processing path: {0}", path);

            if (_diskProvider.FolderExists(path))
            {
                var directoryInfo = new DirectoryInfo(path);
                var folderSeries = series ?? _parsingService.GetSeries(GetCleanedUpFolderName(directoryInfo.Name));

                if (folderSeries == null)
                {
                    _logger.Debug("Unknown Series {0}", GetCleanedUpFolderName(directoryInfo.Name));

                    return EarlyBatch(UnknownSeriesResult("Unknown Series"));
                }

                return DecideFolder(directoryInfo, importMode, folderSeries, downloadClientItem);
            }

            if (_diskProvider.FileExists(path))
            {
                var fileInfo = new FileInfo(path);
                var fileSeries = series ?? _parsingService.GetSeries(Path.GetFileNameWithoutExtension(fileInfo.Name));

                if (fileSeries == null)
                {
                    _logger.Debug("Unknown Series for file: {0}", fileInfo.Name);

                    return EarlyBatch(UnknownSeriesResult(string.Format("Unknown Series for file: {0}", fileInfo.Name), fileInfo.FullName));
                }

                return DecideFile(fileInfo, importMode, fileSeries, downloadClientItem);
            }

            LogInaccessiblePathError(path);
            return EarlyBatch();
        }

        public List<ImportResult> ImportDecidedBatch(DownloadedEpisodesImportBatch batch)
        {
            if (batch.EarlyResults != null)
            {
                return batch.EarlyResults;
            }

            var importMode = batch.ImportMode;
            var importResults = _importApprovedEpisodes.Import(batch.Decisions, true, batch.DownloadClientItem, importMode);

            if (importMode == ImportMode.Auto)
            {
                importMode = (batch.DownloadClientItem == null || batch.DownloadClientItem.CanMoveFiles) ? ImportMode.Move : ImportMode.Copy;
            }

            // Folder cleanup / empty-result checks only apply to a folder import (a single-file import
            // leaves DirectoryInfo null, matching the original ProcessFile which did neither).
            if (batch.DirectoryInfo != null)
            {
                if (importMode == ImportMode.Move &&
                    importResults.Any(i => i.Result == ImportResultType.Imported) &&
                    ShouldDeleteFolder(batch.DirectoryInfo, batch.Series))
                {
                    _logger.Debug("Deleting folder after importing valid files");

                    try
                    {
                        _diskProvider.DeleteFolder(batch.DirectoryInfo.FullName, true);
                    }
                    catch (IOException e)
                    {
                        _logger.Debug(e, "Unable to delete folder after importing: {0}", e.Message);
                    }
                }
                else if (importResults.Empty())
                {
                    importResults.AddIfNotNull(CheckEmptyResultForIssue(batch.DirectoryInfo.FullName));
                }
            }

            return importResults;
        }

        public bool ShouldDeleteFolder(DirectoryInfo directoryInfo, Series series)
        {
            try
            {
                var videoFiles = _diskScanService.GetVideoFiles(directoryInfo.FullName);
                var rarFiles = _diskProvider.GetFiles(directoryInfo.FullName, true).Where(f =>
                    Path.GetExtension(f).Equals(".rar",
                        StringComparison.OrdinalIgnoreCase));

                foreach (var videoFile in videoFiles)
                {
                    var episodeParseResult = Parser.Parser.ParseTitle(Path.GetFileName(videoFile));

                    if (episodeParseResult == null)
                    {
                        _logger.Warn("Unable to parse file on import: [{0}]", videoFile);
                        return false;
                    }

                    if (_detectSample.IsSample(series, videoFile, episodeParseResult.IsPossibleSpecialEpisode) !=
                        DetectSampleResult.Sample)
                    {
                        _logger.Warn("Non-sample file detected: [{0}]", videoFile);
                        return false;
                    }
                }

                if (rarFiles.Any(f => _diskProvider.GetFileSize(f) > 10.Megabytes()))
                {
                    _logger.Warn("RAR file detected, will require manual cleanup");
                    return false;
                }

                return true;
            }
            catch (DirectoryNotFoundException e)
            {
                _logger.Debug(e, "Folder {0} has already been removed", directoryInfo.FullName);
                return false;
            }
            catch (Exception e)
            {
                _logger.Debug(e, "Unable to determine whether folder {0} should be removed", directoryInfo.FullName);
                return false;
            }
        }

        private List<ImportResult> ProcessFolder(DirectoryInfo directoryInfo, ImportMode importMode, DownloadClientItem downloadClientItem)
        {
            var cleanedUpName = GetCleanedUpFolderName(directoryInfo.Name);
            var series = _parsingService.GetSeries(cleanedUpName);

            if (series == null)
            {
                _logger.Debug("Unknown Series {0}", cleanedUpName);

                return new List<ImportResult>
                       {
                           UnknownSeriesResult("Unknown Series")
                       };
            }

            return ImportDecidedBatch(DecideFolder(directoryInfo, importMode, series, downloadClientItem));
        }

        // Read-only DECIDE phase for a folder: everything the original ProcessFolder did up to and
        // including GetImportDecisions, but WITHOUT importing. The import, folder cleanup and empty-result
        // checks now live in ImportDecidedBatch (the serial commit phase).
        private DownloadedEpisodesImportBatch DecideFolder(DirectoryInfo directoryInfo, ImportMode importMode, Series series, DownloadClientItem downloadClientItem)
        {
            if (_seriesService.SeriesPathExists(directoryInfo.FullName))
            {
                _logger.Warn("Unable to process folder that is mapped to an existing series");
                return EarlyBatch(RejectionResult(ImportRejectionReason.SeriesFolder, "Import path is mapped to a series folder"));
            }

            var folderInfo = Parser.Parser.ParseTitle(directoryInfo.Name);
            var videoFiles = _diskScanService.FilterPaths(directoryInfo.FullName, _diskScanService.GetVideoFiles(directoryInfo.FullName));

            if (downloadClientItem == null)
            {
                foreach (var videoFile in videoFiles)
                {
                    if (_diskProvider.IsFileLocked(videoFile))
                    {
                        return EarlyBatch(FileIsLockedResult(videoFile));
                    }
                }
            }

            var decisions = _importDecisionMaker.GetImportDecisions(videoFiles.ToList(), series, downloadClientItem, folderInfo, true);

            return new DownloadedEpisodesImportBatch
            {
                Decisions = decisions,
                Series = series,
                ImportMode = importMode,
                DownloadClientItem = downloadClientItem,
                DirectoryInfo = directoryInfo
            };
        }

        private List<ImportResult> ProcessFile(FileInfo fileInfo, ImportMode importMode, DownloadClientItem downloadClientItem)
        {
            var series = _parsingService.GetSeries(Path.GetFileNameWithoutExtension(fileInfo.Name));

            if (series == null)
            {
                _logger.Debug("Unknown Series for file: {0}", fileInfo.Name);

                return new List<ImportResult>
                       {
                           UnknownSeriesResult(string.Format("Unknown Series for file: {0}", fileInfo.Name), fileInfo.FullName)
                       };
            }

            return ImportDecidedBatch(DecideFile(fileInfo, importMode, series, downloadClientItem));
        }

        // Read-only DECIDE phase for a single file: the extension guards and GetImportDecisions from the
        // original ProcessFile, without importing. A single-file batch leaves DirectoryInfo null so the
        // commit phase skips folder cleanup, exactly as the original ProcessFile did.
        private DownloadedEpisodesImportBatch DecideFile(FileInfo fileInfo, ImportMode importMode, Series series, DownloadClientItem downloadClientItem)
        {
            if (Path.GetFileNameWithoutExtension(fileInfo.Name).StartsWith("._"))
            {
                _logger.Debug("[{0}] starts with '._', skipping", fileInfo.FullName);

                return EarlyBatch(new ImportResult(new ImportDecision(new LocalEpisode { Path = fileInfo.FullName }, new ImportRejection(ImportRejectionReason.InvalidFilePath, "Invalid video file, filename starts with '._'")), "Invalid video file, filename starts with '._'"));
            }

            var extension = Path.GetExtension(fileInfo.Name);

            if (FileExtensions.DangerousExtensions.Contains(extension))
            {
                return EarlyBatch(new ImportResult(new ImportDecision(new LocalEpisode { Path = fileInfo.FullName },
                        new ImportRejection(ImportRejectionReason.DangerousFile, $"Caution: Found potentially dangerous file with extension: {extension}")),
                    $"Caution: Found potentially dangerous file with extension: {extension}"));
            }

            if (FileExtensions.ExecutableExtensions.Contains(extension))
            {
                return EarlyBatch(new ImportResult(new ImportDecision(new LocalEpisode { Path = fileInfo.FullName },
                        new ImportRejection(ImportRejectionReason.ExecutableFile, $"Caution: Found executable file with extension: '{extension}'")),
                    $"Caution: Found executable file with extension: '{extension}'"));
            }

            if (extension.IsNullOrWhiteSpace() || !MediaFileExtensions.Extensions.Contains(extension))
            {
                _logger.Debug("[{0}] has an unsupported extension: '{1}'", fileInfo.FullName, extension);

                return EarlyBatch(new ImportResult(new ImportDecision(new LocalEpisode { Path = fileInfo.FullName },
                        new ImportRejection(ImportRejectionReason.UnsupportedExtension, $"Invalid video file, unsupported extension: '{extension}'")),
                    $"Invalid video file, unsupported extension: '{extension}'"));
            }

            if (downloadClientItem == null)
            {
                if (_diskProvider.IsFileLocked(fileInfo.FullName))
                {
                    return EarlyBatch(FileIsLockedResult(fileInfo.FullName));
                }
            }

            var decisions = _importDecisionMaker.GetImportDecisions(new List<string>() { fileInfo.FullName }, series, downloadClientItem, null, true);

            return new DownloadedEpisodesImportBatch
            {
                Decisions = decisions,
                Series = series,
                ImportMode = importMode,
                DownloadClientItem = downloadClientItem,
                DirectoryInfo = null
            };
        }

        private static DownloadedEpisodesImportBatch EarlyBatch(params ImportResult[] results)
        {
            return new DownloadedEpisodesImportBatch
            {
                EarlyResults = results.ToList()
            };
        }

        private string GetCleanedUpFolderName(string folder)
        {
            folder = folder.Replace("_UNPACK_", "")
                           .Replace("_FAILED_", "");

            return folder;
        }

        private ImportResult FileIsLockedResult(string videoFile)
        {
            _logger.Debug("[{0}] is currently locked by another process, skipping", videoFile);
            return new ImportResult(new ImportDecision(new LocalEpisode { Path = videoFile }, new ImportRejection(ImportRejectionReason.FileLocked, "Locked file, try again later")), "Locked file, try again later");
        }

        private ImportResult UnknownSeriesResult(string message, string videoFile = null)
        {
            var localEpisode = videoFile == null ? null : new LocalEpisode { Path = videoFile };

            return new ImportResult(new ImportDecision(localEpisode, new ImportRejection(ImportRejectionReason.UnknownSeries, "Unknown Series")), message);
        }

        private ImportResult RejectionResult(ImportRejectionReason reason, string message)
        {
            return new ImportResult(new ImportDecision(null, new ImportRejection(reason, message)), message);
        }

        private ImportResult CheckEmptyResultForIssue(string folder)
        {
            var files = _diskProvider.GetFiles(folder, true);

            if (files.Any(file => FileExtensions.DangerousExtensions.Contains(Path.GetExtension(file))))
            {
                return RejectionResult(ImportRejectionReason.DangerousFile, "Caution: Found potentially dangerous file");
            }

            if (files.Any(file => FileExtensions.ExecutableExtensions.Contains(Path.GetExtension(file))))
            {
                return RejectionResult(ImportRejectionReason.ExecutableFile, "Caution: Found executable file");
            }

            if (files.Any(file => FileExtensions.ArchiveExtensions.Contains(Path.GetExtension(file))))
            {
                return RejectionResult(ImportRejectionReason.ArchiveFile, "Found archive file, might need to be extracted");
            }

            return null;
        }

        private void LogInaccessiblePathError(string path)
        {
            if (_runtimeInfo.IsWindowsService)
            {
                var mounts = _diskProvider.GetMounts();
                var mount = mounts.FirstOrDefault(m => m.RootDirectory == Path.GetPathRoot(path));

                if (mount == null)
                {
                    _logger.Error("Import failed, path does not exist or is not accessible by Sonarr: {0}. Unable to find a volume mounted for the path. If you're using a mapped network drive see the FAQ for more info", path);
                    return;
                }

                if (mount.DriveType == DriveType.Network)
                {
                    _logger.Error("Import failed, path does not exist or is not accessible by Sonarr: {0}. It's recommended to avoid mapped network drives when running as a Windows service. See the FAQ for more info", path);
                    return;
                }
            }

            if (OsInfo.IsWindows)
            {
                if (path.StartsWith(@"\\"))
                {
                    _logger.Error("Import failed, path does not exist or is not accessible by Sonarr: {0}. Ensure the user running Sonarr has access to the network share", path);
                    return;
                }
            }

            _logger.Error("Import failed, path does not exist or is not accessible by Sonarr: {0}. Ensure the path exists and the user running Sonarr has the correct permissions to access this file/folder", path);
        }
    }
}
