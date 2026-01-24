using TaleWorlds.CampaignSystem;

namespace BannerBros.Core;

/// <summary>
/// Main campaign behavior for BannerBros co-op functionality.
/// </summary>
public class BannerBrosCampaignBehavior : CampaignBehaviorBase
{
    public override void RegisterEvents()
    {
        CampaignEvents.OnSessionLaunchedEvent.AddNonSerializedListener(this, OnSessionLaunched);
        CampaignEvents.TickEvent.AddNonSerializedListener(this, OnTick);
        CampaignEvents.HourlyTickEvent.AddNonSerializedListener(this, OnHourlyTick);
        CampaignEvents.DailyTickEvent.AddNonSerializedListener(this, OnDailyTick);
    }

    public override void SyncData(IDataStore dataStore)
    {
        // Sync co-op specific data with save game
    }

    private void OnSessionLaunched(CampaignGameStarter starter)
    {
        BannerBrosModule.LogMessage("Campaign session started");
    }

    private void OnTick(float dt)
    {
        // Called every frame during campaign
        // Sync player positions and states
        SyncPlayerStates(dt);
    }

    private void OnHourlyTick()
    {
        // Sync world state every in-game hour
        if (BannerBrosModule.Instance?.IsConnected == true)
        {
            SyncWorldState();
        }
    }

    private void OnDailyTick()
    {
        // Daily sync for less frequent data
    }

    private void SyncPlayerStates(float dt)
    {
        var module = BannerBrosModule.Instance;
        if (module?.IsConnected != true) return;

        // Get local player's current state
        var localPlayer = module.PlayerManager.GetLocalPlayer();
        if (localPlayer == null) return;

        // Update position from main party
        var mainParty = Campaign.Current?.MainParty;
        if (mainParty != null)
        {
            var pos = mainParty.Position2D;
            localPlayer.MapPositionX = pos.X;
            localPlayer.MapPositionY = pos.Y;

            // Send position update to other players
            // NetworkManager handles throttling
        }

        // Check and update player state
        UpdateLocalPlayerState(localPlayer);
    }

    private void UpdateLocalPlayerState(CoopPlayer player)
    {
        // Determine current state based on game context
        var newState = DeterminePlayerState();

        if (player.State != newState)
        {
            player.State = newState;
            // Notify network of state change
        }
    }

    private PlayerState DeterminePlayerState()
    {
        // Check various game states to determine player's current state
        // This integrates with Bannerlord's state system

        if (Campaign.Current == null)
            return PlayerState.OnMap;

        // Check if in battle
        if (Mission.Current != null)
            return PlayerState.InBattle;

        // Check if in dialogue - would check ConversationManager
        // Check if in trade - would check trade screen state
        // Check if in town/village/castle - would check settlement state

        return PlayerState.OnMap;
    }

    private void SyncWorldState()
    {
        var module = BannerBrosModule.Instance;
        if (module?.IsHost != true) return;

        // Host broadcasts world state updates
        var worldState = module.WorldStateManager;
        worldState.UpdateServerTime(CampaignTime.Now);

        // Broadcast to all clients
    }
}
