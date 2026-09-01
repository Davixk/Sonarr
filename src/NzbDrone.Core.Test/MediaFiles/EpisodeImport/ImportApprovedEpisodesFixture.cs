using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using FFMpegCore;
using FizzWare.NBuilder;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Download;
using NzbDrone.Core.History;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.EpisodeImport;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.MediaFiles.MediaInfo;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Core.Tv;
using NzbDrone.Test.Common;

namespace NzbDrone.Core.Test.MediaFiles.EpisodeImport
{
    [TestFixture]
    public class ImportApprovedEpisodesFixture : CoreTest<ImportApprovedEpisodes>
    {
        private List<ImportDecision> _rejectedDecisions;
        private List<ImportDecision> _approvedDecisions;

        private DownloadClientItem _downloadClientItem;

        [SetUp]
        public void Setup()
        {
            _rejectedDecisions = new List<ImportDecision>();
            _approvedDecisions = new List<ImportDecision>();

            var outputPath = @"C:\Test\Unsorted\TV\30.Rock.S01E01".AsOsAgnostic();

            var series = Builder<Series>.CreateNew()
                                        .With(e => e.QualityProfile = new QualityProfile { Items = Qualities.QualityFixture.GetDefaultQualities() })
                                        .With(s => s.Path = @"C:\Test\TV\30 Rock".AsOsAgnostic())
                                        .Build();

            var episodes = Builder<Episode>.CreateListOfSize(5)
                                           .Build();

            _rejectedDecisions.Add(new ImportDecision(new LocalEpisode(), new ImportRejection(ImportRejectionReason.Unknown, "Rejected!")));
            _rejectedDecisions.Add(new ImportDecision(new LocalEpisode(), new ImportRejection(ImportRejectionReason.Unknown, "Rejected!")));
            _rejectedDecisions.Add(new ImportDecision(new LocalEpisode(), new ImportRejection(ImportRejectionReason.Unknown, "Rejected!")));
            _rejectedDecisions.ForEach(r => r.LocalEpisode.FileEpisodeInfo = new ParsedEpisodeInfo());

            foreach (var episode in episodes)
            {
                _approvedDecisions.Add(new ImportDecision(
                                           new LocalEpisode
                                               {
                                                   Series = series,
                                                   Episodes = new List<Episode> { episode },
                                                   Path = Path.Combine(series.Path, "30 Rock - S01E01 - Pilot.avi"),
                                                   Quality = new QualityModel(Quality.Bluray720p),
                                                   ReleaseGroup = "DRONE",
                                                   FileEpisodeInfo = new ParsedEpisodeInfo()
                                               }));
            }

            Mocker.GetMock<IUpgradeMediaFiles>()
                  .Setup(s => s.UpgradeEpisodeFile(It.IsAny<EpisodeFile>(), It.IsAny<LocalEpisode>(), It.IsAny<bool>()))
                  .Returns(new EpisodeFileMoveResult());

            Mocker.GetMock<IHistoryService>()
                .Setup(x => x.FindByDownloadId(It.IsAny<string>()))
                .Returns(new List<EpisodeHistory>());

            _downloadClientItem = Builder<DownloadClientItem>.CreateNew()
                .With(d => d.OutputPath = new OsPath(outputPath))
                .Build();
        }

        [TearDown]
        public void TearDown()
        {
            Environment.SetEnvironmentVariable("DV_REJECT_PROFILES", null);
            Environment.SetEnvironmentVariable("DV_REJECT_COMPAT_IDS", null);
        }

        private void GivenNewDownload()
        {
            _approvedDecisions.ForEach(a => a.LocalEpisode.Path = Path.Combine(_downloadClientItem.OutputPath.ToString(), Path.GetFileName(a.LocalEpisode.Path)));
        }

        private MediaInfoModel GivenDoviMediaInfo(int profile, int compatId)
        {
            var dovi = (DoviConfigurationRecordSideData)Assembly.GetAssembly(typeof(FFProbe)).CreateInstance("FFMpegCore.DoviConfigurationRecordSideData");
            dovi.DvProfile = profile;
            dovi.DvBlSignalCompatibilityId = compatId;

            return new MediaInfoModel { DoviConfigurationRecord = dovi };
        }

        private void GivenExistingFileOnDisk()
        {
            Mocker.GetMock<IMediaFileService>()
                  .Setup(s => s.GetFilesWithRelativePath(It.IsAny<int>(), It.IsAny<string>()))
                  .Returns(new List<EpisodeFile>());
        }

        [Test]
        public void should_not_import_any_if_there_are_no_approved_decisions()
        {
            Subject.Import(_rejectedDecisions, false).Where(i => i.Result == ImportResultType.Imported).Should().BeEmpty();

            Mocker.GetMock<IMediaFileService>().Verify(v => v.Add(It.IsAny<EpisodeFile>()), Times.Never());
        }

        [Test]
        public void should_import_each_approved()
        {
            GivenExistingFileOnDisk();

            Subject.Import(_approvedDecisions, false).Should().HaveCount(5);
        }

        [Test]
        public void should_surface_the_exception_message_when_import_throws()
        {
            GivenExistingFileOnDisk();

            Mocker.GetMock<IMediaFileService>()
                  .Setup(s => s.Add(It.IsAny<EpisodeFile>()))
                  .Throws(new System.Exception("a specific import failure reason"));

            var result = Subject.Import(_approvedDecisions, false);

            result.Should().HaveCount(5);
            result.First().Errors.Should().Contain(e => e.Contains("a specific import failure reason"));

            ExceptionVerification.ExpectedWarns(5);
        }

        [Test]
        public void should_revert_and_reject_excluded_dolby_vision_from_remote_client()
        {
            // fork24: a remote/debrid import whose pre-move probe missed the DV record; the reliable local
            // re-probe here catches Profile 5 -> the just-committed file is reverted, no import event fires
            // (Plex never sees it), and the decision is rewritten as a DolbyVisionExcluded rejection.
            Environment.SetEnvironmentVariable("DV_REJECT_PROFILES", "5");

            GivenNewDownload();
            _downloadClientItem.Title = "30.Rock.S01E01";
            _downloadClientItem.CanMoveFiles = false;

            Mocker.GetMock<IUpgradeMediaFiles>()
                  .Setup(s => s.UpgradeEpisodeFile(It.IsAny<EpisodeFile>(), It.IsAny<LocalEpisode>(), It.IsAny<bool>()))
                  .Callback<EpisodeFile, LocalEpisode, bool>((ef, le, c) => ef.RelativePath = "30 Rock - S01E01 - Pilot.mkv")
                  .Returns(new EpisodeFileMoveResult());

            Mocker.GetMock<IMediaFileService>()
                  .Setup(s => s.Add(It.IsAny<EpisodeFile>()))
                  .Returns<EpisodeFile>(ef => ef);

            Mocker.GetMock<IVideoFileInfoReader>()
                  .Setup(r => r.GetMediaInfo(It.IsAny<string>()))
                  .Returns(GivenDoviMediaInfo(5, 0));

            var result = Subject.Import(new List<ImportDecision> { _approvedDecisions.First() }, true, _downloadClientItem);

            Mocker.GetMock<IDeleteMediaFiles>()
                  .Verify(v => v.DeleteEpisodeFile(It.IsAny<Series>(), It.IsAny<EpisodeFile>()), Times.Once());

            Mocker.GetMock<IEventAggregator>()
                  .Verify(v => v.PublishEvent(It.IsAny<EpisodeImportedEvent>()), Times.Never());

            result.Should().ContainSingle(r => r.Result == ImportResultType.Rejected &&
                r.ImportDecision.Rejections.Any(x => x.Reason == ImportRejectionReason.DolbyVisionExcluded));

            ExceptionVerification.ExpectedWarns(1);
        }

        [Test]
        public void should_not_re_probe_local_client_imports()
        {
            // a local client (CanMoveFiles true) was already probed reliably pre-move; no extra re-probe.
            Environment.SetEnvironmentVariable("DV_REJECT_PROFILES", "5");

            GivenNewDownload();
            _downloadClientItem.Title = "30.Rock.S01E01";
            _downloadClientItem.CanMoveFiles = true;

            Subject.Import(new List<ImportDecision> { _approvedDecisions.First() }, true, _downloadClientItem);

            Mocker.GetMock<IVideoFileInfoReader>()
                  .Verify(r => r.GetMediaInfo(It.IsAny<string>()), Times.Never());

            Mocker.GetMock<IEventAggregator>()
                  .Verify(v => v.PublishEvent(It.IsAny<EpisodeImportedEvent>()), Times.Once());
        }

        [Test]
        public void should_only_import_approved()
        {
            GivenExistingFileOnDisk();

            var all = new List<ImportDecision>();
            all.AddRange(_rejectedDecisions);
            all.AddRange(_approvedDecisions);

            var result = Subject.Import(all, false);

            result.Should().HaveCount(all.Count);
            result.Where(i => i.Result == ImportResultType.Imported).Should().HaveCount(_approvedDecisions.Count);
        }

        [Test]
        public void should_only_import_each_episode_once()
        {
            GivenExistingFileOnDisk();

            var all = new List<ImportDecision>();
            all.AddRange(_approvedDecisions);
            all.Add(new ImportDecision(_approvedDecisions.First().LocalEpisode));

            var result = Subject.Import(all, false);

            result.Where(i => i.Result == ImportResultType.Imported).Should().HaveCount(_approvedDecisions.Count);
        }

        [Test]
        public void should_move_new_downloads()
        {
            Subject.Import(new List<ImportDecision> { _approvedDecisions.First() }, true);

            Mocker.GetMock<IUpgradeMediaFiles>()
                  .Verify(v => v.UpgradeEpisodeFile(It.IsAny<EpisodeFile>(), _approvedDecisions.First().LocalEpisode, false),
                          Times.Once());
        }

        [Test]
        public void should_publish_EpisodeImportedEvent_for_new_downloads()
        {
            Subject.Import(new List<ImportDecision> { _approvedDecisions.First() }, true);

            Mocker.GetMock<IEventAggregator>()
                .Verify(v => v.PublishEvent(It.IsAny<EpisodeImportedEvent>()), Times.Once());
        }

        [Test]
        public void should_not_move_existing_files()
        {
            GivenExistingFileOnDisk();

            Subject.Import(new List<ImportDecision> { _approvedDecisions.First() }, false);

            Mocker.GetMock<IUpgradeMediaFiles>()
                  .Verify(v => v.UpgradeEpisodeFile(It.IsAny<EpisodeFile>(), _approvedDecisions.First().LocalEpisode, false),
                          Times.Never());
        }

        [Test]
        public void should_import_larger_files_first()
        {
            GivenExistingFileOnDisk();

            var fileDecision = _approvedDecisions.First();
            fileDecision.LocalEpisode.Size = 1.Gigabytes();

            var sampleDecision = new ImportDecision(
                new LocalEpisode
                 {
                     Series = fileDecision.LocalEpisode.Series,
                     Episodes = new List<Episode> { fileDecision.LocalEpisode.Episodes.First() },
                     Path = @"C:\Test\TV\30 Rock\30 Rock - S01E01 - Pilot.avi".AsOsAgnostic(),
                     Quality = new QualityModel(Quality.Bluray720p),
                     Size = 80.Megabytes()
                 });

            var all = new List<ImportDecision>();
            all.Add(fileDecision);
            all.Add(sampleDecision);

            var results = Subject.Import(all, false);

            results.Should().HaveCount(all.Count);
            results.Should().ContainSingle(d => d.Result == ImportResultType.Imported);
            results.Should().ContainSingle(d => d.Result == ImportResultType.Imported && d.ImportDecision.LocalEpisode.Size == fileDecision.LocalEpisode.Size);
        }

        [Test]
        public void should_copy_when_cannot_move_files_downloads()
        {
            GivenNewDownload();
            _downloadClientItem.Title = "30.Rock.S01E01";
            _downloadClientItem.CanMoveFiles = false;

            Subject.Import(new List<ImportDecision> { _approvedDecisions.First() }, true, _downloadClientItem);

            Mocker.GetMock<IUpgradeMediaFiles>()
                  .Verify(v => v.UpgradeEpisodeFile(It.IsAny<EpisodeFile>(), _approvedDecisions.First().LocalEpisode, true), Times.Once());
        }

        [Test]
        public void should_use_override_importmode()
        {
            GivenNewDownload();
            _downloadClientItem.Title = "30.Rock.S01E01";
            _downloadClientItem.CanMoveFiles = false;

            Subject.Import(new List<ImportDecision> { _approvedDecisions.First() }, true, _downloadClientItem, ImportMode.Move);

            Mocker.GetMock<IUpgradeMediaFiles>()
                  .Verify(v => v.UpgradeEpisodeFile(It.IsAny<EpisodeFile>(), _approvedDecisions.First().LocalEpisode, false), Times.Once());
        }

        [Test]
        public void should_use_file_name_only_for_download_client_item_without_a_job_folder()
        {
            var fileName = "Series.Title.S01E01.720p.HDTV.x264-Sonarr.mkv";
            var path = Path.Combine(@"C:\Test\Unsorted\TV\".AsOsAgnostic(), fileName);

            _downloadClientItem.OutputPath = new OsPath(path);
            _approvedDecisions.First().LocalEpisode.Path = path;

            Subject.Import(new List<ImportDecision> { _approvedDecisions.First() }, true, _downloadClientItem);

            Mocker.GetMock<IMediaFileService>().Verify(v => v.Add(It.Is<EpisodeFile>(c => c.OriginalFilePath == fileName)));
        }

        [Test]
        public void should_use_folder_and_file_name_only_for_download_client_item_with_a_job_folder()
        {
            var name = "Series.Title.S01E01.720p.HDTV.x264-Sonarr";
            var outputPath = Path.Combine(@"C:\Test\Unsorted\TV\".AsOsAgnostic(), name);

            _downloadClientItem.OutputPath = new OsPath(outputPath);
            _approvedDecisions.First().LocalEpisode.Path = Path.Combine(outputPath, name + ".mkv");

            Subject.Import(new List<ImportDecision> { _approvedDecisions.First() }, true, _downloadClientItem);

            Mocker.GetMock<IMediaFileService>().Verify(v => v.Add(It.Is<EpisodeFile>(c => c.OriginalFilePath == $"{name}\\{name}.mkv".AsOsAgnostic())));
        }

        [Test]
        public void should_include_intermediate_folders_for_download_client_item_with_a_job_folder()
        {
            var name = "Series.Title.S01E01.720p.HDTV.x264-Sonarr";
            var outputPath = Path.Combine(@"C:\Test\Unsorted\TV\".AsOsAgnostic(), name);

            _downloadClientItem.OutputPath = new OsPath(outputPath);
            _approvedDecisions.First().LocalEpisode.Path = Path.Combine(outputPath, "subfolder", name + ".mkv");

            Subject.Import(new List<ImportDecision> { _approvedDecisions.First() }, true, _downloadClientItem);

            Mocker.GetMock<IMediaFileService>().Verify(v => v.Add(It.Is<EpisodeFile>(c => c.OriginalFilePath == $"{name}\\subfolder\\{name}.mkv".AsOsAgnostic())));
        }

        [Test]
        public void should_use_folder_info_release_title_to_find_relative_path()
        {
            var name = "Series.Title.S01E01.720p.HDTV.x264-Sonarr";
            var outputPath = Path.Combine(@"C:\Test\Unsorted\TV\".AsOsAgnostic(), name);
            var localEpisode = _approvedDecisions.First().LocalEpisode;

            localEpisode.FolderEpisodeInfo = new ParsedEpisodeInfo { ReleaseTitle = name };
            localEpisode.Path = Path.Combine(outputPath, "subfolder", name + ".mkv");

            Subject.Import(new List<ImportDecision> { _approvedDecisions.First() }, true, null);

            Mocker.GetMock<IMediaFileService>().Verify(v => v.Add(It.Is<EpisodeFile>(c => c.OriginalFilePath == $"{name}\\subfolder\\{name}.mkv".AsOsAgnostic())));
        }

        [Test]
        public void should_get_relative_path_when_there_is_no_grandparent_windows()
        {
            WindowsOnly();

            var name = "Series.Title.S01E01.720p.HDTV.x264-Sonarr";
            var outputPath = @"C:\";
            var localEpisode = _approvedDecisions.First().LocalEpisode;

            localEpisode.FolderEpisodeInfo = new ParsedEpisodeInfo { ReleaseTitle = name };
            localEpisode.Path = Path.Combine(outputPath, name + ".mkv");

            Subject.Import(new List<ImportDecision> { _approvedDecisions.First() }, true, null);

            Mocker.GetMock<IMediaFileService>().Verify(v => v.Add(It.Is<EpisodeFile>(c => c.OriginalFilePath == $"{name}.mkv".AsOsAgnostic())));
        }

        [Test]
        public void should_get_relative_path_when_there_is_no_grandparent_mono()
        {
            PosixOnly();

            var name = "Series.Title.S01E01.720p.HDTV.x264-Sonarr";
            var outputPath = "/";
            var localEpisode = _approvedDecisions.First().LocalEpisode;

            localEpisode.FolderEpisodeInfo = new ParsedEpisodeInfo { ReleaseTitle = name };
            localEpisode.Path = Path.Combine(outputPath, name + ".mkv");

            Subject.Import(new List<ImportDecision> { _approvedDecisions.First() }, true, null);

            Mocker.GetMock<IMediaFileService>().Verify(v => v.Add(It.Is<EpisodeFile>(c => c.OriginalFilePath == $"{name}.mkv".AsOsAgnostic())));
        }

        [Test]
        public void should_get_relative_path_when_there_is_no_grandparent_for_UNC_path()
        {
            WindowsOnly();

            var name = "Series.Title.S01E01.720p.HDTV.x264-Sonarr";
            var outputPath = @"\\server\share";
            var localEpisode = _approvedDecisions.First().LocalEpisode;

            localEpisode.FolderEpisodeInfo = new ParsedEpisodeInfo { ReleaseTitle = name };
            localEpisode.Path = Path.Combine(outputPath, name + ".mkv");

            Subject.Import(new List<ImportDecision> { _approvedDecisions.First() }, true, null);

            Mocker.GetMock<IMediaFileService>().Verify(v => v.Add(It.Is<EpisodeFile>(c => c.OriginalFilePath == $"{name}.mkv")));
        }

        [Test]
        public void should_use_folder_info_release_title_to_find_relative_path_when_file_is_not_in_download_client_item_output_directory()
        {
            var name = "Series.Title.S01E01.720p.HDTV.x264-Sonarr";
            var outputPath = Path.Combine(@"C:\Test\Unsorted\TV\".AsOsAgnostic(), name);
            var localEpisode = _approvedDecisions.First().LocalEpisode;

            _downloadClientItem.OutputPath = new OsPath(Path.Combine(@"C:\Test\Unsorted\TV-Other\".AsOsAgnostic(), name));
            localEpisode.FolderEpisodeInfo = new ParsedEpisodeInfo { ReleaseTitle = name };
            localEpisode.Path = Path.Combine(outputPath, "subfolder", name + ".mkv");

            Subject.Import(new List<ImportDecision> { _approvedDecisions.First() }, true, _downloadClientItem);

            Mocker.GetMock<IMediaFileService>().Verify(v => v.Add(It.Is<EpisodeFile>(c => c.OriginalFilePath == $"{name}\\subfolder\\{name}.mkv".AsOsAgnostic())));
        }

        [Test]
        public void should_delete_existing_metadata_files_with_the_same_path()
        {
            Mocker.GetMock<IMediaFileService>()
                  .Setup(s => s.GetFilesWithRelativePath(It.IsAny<int>(), It.IsAny<string>()))
                  .Returns(Builder<EpisodeFile>.CreateListOfSize(1).BuildList());

            Subject.Import(new List<ImportDecision> { _approvedDecisions.First() }, false);

            Mocker.GetMock<IMediaFileService>()
                  .Verify(v => v.Delete(It.IsAny<EpisodeFile>(), DeleteMediaFileReason.ManualOverride), Times.Once());
        }

        [Test]
        public void should_use_folder_info_release_title_to_find_relative_path_when_download_client_item_has_an_empty_output_path()
        {
            var name = "Series.Title.S01E01.720p.HDTV.x264-Sonarr";
            var outputPath = Path.Combine(@"C:\Test\Unsorted\TV\".AsOsAgnostic(), name);
            var localEpisode = _approvedDecisions.First().LocalEpisode;

            _downloadClientItem.OutputPath = default(OsPath);
            localEpisode.FolderEpisodeInfo = new ParsedEpisodeInfo { ReleaseTitle = name };
            localEpisode.Path = Path.Combine(outputPath, "subfolder", name + ".mkv");

            Subject.Import(new List<ImportDecision> { _approvedDecisions.First() }, true, _downloadClientItem);

            Mocker.GetMock<IMediaFileService>().Verify(v => v.Add(It.Is<EpisodeFile>(c => c.OriginalFilePath == $"{name}\\subfolder\\{name}.mkv".AsOsAgnostic())));
        }

        [Test]
        public void should_include_scene_name_with_new_downloads()
        {
            var firstDecision = _approvedDecisions.First();
            firstDecision.LocalEpisode.SceneName = "Series.Title.S01E01.dvdrip-DRONE";

            Subject.Import(new List<ImportDecision> { _approvedDecisions.First() }, true);

            Mocker.GetMock<IUpgradeMediaFiles>()
                  .Verify(v => v.UpgradeEpisodeFile(It.Is<EpisodeFile>(e => e.SceneName == firstDecision.LocalEpisode.SceneName), _approvedDecisions.First().LocalEpisode, false),
                      Times.Once());
        }
    }
}
