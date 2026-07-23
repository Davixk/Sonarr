using System.Collections.Generic;
using System.Linq;
using FizzWare.NBuilder;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.Download;
using NzbDrone.Core.MediaFiles.EpisodeImport;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Core.Tv;

namespace NzbDrone.Core.Test.MediaFiles.EpisodeImport
{
    [TestFixture]
    public class ImportDecisionMakerRevalidateFixture : CoreTest<ImportDecisionMaker>
    {
        private DownloadClientItem _downloadClientItem;
        private Mock<IImportDecisionEngineSpecification> _rejectIfEpisodeHasFile;

        [SetUp]
        public void Setup()
        {
            _downloadClientItem = Builder<DownloadClientItem>.CreateNew().Build();

            // Stands in for the per-episode database-state specifications (already-imported / upgrade): a
            // LocalEpisode whose episode already has a file rejects a second import, exactly the case the
            // concurrent decide phase cannot see against its pre-commit snapshot. Sonarr keys PER EPISODE,
            // so the spec inspects every episode the file maps to.
            _rejectIfEpisodeHasFile = new Mock<IImportDecisionEngineSpecification>();
            _rejectIfEpisodeHasFile.Setup(c => c.IsSatisfiedBy(It.IsAny<LocalEpisode>(), It.IsAny<DownloadClientItem>()))
                                   .Returns<LocalEpisode, DownloadClientItem>((localEpisode, downloadClientItem) =>
                                       localEpisode.Episodes.Any(e => e.EpisodeFileId > 0)
                                           ? ImportSpecDecision.Reject(ImportRejectionReason.EpisodeAlreadyImported, "Episode file already imported")
                                           : ImportSpecDecision.Accept());

            Mocker.SetConstant<IEnumerable<IImportDecisionEngineSpecification>>(new[] { _rejectIfEpisodeHasFile.Object });
        }

        private Episode GivenEpisode(int id, int episodeFileId)
        {
            return new Episode { Id = id, SeasonNumber = 1, EpisodeNumber = id, EpisodeFileId = episodeFileId };
        }

        private ImportDecision GivenApprovedDecision(params int[] episodeIds)
        {
            var localEpisode = new LocalEpisode
            {
                Series = new Series(),
                Path = string.Format(@"C:\Test\The.Series.S01E{0:00}.1080p.mkv", episodeIds.First()),
                Episodes = episodeIds.Select(id => GivenEpisode(id, 0)).ToList()
            };

            var decision = new ImportDecision(localEpisode);
            decision.Approved.Should().BeTrue();

            return decision;
        }

        // Sets the current DB state each requested episode id is refreshed to (keyed by episode file id).
        private void GivenCurrentEpisodeState(Dictionary<int, int> episodeFileIdById)
        {
            Mocker.GetMock<IEpisodeService>()
                  .Setup(s => s.GetEpisodes(It.IsAny<IEnumerable<int>>()))
                  .Returns<IEnumerable<int>>(ids =>
                      ids.Select(id => GivenEpisode(id, episodeFileIdById.TryGetValue(id, out var fileId) ? fileId : 0)).ToList());
        }

        [Test]
        public void should_keep_approved_when_episode_state_is_unchanged()
        {
            var decision = GivenApprovedDecision(1);
            GivenCurrentEpisodeState(new Dictionary<int, int> { { 1, 0 } });

            var result = Subject.RevalidateApprovedDecisions(new List<ImportDecision> { decision }, _downloadClientItem);

            result.Single().Approved.Should().BeTrue();
        }

        [Test]
        public void should_reject_when_episode_already_imported_by_an_earlier_commit()
        {
            var decision = GivenApprovedDecision(1);

            // Another download for the same episode was committed first, so the episode now has a file.
            GivenCurrentEpisodeState(new Dictionary<int, int> { { 1, 99 } });

            var result = Subject.RevalidateApprovedDecisions(new List<ImportDecision> { decision }, _downloadClientItem);

            result.Single().Approved.Should().BeFalse();
        }

        [Test]
        public void should_import_episode_only_once_when_two_downloads_race()
        {
            var first = GivenApprovedDecision(1);
            var second = GivenApprovedDecision(1);

            // First download commits while the episode still has no file: it is approved and imports.
            GivenCurrentEpisodeState(new Dictionary<int, int> { { 1, 0 } });
            var firstResult = Subject.RevalidateApprovedDecisions(new List<ImportDecision> { first }, _downloadClientItem);
            firstResult.Single().Approved.Should().BeTrue();

            // The first commit imported the episode, so the second download re-validates against an episode
            // that now has a file and is rejected: the episode is imported exactly once.
            GivenCurrentEpisodeState(new Dictionary<int, int> { { 1, 99 } });
            var secondResult = Subject.RevalidateApprovedDecisions(new List<ImportDecision> { second }, _downloadClientItem);
            secondResult.Single().Approved.Should().BeFalse();
        }

        [Test]
        public void should_reject_only_already_imported_episodes_in_a_season_pack()
        {
            // A season pack is a folder of per-episode files, one decision each. Another download already
            // imported episode 2 in this same serial pass, so only that episode's file must be rejected;
            // the rest of the pack still imports.
            var episode1 = GivenApprovedDecision(1);
            var episode2 = GivenApprovedDecision(2);
            var episode3 = GivenApprovedDecision(3);

            GivenCurrentEpisodeState(new Dictionary<int, int> { { 1, 0 }, { 2, 99 }, { 3, 0 } });

            var result = Subject.RevalidateApprovedDecisions(new List<ImportDecision> { episode1, episode2, episode3 }, _downloadClientItem);

            result[0].Approved.Should().BeTrue("episode 1 was not imported by the other download");
            result[1].Approved.Should().BeFalse("episode 2 was already imported by the other download");
            result[2].Approved.Should().BeTrue("episode 3 was not imported by the other download");
        }

        [Test]
        public void should_not_re_evaluate_already_rejected_decisions()
        {
            var localEpisode = new LocalEpisode
            {
                Series = new Series(),
                Path = @"C:\Test\rejected.mkv",
                Episodes = new List<Episode> { GivenEpisode(1, 0) }
            };

            var rejected = new ImportDecision(localEpisode, new ImportRejection(ImportRejectionReason.Sample, "Sample"));

            var result = Subject.RevalidateApprovedDecisions(new List<ImportDecision> { rejected }, _downloadClientItem);

            result.Single().Approved.Should().BeFalse();

            // A rejected decision is passed through untouched; its episode state is never refreshed.
            Mocker.GetMock<IEpisodeService>()
                  .Verify(s => s.GetEpisodes(It.IsAny<IEnumerable<int>>()), Times.Never());
        }
    }
}
