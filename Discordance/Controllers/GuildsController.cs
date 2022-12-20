﻿using System.Collections.Generic;
using System.Linq;
using Discord.WebSocket;
using Microsoft.AspNetCore.Mvc;

namespace Discordance.Controllers;

[Route("api/[controller]/[action]")]
public class GuildsController : ControllerBase
{
    private readonly DiscordShardedClient _client;

    public GuildsController(DiscordShardedClient client)
    {
        _client = client;
    }

    [HttpGet]
    [Route("")]
    public IEnumerable<Channel> GetVoiceChannels(ulong guildId)
    {
        var guild = _client.GetGuild(guildId);
        if (guild is null)
            return Enumerable.Empty<Channel>();

        return guild.Channels.Where(x => x is SocketVoiceChannel && guild.CurrentUser.GetPermissions(x).Connect)
            .Select(x => new Channel(x.Id, x.Name));
    }

    [HttpGet]
    [Route("")]
    public bool IsConnected(ulong guildId)
    {
        var guild = _client.GetGuild(guildId);

        return guild?.CurrentUser.VoiceChannel != null;
    }

    [HttpGet]
    [Route("")]
    public bool IsUserConnected(ulong guildId, ulong userId)
    {
        var guild = _client.GetGuild(guildId);
        var user = guild?.GetUser(userId);

        return user?.VoiceChannel != null;
    }

    public record Channel(ulong Id, string Name);
}