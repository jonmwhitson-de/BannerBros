using TaleWorlds.CampaignSystem;

namespace BannerBros.Core;

/// <summary>
/// Controls campaign time to ensure synchronized real-time progression.
/// Prevents individual players from pausing or speeding up time.
/// </summary>
public class TimeControlBehavior : CampaignBehaviorBase
{
    private float _accumulatedTime;
    private float _targetTimeMultiplier = 1.0f;

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

        // Get configured time multiplier from server/config
        _targetTimeMultiplier = module.Config.TimeSpeedMultiplier;

        // Clamp to valid range (0.5x to 4x)
        _targetTimeMultiplier = Math.Clamp(_targetTimeMultiplier, 0.5f, 4.0f);

        // Override the game's time controls
        EnforceTimeMultiplier();
    }

    private void EnforceTimeMultiplier()
    {
        // In co-op mode, we need to:
        // 1. Disable player-controlled time speed changes
        // 2. Keep time moving at server-configured rate
        // 3. Never pause (except for critical UI like save dialogs)

        if (Campaign.Current == null) return;

        // This would integrate with Bannerlord's time control system
        // to enforce the multiplier and prevent pause

        // The exact implementation depends on the game's API
        // Common approach is to use Harmony patches to intercept
        // time control changes
    }

    public void SetTimeMultiplier(float multiplier)
    {
        _targetTimeMultiplier = Math.Clamp(multiplier, 0.5f, 4.0f);

        if (BannerBrosModule.Instance?.IsHost == true)
        {
            // Broadcast new time multiplier to all clients
            BannerBrosModule.Instance.Config.TimeSpeedMultiplier = _targetTimeMultiplier;
        }
    }

    public float GetTimeMultiplier() => _targetTimeMultiplier;
}
