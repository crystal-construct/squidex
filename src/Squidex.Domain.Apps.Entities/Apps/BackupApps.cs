﻿// ==========================================================================
//  Squidex Headless CMS
// ==========================================================================
//  Copyright (c) Squidex UG (haftungsbeschraenkt)
//  All rights reserved. Licensed under the MIT license.
// ==========================================================================

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Orleans;
using Squidex.Domain.Apps.Entities.Apps.Indexes;
using Squidex.Domain.Apps.Entities.Apps.State;
using Squidex.Domain.Apps.Entities.Backup;
using Squidex.Domain.Apps.Events;
using Squidex.Domain.Apps.Events.Apps;
using Squidex.Infrastructure;
using Squidex.Infrastructure.EventSourcing;
using Squidex.Infrastructure.Orleans;
using Squidex.Infrastructure.States;
using Squidex.Shared.Users;

namespace Squidex.Domain.Apps.Entities.Apps
{
    public sealed class BackupApps : BackupHandlerWithStore
    {
        private const string UsersFile = "Users.json";
        private const string SettingsFile = "Settings.json";
        private readonly IGrainFactory grainFactory;
        private readonly IUserResolver userResolver;
        private readonly IAppsByNameIndex appsByNameIndex;
        private readonly HashSet<string> activeUsers = new HashSet<string>();
        private Dictionary<string, string> usersWithEmail = new Dictionary<string, string>();
        private Dictionary<string, RefToken> userMapping = new Dictionary<string, RefToken>();
        private bool isReserved;
        private bool isActorAssigned;
        private string appName;

        public override string Name { get; } = "Apps";

        public BackupApps(IStore<Guid> store, IGrainFactory grainFactory, IUserResolver userResolver)
            : base(store)
        {
            Guard.NotNull(grainFactory, nameof(grainFactory));
            Guard.NotNull(userResolver, nameof(userResolver));

            this.grainFactory = grainFactory;

            this.userResolver = userResolver;

            appsByNameIndex = grainFactory.GetGrain<IAppsByNameIndex>(SingleGrain.Id);
        }

        public override async Task BackupEventAsync(Envelope<IEvent> @event, Guid appId, BackupWriter writer)
        {
            if (@event.Payload is AppContributorAssigned appContributorAssigned)
            {
                var userId = appContributorAssigned.ContributorId;

                if (!usersWithEmail.ContainsKey(userId))
                {
                    var user = await userResolver.FindByIdOrEmailAsync(userId);

                    if (user != null)
                    {
                        usersWithEmail.Add(userId, user.Email);
                    }
                }
            }
        }

        public override async Task BackupAsync(Guid appId, BackupWriter writer)
        {
            await WriteUsersAsync(writer);
            await WriteSettingsAsync(writer, appId);
        }

        public async override Task RestoreEventAsync(Envelope<IEvent> @event, Guid appId, BackupReader reader, RefToken actor)
        {
            switch (@event.Payload)
            {
                case AppCreated appCreated:
                    {
                        appName = appCreated.Name;

                        await ResolveUsersAsync(reader, actor);
                        await ReserveAppAsync(appId);

                        break;
                    }

                case AppContributorAssigned contributorAssigned:
                    {
                        if (isActorAssigned)
                        {
                            contributorAssigned.ContributorId = MapUser(contributorAssigned.ContributorId, actor).Identifier;
                        }
                        else
                        {
                            isActorAssigned = true;

                            contributorAssigned.ContributorId = actor.Identifier;
                        }

                        activeUsers.Add(contributorAssigned.ContributorId);
                        break;
                    }

                case AppContributorRemoved contributorRemoved:
                    {
                        contributorRemoved.ContributorId = MapUser(contributorRemoved.ContributorId, actor).Identifier;

                        activeUsers.Remove(contributorRemoved.ContributorId);
                        break;
                    }
            }

            if (@event.Payload is SquidexEvent squidexEvent)
            {
                squidexEvent.Actor = MapUser(squidexEvent.Actor.Identifier, actor);
            }
        }

        public override Task RestoreAsync(Guid appId, BackupReader reader)
        {
            return ReadSettingsAsync(reader, appId);
        }

        private async Task ReserveAppAsync(Guid appId)
        {
            if (!(isReserved = await appsByNameIndex.ReserveAppAsync(appId, appName)))
            {
                throw new BackupRestoreException("The app id or name is not available.");
            }
        }

        public override async Task CleanupRestoreAsync(Guid appId)
        {
            if (isReserved)
            {
                await appsByNameIndex.ReserveAppAsync(appId, appName);
            }
        }

        private RefToken MapUser(string userId, RefToken fallback)
        {
            return userMapping.GetOrAdd(userId, fallback);
        }

        private async Task ResolveUsersAsync(BackupReader reader, RefToken actor)
        {
            await ReadUsersAsync(reader);

            foreach (var kvp in usersWithEmail)
            {
                var user = await userResolver.FindByIdOrEmailAsync(kvp.Value);

                if (user != null)
                {
                    userMapping[kvp.Key] = new RefToken(RefTokenType.Subject, user.Id);
                }
                else
                {
                    userMapping[kvp.Key] = actor;
                }
            }
        }

        private async Task ReadUsersAsync(BackupReader reader)
        {
            var json = await reader.ReadJsonAttachmentAsync(UsersFile);

            usersWithEmail = json.ToObject<Dictionary<string, string>>();
        }

        private async Task WriteUsersAsync(BackupWriter writer)
        {
            var json = JObject.FromObject(usersWithEmail);

            await writer.WriteJsonAsync(UsersFile, json);
        }

        private async Task WriteSettingsAsync(BackupWriter writer, Guid appId)
        {
            var json = await grainFactory.GetGrain<IAppUISettingsGrain>(appId).GetAsync();

            await writer.WriteJsonAsync(SettingsFile, json);
        }

        private async Task ReadSettingsAsync(BackupReader reader, Guid appId)
        {
            var json = await reader.ReadJsonAttachmentAsync(SettingsFile);

            await grainFactory.GetGrain<IAppUISettingsGrain>(appId).SetAsync((JObject)json);
        }

        public override async Task CompleteRestoreAsync(Guid appId, BackupReader reader)
        {
            await RebuildAsync<AppState, AppGrain>(appId, (e, s) => s.Apply(e));

            await appsByNameIndex.AddAppAsync(appId, appName);

            foreach (var user in activeUsers)
            {
                await grainFactory.GetGrain<IAppsByUserIndex>(user).AddAppAsync(appId);
            }
        }
    }
}
