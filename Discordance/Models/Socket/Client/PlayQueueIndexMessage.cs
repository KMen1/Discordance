﻿namespace Discordance.Models.Socket.Client;

public struct PlayQueueIndexMessage : IClientMessage
{
    public int Index { get; init; }
}