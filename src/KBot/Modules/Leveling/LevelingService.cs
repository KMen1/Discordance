﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Discord;
using Discord.WebSocket;
using KBot.Services;
using Serilog;

namespace KBot.Modules.Leveling;

public class LevelingModule : IInjectable
{
    private readonly DatabaseService _database;
    private readonly ConcurrentQueue<(SocketGuildUser, int)> _XpQueue = new();

    public LevelingModule(DiscordSocketClient client, DatabaseService database)
    {
        _database = database;
        client.UserVoiceStateUpdated += OnUserVoiceStateUpdatedAsync;
        client.MessageReceived += OnMessageReceivedAsync;
        Log.Logger.Information("Leveling Module Loaded");
        Task.Run(CheckForLevelUpAsync);
    }

    private async Task CheckForLevelUpAsync()
    {
        while (true)
        {
            await Task.Delay(5000).ConfigureAwait(false);
            
            var usersToUpdate = new List<(SocketGuildUser, int)>();
            while (_XpQueue.TryDequeue(out var user)) usersToUpdate.Add(user);

            if (usersToUpdate.Count == 0)
                continue;

            var config = await _database.GetGuildConfigAsync(usersToUpdate[0].Item1.Guild).ConfigureAwait(false);
            var toNotify = new List<(SocketGuildUser, int)>();
            foreach (var (user, Xp) in usersToUpdate)
            {
                var oldUserData = await _database.GetUserAsync(user.Guild, user).ConfigureAwait(false);
                var newUserData = await _database.UpdateUserAsync(user.Guild, user, x =>
                {
                    x.Xp += Xp;

                    if (x.Xp < x.XpNeeded) return;
                    switch (Xp % x.XpNeeded)
                    {
                        case 0:
                        {
                            x.Level += x.Xp / x.XpNeeded;
                            x.Xp = 0;
                            break;
                        }
                        case > 0:
                        {
                            x.Level += x.Xp / x.XpNeeded;
                            var total = 0;
                            for (var i = x.Level; i < x.Level + (x.Xp / x.XpNeeded); i++)
                            {
                                total += (int)Math.Pow(i * 4, 2);
                            }
                            x.Xp -= total;
                            break;
                        }
                    }
                    
                }).ConfigureAwait(false);
                
                if (newUserData.Level == oldUserData.Level)
                    continue;
                toNotify.Add((user, newUserData.Level));
                
                var lowerLevelRoles = config.Leveling.LevelRoles.FindAll(x => x.Level <= newUserData.Level);
                if (lowerLevelRoles.Count == 0) continue;

                var roles = lowerLevelRoles.OrderByDescending(x => x.Level).ToList();
                var highestRole = roles[0];
                
                if (user.Roles.All(x => x.Id != highestRole.Id))
                {
                    var role = user.Guild.GetRole(highestRole.Id);
                    await user.AddRoleAsync(role).ConfigureAwait(false);
                    var embed = new EmbedBuilder()
                        .WithAuthor(user.Guild.Name, user.Guild.IconUrl)
                        .WithTitle("You got a reward!")
                        .WithDescription(role.Mention)
                        .WithColor(Color.Gold)
                        .Build();
                    var dmchannel = await user.CreateDMChannelAsync().ConfigureAwait(false);
                    await dmchannel.SendMessageAsync(embed: embed).ConfigureAwait(false);
                }
                foreach (var roleToRemove in roles.Skip(1).Select(x => user.Guild.GetRole(x.Id)).Where(x => user.Roles.Contains(x)))
                {
                    await user.RemoveRoleAsync(roleToRemove).ConfigureAwait(false);
                }
            }
            
            if (toNotify.Count == 0)
                continue;

            var notifyChannel = toNotify[0].Item1.Guild.GetTextChannel(config.Leveling.AnnounceChannelId);
            await Task.WhenAll(toNotify.Select(async x =>
            {
                var (user, level) = x;
                await notifyChannel.SendMessageAsync($"🥳 Congrats {user.Mention}, you reached level **{level}**").ConfigureAwait(false);
            })).ConfigureAwait(false);
        }
    }

    private async Task OnMessageReceivedAsync(SocketMessage message)
    {
        if (message.Author is not SocketGuildUser user || user.IsBot || user.IsWebhook)
            return;

        var config = await _database.GetGuildConfigAsync(user.Guild).ConfigureAwait(false);
        if (!config.Leveling.Enabled) return;

        _ = Task.Run(() =>
        {
            if (message.Content.Length < 3)
                return;

            var rate = new Random().NextDouble();
            var msgLength = message.Content.Length;
            var pointsToGive = (int)Math.Floor((rate * 100) + (msgLength / 2));

            _XpQueue.Enqueue((user, pointsToGive));
        }).ConfigureAwait(false);
    }

    private async Task OnUserVoiceStateUpdatedAsync(SocketUser socketUser, SocketVoiceState before, SocketVoiceState after)
    {
        if (socketUser is not SocketGuildUser user || socketUser.IsBot) return;

        var guild = user.Guild;
        var config = await _database.GetGuildConfigAsync(guild).ConfigureAwait(false);
        if (!config.Leveling.Enabled) return;

        _ = Task.Run(async () =>
        {
            if (before.VoiceChannel is not null)
                await ScanVoiceChannelAsync(before.VoiceChannel);
            if (after.VoiceChannel is not null && after.VoiceChannel != before.VoiceChannel)
                await ScanVoiceChannelAsync(after.VoiceChannel);
            else if (after.VoiceChannel is null)
                await UserLeftChannelAsync(user);
        }).ConfigureAwait(false);
    }

    private async Task ScanVoiceChannelAsync(SocketVoiceChannel channel)
    {
        foreach (var user in channel.Users)
        {
            await ScanUserAsync(user);
        }
    }

    private async Task ScanUserAsync(SocketGuildUser user)
    {
        if (IsActive(user))
            await _database.UpdateUserAsync(user.Guild, user, x => x.LastVoiceActivityDate = DateTime.UtcNow).ConfigureAwait(false);
        else
            await UserLeftChannelAsync(user).ConfigureAwait(false);
    }

    private async Task UserLeftChannelAsync(SocketGuildUser user)
    {
        var dbUser = await _database.GetUserAsync(user.Guild, user).ConfigureAwait(false);

        var joinDate = dbUser.LastVoiceActivityDate ?? DateTime.MinValue;
        var minutes = (int)(DateTime.UtcNow - joinDate).TotalMinutes;
        if (minutes < 1)
            return;
        var Xp = minutes * 100;
        _XpQueue.Enqueue((user, Xp));
    }

    private static bool IsActive(IVoiceState user)
    {
        return !user.IsMuted && !user.IsDeafened && !user.IsSelfMuted && !user.IsSelfDeafened;
    }
}