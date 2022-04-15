﻿using System.Threading.Tasks;
using Discord;
using Discord.Interactions;

namespace KBot.Modules.Suggestions;

[Group("suggestion", "Suggestions")]
public class SuggestionCommands : SlashModuleBase
{
    [SlashCommand("create", "Create a new suggestion")]
    public async Task CreateSuggestionAsync(string title, string description)
    {
        await DeferAsync().ConfigureAwait(false);

        var embed = new EmbedBuilder()
            .WithAuthor(Context.User.Username, Context.User.GetAvatarUrl())
            .WithTitle(title)
            .WithDescription(description)
            .WithColor(Color.Blue)
            .Build();
        var comp = new ComponentBuilder()
            .WithButton("Accept", $"suggest-accept:{Context.User.Id}", ButtonStyle.Success, new Emoji("✅"))
            .WithButton("Deny", $"suggest-decline:{Context.User.Id}", ButtonStyle.Danger, new Emoji("❌"))
            .Build();

        var config = await GetGuildConfigAsync().ConfigureAwait(false);
        if (!config.Suggestions.Enabled)
        {
            await FollowupAsync("Suggestions are not enabled on this server.").ConfigureAwait(false);
            return;
        }

        var suggestionChannel = Context.Guild.GetTextChannel(config.Suggestions.AnnounceChannelId);
        await suggestionChannel.SendMessageAsync(embed: embed, components: comp).ConfigureAwait(false);
        await FollowupWithEmbedAsync(Color.Green, "Suggestion Created", $"In Channel: {suggestionChannel.Mention}")
            .ConfigureAwait(false);
    }
}