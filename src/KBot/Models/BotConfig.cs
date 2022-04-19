﻿namespace KBot.Models;

public class BotConfig
{
    public ClientConfig Client { get; init; }
    public LavalinkConfig Lavalink { get; init; }
    public MongoDbConfig MongoDb { get; init; }
    public OsuApiConfig OsuApi { get; init; }
    public CloudinaryConfig Cloudinary { get; init; }
    public GoogleConfig Google { get; init; }
    public RedisConfig Redis { get; init; }
}

public class ClientConfig
{
    public string Token { get; init; }
    public string Game { get; init; }
}

public class LavalinkConfig
{
    public string Host { get; init; }
    public ushort Port { get; init; }
    public string Password { get; init; }
}

public class MongoDbConfig
{
    public string ConnectionString { get; init; }
    public string Database { get; init; }
    public string GuildCollection { get; init; }
    public string ConfigCollection { get; init; }
    public string UserCollection { get; init; }
    public string TransactionCollection { get; init; }
    public string WarnCollection { get; init; }
    public string ButtonRoleCollection { get; init; }
}

public class OsuApiConfig
{
    public ulong AppId { get; init; }
    public string AppSecret { get; init; }
}

public class CloudinaryConfig
{
    public string CloudName { get; init; }
    public string ApiKey { get; init; }
    public string ApiSecret { get; init; }
}

public class GoogleConfig
{
    public string ApiKey { get; init; }
}

public class RedisConfig
{
    public string Endpoint { get; init; }
}