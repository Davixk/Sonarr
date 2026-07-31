using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using NLog;
using NzbDrone.Common;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Tv;

namespace NzbDrone.Core.MediaFiles
{
    public interface IMediaFileTableCleanupService
    {
        void Clean(Series series, List<string> filesOnDisk);
    }

    public class MediaFileTableCleanupService : IMediaFileTableCleanupService
    {
        private readonly IMediaFileService _mediaFileService;
        private readonly IEpisodeService _episodeService;
        private readonly Logger _logger;

        public MediaFileTableCleanupService(IMediaFileService mediaFileService,
                                            IEpisodeService episodeService,
                                            Logger logger)
        {
            _mediaFileService = mediaFileService;
            _episodeService = episodeService;
            _logger = logger;
        }

        public void Clean(Series series, List<string> filesOnDisk)
        {
            var seriesFiles = _mediaFileService.GetFilesBySeries(series.Id);
            var episodes = _episodeService.GetEpisodeBySeries(series.Id);

            var filesOnDiskKeys = new HashSet<string>(filesOnDisk, PathEqualityComparer.Instance);

            // fork4: if disk enumeration returned nothing while the DB still holds file records, a mount or
            // enumeration failure is far likelier than every file having genuinely vanished. Skip the
            // deletions this pass rather than mass-marking the whole library missing. On by default.
            if (filesOnDiskKeys.Count == 0 && seriesFiles.Count > 0)
            {
                _logger.Warn("Disk enumeration returned no files for {0} while {1} record(s) exist; skipping cleanup deletions to avoid data loss on a possible mount failure.", series, seriesFiles.Count);
                return;
            }

            // fork4: optional fractional cap (CLEANUP_MAX_DELETE_FRACTION, default 1.0 = off). When set
            // below 1 and the share of records that would be deleted this pass exceeds it, skip the
            // deletions rather than remove a suspiciously large fraction at once.
            var maxDeleteFraction = GetMaxDeleteFraction();

            if (maxDeleteFraction < 1.0 && seriesFiles.Count > 0)
            {
                var wouldDelete = seriesFiles.Count(seriesFile => !filesOnDiskKeys.Contains(Path.Combine(series.Path, seriesFile.RelativePath)));

                if ((double)wouldDelete / seriesFiles.Count > maxDeleteFraction)
                {
                    _logger.Warn("Cleanup would delete {0} of {1} record(s) for {2}, exceeding CLEANUP_MAX_DELETE_FRACTION={3}; skipping deletions this pass.", wouldDelete, seriesFiles.Count, series, maxDeleteFraction);
                    return;
                }
            }

            foreach (var seriesFile in seriesFiles)
            {
                var episodeFile = seriesFile;
                var episodeFilePath = Path.Combine(series.Path, episodeFile.RelativePath);

                try
                {
                    if (!filesOnDiskKeys.Contains(episodeFilePath))
                    {
                        _logger.Debug("File [{0}] no longer exists on disk, removing from db", episodeFilePath);
                        _mediaFileService.Delete(seriesFile, DeleteMediaFileReason.MissingFromDisk);
                        continue;
                    }

                    if (episodes.None(e => e.EpisodeFileId == episodeFile.Id))
                    {
                        _logger.Debug("File [{0}] is not assigned to any episodes, removing from db", episodeFilePath);
                        _mediaFileService.Delete(episodeFile, DeleteMediaFileReason.NoLinkedEpisodes);
                        continue;
                    }

// var localEpsiode = _parsingService.GetLocalEpisode(episodeFile.Path, series);
//
//                    if (localEpsiode == null || episodes.Count != localEpsiode.Episodes.Count)
//                    {
//                        _logger.Debug("File [{0}] parsed episodes has changed, removing from db", episodeFile.Path);
//                        _mediaFileService.Delete(episodeFile);
//                        continue;
//                    }
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Unable to cleanup EpisodeFile in DB: {0}", episodeFile.Id);
                }
            }

            foreach (var e in episodes)
            {
                var episode = e;

                if (episode.EpisodeFileId > 0 && seriesFiles.None(f => f.Id == episode.EpisodeFileId))
                {
                    episode.EpisodeFileId = 0;
                    _episodeService.UpdateEpisode(episode);
                }
            }
        }

        // Reads CLEANUP_MAX_DELETE_FRACTION. Default 1.0 (cap off). Only a value in (0,1] arms the cap;
        // anything else leaves it off so a typo can never start skipping legitimate cleanups.
        private static double GetMaxDeleteFraction()
        {
            var raw = Environment.GetEnvironmentVariable("CLEANUP_MAX_DELETE_FRACTION");

            if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var fraction) && fraction > 0.0 && fraction <= 1.0)
            {
                return fraction;
            }

            return 1.0;
        }
    }
}
