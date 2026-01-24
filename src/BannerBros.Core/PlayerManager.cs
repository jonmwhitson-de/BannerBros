using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;

namespace BannerBros.Core;

/// <summary>
/// Manages connected players and their associated game state.
/// </summary>
public class PlayerManager
{
    private readonly Dictionary<int, CoopPlayer> _players = new();
    private readonly object _lock = new();

    public IReadOnlyDictionary<int, CoopPlayer> Players => _players;
    public int PlayerCount => _players.Count;
    public int LocalPlayerId { get; set; } = -1;

    public event Action<CoopPlayer>? OnPlayerJoined;
    public event Action<CoopPlayer>? OnPlayerLeft;
    public event Action<CoopPlayer>? OnPlayerUpdated;

    public void AddPlayer(CoopPlayer player)
    {
        lock (_lock)
        {
            _players[player.NetworkId] = player;
        }
        OnPlayerJoined?.Invoke(player);
        BannerBrosModule.LogMessage($"{player.Name} joined the game");
    }

    public void RemovePlayer(int networkId)
    {
        CoopPlayer? player;
        lock (_lock)
        {
            if (_players.TryGetValue(networkId, out player))
            {
                _players.Remove(networkId);

                // Despawn their party when they disconnect
                if (player.PartyId != null)
                {
                    DespawnPlayerParty(player);
                }
            }
        }

        if (player != null)
        {
            OnPlayerLeft?.Invoke(player);
            BannerBrosModule.LogMessage($"{player.Name} left the game");
        }
    }

    public CoopPlayer? GetPlayer(int networkId)
    {
        lock (_lock)
        {
            return _players.TryGetValue(networkId, out var player) ? player : null;
        }
    }

    public CoopPlayer? GetLocalPlayer()
    {
        return LocalPlayerId >= 0 ? GetPlayer(LocalPlayerId) : null;
    }

    public void UpdatePlayerPosition(int networkId, float x, float y)
    {
        var player = GetPlayer(networkId);
        if (player != null)
        {
            player.MapPositionX = x;
            player.MapPositionY = y;
            OnPlayerUpdated?.Invoke(player);
        }
    }

    public void UpdatePlayerState(int networkId, PlayerState state)
    {
        var player = GetPlayer(networkId);
        if (player != null)
        {
            player.State = state;
            OnPlayerUpdated?.Invoke(player);
        }
    }

    private void DespawnPlayerParty(CoopPlayer player)
    {
        // Find and remove the party from the campaign
        // This happens when a player disconnects
        if (Campaign.Current != null && player.PartyId != null)
        {
            var party = Campaign.Current.MobileParties
                .FirstOrDefault(p => p.StringId == player.PartyId);

            if (party != null)
            {
                // Remove party from map - implementation depends on Bannerlord API
                BannerBrosModule.LogMessage($"Despawning party for disconnected player {player.Name}");
            }
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _players.Clear();
        }
        LocalPlayerId = -1;
    }
}

/// <summary>
/// Represents a connected player in the co-op session.
/// </summary>
public class CoopPlayer
{
    public int NetworkId { get; set; }
    public string Name { get; set; } = "";
    public string? HeroId { get; set; }
    public string? PartyId { get; set; }
    public string? ClanId { get; set; }
    public string? KingdomId { get; set; }

    public float MapPositionX { get; set; }
    public float MapPositionY { get; set; }

    public PlayerState State { get; set; } = PlayerState.OnMap;
    public string? CurrentBattleId { get; set; }

    public bool IsHost { get; set; }
    public int Ping { get; set; }
}

/// <summary>
/// Current state of a player.
/// </summary>
public enum PlayerState
{
    OnMap,
    InBattle,
    InDialogue,
    InTrade,
    InMenu,
    InTown,
    InVillage,
    InCastle
}
