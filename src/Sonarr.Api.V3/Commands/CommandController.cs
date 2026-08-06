using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.AspNetCore.Mvc;
using NzbDrone.Common.Composition;
using NzbDrone.Common.Serializer;
using NzbDrone.Common.TPL;
using NzbDrone.Core.Datastore.Events;
using NzbDrone.Core.IndexerSearch;
using NzbDrone.Core.MediaFiles.EpisodeImport.Manual;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.ProgressMessaging;
using NzbDrone.Core.Tv;
using NzbDrone.SignalR;
using Sonarr.Http;
using Sonarr.Http.REST;
using Sonarr.Http.REST.Attributes;
using Sonarr.Http.Validation;

namespace Sonarr.Api.V3.Commands
{
    [V3ApiController]
    public class CommandController : RestControllerWithSignalR<CommandResource, CommandModel>, IHandle<CommandUpdatedEvent>
    {
        private readonly IManageCommandQueue _commandQueueManager;
        private readonly IEpisodeService _episodeService;
        private readonly KnownTypes _knownTypes;
        private readonly Debouncer _debouncer;
        private readonly Dictionary<int, CommandResource> _pendingUpdates;

        private readonly CommandPriorityComparer _commandPriorityComparer = new CommandPriorityComparer();

        public CommandController(IManageCommandQueue commandQueueManager,
                             IBroadcastSignalRMessage signalRBroadcaster,
                             IEpisodeService episodeService,
                             KnownTypes knownTypes)
            : base(signalRBroadcaster)
        {
            _commandQueueManager = commandQueueManager;
            _episodeService = episodeService;
            _knownTypes = knownTypes;

            _debouncer = new Debouncer(SendUpdates, TimeSpan.FromSeconds(0.1));
            _pendingUpdates = new Dictionary<int, CommandResource>();

            PostValidator.RuleFor(c => c.Name).NotBlank();
        }

        protected override CommandResource GetResourceById(int id)
        {
            return _commandQueueManager.Get(id).ToResource();
        }

        [RestPostById]
        [Consumes("application/json")]
        [Produces("application/json")]
        public ActionResult<CommandResource> StartCommand([FromBody] CommandResource commandResource)
        {
            var commandType =
                _knownTypes.GetImplementations(typeof(Command))
                               .Single(c => c.Name.Replace("Command", "")
                                             .Equals(commandResource.Name, StringComparison.InvariantCultureIgnoreCase));

            Request.Body.Seek(0, SeekOrigin.Begin);
            using (var reader = new StreamReader(Request.Body))
            {
                var body = reader.ReadToEnd();
                var priority = CommandPriority.Normal;

                if (commandType == typeof(ManualImportCommand))
                {
                    priority = CommandPriority.High;
                }
                else if (commandType == typeof(EpisodeSearchCommand))
                {
                    // fork10: demote user/automation-triggered episode searches to Low so they yield the shared
                    // worker lane to PMD (High) and Season/Series searches (Normal) instead of contending at
                    // Normal. Season, Series, Missing and CutoffUnmet searches are unaffected.
                    priority = CommandPriority.Low;
                }

                var command = STJson.Deserialize(body, commandType) as Command;

                command.SuppressMessages = !command.SendUpdatesToClient;
                command.SendUpdatesToClient = true;
                command.ClientUserAgent = Request.Headers["UserAgent"];

                ValidateManualImport(command);

                var trackedCommand = _commandQueueManager.Push(command, priority, CommandTrigger.Manual);

                return Created(trackedCommand.Id);
            }
        }

        // fork6: reject a ManualImport whose selected episodeIds for a single file span more than one season,
        // synchronously at accept time, instead of enqueueing a command that throws InvalidSeasonException in
        // the executor and collapses to a bare "Failed to import episode". A single file maps to exactly one
        // EpisodeFile with a single SeasonNumber, so a cross-season selection can never import.
        private void ValidateManualImport(Command command)
        {
            if (command is not ManualImportCommand manualImportCommand)
            {
                return;
            }

            foreach (var file in manualImportCommand.Files)
            {
                if (file.EpisodeIds is not { Count: > 1 })
                {
                    continue;
                }

                var seasons = _episodeService.GetEpisodes(file.EpisodeIds)
                                             .Select(e => e.SeasonNumber)
                                             .Distinct()
                                             .OrderBy(s => s)
                                             .ToList();

                if (seasons.Count > 1)
                {
                    throw new BadRequestException($"Episodes selected for '{file.Path}' span multiple seasons ({string.Join(", ", seasons)}). All episodes for a single file must belong to the same season.");
                }
            }
        }

        [HttpGet]
        [Produces("application/json")]
        public List<CommandResource> GetStartedCommands()
        {
            return _commandQueueManager.All()
                .OrderBy(c => c.Status, _commandPriorityComparer)
                .ThenByDescending(c => c.Priority)
                .ToResource();
        }

        [RestDeleteById]
        public void CancelCommand(int id)
        {
            _commandQueueManager.Cancel(id);
        }

        [HttpDelete]
        public object CancelCommands([FromQuery] string name = null)
        {
            return new { cancelled = _commandQueueManager.CancelMany(name).Count };
        }

        [NonAction]
        public void Handle(CommandUpdatedEvent message)
        {
            if (message.Command.Body.SendUpdatesToClient)
            {
                lock (_pendingUpdates)
                {
                    _pendingUpdates[message.Command.Id] = message.Command.ToResource();
                }

                _debouncer.Execute();
            }
        }

        private void SendUpdates()
        {
            lock (_pendingUpdates)
            {
                var pendingUpdates = _pendingUpdates.Values.ToArray();
                _pendingUpdates.Clear();

                foreach (var pendingUpdate in pendingUpdates)
                {
                    BroadcastResourceChange(ModelAction.Updated, pendingUpdate);

                    if (pendingUpdate.Name == typeof(MessagingCleanupCommand).Name.Replace("Command", "") &&
                        pendingUpdate.Status == CommandStatus.Completed)
                    {
                        BroadcastResourceChange(ModelAction.Sync);
                    }
                }
            }
        }
    }
}
