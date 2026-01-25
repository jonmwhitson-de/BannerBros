using TaleWorlds.CampaignSystem;

namespace BannerBros.Core;

/// <summary>
/// Manages world state synchronization between players.
/// </summary>
public class WorldStateManager
{
    private readonly Dictionary<string, BattleInstance> _activeBattles = new();
    private readonly object _lock = new();

    public IReadOnlyDictionary<string, BattleInstance> ActiveBattles => _activeBattles;

    public event Action<BattleInstance>? OnBattleStarted;
    public event Action<BattleInstance>? OnBattleEnded;
    public event Action<BattleInstance, int>? OnPlayerJoinedBattle;

    public CampaignTime ServerTime { get; private set; }
    public float TimeMultiplier { get; set; } = 1.0f;

    public string CreateBattle(int initiatorPlayerId, string mapPosition, BattleSide initiatorSide)
    {
        var battleId = Guid.NewGuid().ToString("N").Substring(0, 8);
        var battle = new BattleInstance
        {
            BattleId = battleId,
            InitiatorPlayerId = initiatorPlayerId,
            MapPosition = mapPosition,
            StartTime = DateTime.UtcNow,
            IsActive = true
        };

        battle.PlayerSides[initiatorPlayerId] = initiatorSide;

        lock (_lock)
        {
            _activeBattles[battleId] = battle;
        }

        OnBattleStarted?.Invoke(battle);
        return battleId;
    }

    public bool JoinBattle(string battleId, int playerId, BattleSide side)
    {
        lock (_lock)
        {
            if (_activeBattles.TryGetValue(battleId, out var battle) && battle.IsActive)
            {
                battle.PlayerSides[playerId] = side;
                OnPlayerJoinedBattle?.Invoke(battle, playerId);
                return true;
            }
        }
        return false;
    }

    public void EndBattle(string battleId)
    {
        BattleInstance? battle;
        lock (_lock)
        {
            if (_activeBattles.TryGetValue(battleId, out battle))
            {
                battle.IsActive = false;
                battle.EndTime = DateTime.UtcNow;
                _activeBattles.Remove(battleId);
            }
        }

        if (battle != null)
        {
            OnBattleEnded?.Invoke(battle);
        }
    }

    public BattleInstance? GetBattle(string battleId)
    {
        lock (_lock)
        {
            return _activeBattles.TryGetValue(battleId, out var battle) ? battle : null;
        }
    }

    public BattleInstance? FindBattleAtPosition(float x, float y, float radius = 1.0f)
    {
        lock (_lock)
        {
            // Find any active battle near the given map position
            // This is used when a player wants to join an ongoing battle
            foreach (var battle in _activeBattles.Values)
            {
                if (battle.IsActive && IsPositionNear(battle.MapPosition, x, y, radius))
                {
                    return battle;
                }
            }
        }
        return null;
    }

    private bool IsPositionNear(string mapPosition, float x, float y, float radius)
    {
        // Parse map position and check distance
        // Format: "x,y"
        var parts = mapPosition.Split(',');
        if (parts.Length == 2 &&
            float.TryParse(parts[0], out var bx) &&
            float.TryParse(parts[1], out var by))
        {
            var dx = bx - x;
            var dy = by - y;
            return (dx * dx + dy * dy) <= (radius * radius);
        }
        return false;
    }

    public void UpdateServerTime(CampaignTime time)
    {
        ServerTime = time;
    }
}

/// <summary>
/// Represents an active battle instance that players can join.
/// </summary>
public class BattleInstance
{
    public string BattleId { get; set; } = "";
    public int InitiatorPlayerId { get; set; }
    public string MapPosition { get; set; } = "";
    public DateTime StartTime { get; set; }
    public DateTime? EndTime { get; set; }
    public bool IsActive { get; set; }

    /// <summary>
    /// Maps player network IDs to their chosen battle side.
    /// </summary>
    public Dictionary<int, BattleSide> PlayerSides { get; } = new();

    public IEnumerable<int> GetPlayersOnSide(BattleSide side)
    {
        return PlayerSides.Where(kvp => kvp.Value == side).Select(kvp => kvp.Key);
    }
}

public enum BattleSide
{
    Attacker,
    Defender
}
