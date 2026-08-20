using System;
using System.Collections.Generic;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.History;
using NzbDrone.Core.MediaFiles.EpisodeImport.Specifications;
using NzbDrone.Core.MediaFiles.MediaInfo;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Test.Common;

namespace NzbDrone.Core.Test.MediaFiles.EpisodeImport.Specifications
{
    // fork22 (Area-1 option B): the grabbed-release audio-language gate.
    [TestFixture]
    public class AudioLanguageSpecificationFixture : CoreTest<AudioLanguageSpecification>
    {
        private LocalEpisode _localEpisode;

        [SetUp]
        public void Setup()
        {
            _localEpisode = new LocalEpisode
            {
                Path = @"C:\Test\Unsorted\Show\show.s01e01.mkv".AsOsAgnostic(),
                MediaInfo = new MediaInfoModel { AudioLanguages = new List<string> { "eng" } }
            };

            GivenGrabbedTitle("The.Show.S01E01.1080p.WEB-DL");
        }

        private void GivenGrabbedTitle(string sourceTitle)
        {
            var history = new EpisodeHistory
            {
                SourceTitle = sourceTitle,
                Date = DateTime.UtcNow,
                Data = new Dictionary<string, string>()
            };

            _localEpisode.Release = new GrabbedReleaseInfo(new List<EpisodeHistory> { history });
        }

        [Test]
        public void should_reject_when_the_grabbed_language_is_absent_from_the_file_audio()
        {
            // The motivating fraud: grabbed as Italian, file contains only English audio.
            GivenGrabbedTitle("The.Show.S01E01.ITALIAN.1080p.WEB-DL");
            _localEpisode.MediaInfo.AudioLanguages = new List<string> { "eng" };

            Subject.IsSatisfiedBy(_localEpisode, null).Accepted.Should().BeFalse();
        }

        [Test]
        public void should_accept_when_the_file_audio_contains_the_grabbed_language()
        {
            GivenGrabbedTitle("The.Show.S01E01.ITALIAN.1080p.WEB-DL");
            _localEpisode.MediaInfo.AudioLanguages = new List<string> { "ita", "eng" };

            Subject.IsSatisfiedBy(_localEpisode, null).Accepted.Should().BeTrue();
        }

        [Test]
        public void should_accept_when_the_release_has_no_language_tag()
        {
            GivenGrabbedTitle("The.Show.S01E01.1080p.WEB-DL");
            _localEpisode.MediaInfo.AudioLanguages = new List<string> { "eng" };

            Subject.IsSatisfiedBy(_localEpisode, null).Accepted.Should().BeTrue();
        }

        [Test]
        public void should_accept_when_media_info_is_null()
        {
            GivenGrabbedTitle("The.Show.S01E01.ITALIAN.1080p.WEB-DL");
            _localEpisode.MediaInfo = null;

            Subject.IsSatisfiedBy(_localEpisode, null).Accepted.Should().BeTrue();
        }
    }
}
