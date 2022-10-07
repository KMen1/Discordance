﻿using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Discord;
using Discord.WebSocket;
using Discordance.Enums;
using Discordance.Extensions;
using Discordance.Modules.Music;
using Google.Apis.YouTube.v3;
using Lavalink4NET;
using Lavalink4NET.Cluster;
using Lavalink4NET.Events;
using Lavalink4NET.Player;
using Lavalink4NET.Rest;
using Lavalink4NET.Tracking;
using Microsoft.Extensions.Caching.Memory;

namespace Discordance.Services;

public class AudioService
{
    private readonly IAudioService _audioService;
    private readonly IMemoryCache _cache;
    private readonly SearchResource.ListRequest? _searchRequest;

    public AudioService(
        IAudioService audioService,
        YouTubeService youtubeService,
        DiscordShardedClient client,
        IMemoryCache cache,
        InactivityTrackingService trackingService
    )
    {
        _audioService = audioService;
        _audioService.TrackEnd += OnTrackEnd;
        _cache = cache;
        _searchRequest = youtubeService.Search.List("snippet");
        _searchRequest.MaxResults = 1;
        _searchRequest.Type = "video";
        client.MessageReceived += ListenForSongRequests;
        foreach (var node in ((LavalinkCluster) _audioService).Nodes) node.UseSponsorBlock();

        trackingService.BeginTracking();
    }

    public async Task<(Embed? embed, MessageComponent? components)> PlayAsync(
        ulong guildId,
        IUser user,
        LavalinkTrack track
    )
    {
        var player = GetPlayer(guildId)!;
        await player.PlayAsync(track).ConfigureAwait(false);
        player.AppendAction(
            _cache
                .GetMessage(_cache.GetLangKey(guildId), "track_added")
                .FormatWithTimestamp(user.Mention, track.ToHyperLink())
        );
        if (player.MessageId is null)
            return (GetEmbed(player), GetComponents(player));
        await UpdateMessageAsync(player).ConfigureAwait(false);
        return (null, null);
    }

    public async Task<(Embed? embed, MessageComponent? components)> PlayAsync(
        ulong guildId,
        IUser user,
        LavalinkTrack[] tracks
    )
    {
        var player = GetPlayer(guildId)!;

        await player.PlayAsync(tracks[0]).ConfigureAwait(false);
        player.Queue.AddRange(tracks[1..]);
        player.AppendAction(
            _cache
                .GetMessage(_cache.GetLangKey(guildId), "player_playlist_added")
                .FormatWithTimestamp(user.Mention, tracks.Length)
        );
        if (player.MessageId is null)
            return (GetEmbed(player), GetComponents(player));
        await UpdateMessageAsync(player).ConfigureAwait(false);
        return (null, null);
    }

    public async Task SkipAsync(ulong guildId, IUser user)
    {
        var player = GetPlayer(guildId)!;
        var lang = _cache.GetLangKey(guildId);

        if (player.Queue.Count == 0 && player.IsAutoPlay)
        {
            var next = await GetRelatedTrack(player.CurrentTrack!.TrackIdentifier, user).ConfigureAwait(false);
            next!.Context = user;
            await player.PlayAsync(next, false).ConfigureAwait(false);
            await UpdateMessageAsync(player).ConfigureAwait(false);
            return;
        }
        
        if (player.RequestedBy.Id != user.Id)
        {
            var result = await player.VoteAsync(user.Id).ConfigureAwait(false);
            if (!result.WasAdded)
                return;

            if (!result.WasSkipped)
            {
                player.AppendAction(
                    _cache
                        .GetMessage(lang, "player_voteskip")
                        .FormatWithTimestamp(user.Mention, player.VoteSkipRequired)
                );
                await UpdateMessageAsync(player).ConfigureAwait(false);
                return;
            }

            player.AppendAction(
                _cache
                    .GetMessage(lang, "player_voteskipped")
                    .FormatWithTimestamp(player.CurrentTrack!.ToHyperLink())
            );
            await UpdateMessageAsync(player).ConfigureAwait(false);
            return;
        }

        await player.SkipAsync().ConfigureAwait(false);
        player.AppendAction(
            _cache
                .GetMessage(lang, "player_skip")
                .FormatWithTimestamp(user.Mention, player.CurrentTrack!.ToHyperLink())
        );
        await UpdateMessageAsync(player);
    }

    public async Task RewindAsync(ulong guildId, IUser user)
    {
        var player = GetPlayer(guildId)!;
        var lang = _cache.GetLangKey(guildId);
        if (player.History.Count == 0)
            return;
        var track = player.History[^1];
        player.History.Remove(track);
        player.AppendAction(
            _cache.GetMessage(lang, "player_previous").FormatWithTimestamp(user.Mention)
        );
        await player.PlayAsync(track);
        await UpdateMessageAsync(player);
    }

    public async Task PauseOrResumeAsync(ulong guildId, IUser user)
    {
        var player = GetPlayer(guildId)!;
        var lang = _cache.GetLangKey(guildId);
        switch (player.State)
        {
            case PlayerState.Playing:
            {
                await player.PauseAsync().ConfigureAwait(false);
                player.AppendAction(
                    _cache.GetMessage(lang, "player_pause").FormatWithTimestamp(user.Mention)
                );
                await UpdateMessageAsync(player).ConfigureAwait(false);
                break;
            }
            case PlayerState.Paused:
            {
                await player.ResumeAsync().ConfigureAwait(false);
                player.AppendAction(
                    _cache
                        .GetMessage(lang, "player_resume")
                        .FormatWithTimestamp(user.Mention)
                );
                await UpdateMessageAsync(player).ConfigureAwait(false);
                break;
            }
        }
    }

    public async Task SetFilterAsync(ulong guildId, IUser user, FilterType filterType)
    {
        var player = GetPlayer(guildId)!;
        var lang = _cache.GetLangKey(guildId);
        if (filterType is not FilterType.None)
            player.Filters.Clear();
        player.Filters.ApplyFilter(filterType);
        await player.Filters.CommitAsync().ConfigureAwait(false);

        player.CurrentFilter = _cache.GetMessage(
            lang,
            $"filter_{filterType.ToString().ToLower()}"
        );
        player.AppendAction(
            _cache
                .GetMessage(lang, "set_filter")
                .FormatWithTimestamp(user.Mention, player.CurrentFilter)
        );
        await UpdateMessageAsync(player).ConfigureAwait(false);
    }

    public async Task<int> SetVolumeAsync(ulong guildId, IUser user, float volume)
    {
        var player = GetPlayer(guildId)!;
        var lang = _cache.GetLangKey(guildId);
        await player.SetVolumeAsync(volume).ConfigureAwait(false);
        player.AppendAction(
            _cache
                .GetMessage(lang, "player_volume")
                .FormatWithTimestamp(user.Mention, (int) (volume * 100))
        );
        await UpdateMessageAsync(player).ConfigureAwait(false);
        return (int) (volume * 100);
    }

    public async Task<int> ClearQueueAsync(ulong guildId, IUser user)
    {
        var player = GetPlayer(guildId)!;
        var lang = _cache.GetLangKey(guildId);
        var cleared = player.Queue.Clear();
        player.AppendAction(
            _cache.GetMessage(lang, "player_queue_cleared").FormatWithTimestamp(user.Mention)
        );
        await UpdateMessageAsync(player).ConfigureAwait(false);
        return cleared;
    }

    public Task ToggleLoopAsync(ulong guildId, IUser user)
    {
        var player = GetPlayer(guildId)!;
        var lang = _cache.GetLangKey(guildId);
        player.ToggleLoop();
        player.AppendAction(
            _cache
                .GetMessage(lang, player.IsLooping ? "player_loop_enabled" : "player_loop_disabled")
                .FormatWithTimestamp(user.Mention)
        );
        return UpdateMessageAsync(player);
    }

    public Task ToggleAutoPlayAsync(ulong guildId, IUser user)
    {
        var player = GetPlayer(guildId)!;
        var lang = _cache.GetLangKey(guildId);
        player.ToggleAutoPlay();
        player.AppendAction(
            _cache
                .GetMessage(
                    lang,
                    player.IsAutoPlay ? "player_autoplay_enabled" : "player_autoplay_disabled"
                )
                .FormatWithTimestamp(user.Mention)
        );
        return UpdateMessageAsync(player);
    }

    private async Task UpdateMessageAsync(DiscordancePlayer player)
    {
        var channel = player.TextChannel;
        var messageId = player.MessageId;

        var message = await channel.GetMessageAsync(messageId.GetValueOrDefault()).ConfigureAwait(false);
        if (message is not IUserMessage userMessage)
            userMessage = await channel
                .SendMessageAsync(embed: GetEmbed(player), components: GetComponents(player))
                .ConfigureAwait(false);

        await userMessage
            .ModifyAsync(
                x =>
                {
                    x.Embed = GetEmbed(player);
                    x.Components = GetComponents(player);
                }
            )
            .ConfigureAwait(false);
    }

    private Embed GetEmbed(DiscordancePlayer player)
    {
        var config = _cache.GetGuildConfig(player.GuildId);
        var lang = config.Language;
        var isAnon = config.Music.IsAnonymous;
        var track = player.CurrentTrack;
        var requester = (IUser) track!.Context!;
        return new EmbedBuilder()
            .WithAuthor(
                _cache.GetMessage(lang, "now_playing"),
                "https://bestanimations.com/media/discs/895872755cd-animated-gif-9.gif"
            )
            .WithTitle(track.Title)
            .WithDescription(string.Join("\n", player.Actions))
            .WithUrl(track.Uri?.ToString() ?? "")
            .WithImageUrl($"https://img.youtube.com/vi/{track.TrackIdentifier}/maxresdefault.jpg")
            .WithColor(new Color(31, 31, 31))
            .AddField(
                _cache.GetMessage(lang, "added_by"),
                isAnon ? requester.Mention : "`Anonymous`",
                true
            )
            .AddField(_cache.GetMessage(lang, "channel"), player.VoiceChannel.Mention, true)
            .AddField(
                _cache.GetMessage(lang, "length"),
                $"`{track.Duration.ToTimeString()}`",
                true
            )
            .AddField(
                _cache.GetMessage(lang, "volume"),
                $"`{Math.Round(player.Volume * 100).ToString(CultureInfo.InvariantCulture)}%`",
                true
            )
            .AddField(_cache.GetMessage(lang, "filter"), $"`{player.CurrentFilter}`", true)
            .AddField(
                _cache.GetMessage(lang, "in_queue"),
                $"`{player.Queue.Count.ToString()}`",
                true
            )
            .Build();
    }

    private MessageComponent GetComponents(DiscordancePlayer player)
    {
        var lang = _cache.GetLangKey(player.GuildId);
        var state = player.State;
        return new ComponentBuilder()
            .WithButton(
                _cache.GetMessage(lang, "back"),
                "previous",
                emote: new Emoji("⏮"),
                disabled: player.History.Count == 0,
                row: 0
            )
            .WithButton(
                state == PlayerState.Paused
                    ? _cache.GetMessage(lang, "resume")
                    : _cache.GetMessage(lang, "pause"),
                "pause",
                emote: state == PlayerState.Paused ? new Emoji("▶") : new Emoji("⏸"),
                row: 0
            )
            .WithButton(
                _cache.GetMessage(lang, "stop"),
                "stop",
                emote: new Emoji("⏹"),
                row: 0
            )
            .WithButton(
                _cache
                    .GetMessage(lang, "skip")
                    .Format(player.VoteSkipCount, player.VoteSkipRequired),
                "next",
                emote: new Emoji("⏭"),
                disabled: player.Queue.Count == 0 && !player.IsAutoPlay,
                row: 0
            )
            .WithButton(
                _cache.GetMessage(lang, "volume_down"),
                "volumedown",
                emote: new Emoji("🔉"),
                row: 1,
                disabled: player.Volume == 0
            )
            .WithButton(
                player.IsAutoPlay
                    ? _cache.GetMessage(lang, "autoplay_on")
                    : _cache.GetMessage(lang, "autoplay_off"),
                "autoplay",
                emote: new Emoji("🔎"),
                row: 1
            )
            .WithButton(
                player.IsLooping
                    ? _cache.GetMessage(lang, "loop_on")
                    : _cache.GetMessage(lang, "loop_off"),
                "repeat",
                emote: new Emoji("🔁"),
                row: 1
            )
            .WithButton(
                _cache.GetMessage(lang, "volume_up"),
                "volumeup",
                emote: new Emoji("🔊"),
                row: 1,
                disabled: Math.Abs(player.Volume - 1.0f) < 0.01f
            )
            .WithSelectMenu(
                new SelectMenuBuilder()
                    .WithPlaceholder(_cache.GetMessage(lang, "filter_select"))
                    .WithCustomId("filterselectmenu")
                    .WithMinValues(1)
                    .WithMaxValues(1)
                    .AddOption(
                        _cache.GetMessage(lang, "filter_none"),
                        "None",
                        emote: new Emoji("🗑️")
                    )
                    .AddOption(
                        _cache.GetMessage(lang, "filter_bassboost"),
                        "Bassboost",
                        emote: new Emoji("🤔")
                    )
                    .AddOption(
                        _cache.GetMessage(lang, "filter_pop"),
                        "Pop",
                        emote: new Emoji("🎸")
                    )
                    .AddOption(
                        _cache.GetMessage(lang, "filter_soft"),
                        "Soft",
                        emote: new Emoji("✨")
                    )
                    .AddOption(
                        _cache.GetMessage(lang, "filter_treblebass"),
                        "Treblebass",
                        emote: new Emoji("🔊")
                    )
                    .AddOption(
                        _cache.GetMessage(lang, "filter_nightcore"),
                        "Nightcore",
                        emote: new Emoji("🌃")
                    )
                    .AddOption(
                        _cache.GetMessage(lang, "filter_8d"),
                        "Eightd",
                        emote: new Emoji("🎧")
                    )
                    .AddOption(
                        _cache.GetMessage(lang, "filter_chinese"),
                        "China",
                        emote: new Emoji("🍊")
                    )
                    .AddOption(
                        _cache.GetMessage(lang, "filter_vaporwave"),
                        "Vaporwave",
                        emote: new Emoji("💦")
                    )
                    .AddOption(
                        _cache.GetMessage(lang, "filter_speed_up"),
                        "Doubletime",
                        emote: new Emoji("⏫")
                    )
                    .AddOption(
                        _cache.GetMessage(lang, "filter_speed_down"),
                        "Slowmotion",
                        emote: new Emoji("⏬")
                    )
                    .AddOption(
                        _cache.GetMessage(lang, "filter_chipmunk"),
                        "Chipmunk",
                        emote: new Emoji("🐿")
                    )
                    .AddOption(
                        _cache.GetMessage(lang, "filter_darthvader"),
                        "Darthvader",
                        emote: new Emoji("🤖")
                    )
                    .AddOption(
                        _cache.GetMessage(lang, "filter_dance"),
                        "Dance",
                        emote: new Emoji("🕺")
                    )
                    .AddOption(
                        _cache.GetMessage(lang, "filter_vibrato"),
                        "Vibrato",
                        emote: new Emoji("🕸")
                    )
                    .AddOption(
                        _cache.GetMessage(lang, "filter_tremolo"),
                        "Tremolo",
                        emote: new Emoji("📳")
                    ),
                2
            )
            .Build();
    }

    private async Task ListenForSongRequests(SocketMessage arg)
    {
        if (
            arg is not SocketUserMessage message
            || message.Author.IsBot
            || message.Author.IsWebhook
            || message.Author is not SocketGuildUser user
            || user.VoiceChannel is null
            || message.Channel is not SocketTextChannel channel
        )
            return;

        var guild = channel.Guild;
        var config = _cache.GetGuildConfig(guild.Id).Music;

        if (config.RequestChannelId is null)
            return;

        if (channel.Id != config.RequestChannelId)
            return;

        var (player, _) = await GetOrCreatePlayerAsync(guild.Id, user.VoiceChannel, channel)
            .ConfigureAwait(false);

        var tracks = await SearchAsync(message.Content, user).ConfigureAwait(false);
        if (tracks is null)
            return;

        var track = tracks[0];

        await message.DeleteAsync().ConfigureAwait(false);
        if (player.TextChannel.Id != config.RequestChannelId)
        {
            //await player.Message.DeleteAsync().ConfigureAwait(false);
            player.SetMessage(
                await channel
                    .SendMessageAsync(
                        embed: GetEmbed(player),
                        components: GetComponents(player)
                    )
                    .ConfigureAwait(false)
            );
        }

        if (tracks.Length > 1 && config.PlaylistAllowed)
        {
            await PlayAsync(player.GuildId, user, tracks).ConfigureAwait(false);
            return;
        }

        await PlayAsync(player.GuildId, user, track).ConfigureAwait(false);
    }

    private async Task OnTrackEnd(object _, TrackEndEventArgs eventArgs)
    {
        var player = (DiscordancePlayer) eventArgs.Player;
        if (!eventArgs.MayStartNext && eventArgs.Reason != TrackEndReason.Stopped)
            return;

        if (player.IsLooping)
            return;

        var previous = player.CurrentTrack!;
        player.History.Add(previous);

        if (player.Queue.Count > 0)
        {
            await Task.Delay(2000).ConfigureAwait(false);
            await UpdateMessageAsync(player).ConfigureAwait(false);
            return;
        }

        if (player.IsAutoPlay)
        {
            var track = await GetRelatedTrack(previous.TrackIdentifier, (IUser)previous.Context!).ConfigureAwait(false);
            if (track is not null)
            {
                await player.PlayAsync(track, false).ConfigureAwait(false);
                await UpdateMessageAsync(player).ConfigureAwait(false);
                return;
            }
        }

        var message = (IUserMessage) await player.TextChannel
            .GetMessageAsync(player.MessageId!.Value)
            .ConfigureAwait(false);

        await message
            .ModifyAsync(
                x =>
                {
                    x.Embed = new EmbedBuilder()
                        .WithDescription(
                            _cache.GetMessage(player.GuildId, "player_waiting")
                        )
                        .WithColor(new Color(31, 31, 31))
                        .Build();
                    x.Components = new ComponentBuilder().Build();
                }
            )
            .ConfigureAwait(false);
    }

    public bool IsPlaying(ulong guildId, out DiscordancePlayer? player)
    {
        player = _audioService.GetPlayer<DiscordancePlayer>(guildId);
        return player is not null;
    }

    public async Task<(DiscordancePlayer player, bool isNew)> GetOrCreatePlayerAsync(
        ulong guildId,
        IVoiceChannel voiceChannel,
        ITextChannel textChannel
    )
    {
        var config = _cache.GetGuildConfig(guildId);
        if (GetPlayer(guildId) is { } player)
            return (player, false);
        
        player = await _audioService
            .JoinAsync(
                () => new DiscordancePlayer(voiceChannel, textChannel),
                guildId,
                voiceChannel.Id
            )
            .ConfigureAwait(false);
        //if (config.Music.UseSponsorBlock)
        //    player.GetCategories().Add(SegmentCategory.OfftopicMusic);
        if (config.Music.DefaultVolume != 100)
            await player.SetVolumeAsync(config.Music.DefaultVolume).ConfigureAwait(false);
        return (player, true);
    }

    public DiscordancePlayer? GetPlayer(ulong guildId)
    {
        return _audioService.GetPlayer<DiscordancePlayer>(guildId);
    }

    private async Task<LavalinkTrack?> GetRelatedTrack(string videoId, IUser user)
    {
        var relatedVideoId = await GetRelatedVideoId(videoId).ConfigureAwait(false);
        var track = await _audioService
            .GetTrackAsync($"https://www.youtube.com/watch?v={relatedVideoId}")
            .ConfigureAwait(false);
        if (track is not null)
            track.Context = user;
        return track;
    }
    
    private async Task<string> GetRelatedVideoId(string videoId)
    {
        _searchRequest!.RelatedToVideoId = videoId;
        var result = await _searchRequest.ExecuteAsync().ConfigureAwait(false);
        return result.Items.First(x => x.Snippet is not null).Id.VideoId;
    }

    public async Task<LavalinkTrack[]?> SearchAsync(string query, IUser user)
    {
        var results = Uri.IsWellFormedUriString(query, UriKind.Absolute)
            ? await _audioService.LoadTracksAsync(query).ConfigureAwait(false)
            : await _audioService.LoadTracksAsync(query, SearchMode.YouTube).ConfigureAwait(false);

        var tracks = results.Tracks;
        if (tracks is null || tracks.Length == 0)
            return null;
        foreach (var track in tracks)
            track.Context = user;

        return results.PlaylistInfo?.Name is not null ? tracks : new[] {tracks[0]};
    }
}