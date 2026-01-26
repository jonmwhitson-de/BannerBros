using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Library;

namespace BannerBros.Core.Patches;

/// <summary>
/// Patches to control time during co-op mode.
/// Time flows constantly at host's configured rate.
/// </summary>
public static class TimeControlPatches
{
    /// <summary>
    /// Gets whether we're in a co-op session.
    /// </summary>
    public static bool IsInCoopSession => BannerBrosModule.Instance?.IsConnected == true;

    /// <summary>
    /// Prevents the game from pausing by intercepting TimeControlMode setter.
    /// </summary>
    [HarmonyPatch(typeof(Campaign), nameof(Campaign.TimeControlMode), MethodType.Setter)]
    public static class PreventPausePatch
    {
        public static bool Prefix(ref CampaignTimeControlMode value)
        {
            if (!IsInCoopSession) return true;

            // Block any attempt to stop/pause time
            if (value == CampaignTimeControlMode.Stop)
            {
                BannerBrosModule.LogMessage("Co-op: Time cannot be paused");
                value = CampaignTimeControlMode.StoppablePlay;
            }

            return true;
        }
    }
}

/// <summary>
/// Patches for MapScreen time control UI.
/// Hides the time control buttons during co-op.
/// </summary>
public static class MapScreenTimePatches
{
    /// <summary>
    /// Patch to hide time control panel visibility during co-op.
    /// </summary>
    [HarmonyPatch("TaleWorlds.CampaignSystem.ViewModelCollection.Map.MapTimeControlVM", "IsTimeFlowPaused", MethodType.Getter)]
    public static class IsTimeFlowPausedPatch
    {
        public static void Postfix(ref bool __result)
        {
            // In co-op, always report time as not paused
            if (TimeControlPatches.IsInCoopSession)
            {
                __result = false;
            }
        }
    }

    /// <summary>
    /// Block play/pause button clicks during co-op.
    /// </summary>
    [HarmonyPatch("TaleWorlds.CampaignSystem.ViewModelCollection.Map.MapTimeControlVM", "ExecutePlayOrPause")]
    public static class ExecutePlayOrPausePatch
    {
        public static bool Prefix()
        {
            if (!TimeControlPatches.IsInCoopSession) return true;

            BannerBrosModule.LogMessage("Time is controlled by the host in co-op");
            return false;
        }
    }

    /// <summary>
    /// Block fast forward button clicks during co-op.
    /// </summary>
    [HarmonyPatch("TaleWorlds.CampaignSystem.ViewModelCollection.Map.MapTimeControlVM", "ExecuteFastForward")]
    public static class ExecuteFastForwardPatch
    {
        public static bool Prefix()
        {
            if (!TimeControlPatches.IsInCoopSession) return true;

            BannerBrosModule.LogMessage("Time speed is controlled by host");
            return false;
        }
    }
}
