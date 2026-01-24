namespace BannerBros.Network;

/// <summary>
/// Packet for synchronizing player state (position, current activity, etc.)
/// </summary>
public class PlayerStatePacket
{
    public int PlayerId { get; set; }
    public string PlayerName { get; set; } = "";
    public float MapX { get; set; }
    public float MapY { get; set; }
    public int State { get; set; } // PlayerState enum value
    public string? HeroId { get; set; }
    public string? PartyId { get; set; }
    public string? ClanId { get; set; }
    public string? KingdomId { get; set; }
    public int PartySize { get; set; }
    public float PartySpeed { get; set; }
    public bool IsInBattle { get; set; }
    public string? BattleId { get; set; }
}

/// <summary>
/// Packet for synchronizing world state from host to clients.
/// </summary>
public class WorldSyncPacket
{
    public long CampaignTimeTicks { get; set; }
    public float TimeMultiplier { get; set; }
    public int Season { get; set; }
    public int DayOfSeason { get; set; }

    // Active battles
    public List<BattleInfo> ActiveBattles { get; set; } = new();

    // Key world events
    public List<WorldEvent> RecentEvents { get; set; } = new();
}

public class BattleInfo
{
    public string BattleId { get; set; } = "";
    public string MapPosition { get; set; } = "";
    public int InitiatorPlayerId { get; set; }
    public List<int> AttackerPlayerIds { get; set; } = new();
    public List<int> DefenderPlayerIds { get; set; } = new();
}

public class WorldEvent
{
    public int EventType { get; set; }
    public string Data { get; set; } = "";
    public long Timestamp { get; set; }
}

/// <summary>
/// Packet for battle-related events (start, join, end).
/// </summary>
public class BattleEventPacket
{
    public int EventType { get; set; } // BattleEventType enum
    public string BattleId { get; set; } = "";
    public int PlayerId { get; set; }
    public string MapPosition { get; set; } = "";
    public int Side { get; set; } // BattleSide enum
    public string? EnemyPartyId { get; set; }
}

public enum BattleEventType
{
    Started,
    PlayerJoined,
    PlayerLeft,
    Ended,
    VictoryAttacker,
    VictoryDefender,
    Retreat
}

/// <summary>
/// Packet for chat messages between players.
/// </summary>
public class ChatPacket
{
    public int SenderId { get; set; }
    public string SenderName { get; set; } = "";
    public string Message { get; set; } = "";
    public int Channel { get; set; } // 0 = all, 1 = team, 2 = whisper
    public int? TargetPlayerId { get; set; } // For whispers
    public long Timestamp { get; set; }
}

/// <summary>
/// Packet for trade requests between players.
/// </summary>
public class TradeRequestPacket
{
    public int RequestType { get; set; } // TradeRequestType enum
    public int InitiatorId { get; set; }
    public int TargetId { get; set; }

    // Trade offer details
    public int OfferedGold { get; set; }
    public List<TradeItem> OfferedItems { get; set; } = new();
    public int RequestedGold { get; set; }
    public List<TradeItem> RequestedItems { get; set; } = new();

    public bool Accepted { get; set; }
    public string? RejectionReason { get; set; }
}

public class TradeItem
{
    public string ItemId { get; set; } = "";
    public int Count { get; set; }
    public int ModifierValue { get; set; } // For item quality/modifiers
}

public enum TradeRequestType
{
    Initiate,
    UpdateOffer,
    Accept,
    Reject,
    Cancel,
    Complete
}

/// <summary>
/// Packet for player joining/leaving session.
/// </summary>
public class SessionPacket
{
    public int EventType { get; set; } // SessionEventType enum
    public int PlayerId { get; set; }
    public string PlayerName { get; set; } = "";
    public string? CharacterData { get; set; } // Serialized character for new joins
}

public enum SessionEventType
{
    PlayerJoined,
    PlayerLeft,
    PlayerKicked,
    SessionSaving,
    SessionSaved
}
