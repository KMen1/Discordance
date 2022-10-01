﻿using Discord.Interactions;
using Discordance.Services;

namespace Discordance.Modules;

public abstract class ModuleBase : InteractionModuleBase<ShardedInteractionContext>
{
    public MongoService DatabaseService { get; set; } = null!;
    public LocalizationService Localization { get; set; } = null!;
}