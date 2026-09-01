using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Core.Download;
using NzbDrone.Core.History;
using NzbDrone.Core.MediaFiles.EpisodeImport.Specifications;
using NzbDrone.Core.Tv;

namespace NzbDrone.Core.MediaFiles
{
    public interface IDolbyVisionEnforcementService
    {
        bool EnforceOnLibraryFile(Series series, EpisodeFile episodeFile);
    }

    // fork24: the guaranteed backstop for the DV exclusion. The import-time gate + reliable re-probe close
    // the live window, but a file can still be in the library from before this fix (or an import path the
    // gate did not cover). The library scan re-probes every file locally - the only fully reliable read -
    // so this enforces the exclusion on that reliable MediaInfo: remove the file, blocklist the release it
    // came from (with the retraceable [DV-EXCLUDED] reason from its grabbed history), and re-search. All DV
    // enforcement points share DolbyVisionSpecification's exclusion logic + message, so they act identically.
    public class DolbyVisionEnforcementService : IDolbyVisionEnforcementService
    {
        private readonly IDeleteMediaFiles _deleteMediaFiles;
        private readonly IFailedDownloadService _failedDownloadService;
        private readonly IHistoryService _historyService;
        private readonly Logger _logger;

        public DolbyVisionEnforcementService(IDeleteMediaFiles deleteMediaFiles,
                                             IFailedDownloadService failedDownloadService,
                                             IHistoryService historyService,
                                             Logger logger)
        {
            _deleteMediaFiles = deleteMediaFiles;
            _failedDownloadService = failedDownloadService;
            _historyService = historyService;
            _logger = logger;
        }

        public bool EnforceOnLibraryFile(Series series, EpisodeFile episodeFile)
        {
            var message = DolbyVisionSpecification.GetExclusionMessage(episodeFile?.MediaInfo);

            if (message == null)
            {
                return false;
            }

            _logger.Warn("Library file for {0} reads as excluded Dolby Vision ({1}); removing, blocklisting and re-searching", series, message);

            var seriesGrabs = _historyService.GetBySeries(series.Id, EpisodeHistoryEventType.Grabbed)
                .OrderByDescending(h => h.Date)
                .ToList();

            var episodeIds = episodeFile?.Episodes?.Value?.Select(e => e.Id).ToHashSet() ?? new HashSet<int>();

            // prefer the grab that actually produced this file's episode(s) so the right release is blocklisted;
            // fall back to the most recent series grab when the file's episodes are not loaded.
            var grab = (episodeIds.Any() ? seriesGrabs.FirstOrDefault(h => episodeIds.Contains(h.EpisodeId)) : null)
                ?? seriesGrabs.FirstOrDefault();

            _deleteMediaFiles.DeleteEpisodeFile(series, episodeFile);

            if (grab != null)
            {
                _failedDownloadService.MarkAsFailed(grab.Id, message);
            }
            else
            {
                _logger.Debug("No grabbed history for {0}; removed the excluded file but there is no source release to blocklist", series);
            }

            return true;
        }
    }
}
