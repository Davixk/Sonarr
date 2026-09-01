using System;
using System.Collections.Generic;
using System.Reflection;
using FFMpegCore;
using FizzWare.NBuilder;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.Download;
using NzbDrone.Core.History;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.EpisodeImport.Specifications;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.MediaFiles.MediaInfo;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Core.Tv;
using NzbDrone.Test.Common;

namespace NzbDrone.Core.Test.MediaFiles
{
    [TestFixture]
    public class DolbyVisionEnforcementServiceFixture : CoreTest<DolbyVisionEnforcementService>
    {
        private Series _series;
        private EpisodeFile _episodeFile;

        [SetUp]
        public void Setup()
        {
            Environment.SetEnvironmentVariable("DV_REJECT_PROFILES", "5");
            Environment.SetEnvironmentVariable("DV_REJECT_COMPAT_IDS", null);

            _series = Builder<Series>.CreateNew().Build();
            _episodeFile = Builder<EpisodeFile>.CreateNew()
                .With(f => f.MediaInfo = GivenDovi(5, 0))
                .With(f => f.Episodes = new List<Episode> { new Episode { Id = 7 } })
                .Build();
        }

        [TearDown]
        public void TearDown()
        {
            Environment.SetEnvironmentVariable("DV_REJECT_PROFILES", null);
            Environment.SetEnvironmentVariable("DV_REJECT_COMPAT_IDS", null);
        }

        private MediaInfoModel GivenDovi(int profile, int compatId)
        {
            var dovi = (DoviConfigurationRecordSideData)Assembly.GetAssembly(typeof(FFProbe)).CreateInstance("FFMpegCore.DoviConfigurationRecordSideData");
            dovi.DvProfile = profile;
            dovi.DvBlSignalCompatibilityId = compatId;

            return new MediaInfoModel { DoviConfigurationRecord = dovi };
        }

        private void GivenGrabHistory()
        {
            Mocker.GetMock<IHistoryService>()
                .Setup(s => s.GetBySeries(_series.Id, EpisodeHistoryEventType.Grabbed))
                .Returns(new List<EpisodeHistory>
                {
                    new EpisodeHistory { Id = 42, EpisodeId = 7, EventType = EpisodeHistoryEventType.Grabbed }
                });
        }

        [Test]
        public void should_delete_blocklist_and_research_an_excluded_file()
        {
            GivenGrabHistory();

            Subject.EnforceOnLibraryFile(_series, _episodeFile).Should().BeTrue();

            Mocker.GetMock<IDeleteMediaFiles>().Verify(v => v.DeleteEpisodeFile(_series, _episodeFile), Times.Once());
            Mocker.GetMock<IFailedDownloadService>()
                .Verify(v => v.MarkAsFailed(42, It.Is<string>(m => m.StartsWith(DolbyVisionSpecification.BlocklistToken)), false), Times.Once());

            ExceptionVerification.ExpectedWarns(1);
        }

        [Test]
        public void should_do_nothing_for_a_non_excluded_file()
        {
            _episodeFile.MediaInfo = GivenDovi(8, 1);

            Subject.EnforceOnLibraryFile(_series, _episodeFile).Should().BeFalse();

            Mocker.GetMock<IDeleteMediaFiles>().Verify(v => v.DeleteEpisodeFile(It.IsAny<Series>(), It.IsAny<EpisodeFile>()), Times.Never());
            Mocker.GetMock<IFailedDownloadService>().Verify(v => v.MarkAsFailed(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<bool>()), Times.Never());
        }

        [Test]
        public void should_delete_but_not_blocklist_when_no_grab_history()
        {
            Mocker.GetMock<IHistoryService>()
                .Setup(s => s.GetBySeries(_series.Id, EpisodeHistoryEventType.Grabbed))
                .Returns(new List<EpisodeHistory>());

            Subject.EnforceOnLibraryFile(_series, _episodeFile).Should().BeTrue();

            Mocker.GetMock<IDeleteMediaFiles>().Verify(v => v.DeleteEpisodeFile(_series, _episodeFile), Times.Once());
            Mocker.GetMock<IFailedDownloadService>().Verify(v => v.MarkAsFailed(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<bool>()), Times.Never());

            ExceptionVerification.ExpectedWarns(1);
        }

        [Test]
        public void should_enforce_on_every_file_on_scan_when_active()
        {
            GivenGrabHistory();

            var files = new List<EpisodeFile>
            {
                Builder<EpisodeFile>.CreateNew().With(f => f.MediaInfo = GivenDovi(5, 0)).With(f => f.Episodes = new List<Episode> { new Episode { Id = 7 } }).Build(),
                Builder<EpisodeFile>.CreateNew().With(f => f.MediaInfo = GivenDovi(5, 0)).With(f => f.Episodes = new List<Episode> { new Episode { Id = 7 } }).Build()
            };

            Mocker.GetMock<IMediaFileService>()
                .Setup(s => s.GetFilesBySeries(_series.Id))
                .Returns(files);

            Subject.Handle(new SeriesScannedEvent(_series, new List<string>()));

            Mocker.GetMock<IDeleteMediaFiles>().Verify(v => v.DeleteEpisodeFile(_series, It.IsAny<EpisodeFile>()), Times.Exactly(2));

            ExceptionVerification.ExpectedWarns(2);
        }

        [Test]
        public void should_not_enforce_on_scan_when_not_configured()
        {
            Environment.SetEnvironmentVariable("DV_REJECT_PROFILES", null);

            var files = new List<EpisodeFile>
            {
                Builder<EpisodeFile>.CreateNew().With(f => f.MediaInfo = GivenDovi(5, 0)).Build()
            };

            Mocker.GetMock<IMediaFileService>()
                .Setup(s => s.GetFilesBySeries(_series.Id))
                .Returns(files);

            Subject.Handle(new SeriesScannedEvent(_series, new List<string>()));

            Mocker.GetMock<IDeleteMediaFiles>().Verify(v => v.DeleteEpisodeFile(It.IsAny<Series>(), It.IsAny<EpisodeFile>()), Times.Never());
        }
    }
}
