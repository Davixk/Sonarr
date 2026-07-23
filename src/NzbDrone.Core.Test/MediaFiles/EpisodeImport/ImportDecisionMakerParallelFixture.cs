using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using FizzWare.NBuilder;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.Download;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.EpisodeImport;
using NzbDrone.Core.MediaFiles.EpisodeImport.Aggregation;
using NzbDrone.Core.MediaFiles.MediaInfo;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Core.Tv;
using NzbDrone.Test.Common;

namespace NzbDrone.Core.Test.MediaFiles.EpisodeImport
{
    [TestFixture]
    public class ImportDecisionMakerParallelFixture : CoreTest<ImportDecisionMaker>
    {
        private Series _series;
        private Mock<IImportDecisionEngineSpecification> _pass;

        [SetUp]
        public void Setup()
        {
            _pass = new Mock<IImportDecisionEngineSpecification>();
            _pass.Setup(c => c.IsSatisfiedBy(It.IsAny<LocalEpisode>(), It.IsAny<DownloadClientItem>()))
                 .Returns(ImportSpecDecision.Accept());

            _series = Builder<Series>.CreateNew()
                                     .With(s => s.Path = @"C:\Test\Series".AsOsAgnostic())
                                     .With(s => s.QualityProfile = new QualityProfile { Items = Qualities.QualityFixture.GetDefaultQualities() })
                                     .Build();

            Mocker.SetConstant<IEnumerable<IImportDecisionEngineSpecification>>(new[] { _pass.Object });

            Mocker.GetMock<ICustomFormatCalculationService>()
                  .Setup(c => c.ParseCustomFormat(It.IsAny<LocalEpisode>()))
                  .Returns(new List<CustomFormat>());
        }

        [TearDown]
        public void TearDown()
        {
            Environment.SetEnvironmentVariable("IMPORT_PROBE_THREADS", null);
        }

        private List<string> GivenVideoFiles(int count)
        {
            var files = Enumerable.Range(0, count)
                                  .Select(i => $@"C:\Downloads\The.Series.S01E{i:00}.1080p.BluRay.x264-Sonarr\the.series.s01e{i:00}.mkv".AsOsAgnostic())
                                  .ToList();

            Mocker.GetMock<IMediaFileService>()
                  .Setup(c => c.FilterExistingFiles(It.IsAny<List<string>>(), It.IsAny<Series>()))
                  .Returns<List<string>, Series>((f, s) => f);

            return files;
        }

        private void GivenAugmentBlocksOn(IVideoFileInfoReader reader)
        {
            Mocker.GetMock<IAggregationService>()
                  .Setup(s => s.Augment(It.IsAny<LocalEpisode>(), It.IsAny<DownloadClientItem>()))
                  .Callback<LocalEpisode, DownloadClientItem>((localEpisode, downloadClientItem) =>
                  {
                      // Represents the ffprobe bound work inside AggregationService.Augment.
                      localEpisode.MediaInfo = reader.GetMediaInfo(localEpisode.Path);
                      localEpisode.Episodes = new List<Episode> { new Episode { SeasonNumber = 1, EpisodeNumber = 1 } };
                  })
                  .Returns<LocalEpisode, DownloadClientItem>((localEpisode, downloadClientItem) => localEpisode);
        }

        [Test]
        public void should_probe_files_concurrently_up_to_configured_degree()
        {
            var files = GivenVideoFiles(4);
            Environment.SetEnvironmentVariable("IMPORT_PROBE_THREADS", "4");

            var reader = new ConcurrencyLatchReader(expectedConcurrency: 4, timeout: TimeSpan.FromSeconds(3));
            GivenAugmentBlocksOn(reader);

            var decisions = Subject.GetImportDecisions(files, _series, null, null, false, false);

            decisions.Should().HaveCount(4);
            reader.PeakConcurrency.Should().Be(4);
        }

        [Test]
        public void should_return_decisions_in_input_order_when_probes_finish_out_of_order()
        {
            var files = GivenVideoFiles(4);
            Environment.SetEnvironmentVariable("IMPORT_PROBE_THREADS", "4");

            var reader = new ReverseCompletionReader(files, TimeSpan.FromSeconds(5));
            GivenAugmentBlocksOn(reader);

            var decisions = Subject.GetImportDecisions(files, _series, null, null, false, false);

            decisions.Select(d => d.LocalEpisode.Path).Should().Equal(files);
        }

        [Test]
        public void should_run_serially_and_preserve_order_when_degree_is_one()
        {
            var files = GivenVideoFiles(3);
            Environment.SetEnvironmentVariable("IMPORT_PROBE_THREADS", "1");

            var reader = new ConcurrencyLatchReader(expectedConcurrency: 3, timeout: TimeSpan.FromSeconds(1));
            GivenAugmentBlocksOn(reader);

            var decisions = Subject.GetImportDecisions(files, _series, null, null, false, false);

            decisions.Should().HaveCount(3);
            decisions.Select(d => d.LocalEpisode.Path).Should().Equal(files);
            reader.PeakConcurrency.Should().Be(1);
        }

        // Releases callers only once expectedConcurrency of them are blocked at the same time,
        // recording the peak observed concurrency. Serial callers never reach the threshold and
        // fall through on the timeout with a peak of 1.
        private sealed class ConcurrencyLatchReader : IVideoFileInfoReader
        {
            private readonly int _expectedConcurrency;
            private readonly TimeSpan _timeout;
            private readonly object _sync = new object();
            private int _current;
            private bool _released;

            public ConcurrencyLatchReader(int expectedConcurrency, TimeSpan timeout)
            {
                _expectedConcurrency = expectedConcurrency;
                _timeout = timeout;
            }

            public int PeakConcurrency { get; private set; }

            public MediaInfoModel GetMediaInfo(string filename)
            {
                lock (_sync)
                {
                    _current++;

                    if (_current > PeakConcurrency)
                    {
                        PeakConcurrency = _current;
                    }

                    if (_current >= _expectedConcurrency)
                    {
                        _released = true;
                        Monitor.PulseAll(_sync);
                    }
                    else
                    {
                        var deadline = DateTime.UtcNow + _timeout;

                        while (!_released)
                        {
                            var remaining = deadline - DateTime.UtcNow;

                            if (remaining <= TimeSpan.Zero)
                            {
                                break;
                            }

                            Monitor.Wait(_sync, remaining);
                        }
                    }

                    _current--;
                }

                return new MediaInfoModel();
            }

            public TimeSpan? GetRunTime(string filename)
            {
                return TimeSpan.FromMinutes(30);
            }
        }

        // Forces the probes to complete in reverse input order so that a naive implementation that
        // returns results in completion order would scramble the decisions. Requires the probes to
        // run concurrently, which is guaranteed by the parallel decision phase.
        private sealed class ReverseCompletionReader : IVideoFileInfoReader
        {
            private readonly IReadOnlyList<string> _orderedPaths;
            private readonly TimeSpan _timeout;
            private readonly object _sync = new object();
            private int _completedCount;

            public ReverseCompletionReader(IReadOnlyList<string> orderedPaths, TimeSpan timeout)
            {
                _orderedPaths = orderedPaths;
                _timeout = timeout;
            }

            public MediaInfoModel GetMediaInfo(string filename)
            {
                var index = -1;

                for (var i = 0; i < _orderedPaths.Count; i++)
                {
                    if (string.Equals(_orderedPaths[i], filename, StringComparison.Ordinal))
                    {
                        index = i;
                        break;
                    }
                }

                var mustCompleteFirst = index >= 0 ? _orderedPaths.Count - 1 - index : 0;

                lock (_sync)
                {
                    var deadline = DateTime.UtcNow + _timeout;

                    while (_completedCount < mustCompleteFirst)
                    {
                        var remaining = deadline - DateTime.UtcNow;

                        if (remaining <= TimeSpan.Zero)
                        {
                            break;
                        }

                        Monitor.Wait(_sync, remaining);
                    }

                    _completedCount++;
                    Monitor.PulseAll(_sync);
                }

                return new MediaInfoModel();
            }

            public TimeSpan? GetRunTime(string filename)
            {
                return TimeSpan.FromMinutes(30);
            }
        }
    }
}
