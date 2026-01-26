using TaleWorlds.CampaignSystem;

namespace BannerBros.Core;

/// <summary>
/// Controls campaign time to ensure synchronized real-time progression.
/// Prevents individual players from pausing or speeding up time.
/// </summary>
public class TimeControlBehavior : CampaignBehaviorBase
{
    private float _targetTimeMultiplier = 1.0f;
    private CampaignTimeControlMode _lastEnforcedMode;
    private bool _hasLoggedEnforcement;

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

        if (Campaign.Current == null) return;

        // Get configured time multiplier from server/config
        _targetTimeMultiplier = module.Config.TimeSpeedMultiplier;
        _targetTimeMultiplier = Math.Max(0.5f, Math.Min(4.0f, _targetTimeMultiplier));

        // Actively enforce time control every tick
        EnforceTimeControl();
    }

    private void EnforceTimeControl()
    {
        var campaign = Campaign.Current;
        if (campaign == null) return;

        // Get current time control mode
        var currentMode = campaign.TimeControlMode;

        // In co-op, we want time to always run (never paused)
        // StoppablePlay = normal speed, player can pause (but we'll override)
        // We force it to StoppablePlay if it's stopped
        if (currentMode == CampaignTimeControlMode.Stop)
        {
            campaign.TimeControlMode = CampaignTimeControlMode.StoppablePlay;

            if (!_hasLoggedEnforcement)
            {
                BannerBrosModule.LogMessage("Co-op: Time cannot be paused");
                _hasLoggedEnforcement = true;
            }
        }
        else
        {
            _hasLoggedEnforcement = false;
        }

        // Enforce speed multiplier
        // Note: SpeedUpMultiplier might not exist in all versions
        try
        {
            if (Math.Abs(campaign.SpeedUpMultiplier - _targetTimeMultiplier) > 0.01f)
            {
                campaign.SpeedUpMultiplier = _targetTimeMultiplier;
            }
        }
        catch
        {
            // SpeedUpMultiplier might not exist in this version
        }
    }

    public void SetTimeMultiplier(float multiplier)
    {
        _targetTimeMultiplier = Math.Max(0.5f, Math.Min(4.0f, multiplier));

        if (BannerBrosModule.Instance?.IsHost == true)
        {
            BannerBrosModule.Instance.Config.TimeSpeedMultiplier = _targetTimeMultiplier;
            BannerBrosModule.LogMessage($"Time speed set to {_targetTimeMultiplier}x");
        }
    }

    public float GetTimeMultiplier() => _targetTimeMultiplier;
}
