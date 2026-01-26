using HarmonyLib;
using TaleWorlds.CampaignSystem;

namespace BannerBros.Core.Patches;

/// <summary>
/// Patches to control campaign time progression in co-op mode.
/// Prevents individual players from pausing or changing time speed.
/// </summary>
public static class TimeControlPatches
{
    /// <summary>
    /// Gets whether time control is currently enforced (co-op session active).
    /// </summary>
    public static bool IsTimeControlEnforced => BannerBrosModule.Instance?.IsConnected == true;

    /// <summary>
    /// The enforced time speed multiplier from server config.
    /// </summary>
    public static float EnforcedTimeMultiplier => BannerBrosModule.Instance?.Config.TimeSpeedMultiplier ?? 1.0f;

    /// <summary>
    /// Patch for Campaign.TimeControlMode setter.
    /// Prevents changing time control mode (pause/play/fast) during co-op.
    /// </summary>
    [HarmonyPatch(typeof(Campaign), nameof(Campaign.TimeControlMode), MethodType.Setter)]
    public static class TimeControlModePatch
    {
        public static bool Prefix(ref CampaignTimeControlMode value)
        {
            if (!IsTimeControlEnforced) return true;

            BannerBrosModule.LogMessage($"TimeControlMode change blocked: {value} -> StoppablePlay");

            // In co-op mode, always keep time running
            // Only allow Play mode, block Pause and Fast modes
            if (value == CampaignTimeControlMode.Stop ||
                value == CampaignTimeControlMode.UnstoppableFastForward ||
                value == CampaignTimeControlMode.UnstoppableFastForwardForPartyWaitTime ||
                value == CampaignTimeControlMode.FastForward ||
                value == CampaignTimeControlMode.StoppableFastForward)
            {
                // Override to normal play speed
                value = CampaignTimeControlMode.StoppablePlay;
            }

            return true;
        }
    }

    /// <summary>
    /// Patch for Campaign.SpeedUpMultiplier setter.
    /// Enforces the server-configured time multiplier.
    /// </summary>
    [HarmonyPatch(typeof(Campaign), nameof(Campaign.SpeedUpMultiplier), MethodType.Setter)]
    public static class SpeedUpMultiplierPatch
    {
        public static bool Prefix(ref float value)
        {
            if (!IsTimeControlEnforced) return true;

            // Override with server-configured multiplier
            value = EnforcedTimeMultiplier;
            return true;
        }
    }

    /// <summary>
    /// Patch to intercept requests to pause the game.
    /// Called when game tries to pause for various reasons.
    /// </summary>
    [HarmonyPatch(typeof(Campaign), "SetTimeControlModeLock")]
    public static class SetTimeControlModeLockPatch
    {
        public static bool Prefix(bool isLocked)
        {
            if (!IsTimeControlEnforced) return true;

            // Allow locking (which prevents changes) but not unlocking during battle
            // This ensures battles don't pause the campaign for other players
            var localPlayer = BannerBrosModule.Instance?.PlayerManager.GetLocalPlayer();
            if (localPlayer?.State == PlayerState.InBattle)
            {
                // Don't let battles lock/unlock campaign time
                return false;
            }

            return true;
        }
    }
}

/// <summary>
/// Additional patches for MapScreen time controls.
/// </summary>
public static class MapScreenTimePatches
{
    /// <summary>
    /// Patch to disable the time control UI buttons during co-op.
    /// Players shouldn't see pause/speed buttons they can't use.
    /// </summary>
    [HarmonyPatch("TaleWorlds.CampaignSystem.ViewModelCollection.Map.MapTimeControlVM", "ExecutePlayOrPause")]
    public static class ExecutePlayOrPausePatch
    {
        public static bool Prefix()
        {
            if (!TimeControlPatches.IsTimeControlEnforced) return true;

            // Show message explaining why time can't be controlled
            BannerBrosModule.LogMessage("Time is synchronized with co-op session");
            return false;
        }
    }

    [HarmonyPatch("TaleWorlds.CampaignSystem.ViewModelCollection.Map.MapTimeControlVM", "ExecuteFastForward")]
    public static class ExecuteFastForwardPatch
    {
        public static bool Prefix()
        {
            if (!TimeControlPatches.IsTimeControlEnforced) return true;

            BannerBrosModule.LogMessage("Time speed is controlled by host");
            return false;
        }
    }
}
