using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using BannerBros.Network;

namespace BannerBros.Core;

/// <summary>
/// Handles the ability for players to join ongoing battles.
/// When a player moves near an active battle, they can choose to join either side.
/// </summary>
public class BattleJoinBehavior : CampaignBehaviorBase
{
    private const float BATTLE_JOIN_RADIUS = 2.0f;
    private BattleInstance? _nearbyBattle;
    private float _promptCooldown;

    public override void RegisterEvents()
    {
        CampaignEvents.TickEvent.AddNonSerializedListener(this, OnTick);
    }

    public override void SyncData(IDataStore dataStore)
    {
        // No persistent data needed
    }

    private void OnTick(float dt)
    {
        var module = BannerBrosModule.Instance;
        if (module?.IsConnected != true) return;

        _promptCooldown = Math.Max(0, _promptCooldown - dt);

        var localPlayer = module.PlayerManager.GetLocalPlayer();
        if (localPlayer == null || localPlayer.State != PlayerState.OnMap) return;

        // Check if near any active battle
        var nearbyBattle = module.WorldStateManager.FindBattleAtPosition(
            localPlayer.MapPositionX,
            localPlayer.MapPositionY,
            BATTLE_JOIN_RADIUS
        );

        if (nearbyBattle != _nearbyBattle)
        {
            _nearbyBattle = nearbyBattle;

            if (_nearbyBattle != null && _promptCooldown <= 0)
            {
                _promptCooldown = 5.0f; // Don't spam the message
                ShowBattleJoinPrompt(_nearbyBattle);
            }
        }
    }

    private void ShowBattleJoinPrompt(BattleInstance battle)
    {
        var module = BannerBrosModule.Instance;
        if (module == null) return;

        // Get initiator player info
        var initiator = module.PlayerManager.GetPlayer(battle.InitiatorPlayerId);
        var initiatorName = initiator?.Name ?? "Unknown";

        BannerBrosModule.LogMessage($"Battle nearby! {initiatorName} is fighting. Press [J] to join.");
    }

    public void RequestJoinBattle(BattleSide side)
    {
        var module = BannerBrosModule.Instance;
        if (module == null || _nearbyBattle == null) return;

        var localPlayer = module.PlayerManager.GetLocalPlayer();
        if (localPlayer == null) return;

        // Update local state
        if (module.WorldStateManager.JoinBattle(_nearbyBattle.BattleId, localPlayer.NetworkId, side))
        {
            localPlayer.CurrentBattleId = _nearbyBattle.BattleId;

            // Send battle join event to network
            SendBattleJoinPacket(_nearbyBattle, side);

            BannerBrosModule.LogMessage($"Joining battle as {side}");
            TransitionToBattle(_nearbyBattle, side);
        }
    }

    private void SendBattleJoinPacket(BattleInstance battle, BattleSide side)
    {
        var module = BannerBrosModule.Instance;
        var networkManager = NetworkManager.Instance;
        if (module == null || networkManager == null) return;

        var localPlayer = module.PlayerManager.GetLocalPlayer();
        if (localPlayer == null) return;

        var packet = new BattleEventPacket
        {
            EventType = (int)BattleEventType.PlayerJoined,
            BattleId = battle.BattleId,
            PlayerId = localPlayer.NetworkId,
            MapPosition = battle.MapPosition,
            Side = (int)side
        };

        if (networkManager.IsHost)
        {
            networkManager.Send(packet);
        }
        else
        {
            networkManager.SendToServer(packet);
        }
    }

    /// <summary>
    /// Called when a local player starts a new battle (e.g., engaging an enemy).
    /// </summary>
    public void OnBattleStarted(string mapPosition, BattleSide playerSide, string? enemyPartyId = null)
    {
        var module = BannerBrosModule.Instance;
        var networkManager = NetworkManager.Instance;
        if (module == null || networkManager == null) return;

        var localPlayer = module.PlayerManager.GetLocalPlayer();
        if (localPlayer == null) return;

        // Create battle in world state
        var battleId = module.WorldStateManager.CreateBattle(
            localPlayer.NetworkId,
            mapPosition,
            playerSide
        );

        localPlayer.CurrentBattleId = battleId;

        // Send battle started event
        var packet = new BattleEventPacket
        {
            EventType = (int)BattleEventType.Started,
            BattleId = battleId,
            PlayerId = localPlayer.NetworkId,
            MapPosition = mapPosition,
            Side = (int)playerSide,
            EnemyPartyId = enemyPartyId
        };

        if (networkManager.IsHost)
        {
            networkManager.Send(packet);
        }
        else
        {
            networkManager.SendToServer(packet);
        }

        BannerBrosModule.LogMessage("Battle started - other players can join you!");
    }

    /// <summary>
    /// Called when the local player's battle ends.
    /// </summary>
    public void OnBattleEnded(bool victory)
    {
        var module = BannerBrosModule.Instance;
        var networkManager = NetworkManager.Instance;
        if (module == null || networkManager == null) return;

        var localPlayer = module.PlayerManager.GetLocalPlayer();
        if (localPlayer == null || localPlayer.CurrentBattleId == null) return;

        var battleId = localPlayer.CurrentBattleId;

        // End battle in world state
        module.WorldStateManager.EndBattle(battleId);
        localPlayer.CurrentBattleId = null;

        // Send battle ended event
        var packet = new BattleEventPacket
        {
            EventType = (int)(victory ? BattleEventType.VictoryAttacker : BattleEventType.VictoryDefender),
            BattleId = battleId,
            PlayerId = localPlayer.NetworkId
        };

        if (networkManager.IsHost)
        {
            networkManager.Send(packet);
        }
        else
        {
            networkManager.SendToServer(packet);
        }
    }

    private void TransitionToBattle(BattleInstance battle, BattleSide side)
    {
        // This would trigger the actual battle join
        // The implementation depends on how Bannerlord handles
        // adding parties to ongoing battles

        // Key steps:
        // 1. Find the actual MapEvent for this battle
        // 2. Add local player's party to appropriate side
        // 3. Transition player into battle mission
    }

    public bool CanJoinBattle() => _nearbyBattle != null;

    public BattleInstance? GetNearbyBattle() => _nearbyBattle;
}
