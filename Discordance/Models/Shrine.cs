﻿// ReSharper disable UnusedAutoPropertyAccessor.Global

#pragma warning disable CS8618, MA0048
using Newtonsoft.Json;

namespace Discordance.Models;

public partial class Shrines
{
    [JsonProperty("perks")] public ShrinePerk[] Perks { get; set; }

    [JsonProperty("end")] public long End { get; set; }
}

public class ShrinePerk
{
    [JsonProperty("id")] public string Id { get; set; }

    [JsonProperty("bloodpoints")] public long Bloodpoints { get; set; }

    [JsonProperty("shards")] public long Shards { get; set; }
}

public partial class Shrines
{
    public static Shrines FromJson(string json)
    {
        return JsonConvert.DeserializeObject<Shrines>(json)!;
    }
}

public partial class Perk
{
    [JsonProperty("name")] public string Name { get; set; }

    [JsonProperty("character")] public long CharacterId { get; set; }

    //public string CharacterName => DbDService.GetCharacterNameFromId(CharacterId);
}

public partial class Perk
{
    public static Perk FromJson(string json)
    {
        return JsonConvert.DeserializeObject<Perk>(json)!;
    }
}