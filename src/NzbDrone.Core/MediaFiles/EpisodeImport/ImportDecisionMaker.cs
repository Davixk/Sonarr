using System;
using System.Collections.Generic;
using System.Linq;
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
        List<ImportDecision> RevalidateApprovedDecisions(List<ImportDecision> decisions, DownloadClientItem downloadClientItem);
    }

    public class ImportDecisionMaker : IMakeImportDecision
    {
        private readonly IEnumerable<IImportDecisionEngineSpecification> _specifications;
        private readonly IMediaFileService _mediaFileService;
        private readonly IAggregationService _aggregationService;
        private readonly IDiskProvider _diskProvider;
        private readonly IDetectSample _detectSample;
        private readonly ITrackedDownloadService _trackedDownloadService;
        private readonly ICustomFormatCalculationService _formatCalculator;
        private readonly IEpisodeService _episodeService;
        private readonly Logger _logger;

        public ImportDecisionMaker(IEnumerable<IImportDecisionEngineSpecification> specifications,
                                   IMediaFileService mediaFileService,
                                   IAggregationService aggregationService,
                                   IDiskProvider diskProvider,
                                   IDetectSample detectSample,
                                   ITrackedDownloadService trackedDownloadService,
                                   ICustomFormatCalculationService formatCalculator,
                                   IEpisodeService episodeService,
                                   Logger logger)
        {
            _specifications = specifications;
            _mediaFileService = mediaFileService;
            _aggregationService = aggregationService;
            _diskProvider = diskProvider;
            _detectSample = detectSample;
            _trackedDownloadService = trackedDownloadService;
            _formatCalculator = formatCalculator;
            _episodeService = episodeService;
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

            // Force the lazy-loaded quality profile once up front so the parallel passes below only read
            // the cached value instead of racing to load it from the database concurrently (LazyLoad is
            // not thread safe). This is the only lazy-loaded member the parallel region touches.
            _ = series.QualityProfile?.Value;

            // Phase 1 (bounded parallel): sample detection. This folds the previously serial
            // GetNonSampleVideoFileCount pre-pass into the parallel probe region so a slow probe here does
            // not serialize the whole batch. As before, sample detection only runs for scene sources;
            // otherwise all files are assumed to not be samples to avoid probing every file needlessly.
            var sampleResults = new DetectSampleResult?[newFiles.Count];
            int nonSampleVideoFileCount;

            if (sceneSource)
            {
                var isPossibleSpecialEpisode = (downloadClientItemInfo?.IsPossibleSpecialEpisode ?? false) ||
                                               (folderInfo?.IsPossibleSpecialEpisode ?? false);

                Action<int> detectSampleBody = i =>
                {
                    sampleResults[i] = _detectSample.IsSample(series, newFiles[i], isPossibleSpecialEpisode);
                };

                var sampleTimedOut = ImportProbePool.Run(newFiles.Count, detectSampleBody);

                for (var i = 0; i < newFiles.Count; i++)
                {
                    if (sampleTimedOut[i])
                    {
                        // An abandoned sample probe defaults to NotSample so it does not block the batch
                        // here; the same wedged file times out again in Phase 2 and is rejected there.
                        sampleResults[i] = DetectSampleResult.NotSample;
                    }
                }

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

            Func<int, LocalEpisode> buildLocalEpisode = i =>
            {
                var file = newFiles[i];

                return new LocalEpisode
                {
                    Series = series,
                    DownloadClientEpisodeInfo = downloadClientItemInfo,
                    DownloadItem = downloadClientItem,
                    FolderEpisodeInfo = folderInfo,
                    Path = file,
                    SceneSource = sceneSource,
                    ExistingFile = series.Path.IsParentPath(file),
                    OtherVideoFiles = otherVideoFiles
                };
            };

            Action<int> prepareBody = i =>
            {
                prepared[i] = Prepare(buildLocalEpisode(i), downloadClientItem);
            };

            var prepareTimedOut = ImportProbePool.Run(newFiles.Count, prepareBody);

            for (var i = 0; i < newFiles.Count; i++)
            {
                if (prepareTimedOut[i])
                {
                    // The probe for this file was abandoned. Reject it this pass (reusing the generic
                    // Error reason) so the batch completes and the healthy files still import; the file
                    // is logged in serial Phase 3 and stays pending for a future pass rather than
                    // hanging the whole import.
                    var localEpisode = buildLocalEpisode(i);

                    prepared[i] = new PreparedDecision(localEpisode, new ImportDecision(localEpisode, new ImportRejection(ImportRejectionReason.Error, "Probe timed out")), null);
                }
            }

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

        // Re-runs the cheap specifications against the CURRENT database state for each already-approved
        // decision, right before it is committed. The probe/decision phase runs concurrently across
        // downloads against a pre-commit snapshot, so two downloads for the same episode(s) can both be
        // approved. Refreshing each episode's file state here (exactly as AggregateEpisodes does during the
        // decide phase) and re-evaluating lets the already-imported / upgrade / same-episodes specifications
        // reject a second download once an earlier one in the same serial commit pass has imported the
        // episode, reproducing what the original serial "decide immediately before importing" flow would
        // have done. Sonarr re-keys PER EPISODE (a file maps to one or more episodes) rather than Radarr's
        // single per-movie re-key, so a season-pack commit rejects only the episodes another download
        // already imported this pass while still importing the rest. The expensive probe results already
        // carried on each LocalEpisode (media info, custom formats, sample result) are kept and reused.
        public List<ImportDecision> RevalidateApprovedDecisions(List<ImportDecision> decisions, DownloadClientItem downloadClientItem)
        {
            if (decisions == null)
            {
                return null;
            }

            var revalidated = new List<ImportDecision>(decisions.Count);

            foreach (var decision in decisions)
            {
                if (!decision.Approved)
                {
                    revalidated.Add(decision);
                    continue;
                }

                var localEpisode = decision.LocalEpisode;

                if (localEpisode?.Episodes != null && localEpisode.Episodes.Any())
                {
                    var episodeIds = localEpisode.Episodes.Select(e => e.Id).ToList();
                    var refreshed = _episodeService.GetEpisodes(episodeIds);

                    // Only substitute when the DB returned the same set of episodes, so a partial or empty
                    // lookup never silently drops the episodes the file was matched to.
                    if (refreshed != null && refreshed.Count == episodeIds.Count)
                    {
                        localEpisode.Episodes = refreshed;
                    }
                }

                var recheck = GetDecision(localEpisode, downloadClientItem);

                if (!recheck.Approved)
                {
                    _logger.Debug("Import for {0} rejected on commit re-validation: {1}", localEpisode?.Path, string.Join(", ", recheck.Rejections));
                }

                revalidated.Add(recheck);
            }

            return revalidated;
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
