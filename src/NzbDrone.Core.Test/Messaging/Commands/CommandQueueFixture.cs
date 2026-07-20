using System.Collections.Generic;
using FizzWare.NBuilder;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.IndexerSearch;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Core.Tv.Commands;

namespace NzbDrone.Core.Test.Messaging.Commands
{
    [TestFixture]
    public class CommandQueueFixture : CoreTest<CommandQueue>
    {
        private void GivenQueuedSearchCommand(int episodeId)
        {
            var commandModel = Builder<CommandModel>
                .CreateNew()
                .With(c => c.Name = "EpisodeSearch")
                .With(c => c.Body = new EpisodeSearchCommand { EpisodeIds = new List<int> { episodeId } })
                .With(c => c.Status = CommandStatus.Queued)
                .Build();

            Subject.Add(commandModel);
        }

        [Test]
        public void should_not_hand_out_search_command_once_concurrent_search_cap_is_reached()
        {
            Subject.SetMaxConcurrentSearch(2);

            GivenQueuedSearchCommand(1);
            GivenQueuedSearchCommand(2);
            GivenQueuedSearchCommand(3);

            Subject.TryGet(out var first).Should().BeTrue();
            first.Body.Should().BeOfType<EpisodeSearchCommand>();

            Subject.TryGet(out var second).Should().BeTrue();
            second.Body.Should().BeOfType<EpisodeSearchCommand>();

            // The cap is reached; the reserved lane idles rather than starting a third search.
            Subject.TryGet(out var third).Should().BeFalse();
            third.Should().BeNull();
        }

        [Test]
        public void should_hand_out_non_search_command_while_search_cap_is_reached()
        {
            Subject.SetMaxConcurrentSearch(2);

            GivenQueuedSearchCommand(1);
            GivenQueuedSearchCommand(2);

            Subject.TryGet(out _);
            Subject.TryGet(out _);

            var nonSearchCommand = Builder<CommandModel>
                .CreateNew()
                .With(c => c.Name = "RefreshSeries")
                .With(c => c.Body = new RefreshSeriesCommand())
                .With(c => c.Status = CommandStatus.Queued)
                .Build();

            Subject.Add(nonSearchCommand);

            Subject.TryGet(out var command).Should().BeTrue();
            command.Should().NotBeNull();
            command.Body.Should().BeOfType<RefreshSeriesCommand>();
        }
    }
}
