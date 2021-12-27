﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using KBot.Enums;
using KBot.Helpers;
using Victoria;
using Victoria.Enums;
using Victoria.EventArgs;
using Victoria.Responses.Search;

// ReSharper disable InconsistentNaming

namespace KBot.Services;

public class AudioService
{
    private readonly DiscordSocketClient _client;
    private readonly LavaNode _lavaNode;
    
    private readonly List<InteractionContext> ctx = new();
    private readonly List<LavaTrack> previousTracks = new();
    private LavaTrack currentTrack;
    private IUserMessage nowPlayingMessage;
    private bool isPlaying = true;
    private bool isloopEnabled;

    public AudioService(DiscordSocketClient client, LavaNode lavaNode)
    {
        _lavaNode = lavaNode;
        _client = client;
    }

    public void InitializeAsync()
    {
        _client.Ready += OnReadyAsync;
        _lavaNode.OnTrackEnded += OnTrackEnded;
        _lavaNode.OnTrackException += OnTrackException;
    }

    private async Task OnReadyAsync()
    {
        await _lavaNode.ConnectAsync();
    }

    private async Task OnTrackException(TrackExceptionEventArgs arg)
    {
        await arg.Player.StopAsync();
        await arg.Player.ApplyFiltersAsync(FilterHelper.DefaultFilters());
        await arg.Player.TextChannel.SendMessageAsync(embed: await EmbedHelper.MakeError(_client.CurrentUser, arg.Exception.Message));
        await _lavaNode.LeaveAsync(arg.Player.VoiceChannel);
    }

    public async Task<Embed> JoinAsync(IGuild guild, IVoiceChannel vChannel, ITextChannel tChannel, SocketUser user)
    {
        if (_lavaNode.HasPlayer(guild) || vChannel is null) 
            return await EmbedHelper.MakeError(user, "Nem vagy hangcsatornában vagy a lejátszó nem található!");
        
        await _lavaNode.JoinAsync(vChannel, tChannel);
        return await EmbedHelper.MakeJoin(user, vChannel);
    }

    public async Task<Embed> LeaveAsync(IGuild guild, IVoiceChannel vChannel, SocketUser user)
    {
        if (!_lavaNode.HasPlayer(guild) || vChannel is null) 
            return await EmbedHelper.MakeError(user, "Nem vagy hangcsatornában vagy a lejátszó nem található!");
        
        await _lavaNode.LeaveAsync(vChannel);
        return await EmbedHelper.MakeLeave(user, vChannel);
    }

    public async Task<Embed> MoveAsync(IGuild guild, IVoiceChannel vChannel, SocketUser user)
    {
        if (!_lavaNode.HasPlayer(guild))
            return await EmbedHelper.MakeError(user, "Nem vagy hangcsatornában vagy a lejátszó nem található!");
        await _lavaNode.MoveChannelAsync(vChannel);
        return await EmbedHelper.MakeMove(user, _lavaNode.GetPlayer(guild), vChannel);
    }

    public async Task<(Embed, MessageComponent)> PlayAsync(string query, IGuild guild, IVoiceChannel vChannel,
        ITextChannel tChannel, SocketUser user, InteractionContext context)
    {
        var search = Uri.IsWellFormedUriString(query, UriKind.Absolute)
            ? await _lavaNode.SearchAsync(SearchType.Direct, query)
            : await _lavaNode.SearchYouTubeAsync(query);
        if (search.Status == SearchStatus.NoMatches)
            return (await EmbedHelper.MakeError(user, "Nincs találat!"), null);
        var track = search.Tracks.FirstOrDefault();
        var player = _lavaNode.HasPlayer(guild)
            ? _lavaNode.GetPlayer(guild)
            : await _lavaNode.JoinAsync(vChannel, tChannel);
        
        ctx.Add(context);
        
        if (player.Track != null && player.PlayerState is PlayerState.Playing ||
            player.PlayerState is PlayerState.Paused)
        {
            player.Queue.Enqueue(track);
            var newButtons = await ButtonHelper.MakeNowPlayingButtons(CanGoBack(), CanGoForward(player), isPlaying);
            if (nowPlayingMessage != null)
                await nowPlayingMessage.ModifyAsync(x => x.Components = newButtons);
            else
                await ctx[0].Interaction.ModifyOriginalResponseAsync(x => x.Components = newButtons);
            return (
                await EmbedHelper.MakeAddedToQueue(user, track, player), null);
        }

        await player.PlayAsync(track);
        await player.UpdateVolumeAsync(100);
        currentTrack = track;
        isPlaying = true;
        return (
            await EmbedHelper.MakeNowPlaying(user, player, isloopEnabled, player.Volume),
            await ButtonHelper.MakeNowPlayingButtons(CanGoBack(), CanGoForward(player), isPlaying));
    }

    public async Task<Embed> StopAsync(IGuild guild, SocketUser user)
    {
        var player = _lavaNode.GetPlayer(guild);
        if (player == null) 
            return await EmbedHelper.MakeError(user, "Nem vagy hangcsatornában vagy a lejátszó nem található!");
        await player.StopAsync();
        return await EmbedHelper.MakeStop(user, player);
    }

    public async Task<(Embed, MessageComponent)> SkipAsync(IGuild guild, SocketUser user, bool button)
    {
        var player = _lavaNode.GetPlayer(guild);
        if (player == null || player.Queue.Count == 0)
            return (await EmbedHelper.MakeError(user, "A várólista üres vagy a lejátszó nem található!"), null);
        previousTracks.Add(currentTrack);
        await player.SkipAsync();
        if (button)
        {
            return (await EmbedHelper.MakeNowPlaying(user, player, isloopEnabled, player.Volume),
                await ButtonHelper.MakeNowPlayingButtons(CanGoBack(), CanGoForward(player), isPlaying));
        }

        return (await EmbedHelper.MakeSkip(user, player), null);
    }

    public async Task<(Embed, MessageComponent)> PlayPreviousTrack(IGuild guild, SocketUser user)
    {
        var player = _lavaNode.GetPlayer(guild);
        var prev = previousTracks.Last();
        await player.PlayAsync(prev);
        previousTracks.Remove(prev);
        return (
            await EmbedHelper.MakeNowPlaying(user, player, isloopEnabled, player.Volume),
            await ButtonHelper.MakeNowPlayingButtons(CanGoBack(), CanGoForward(player), isPlaying));
    }

    public async Task<(Embed, MessageComponent)> PauseOrResumeAsync(IGuild guild, SocketUser user)
    {
        var player = _lavaNode.GetPlayer(guild);

        if (player == null) return (await EmbedHelper.MakePauseOrResume(user, null, false), null);

        if (player.PlayerState == PlayerState.Playing)
        {
            await player.PauseAsync();
            isPlaying = false;
            return (
                await EmbedHelper.MakePauseOrResume(user, player, true),
                await ButtonHelper.MakeNowPlayingButtons(CanGoBack(), CanGoForward(player), isPlaying));
        }

        await player.ResumeAsync();
        isPlaying = true;
        return (await EmbedHelper.MakePauseOrResume(user, player, false),
            await ButtonHelper.MakeNowPlayingButtons(CanGoBack(), CanGoForward(player), isPlaying));
    }

    public async Task<Embed> SetVolumeAsync(ushort volume, IGuild guild, SocketUser user, VoiceButtonType buttonType = VoiceButtonType.Next)
    {
        var player = _lavaNode.GetPlayer(guild);
        if (player == null) return await EmbedHelper.MakeError(user, "A lejátszó nem található!");
        var currentVolume = player.Volume;
        switch (buttonType)
        {
            case VoiceButtonType.VolumeUp:
            {
                var newVolume = currentVolume + 10;
                await player.UpdateVolumeAsync((ushort) newVolume);
                return await EmbedHelper.MakeNowPlaying(user, player, isloopEnabled, newVolume);
            }
            case VoiceButtonType.VolumeDown:
            {
                var newVolume = currentVolume - 10;
                await player.UpdateVolumeAsync((ushort) newVolume);
                return await EmbedHelper.MakeNowPlaying(user, player, isloopEnabled, newVolume);
            }
            default:
                await player.UpdateVolumeAsync(volume);
                return await EmbedHelper.MakeVolume(user, player, volume);
        }
    }

    public async Task<Embed> SetBassBoostAsync(IGuild guild, SocketUser user)
    {
        var player = _lavaNode.GetPlayer(guild);
        if (player == null) 
            return await EmbedHelper.MakeError(user, "A lejátszó nem található!");

        await player.EqualizerAsync(FilterHelper.BassBoost());
        return await EmbedHelper.MakeFilter(user, player, "BASS BOOST");
    }

    public async Task<Embed> SetNightCoreAsync(IGuild guild, SocketUser user)
    {
        var player = _lavaNode.GetPlayer(guild);
        if (player == null) 
            return await EmbedHelper.MakeError(user, "A lejátszó nem található!");
        
        await player.ApplyFilterAsync(FilterHelper.NightCore());
        return await EmbedHelper.MakeFilter(user, player, "NIGHTCORE");
    }

    public async Task<Embed> SetEightDAsync(IGuild guild, SocketUser user)
    {
        var player = _lavaNode.GetPlayer(guild);
        if (player == null) 
            return await EmbedHelper.MakeError(user, "A lejátszó nem található!");
        await player.ApplyFilterAsync(FilterHelper.EightD());
        return await EmbedHelper.MakeFilter(user, player, "8D");
    }

    public async Task<Embed> SetVaporWaveAsync(IGuild guild, SocketUser user)
    {
        var player = _lavaNode.GetPlayer(guild);
        if (player == null) 
            return await EmbedHelper.MakeError(user, "A lejátszó nem található!");
        await player.ApplyFilterAsync(FilterHelper.VaporWave());
        return await EmbedHelper.MakeFilter(user, player, "VAPORWAVE");
    }

    public async Task<Embed> SetRepeatAsync(IGuild guild, SocketUser user)
    {
        var player = _lavaNode.GetPlayer(guild);
        if (player is not {PlayerState: PlayerState.Playing}) 
            return await EmbedHelper.MakeError(user, "Jelenleg nincs zene lejátszás alatt!");
        isloopEnabled = !isloopEnabled;
        return await EmbedHelper.MakeLoop(user, player);
    }
    public async Task<(Embed, MessageComponent)> SetRepeatAsync(IGuild guild, SocketUser user, SocketMessageComponent interaction)
    {
        var player = _lavaNode.GetPlayer(guild);
        isloopEnabled = !isloopEnabled;
        return (await EmbedHelper.MakeNowPlaying(user, player, isloopEnabled, player.Volume), 
            await ButtonHelper.MakeNowPlayingButtons(CanGoBack(), CanGoForward(player), isPlaying));
    }

    public async Task<Embed> ClearFiltersAsync(IGuild guild, SocketUser user)
    {
        var player = _lavaNode.GetPlayer(guild);
        if (player is not {PlayerState: PlayerState.Playing})
            return await EmbedHelper.MakeError(user, "Jelenleg nincs zene lejátszás alatt!");
        
        await player.ApplyFiltersAsync(FilterHelper.DefaultFilters());
        return await EmbedHelper.MakeFilter(user, player, "MINDEN");
    }

    public async Task<Embed> SetSpeedAsync(float value, IGuild guild, SocketUser user)
    {
        var player = _lavaNode.GetPlayer(guild);
        if (player is not {PlayerState: PlayerState.Playing})
            return await EmbedHelper.MakeError(user, "Jelenleg nincs zene lejátszás alatt!");
        await player.ApplyFilterAsync(FilterHelper.Speed(value));
        return await EmbedHelper.MakeFilter(user, player, $"SEBESSÉG -> {value}");
    }

    public async Task<Embed> SetPitchAsync(float value, IGuild guild, SocketUser user)
    {
        var player = _lavaNode.GetPlayer(guild);
        if (player is not {PlayerState: PlayerState.Playing})
            return await EmbedHelper.MakeError(user, "Jelenleg nincs zene lejátszás alatt!");
        await player.ApplyFilterAsync(FilterHelper.Pitch(value));
        return await EmbedHelper.MakeFilter(user, player, $"HANGMAGASSÁG -> {value}");
    }

    private static bool ShouldPlayNext(TrackEndReason trackEndReason)
    {
        return trackEndReason is TrackEndReason.Finished or TrackEndReason.LoadFailed;
    }

    public async Task<Embed> GetQueue(IGuild guild, SocketUser user)
    {
        var player = _lavaNode.GetPlayer(guild);
        if (player == null) return await EmbedHelper.MakeQueue(user, null, true);

        return await EmbedHelper.MakeQueue(user, player);
    }

    public async Task<Embed> ClearQueue(IGuild guild, SocketUser user)
    {
        var player = _lavaNode.GetPlayer(guild);
        if (player == null) 
            return await EmbedHelper.MakeError(user, "A lejátszó nem található!");
        player.Queue.Clear();
        return await EmbedHelper.MakeQueue(user, player, true);
    }

    private async Task OnTrackEnded(TrackEndedEventArgs args)
    {
        if (!ShouldPlayNext(args.Reason)) return;
        previousTracks.Add(args.Track);
        var player = args.Player;
        if (!player.Queue.TryDequeue(out var queueable))
        {
            if (isloopEnabled) await player.PlayAsync(args.Track);
            // delete now playing message if there is no queue
            var msg = nowPlayingMessage ?? await ctx[0].Interaction.GetOriginalResponseAsync();
            await msg.DeleteAsync();
            nowPlayingMessage = null;
            return;
        }

        if (queueable is not { } track)
            //await player.TextChannel.SendMessageAsync("Next item in queue is not a track.");
            return;
        await player.PlayAsync(track);
        
        await SendNewNowPlayingMessage(player);
    }

    private async Task SendNewNowPlayingMessage(LavaPlayer player)
    {
        var newEmbed = await EmbedHelper.MakeNowPlaying(_client.CurrentUser, player, isloopEnabled, player.Volume);
        var newButtons =
            await ButtonHelper.MakeNowPlayingButtons(CanGoBack(), CanGoForward(player), isPlaying);
        var msg = nowPlayingMessage ?? await ctx[0].Interaction.GetOriginalResponseAsync();
        await msg.ModifyAsync(x =>
        {
            x.Embed = newEmbed;
            x.Components = newButtons;
        });
        nowPlayingMessage = msg;
        /*var channel = msg.Channel;
        await msg.DeleteAsync();
        ctx.Clear();
        nowPlayingMessage = await channel.SendMessageAsync(embed: newEmbed, components: newButtons);*/
    }
    
    private bool CanGoBack()
    {
        return previousTracks.Count > 0;
    }
    
    private static bool CanGoForward(LavaPlayer player)
    {
        return player.Queue.Count > 0;
    }
    
}