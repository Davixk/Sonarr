using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.MediaFiles.EpisodeImport.Aggregation;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Tv;

namespace NzbDrone.Core.MediaFiles.EpisodeImport
{
    public interface IMakeImportDecision
    {
        List<ImportDecision> GetImportDecisions(List<string> videoFiles, Series series);
        List<ImportDecision> GetImportDecisions(List<string> videoFiles, Series series, bool filterExistingFiles);
        List<ImportDecision> GetImportDecisions(List<string> videoFiles, Series series, DownloadClientItem downloadClientItem, ParsedEpisodeInfo folderInfo, bool sceneSource);
        List<ImportDecision> GetImportDecisions(List<string> videoFiles, Series series, DownloadClientItem downloadClientItem, ParsedEpisodeInfo folderInfo, bool sceneSource, bool filterExistingFiles);
        ImportDecision GetDecision(LocalEpisode localEpisode, DownloadClientItem downloadClientItem);
    }

    public class ImportDecisionMaker : IMakeImportDecision
    {
        // Bounded, configurable degree of parallelism for the probe/decision phase. The probe/media-info
        // work (ffprobe) is IO bound, so a slow/hung probe on one file must not block the others. A degree
        // of 1 reproduces the original serial behaviour exactly. Configurable via IMPORT_PROBE_THREADS so
        // slow hardware is never excluded by a hardcoded value.
        private const int DEFAULT_PROBE_THREADS = 4;
        private const int PROBE_THREADS_LOWER_BOUND = 1;
        private const int PROBE_THREADS_UPPER_BOUND = 16;

        private readonly IEnumerable<IImportDecisionEngineSpecification> _specifications;
        private readonly IMediaFileService _mediaFileService;
        private readonly IAggregationService _aggregationService;
        private readonly IDiskProvider _diskProvider;
        private readonly IDetectSample _detectSample;
        private readonly ITrackedDownloadService _trackedDownloadService;
        private readonly ICustomFormatCalculationService _formatCalculator;
        private readonly Logger _logger;

        public ImportDecisionMaker(IEnumerable<IImportDecisionEngineSpecification> specifications,
                                   IMediaFileService mediaFileService,
                                   IAggregationService aggregationService,
                                   IDiskProvider diskProvider,
                                   IDetectSample detectSample,
                                   ITrackedDownloadService trackedDownloadService,
                                   ICustomFormatCalculationService formatCalculator,
                                   Logger logger)
        {
            _specifications = specifications;
            _mediaFileService = mediaFileService;
            _aggregationService = aggregationService;
            _diskProvider = diskProvider;
            _detectSample = detectSample;
            _trackedDownloadService = trackedDownloadService;
            _formatCalculator = formatCalculator;
            _logger = logger;
        }

        public List<ImportDecision> GetImportDecisions(List<string> videoFiles, Series series)
        {
            return GetImportDecisions(videoFiles, series, false);
        }

        public List<ImportDecision> GetImportDecisions(List<string> videoFiles, Series series, bool filterExistingFiles)
        {
            return GetImportDecisions(videoFiles, series, null, null, false, filterExistingFiles);
        }

        public List<ImportDecision> GetImportDecisions(List<string> videoFiles, Series series, DownloadClientItem downloadClientItem, ParsedEpisodeInfo folderInfo, bool sceneSource)
        {
            return GetImportDecisions(videoFiles, series, downloadClientItem, folderInfo, sceneSource, true);
        }

        public List<ImportDecision> GetImportDecisions(List<string> videoFiles, Series series, DownloadClientItem downloadClientItem, ParsedEpisodeInfo folderInfo, bool sceneSource, bool filterExistingFiles)
        {
            var newFiles = filterExistingFiles ? _mediaFileService.FilterExistingFiles(videoFiles.ToList(), series) : videoFiles.ToList();

            _logger.Debug("Analyzing {0}/{1} files.", newFiles.Count, videoFiles.Count);

            ParsedEpisodeInfo downloadClientItemInfo = null;

            if (downloadClientItem != null)
            {
                downloadClientItemInfo = Parser.Parser.ParseTitle(downloadClientItem.Title);
            }

            var degreeOfParallelism = GetProbeDegreeOfParallelism();

            // Force the lazy-loaded quality profile once up front so the parallel passes below only read
            // the cached value instead of racing to load it from the database concurrently (LazyLoad is
            // not thread safe). This is the only lazy-loaded member the parallel region touches.
            _ = series.QualityProfile?.Value;

            // Phase 1 (bounded parallel): sample detection. This folds the previously serial
            // GetNonSampleVideoFileCount pre-pass into the parallel probe region so a slow probe here does
            // not serialize the whole batch. The result is stored on LocalEpisode and reused by the sample
            // specification. As before, sample detection only runs for scene sources; otherwise all files
            // are assumed to not be samples to avoid probing every file needlessly.
            var sampleResults = new DetectSampleResult?[newFiles.Count];
            int nonSampleVideoFileCount;

            if (sceneSource)
            {
                var isPossibleSpecialEpisode = (downloadClientItemInfo?.IsPossibleSpecialEpisode ?? false) ||
                                               (folderInfo?.IsPossibleSpecialEpisode ?? false);

                RunInParallel(newFiles.Count, degreeOfParallelism, i =>
                {
                    sampleResults[i] = _detectSample.IsSample(series, newFiles[i], isPossibleSpecialEpisode);
                });

                nonSampleVideoFileCount = sampleResults.Count(r => r != DetectSampleResult.Sample);
            }
            else
            {
                nonSampleVideoFileCount = videoFiles.Count;
            }

            var otherVideoFiles = nonSampleVideoFileCount > 1;

            // Phase 2 (bounded parallel): the probe/aggregate heavy per-file work (parse, media info via
            // Augment, custom formats). Results are collected by input index so ordering stays
            // deterministic regardless of the order the probes complete in.
            var prepared = new PreparedDecision[newFiles.Count];

            RunInParallel(newFiles.Count, degreeOfParallelism, i =>
            {
                var file = newFiles[i];

                var localEpisode = new LocalEpisode
                {
                    Series = series,
                    DownloadClientEpisodeInfo = downloadClientItemInfo,
                    DownloadItem = downloadClientItem,
                    FolderEpisodeInfo = folderInfo,
                    Path = file,
                    SceneSource = sceneSource,
                    ExistingFile = series.Path.IsParentPath(file),
                    OtherVideoFiles = otherVideoFiles,
                    SampleResult = sampleResults[i]
                };

                prepared[i] = Prepare(localEpisode, downloadClientItem);
            });

            // Phase 3 (serial, input order): evaluate specifications, assemble decisions and log.
            // Specification evaluation, history/DB lookups and logging all stay single threaded and
            // ordered to keep behaviour, logs and tests deterministic.
            var decisions = new List<ImportDecision>(prepared.Length);

            foreach (var item in prepared)
            {
                if (item.Error != null)
                {
                    _logger.Error(item.Error, "Couldn't import file. {0}", item.LocalEpisode.Path);
                }

                var decision = item.Decision ?? GetDecision(item.LocalEpisode, downloadClientItem);

                LogDecision(decision, item.LocalEpisode);

                decisions.AddIfNotNull(decision);
            }

            return decisions;
        }

        public ImportDecision GetDecision(LocalEpisode localEpisode, DownloadClientItem downloadClientItem)
        {
            var reasons = _specifications.Select(c => EvaluateSpec(c, localEpisode, downloadClientItem))
                                         .Where(c => c != null);

            return new ImportDecision(localEpisode, reasons.ToArray());
        }

        private PreparedDecision Prepare(LocalEpisode localEpisode, DownloadClientItem downloadClientItem)
        {
            // Runs inside the bounded parallel region: only the probe/aggregate heavy IO happens here. Any
            // early rejection is captured and returned so the serial phase can log and assemble it in
            // deterministic input order. Exceptions are captured (not logged here) so all logging stays
            // serial.
            try
            {
                var fileEpisodeInfo = Parser.Parser.ParsePath(localEpisode.Path);

                localEpisode.FileEpisodeInfo = fileEpisodeInfo;
                localEpisode.Size = _diskProvider.GetFileSize(localEpisode.Path);
                localEpisode.ReleaseType = localEpisode.DownloadClientEpisodeInfo?.ReleaseType ??
                                           localEpisode.FolderEpisodeInfo?.ReleaseType ??
                                           localEpisode.FileEpisodeInfo?.ReleaseType ??
                                           ReleaseType.Unknown;

                _aggregationService.Augment(localEpisode, downloadClientItem);

                if (localEpisode.Episodes.Empty())
                {
                    if (IsPartialSeason(localEpisode))
                    {
                        return new PreparedDecision(localEpisode, new ImportDecision(localEpisode, new ImportRejection(ImportRejectionReason.PartialSeason, "Partial season packs are not supported")), null);
                    }

                    if (IsSeasonExtra(localEpisode))
                    {
                        return new PreparedDecision(localEpisode, new ImportDecision(localEpisode, new ImportRejection(ImportRejectionReason.SeasonExtra, "Extras are not supported")), null);
                    }

                    return new PreparedDecision(localEpisode, new ImportDecision(localEpisode, new ImportRejection(ImportRejectionReason.InvalidSeasonOrEpisode, "Invalid season or episode")), null);
                }

                if (downloadClientItem?.DownloadId.IsNotNullOrWhiteSpace() == true)
                {
                    var trackedDownload = _trackedDownloadService.Find(downloadClientItem.DownloadId);

                    if (trackedDownload?.RemoteEpisode?.Release?.IndexerFlags != null)
                    {
                        localEpisode.IndexerFlags = trackedDownload.RemoteEpisode.Release.IndexerFlags;
                    }
                }

                localEpisode.CustomFormats = _formatCalculator.ParseCustomFormat(localEpisode);
                localEpisode.CustomFormatScore = localEpisode.Series.QualityProfile?.Value.CalculateCustomFormatScore(localEpisode.CustomFormats) ?? 0;

                return new PreparedDecision(localEpisode, null, null);
            }
            catch (AugmentingFailedException)
            {
                return new PreparedDecision(localEpisode, new ImportDecision(localEpisode, new ImportRejection(ImportRejectionReason.UnableToParse, "Unable to parse file")), null);
            }
            catch (Exception ex)
            {
                return new PreparedDecision(localEpisode, new ImportDecision(localEpisode, new ImportRejection(ImportRejectionReason.Error, "Unexpected error processing file")), ex);
            }
        }

        private void LogDecision(ImportDecision decision, LocalEpisode localEpisode)
        {
            if (decision == null)
            {
                _logger.Error("Unable to make a decision on {0}", localEpisode.Path);
            }
            else if (decision.Rejections.Any())
            {
                _logger.Debug("File rejected for the following reasons: {0}", string.Join(", ", decision.Rejections));
            }
            else
            {
                _logger.Debug("File accepted");
            }
        }

        private ImportRejection EvaluateSpec(IImportDecisionEngineSpecification spec, LocalEpisode localEpisode, DownloadClientItem downloadClientItem)
        {
            try
            {
                var result = spec.IsSatisfiedBy(localEpisode, downloadClientItem);

                if (!result.Accepted)
                {
                    return new ImportRejection(result.Reason, result.Message);
                }
            }
            catch (Exception e)
            {
                // e.Data.Add("report", remoteEpisode.Report.ToJson());
                // e.Data.Add("parsed", remoteEpisode.ParsedEpisodeInfo.ToJson());
                _logger.Error(e, "Couldn't evaluate decision on {0}", localEpisode.Path);
                return new ImportRejection(ImportRejectionReason.DecisionError, $"{spec.GetType().Name}: {e.Message}");
            }

            return null;
        }

        private int GetProbeDegreeOfParallelism()
        {
            var envValue = Environment.GetEnvironmentVariable("IMPORT_PROBE_THREADS") ?? $"{DEFAULT_PROBE_THREADS}";
            var threads = DEFAULT_PROBE_THREADS;

            if (int.TryParse(envValue, out var parsedThreads))
            {
                threads = parsedThreads;
            }

            threads = Math.Max(PROBE_THREADS_LOWER_BOUND, threads);
            threads = Math.Min(PROBE_THREADS_UPPER_BOUND, threads);

            return threads;
        }

        // Runs body(i) for i in [0, count) across at most 'degree' dedicated worker threads. A degree of
        // 1 (or a single item) runs inline on the calling thread, reproducing the original serial
        // behaviour exactly. Dedicated threads are used (rather than the thread pool) so exactly 'degree'
        // probes run concurrently without waiting on thread-pool injection, bounding concurrent ffprobe
        // processes to 'degree'. The first exception thrown by any worker is rethrown to the caller.
        private static void RunInParallel(int count, int degree, Action<int> body)
        {
            if (count <= 0)
            {
                return;
            }

            if (degree <= 1 || count == 1)
            {
                for (var i = 0; i < count; i++)
                {
                    body(i);
                }

                return;
            }

            var workerCount = Math.Min(degree, count);
            var nextIndex = -1;
            Exception firstError = null;
            var threads = new Thread[workerCount];

            for (var w = 0; w < workerCount; w++)
            {
                var thread = new Thread(() =>
                {
                    int index;

                    while ((index = Interlocked.Increment(ref nextIndex)) < count)
                    {
                        try
                        {
                            body(index);
                        }
                        catch (Exception ex)
                        {
                            Interlocked.CompareExchange(ref firstError, ex, null);
                        }
                    }
                })
                {
                    IsBackground = true,
                    Name = "ImportProbe"
                };

                threads[w] = thread;
                thread.Start();
            }

            foreach (var thread in threads)
            {
                thread.Join();
            }

            if (firstError != null)
            {
                ExceptionDispatchInfo.Capture(firstError).Throw();
            }
        }

        private bool IsPartialSeason(LocalEpisode localEpisode)
        {
            var downloadClientEpisodeInfo = localEpisode.DownloadClientEpisodeInfo;
            var folderEpisodeInfo = localEpisode.FolderEpisodeInfo;
            var fileEpisodeInfo = localEpisode.FileEpisodeInfo;

            if (downloadClientEpisodeInfo != null && downloadClientEpisodeInfo.IsPartialSeason)
            {
                return true;
            }

            if (folderEpisodeInfo != null && folderEpisodeInfo.IsPartialSeason)
            {
                return true;
            }

            if (fileEpisodeInfo != null && fileEpisodeInfo.IsPartialSeason)
            {
                return true;
            }

            return false;
        }

        private bool IsSeasonExtra(LocalEpisode localEpisode)
        {
            var downloadClientEpisodeInfo = localEpisode.DownloadClientEpisodeInfo;
            var folderEpisodeInfo = localEpisode.FolderEpisodeInfo;
            var fileEpisodeInfo = localEpisode.FileEpisodeInfo;

            if (downloadClientEpisodeInfo != null && downloadClientEpisodeInfo.IsSeasonExtra)
            {
                return true;
            }

            if (folderEpisodeInfo != null && folderEpisodeInfo.IsSeasonExtra)
            {
                return true;
            }

            if (fileEpisodeInfo != null && fileEpisodeInfo.IsSeasonExtra)
            {
                return true;
            }

            return false;
        }

        private sealed class PreparedDecision
        {
            public PreparedDecision(LocalEpisode localEpisode, ImportDecision decision, Exception error)
            {
                LocalEpisode = localEpisode;
                Decision = decision;
                Error = error;
            }

            public LocalEpisode LocalEpisode { get; }

            public ImportDecision Decision { get; }

            public Exception Error { get; }
        }
    }
}
