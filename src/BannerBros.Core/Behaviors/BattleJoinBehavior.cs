using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;

namespace BannerBros.Core;

/// <summary>
/// Handles the ability for players to join ongoing battles.
/// When a player moves near an active battle, they can choose to join either side.
/// </summary>
public class BattleJoinBehavior : CampaignBehaviorBase
{
    private const float BATTLE_JOIN_RADIUS = 2.0f;
    private BattleInstance? _nearbyBattle;

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

            if (_nearbyBattle != null)
            {
                // Show UI prompt to join battle
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

        // Show inquiry to player
        // In actual implementation, this would use Bannerlord's inquiry system
        BannerBrosModule.LogMessage($"Battle nearby! {initiatorName} is fighting. Press [J] to join.");
    }

    public void RequestJoinBattle(BattleSide side)
    {
        var module = BannerBrosModule.Instance;
        if (module == null || _nearbyBattle == null) return;

        var localPlayer = module.PlayerManager.GetLocalPlayer();
        if (localPlayer == null) return;

        // Send join request to server/host
        if (module.WorldStateManager.JoinBattle(_nearbyBattle.BattleId, localPlayer.NetworkId, side))
        {
            BannerBrosModule.LogMessage($"Joining battle as {side}");
            TransitionToBattle(_nearbyBattle, side);
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
