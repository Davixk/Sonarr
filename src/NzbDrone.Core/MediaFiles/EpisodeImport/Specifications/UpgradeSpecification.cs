using System.Linq;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.Download;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;

namespace NzbDrone.Core.MediaFiles.EpisodeImport.Specifications
{
    public class UpgradeSpecification : IImportDecisionEngineSpecification
    {
        private readonly IConfigService _configService;
        private readonly ICustomFormatCalculationService _formatService;
        private readonly Logger _logger;

        public UpgradeSpecification(IConfigService configService,
                                    ICustomFormatCalculationService formatService,
                                    Logger logger)
        {
            _configService = configService;
            _formatService = formatService;
            _logger = logger;
        }

        public ImportSpecDecision IsSatisfiedBy(LocalEpisode localEpisode, DownloadClientItem downloadClientItem)
        {
            var downloadPropersAndRepacks = _configService.DownloadPropersAndRepacks;
            var qualityProfile = localEpisode.Series.QualityProfile.Value;
            var qualityComparer = new QualityModelComparer(qualityProfile);

            foreach (var episode in localEpisode.Episodes.Where(e => e.EpisodeFileId > 0))
            {
                var episodeFile = episode.EpisodeFile.Value;

                if (episodeFile == null)
                {
                    _logger.Trace("Unable to get episode file details from the DB. EpisodeId: {0} EpisodeFileId: {1}", episode.Id, episode.EpisodeFileId);
                    continue;
                }

                var qualityCompare = qualityComparer.Compare(localEpisode.Quality.Quality, episodeFile.Quality.Quality);

                if (qualityCompare < 0)
                {
                    _logger.Debug("This file isn't a quality upgrade for all episodes. Existing quality: {0}. New Quality {1}. Skipping {2}", episodeFile.Quality.Quality, localEpisode.Quality.Quality, localEpisode.Path);
                    return ImportSpecDecision.Reject(ImportRejectionReason.NotQualityUpgrade, "Not an upgrade for existing episode file(s). Existing quality: {0}. New Quality {1}.", episodeFile.Quality.Quality, localEpisode.Quality.Quality);
                }

                // Same quality, propers/repacks are preferred and it is not a revision update. Reject revision downgrade.

                if (qualityCompare == 0 &&
                    downloadPropersAndRepacks != ProperDownloadTypes.DoNotPrefer &&
                    localEpisode.Quality.Revision.CompareTo(episodeFile.Quality.Revision) < 0)
                {
                    _logger.Debug("This file isn't a quality revision upgrade for all episodes. Skipping {0}", localEpisode.Path);
                    return ImportSpecDecision.Reject(ImportRejectionReason.NotRevisionUpgrade, "Not a quality revision upgrade for existing episode file(s)");
                }

                var currentFormats = _formatService.ParseCustomFormat(episodeFile);
                var currentFormatScore = qualityProfile.CalculateCustomFormatScore(currentFormats);
                var newFormats = localEpisode.CustomFormats;
                var newFormatScore = localEpisode.CustomFormatScore;

                // fork27: an IDENTICAL release re-presented must be a NO-OP, never a delete+re-import. The
                // MediaInfo probe of a remote/debrid file is non-deterministic ({MediaInfo VideoDynamicRangeType}
                // flaps), so a fresh grab of the SAME release re-imports, re-probes, and renames the file - which
                // kills Plex's stored path. When the incoming file is byte-identical to the existing one (same
                // quality, same size, and the same custom-format score - and no custom format reads MediaInfo, so
                // a flapped probe verdict cannot change the score), the ONLY thing that changed is the probe, which
                // is not a quality change. Reject it so the existing file and its name are untouched. Genuinely
                // different releases (different size, or a real CF/quality/revision improvement) are unaffected;
                // stock same-score-different-file behaviour (e.g. a language "upgrade") is preserved.
                if (qualityCompare == 0 &&
                    newFormatScore == currentFormatScore &&
                    localEpisode.Size > 0 &&
                    localEpisode.Size == episodeFile.Size)
                {
                    _logger.Debug("Identical release re-presented (same size {0}, same custom-format score {1}); probe noise is not an upgrade, skipping {2}", localEpisode.Size, newFormatScore, localEpisode.Path);

                    return ImportSpecDecision.Reject(ImportRejectionReason.NotCustomFormatUpgrade, "Identical release already imported (same size and custom formats); not replacing on probe noise");
                }

                if (qualityCompare == 0 && newFormatScore < currentFormatScore)
                {
                    _logger.Debug("New item's custom formats [{0}] ({1}) do not improve on [{2}] ({3}), skipping",
                        newFormats != null ? newFormats.ConcatToString() : "",
                        newFormatScore,
                        currentFormats != null ? currentFormats.ConcatToString() : "",
                        currentFormatScore);

                    return ImportSpecDecision.Reject(ImportRejectionReason.NotCustomFormatUpgrade,
                        "Not a Custom Format upgrade for existing episode file(s). New: [{0}] ({1}) do not improve on Existing: [{2}] ({3})",
                        newFormats != null ? newFormats.ConcatToString() : "",
                        newFormatScore,
                        currentFormats != null ? currentFormats.ConcatToString() : "",
                        currentFormatScore);
                }

                _logger.Debug("New item's custom formats [{0}] ({1}) improve on [{2}] ({3}), accepting",
                    newFormats != null ? newFormats.ConcatToString() : "",
                    newFormatScore,
                    currentFormats != null ? currentFormats.ConcatToString() : "",
                    currentFormatScore);
            }

            return ImportSpecDecision.Accept();
        }
    }
}
