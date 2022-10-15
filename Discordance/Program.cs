﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using CloudinaryDotNet;
using Discord;
using Discord.Addons.Hosting;
using Discord.Interactions;
using Discord.WebSocket;
using Discordance.Services;
using DotNetEnv;
using Google.Apis.Services;
using Google.Apis.YouTube.v3;
using Hangfire;
using Hangfire.Mongo;
using Hangfire.Mongo.Migration.Strategies;
using Hangfire.Mongo.Migration.Strategies.Backup;
using Lavalink4NET;
using Lavalink4NET.Artwork;
using Lavalink4NET.Cluster;
using Lavalink4NET.DiscordNet;
using Lavalink4NET.Logging;
using Lavalink4NET.Tracking;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MongoDB.Driver;
using Newtonsoft.Json;
using OsuSharp;
using OsuSharp.Extensions;
using Serilog;
using Serilog.Events;
using StackExchange.Redis;
using ILogger = Lavalink4NET.Logging.ILogger;

Env.Load();
Log.Logger = new LoggerConfiguration().Enrich
    .FromLogContext()
    .MinimumLevel.Debug()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Error)
    .MinimumLevel.Override("Hangfire", LogEventLevel.Information)
    .WriteTo.Console()
    .WriteTo.Sentry(
        x =>
        {
            x.MinimumBreadcrumbLevel = LogEventLevel.Warning;
            x.MinimumEventLevel = LogEventLevel.Warning;
            x.Dsn = Environment.GetEnvironmentVariable("SENTRY_DSN");
            x.Debug = false;
            x.AttachStacktrace = true;
            x.SendDefaultPii = true;
            x.TracesSampleRate = 1.0;
            x.Release = FileVersionInfo
                .GetVersionInfo(Assembly.GetExecutingAssembly().Location)
                .FileVersion;
        }
    )
    .CreateLogger();

var host = Host.CreateDefaultBuilder()
    .UseSerilog()
    .ConfigureDiscordShardedHost(
        (_, config) =>
        {
            config.SocketConfig = new DiscordSocketConfig
            {
                LogLevel = LogSeverity.Info,
                AlwaysDownloadUsers = true,
                MessageCacheSize = 200,
                GatewayIntents = GatewayIntents.All,
                LogGatewayIntentWarnings = false,
                DefaultRetryMode = RetryMode.AlwaysFail
            };
            config.Token = Environment.GetEnvironmentVariable("DISCORD_TOKEN")!;
        }
    )
    .UseInteractionService(
        (_, config) =>
        {
            config.DefaultRunMode = RunMode.Async;
            config.LogLevel = LogSeverity.Info;
            config.UseCompiledLambda = true;
            config.LocalizationManager = new JsonLocalizationManager("Resources", "DCLocalization");
        }
    )
    .ConfigureOsuSharp(
        (_, options) =>
        {
            options.Configuration = new OsuClientConfiguration
            {
                ClientId = int.Parse(Environment.GetEnvironmentVariable("OSU_APP_ID")!),
                ClientSecret = Environment.GetEnvironmentVariable("OSU_APP_SECRET")!
            };
        }
    )
    .ConfigureServices(
        (_, services) =>
        {
            services.AddHostedService<InteractionHandler>();
            services.AddSingleton<IDiscordClientWrapper, DiscordClientWrapper>();
            services.AddSingleton<IAudioService, LavalinkNode>();
            services.AddSingleton(
                new LavalinkNodeOptions
                {
                    AllowResuming = true,
                    DisconnectOnStop = false,
                    WebSocketUri = $"ws://{Environment.GetEnvironmentVariable("LAVALINK_HOST")}:{Environment.GetEnvironmentVariable("LAVALINK_PORT")}",
                    RestUri = $"http://{Environment.GetEnvironmentVariable("LAVALINK_HOST")}:{Environment.GetEnvironmentVariable("LAVALINK_PORT")}",
                    Password = Environment.GetEnvironmentVariable("LAVALINK_PASSWORD")!,
                }
            );
            services.AddSingleton(
                new InactivityTrackingOptions
                {
                    DisconnectDelay = TimeSpan.FromMinutes(3),
                    PollInterval = TimeSpan.FromSeconds(5)
                }
            );
            services.AddSingleton<InactivityTrackingService>();
            services.AddSingleton<IMongoClient>(
                new MongoClient(Environment.GetEnvironmentVariable("MONGO_CONNECTION_STRING"))
            );
            services.AddSingleton(
                new Cloudinary(
                    new Account(
                        Environment.GetEnvironmentVariable("CLOUDINARY_NAME"),
                        Environment.GetEnvironmentVariable("CLOUDINARY_KEY"),
                        Environment.GetEnvironmentVariable("CLOUDINARY_SECRET")
                    )
                )
            );
            services.AddSingleton(
                new BaseClientService.Initializer
                {
                    ApiKey = Environment.GetEnvironmentVariable("GOOGLE_API_KEY"),
                    ApplicationName = "Discordance"
                }
            );
            services.AddSingleton<YouTubeService>();
            services.AddSingleton<IConnectionMultiplexer>(
                ConnectionMultiplexer.Connect(
                    new ConfigurationOptions
                    {
                        EndPoints = {Environment.GetEnvironmentVariable("REDIS_ENDPOINT")!},
                        Password = Environment.GetEnvironmentVariable("REDIS_PASSWORD"),
                        AsyncTimeout = 15000
                    }
                )
            );
            services.AddSingleton<AudioService>();
            services.AddSingleton<ArtworkService>();
            services.AddSingleton<MongoService>();
            services.AddSingleton<TemporaryChannelService>();
            services.AddSingleton<GameService>();
            services.Configure<HostOptions>(
                x =>
                    x.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore
            );
            services.AddSingleton<ImageService>();
            services.AddHangfire(
                x =>
                {
                    x.UseMongoStorage(
                        Environment.GetEnvironmentVariable("MONGO_CONNECTION_STRING"),
                        Environment.GetEnvironmentVariable("MONGO_DATABASE"),
                        new MongoStorageOptions
                        {
                            MigrationOptions = new MongoMigrationOptions
                            {
                                MigrationStrategy = new MigrateMongoMigrationStrategy(),
                                BackupStrategy = new CollectionMongoBackupStrategy()
                            },
                            CheckConnection = true
                        }
                    );
                    x.UseSerilogLogProvider();
                }
            );
            services.AddSingleton<ILogger, EventLogger>();
            services.AddHangfireServer();
            services.AddHttpClient();
            services.AddMemoryCache();
        }
    )
    .UseConsoleLifetime()
    .Build();

var cache = host.Services.GetRequiredService<IMemoryCache>();

var localizationData = File.ReadAllText("Resources/Localization.json") ?? throw new FileNotFoundException("Localization.json not found");

var localization = JsonConvert.DeserializeObject<
    Dictionary<string, Dictionary<string, string>>>(localizationData) ?? throw new JsonException("Localization.json is not valid JSON");

foreach (var message in localization) cache.Set(message.Key, message.Value);

var client = host.Services.GetRequiredService<DiscordShardedClient>();

var audioService = host.Services.GetRequiredService<IAudioService>();
var needToConnect = true;
client.ShardReady += async _ =>
{
    if (needToConnect)
    {
        await audioService.InitializeAsync();
        needToConnect = false;
    }
};

await host.RunAsync().ConfigureAwait(false);