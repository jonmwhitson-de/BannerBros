using TaleWorlds.CampaignSystem;

namespace BannerBros.Core;

/// <summary>
/// Controls campaign time to ensure synchronized real-time progression.
/// In co-op mode, time flows constantly at the host's configured rate.
/// </summary>
public class TimeControlBehavior : CampaignBehaviorBase
{
    private float _hostTimeMultiplier = 1.0f;

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
        try
        {
            var module = BannerBrosModule.Instance;
            if (module?.IsConnected != true) return;

            if (Campaign.Current == null) return;

            // CLIENTS: Always freeze time - they receive state from host
            // Without this, client's own AI moves parties and fights against sync
            if (!module.IsHost)
            {
                FreezeClientTime();
                return;
            }

            // Get host's configured time multiplier
            _hostTimeMultiplier = module.Config.TimeSpeedMultiplier;
            _hostTimeMultiplier = Math.Max(0.5f, Math.Min(4.0f, _hostTimeMultiplier));

            // Force time to always run at constant rate (host only)
            if (module.IsHost)
            {
                ForceConstantTimeFlow();
            }
        }
        catch (Exception ex)
        {
            BannerBrosModule.LogMessage($"TimeControl OnTick error: {ex.Message}");
        }
    }

    /// <summary>
    /// Freezes campaign time on client to prevent world divergence.
    /// The client's world is just a "viewer" - all state comes from host.
    /// </summary>
    private void FreezeClientTime()
    {
        var campaign = Campaign.Current;
        if (campaign == null) return;

        // Stop time on client - their world is frozen
        try
        {
            campaign.TimeControlMode = CampaignTimeControlMode.Stop;
        }
        catch
        {
            // Fallback: try setting time speed to 0
            try
            {
                campaign.SpeedUpMultiplier = 0f;
            }
            catch { }
        }
    }

    private void ForceConstantTimeFlow()
    {
        var campaign = Campaign.Current;
        if (campaign == null) return;

        // Always keep time running - never allow pause
        if (campaign.TimeControlMode == CampaignTimeControlMode.Stop)
        {
            campaign.TimeControlMode = CampaignTimeControlMode.StoppablePlay;
        }

        // Force the host's time multiplier
        try
        {
            campaign.SpeedUpMultiplier = _hostTimeMultiplier;
        }
        catch
        {
            // Property might not exist in this version
        }
    }

    /// <summary>
    /// Called by host to change the time multiplier for all players.
    /// </summary>
    public void SetTimeMultiplier(float multiplier)
    {
        _hostTimeMultiplier = Math.Max(0.5f, Math.Min(4.0f, multiplier));

        var module = BannerBrosModule.Instance;
        if (module?.IsHost == true)
        {
            module.Config.TimeSpeedMultiplier = _hostTimeMultiplier;
            BannerBrosModule.LogMessage($"Time speed set to {_hostTimeMultiplier}x");
            // TODO: Broadcast to clients via network
        }
    }

    public float GetTimeMultiplier() => _hostTimeMultiplier;
}
