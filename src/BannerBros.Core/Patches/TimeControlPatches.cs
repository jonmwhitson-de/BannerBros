using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Library;

namespace BannerBros.Core.Patches;

/// <summary>
/// Patches to control time during co-op mode.
/// Time flows constantly at host's configured rate.
/// CRITICAL: Prevents any form of pause to keep worlds in sync.
/// </summary>
public static class TimeControlPatches
{
    /// <summary>
    /// Gets whether we're in a co-op session.
    /// </summary>
    public static bool IsInCoopSession => BannerBrosModule.Instance?.IsConnected == true;

    /// <summary>
    /// Temporarily allow pausing (e.g., during battles or waiting for client to load).
    /// </summary>
    public static bool AllowPauseOverride { get; set; } = false;

    /// <summary>
    /// True when host is waiting for a client to finish loading the save file.
    /// During this time, pause IS allowed to keep worlds synced.
    /// </summary>
    public static bool IsWaitingForClientLoad { get; set; } = false;

    private static bool _hasLoggedPauseBlock = false;

    /// <summary>
    /// Prevents the game from pausing by intercepting TimeControlMode setter.
    /// </summary>
    [HarmonyPatch(typeof(Campaign), nameof(Campaign.TimeControlMode), MethodType.Setter)]
    public static class PreventPausePatch
    {
        public static bool Prefix(ref CampaignTimeControlMode value)
        {
            try
            {
                if (!IsInCoopSession) return true;
                if (AllowPauseOverride) return true;

                // Allow pause when waiting for client to load - this keeps worlds synced
                if (IsWaitingForClientLoad) return true;

                // Block any attempt to stop/pause time
                if (value == CampaignTimeControlMode.Stop ||
                    value == CampaignTimeControlMode.StoppableFastForwardForPartyWaitTime ||
                    value == CampaignTimeControlMode.UnstoppableFastForwardForPartyWaitTime)
                {
                    if (!_hasLoggedPauseBlock)
                    {
                        BannerBrosModule.LogMessage("[Co-op] Time pause blocked - worlds must stay synced");
                        _hasLoggedPauseBlock = true;
                    }
                    value = CampaignTimeControlMode.StoppablePlay;
                }
            }
            catch
            {
                // Ignore errors in time control patch
            }

            return true;
        }
    }

    /// <summary>
    /// Intercepts SetTimeSpeed to prevent speed 0 (pause).
    /// </summary>
    [HarmonyPatch(typeof(Campaign), nameof(Campaign.SetTimeSpeed))]
    public static class PreventSetTimeSpeedZeroPatch
    {
        public static bool Prefix(ref int value)
        {
            try
            {
                if (!IsInCoopSession) return true;
                if (AllowPauseOverride) return true;

                // Allow pause when waiting for client to load
                if (IsWaitingForClientLoad) return true;

                // Block speed 0 (pause)
                if (value == 0)
                {
                    if (!_hasLoggedPauseBlock)
                    {
                        BannerBrosModule.LogMessage("[Co-op] Time speed 0 blocked - using speed 1");
                        _hasLoggedPauseBlock = true;
                    }
                    value = 1;
                }
            }
            catch
            {
                // Ignore errors
            }

            return true;
        }
    }

    /// <summary>
    /// Resets the log flag periodically so we don't spam but still inform occasionally.
    /// </summary>
    public static void ResetLogFlag()
    {
        _hasLoggedPauseBlock = false;
    }
}

/// <summary>
/// Patches for game state changes that typically cause pausing.
/// Intercepts various screens/menus that auto-pause.
/// </summary>
public static class GameStatePausePatches
{
    /// <summary>
    /// Patch CampaignState to prevent auto-pause when certain screens open.
    /// </summary>
    [HarmonyPatch("TaleWorlds.CampaignSystem.GameState.MapState", "OnMapScreenActivate")]
    public static class MapStateActivatePatch
    {
        public static void Postfix()
        {
            try
            {
                if (!TimeControlPatches.IsInCoopSession) return;

                // Force time to play after map screen activates
                var campaign = Campaign.Current;
                if (campaign != null)
                {
                    campaign.SetTimeSpeed(1);
                }
            }
            catch { }
        }
    }

    /// <summary>
    /// Prevent pause when Encyclopedia or other screens open.
    /// </summary>
    [HarmonyPatch("TaleWorlds.CampaignSystem.GameState.MapState", "OnEncyclopediaScreenActivate")]
    public static class EncyclopediaActivatePatch
    {
        public static void Postfix()
        {
            try
            {
                if (!TimeControlPatches.IsInCoopSession) return;

                // Encyclopedia shouldn't pause in co-op
                var campaign = Campaign.Current;
                if (campaign != null)
                {
                    campaign.SetTimeSpeed(1);
                }
            }
            catch { }
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
            try
            {
                // In co-op, always report time as not paused
                if (TimeControlPatches.IsInCoopSession)
                {
                    __result = false;
                }
            }
            catch
            {
                // Ignore errors
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
            try
            {
                if (!TimeControlPatches.IsInCoopSession) return true;

                BannerBrosModule.LogMessage("Time is controlled by the host in co-op");
                return false;
            }
            catch
            {
                return true;
            }
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
            try
            {
                if (!TimeControlPatches.IsInCoopSession) return true;

                BannerBrosModule.LogMessage("Time speed is controlled by host");
                return false;
            }
            catch
            {
                return true;
            }
        }
    }
}
