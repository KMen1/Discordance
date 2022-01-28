﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Discord;
using Discord.WebSocket;
using KBot.Enums;
using KBot.Modules.Audio.Helpers;
using Victoria;
using Victoria.Enums;
using Victoria.EventArgs;
using Victoria.Filters;
using Victoria.Responses.Search;

namespace KBot.Modules.Audio;

public class AudioService
{
    private readonly DiscordSocketClient _client;
    private readonly LavaNode _lavaNode;

    private Dictionary<ulong, bool> LoopEnabled { get; } = new();
    private Dictionary<ulong, string> FilterEnabled { get; } = new();
    private Dictionary<ulong, IUserMessage> NowPlayingMessage { get; } = new();
    private Dictionary<ulong, LinkedList<(LavaTrack track, SocketUser user)>> Queue { get; } = new();
    private Dictionary<ulong, List<LavaTrack>> QueueHistory { get; } = new();

    public AudioService(DiscordSocketClient client, LavaNode lavaNode)
    {
        _lavaNode = lavaNode;
        _client = client;
    }

    public void Initialize()
    {
        _client.Ready += OnReadyAsync;
        _client.UserVoiceStateUpdated += OnUserVoiceStateUpdatedAsync;
        _lavaNode.OnTrackEnded += OnTrackEndedAsync;
        _lavaNode.OnTrackException += OnTrackExceptionAsync;
    }

    private Task OnReadyAsync()
    {
        return _lavaNode.ConnectAsync();
    }

    private async Task OnUserVoiceStateUpdatedAsync(SocketUser user, SocketVoiceState before, SocketVoiceState after)
    {
        var guild = before.VoiceChannel?.Guild ?? after.VoiceChannel?.Guild;
        if (guild == null)
        {
            return;
        }
        var nowPlayingMessage = NowPlayingMessage.GetValueOrDefault(guild.Id);
        if (user.Id == _client.CurrentUser.Id && after.VoiceChannel is null && nowPlayingMessage is not null)
        {
            await DisconnectAsync(before.VoiceChannel?.Guild).ConfigureAwait(false);
            await nowPlayingMessage.Channel.SendMessageAsync(embed: new EmbedBuilder().WithColor(Color.Red)
                .WithDescription("Egy barom lecsatlakoztatott a hangcsatornából ezért nem tudom folytatni a kívánt muzsika lejátszását. \n" +
                                 "Szánalmas vagy bárki is legyél, ha egyszer találkoznánk a torkodon nyomnám le azt az ujjadat amivel rákattintottál a lecsatlakoztatásra!")
                .Build()).ConfigureAwait(false);
            await nowPlayingMessage.DeleteAsync().ConfigureAwait(false);
        }
    }

    private async Task OnTrackExceptionAsync(TrackExceptionEventArgs arg)
    {
        await arg.Player.StopAsync().ConfigureAwait(false);
        await arg.Player.ApplyFiltersAsync(Array.Empty<IFilter>(), equalizerBands: Array.Empty<EqualizerBand>()).ConfigureAwait(false);
        await arg.Player.TextChannel.SendMessageAsync(embed: Embeds.ErrorEmbed(arg.Exception.Message)).ConfigureAwait(false);
        await _lavaNode.LeaveAsync(arg.Player.VoiceChannel).ConfigureAwait(false);
    }

    public async Task<Embed> DisconnectAsync(IGuild guild)
    {
        if (!_lavaNode.HasPlayer(guild))
        {
            return Embeds.ErrorEmbed("Ezen a szerveren nem található lejátszó!");
        }
        var voiceChannel = _lavaNode.GetPlayer(guild).VoiceChannel;
        await _lavaNode.LeaveAsync(voiceChannel).ConfigureAwait(false);
        ResetPlayer(guild.Id);
        return Embeds.LeaveEmbed(voiceChannel);
    }

    public async Task<Embed> MoveAsync(IGuild guild, SocketUser user)
    {
        if (!_lavaNode.HasPlayer(guild))
        {
            return Embeds.ErrorEmbed("Ezen a szerveren nem található lejátszó!");
        }
        var voiceChannel = ((IVoiceState) user).VoiceChannel;
        if (voiceChannel is null)
        {
            return Embeds.ErrorEmbed("Nem vagy hangcsatornában!");
        }
        await _lavaNode.MoveChannelAsync(voiceChannel).ConfigureAwait(false);
        return Embeds.MoveEmbed(voiceChannel);
    }
    public async Task PlayAsync(IGuild guild, ITextChannel tChannel, SocketUser user, IDiscordInteraction interaction,
        string query)
    {
        var voiceChannel = ((IVoiceState)user).VoiceChannel;
        if (voiceChannel is null)
        {
            await interaction.FollowupAsync(embed: Embeds.ErrorEmbed("Nem vagy hangcsatornában!")).ConfigureAwait(false);
            return;
        }
        var search = Uri.IsWellFormedUriString(query, UriKind.Absolute)
            ? await _lavaNode.SearchAsync(SearchType.Direct, query).ConfigureAwait(false) :
            await _lavaNode.SearchYouTubeAsync(query).ConfigureAwait(false);
        if (search.Status == SearchStatus.NoMatches)
        {
            await interaction.FollowupAsync(embed: Embeds.ErrorEmbed("Nincs találat!")).ConfigureAwait(false);
            return;
        }

        var queue = Queue.GetValueOrDefault(guild.Id) ?? new LinkedList<(LavaTrack, SocketUser)>();
        var player = _lavaNode.HasPlayer(guild) ? _lavaNode.GetPlayer(guild) : await _lavaNode.JoinAsync(voiceChannel, tChannel).ConfigureAwait(false);
        if (IsPlaying(player))
        {
            if (!string.IsNullOrWhiteSpace(search.Playlist.Name))
            {
                foreach (var track in search.Tracks)
                {
                    queue.AddLast((track, user));
                }
                Queue[guild.Id] = queue;
                var msg = await interaction
                    .FollowupAsync(embed: Embeds.AddedToQueueEmbed(search.Tracks.ToList()),
                        ephemeral: true).ConfigureAwait(false);
                await Task.Delay(5000).ConfigureAwait(false);
                await msg.DeleteAsync().ConfigureAwait(false);
            }
            else
            {
                queue.AddLast((search.Tracks.First(), user));
                Queue[guild.Id] = queue;
                var msg = await interaction
                    .FollowupAsync(embed: Embeds.AddedToQueueEmbed(new List<LavaTrack>{search.Tracks.First()}),
                        ephemeral: true).ConfigureAwait(false);
                await Task.Delay(5000).ConfigureAwait(false);
                await msg.DeleteAsync().ConfigureAwait(false);
            }
            await UpdateNowPlayingMessageAsync(guild.Id, player, null, true, true).ConfigureAwait(false);
            return;
        }

        NowPlayingMessage.Add(guild.Id, await interaction.GetOriginalResponseAsync().ConfigureAwait(false));

        if (!string.IsNullOrWhiteSpace(search.Playlist.Name))
        {
            foreach (var track in search.Tracks.Skip(1))
            {
                queue.AddLast((track, user));
            }
            Queue[guild.Id] = queue;
        }
        await player.PlayAsync(search.Tracks.First()).ConfigureAwait(false);
        await player.UpdateVolumeAsync(100).ConfigureAwait(false);
        await interaction.FollowupAsync(
            embed: await Embeds.NowPlayingEmbed(user, player, LoopEnabled.GetValueOrDefault(guild.Id), FilterEnabled.GetValueOrDefault(guild.Id), 0).ConfigureAwait(false),
            components: Components.NowPlayingComponents(CanGoBack(guild.Id), CanGoForward(guild.Id), player)).ConfigureAwait(false);
    }

    public async Task StopAsync(IGuild guild)
    {
        var player = _lavaNode.GetPlayer(guild);
        if (player is null)
        {
            return;
        }
        await player.StopAsync().ConfigureAwait(false);
    }

    public async Task PlayNextTrackAsync(IGuild guild)
    {
        var player = _lavaNode.GetPlayer(guild);
        var nextTrack = Queue.GetValueOrDefault(guild.Id)?.First?.Value;
        if (player is null || nextTrack is null)
        {
            return;
        }

        var queuehistory = QueueHistory.GetValueOrDefault(guild.Id) ?? new List<LavaTrack>();
        queuehistory.Add(player.Track);
        QueueHistory[guild.Id] = queuehistory;
        Queue.GetValueOrDefault(guild.Id)?.RemoveFirst();
        await player.PlayAsync(nextTrack.Value.track).ConfigureAwait(false);
        await UpdateNowPlayingMessageAsync(guild.Id, player, nextTrack.Value.user, true, true).ConfigureAwait(false);
    }

    public async Task PlayPreviousTrackAsync(IGuild guild, SocketUser user)
    {
        var player = _lavaNode.GetPlayer(guild);
        var prev = QueueHistory.GetValueOrDefault(guild.Id)?.LastOrDefault();
        if (prev is null)
        {
            await UpdateNowPlayingMessageAsync(guild.Id, player, null, updateComponents: true).ConfigureAwait(false);
            return;
        }
        await player.PlayAsync(prev).ConfigureAwait(false);
        QueueHistory[guild.Id].Remove(prev);
        await UpdateNowPlayingMessageAsync(guild.Id, player, user, true, true).ConfigureAwait(false);
    }

    public async Task PauseOrResumeAsync(IGuild guild)
    {
        var player = _lavaNode.GetPlayer(guild);
        if (player is null)
        {
            return;
        }

        switch (player.PlayerState)
        {
            case PlayerState.Playing:
            {
                await player.PauseAsync().ConfigureAwait(false);
                await UpdateNowPlayingMessageAsync(guild.Id, player, null, updateComponents: true).ConfigureAwait(false);
                break;
            }
            case PlayerState.Paused:
            {
                await player.ResumeAsync().ConfigureAwait(false);
                await UpdateNowPlayingMessageAsync(guild.Id, player, null, updateComponents: true).ConfigureAwait(false);
                break;
            }
        }
    }

    public async Task SetVolumeAsync(IGuild guild, VoiceButtonType buttonType)
    {
        var player = _lavaNode.GetPlayer(guild);
        if (player is null)
        {
            return;
        }
        var currentVolume = player.Volume;
        if ((currentVolume == 0 && buttonType == VoiceButtonType.VolumeDown) || (currentVolume == 100 && buttonType == VoiceButtonType.VolumeUp))
        {
            return;
        }
        switch (buttonType)
        {
            case VoiceButtonType.VolumeUp:
            {
                await player.UpdateVolumeAsync((ushort)(currentVolume + 10)).ConfigureAwait(false);
                await UpdateNowPlayingMessageAsync(guild.Id, player, null, true, true).ConfigureAwait(false);
                break;
            }
            case VoiceButtonType.VolumeDown:
            {
                await player.UpdateVolumeAsync((ushort)(currentVolume - 10)).ConfigureAwait(false); 
                await UpdateNowPlayingMessageAsync(guild.Id, player, null, true, true).ConfigureAwait(false);
                break;
            }
        }
    }

    public async Task<Embed> SetVolumeAsync(IGuild guild, ushort volume)
    {
        var player = _lavaNode.GetPlayer(guild);
        if (player is null)
        {
            return Embeds.ErrorEmbed("A lejátszó nem található!");
        }

        await player.UpdateVolumeAsync(volume).ConfigureAwait(false);
        await UpdateNowPlayingMessageAsync(guild.Id, player, null, true, true).ConfigureAwait(false);
        return Embeds.VolumeEmbed(player);
    }

    public async Task SetFiltersAsync(IGuild guild, FilterType filterType)
    {
        var player = _lavaNode.GetPlayer(guild);
        if (player is null)
        {
            return;
        }
        await player.ApplyFiltersAsync(Array.Empty<IFilter>(), equalizerBands: Array.Empty<EqualizerBand>()).ConfigureAwait(false);
        switch (filterType)
        {
            case FilterType.Bassboost:
            {
                await player.EqualizerAsync(Filters.BassBoost()).ConfigureAwait(false);
                break;
            }
            case FilterType.Pop:
            {
                await player.EqualizerAsync(Filters.Pop()).ConfigureAwait(false);
                break;
            }
            case FilterType.Soft:
            {
                await player.EqualizerAsync(Filters.Soft()).ConfigureAwait(false);
                break;
            }
            case FilterType.Treblebass:
            {
                await player.EqualizerAsync(Filters.TrebleBass()).ConfigureAwait(false);
                break;
            }
            case FilterType.Nightcore:
            {
                await player.ApplyFilterAsync(Filters.NightCore()).ConfigureAwait(false);
                break;
            }
            case FilterType.Eightd:
            {
                await player.ApplyFilterAsync(Filters.EightD()).ConfigureAwait(false);
                break;
            }
            case FilterType.Vaporwave:
            {
                var (filter, eq) = Filters.VaporWave();
                await player.ApplyFiltersAsync(filter, equalizerBands: eq).ConfigureAwait(false);
                break;
            }
            case FilterType.Doubletime:
            {
                await player.ApplyFilterAsync(Filters.Doubletime()).ConfigureAwait(false);
                break;
            }
            case FilterType.Slowmotion:
            {
                await player.ApplyFilterAsync(Filters.Slowmotion()).ConfigureAwait(false);
                break;
            }
            case FilterType.Chipmunk:
            {
                await player.ApplyFilterAsync(Filters.Chipmunk()).ConfigureAwait(false);
                break;
            }
            case FilterType.Darthvader:
            {
                await player.ApplyFilterAsync(Filters.Darthvader()).ConfigureAwait(false);
                break;
            }
            case FilterType.Dance:
            {
                await player.ApplyFilterAsync(Filters.Dance()).ConfigureAwait(false);
                break;
            }
            case FilterType.China:
            {
                await player.ApplyFilterAsync(Filters.China()).ConfigureAwait(false);
                break;
            }
            case FilterType.Vibrate:
            {
                await player.ApplyFiltersAsync(Filters.Vibrate()).ConfigureAwait(false);
                break;
            }
            case FilterType.Vibrato:
            {
                await player.ApplyFilterAsync(Filters.Vibrato()).ConfigureAwait(false);
                break;
            }
            case FilterType.Tremolo:
            {
                await player.ApplyFilterAsync(Filters.Tremolo()).ConfigureAwait(false);
                break;
            }
        }
        FilterEnabled[guild.Id] = filterType.ToString();
        await UpdateNowPlayingMessageAsync(guild.Id, player, null, true).ConfigureAwait(false);
    }

    public Task SetRepeatAsync(IGuild guild)
    {
        var player = _lavaNode.GetPlayer(guild);
        LoopEnabled[guild.Id] = !LoopEnabled.GetValueOrDefault(guild.Id);
        return UpdateNowPlayingMessageAsync(guild.Id, player, null, true, true);
    }

    public async Task ClearFiltersAsync(IGuild guild)
    {
        var player = _lavaNode.GetPlayer(guild);
        if (player is not {PlayerState: PlayerState.Playing})
        {
            return;
        }
        await player.ApplyFiltersAsync(Array.Empty<IFilter>(), equalizerBands: Array.Empty<EqualizerBand>()).ConfigureAwait(false);
        FilterEnabled[guild.Id] = null;
        await UpdateNowPlayingMessageAsync(guild.Id, player, null, true).ConfigureAwait(false);
    }

    public Embed GetQueue(IGuild guild)
    {
        var player = _lavaNode.GetPlayer(guild);
        return player is null ?
            Embeds.ErrorEmbed("A lejátszó nem található!") :
            Embeds.QueueEmbed(player, Queue.GetValueOrDefault(guild.Id));
    }

    public async Task<Embed> ClearQueueAsync(IGuild guild)
    {
        var player = _lavaNode.GetPlayer(guild);
        if (player is null)
        {
            return Embeds.ErrorEmbed("A lejátszó nem található!");
        }
        Queue.GetValueOrDefault(guild.Id)?.Clear();
        await UpdateNowPlayingMessageAsync(guild.Id, player, null, true).ConfigureAwait(false);
        return Embeds.QueueEmbed(player, null, true);
    }

    private async Task OnTrackEndedAsync(TrackEndedEventArgs args)
    {
        if (!ShouldPlayNext(args.Reason))
        {
            return;
        }
        var guild = args.Player.TextChannel.Guild;
        var player = args.Player;

        if (LoopEnabled.GetValueOrDefault(guild.Id))
        {
            await player.PlayAsync(args.Track).ConfigureAwait(false);
            return;
        }
        var nextTrack = Queue.GetValueOrDefault(args.Player.TextChannel.GuildId)?.First?.Value;
        if (nextTrack is not null)
        {
            Queue.GetValueOrDefault(args.Player.TextChannel.GuildId)?.RemoveFirst();
            await args.Player.PlayAsync(nextTrack.Value.track).ConfigureAwait(false);

            var queuehistory = QueueHistory.GetValueOrDefault(guild.Id) ?? new List<LavaTrack>();
            queuehistory.Add(args.Track);
            QueueHistory[guild.Id] = queuehistory;
            await UpdateNowPlayingMessageAsync(guild.Id, player, nextTrack.Value.user,true, true).ConfigureAwait(false);
            return;
        }
        NowPlayingMessage.GetValueOrDefault(guild.Id)?.DeleteAsync().ConfigureAwait(false);
        await _lavaNode.LeaveAsync(player.VoiceChannel).ConfigureAwait(false);
        ResetPlayer(guild.Id);
    }

    private async Task UpdateNowPlayingMessageAsync(ulong guildId, LavaPlayer player, IUser user,
        bool updateEmbed = false, bool updateComponents = false)
    {
        var message = NowPlayingMessage.GetValueOrDefault(guildId);
        if (message is null)
        {
            return;
        }

        user ??= message.Interaction.User;
        var queueLength = Queue.GetValueOrDefault(guildId)!.Count;
        var embed = await Embeds.NowPlayingEmbed(user, player, LoopEnabled.GetValueOrDefault(guildId),
            FilterEnabled.GetValueOrDefault(guildId), queueLength).ConfigureAwait(false);
        var components = Components.NowPlayingComponents(CanGoBack(guildId), CanGoForward(guildId), player);

        if (updateEmbed && updateComponents)
        {
            await message.ModifyAsync(x =>
            {
                x.Embed = embed;
                x.Components = components;
            }).ConfigureAwait(false);
        }
        else if (updateEmbed)
        {
            await message.ModifyAsync(x => x.Embed = embed).ConfigureAwait(false);
        }
        else if (updateComponents)
        {
            await message.ModifyAsync(x => x.Components = components).ConfigureAwait(false);
        }
    }

    private static bool ShouldPlayNext(TrackEndReason trackEndReason)
    {
        return trackEndReason is TrackEndReason.Finished or TrackEndReason.LoadFailed;
    }
    private bool CanGoBack(ulong guildId)
    {
        return QueueHistory.GetValueOrDefault(guildId) is {Count: > 0};
    }
    private bool CanGoForward(ulong guildId)
    {
        return Queue.GetValueOrDefault(guildId) is {Count: > 0};
    }
    private static bool IsPlaying(LavaPlayer player)
    {
        return (player.Track is not null && player.PlayerState is PlayerState.Playing) ||
               player.PlayerState is PlayerState.Paused;
    }

    private void ResetPlayer(ulong guildId)
    {
        NowPlayingMessage[guildId] = null;
        FilterEnabled[guildId] = null;
        QueueHistory[guildId] = null;
        Queue[guildId] = null;
    }
}