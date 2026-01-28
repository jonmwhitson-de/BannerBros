using System;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.Library;
using BannerBros.Network;

namespace BannerBros.Core.Patches;

/// <summary>
/// Patches to intercept map clicks in spectator mode.
/// Client clicks are converted to commands sent to host instead of local movement.
/// </summary>
public static class MapClickPatches
{
    /// <summary>
    /// Checks if we're in spectator mode and should intercept movement.
    /// </summary>
    public static bool ShouldInterceptMovement()
    {
        var module = BannerBrosModule.Instance;
        if (module == null) return false;
        if (!module.IsConnected) return false;
        if (module.IsHost) return false;

        // In spectator mode, intercept all movement
        return module.SpectatorModeManager?.IsSpectatorMode == true;
    }

    /// <summary>
    /// Gets the MobileParty from MobilePartyAi using reflection for API compatibility.
    /// </summary>
    private static MobileParty? GetPartyFromAi(MobilePartyAi ai)
    {
        try
        {
            // Try "MobileParty" property
            var prop = ai.GetType().GetProperty("MobileParty");
            if (prop != null)
                return prop.GetValue(ai) as MobileParty;

            // Try "Party" property
            prop = ai.GetType().GetProperty("Party");
            if (prop != null)
                return prop.GetValue(ai) as MobileParty;

            // Try "_mobileParty" field
            var field = ai.GetType().GetField("_mobileParty",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (field != null)
                return field.GetValue(ai) as MobileParty;
        }
        catch { }

        return null;
    }

    /// <summary>
    /// Patch for MobileParty.SetMoveGoToPoint - intercepts movement commands.
    /// When client clicks on map, this converts it to a network command.
    /// </summary>
    [HarmonyPatch(typeof(MobilePartyAi), "SetMoveGoToPoint")]
    public static class SetMoveGoToPointPatch
    {
        public static bool Prefix(MobilePartyAi __instance, Vec2 point)
        {
            try
            {
                // Log all calls in spectator mode for debugging
                var module = BannerBrosModule.Instance;
                var spectatorMgr = module?.SpectatorModeManager;

                if (spectatorMgr?.IsSpectatorMode == true)
                {
                    var party = GetPartyFromAi(__instance);
                    BannerBrosModule.LogMessage($"[MapClick] SetMoveGoToPoint called - Party: {party?.StringId ?? "null"}, AssignedId: {spectatorMgr.AssignedPartyId ?? "null"}");
                }

                if (!ShouldInterceptMovement()) return true;

                // Check if this is our assigned party
                var partyToCheck = GetPartyFromAi(__instance);

                if (spectatorMgr?.AssignedPartyId == partyToCheck?.StringId)
                {
                    // This is our assigned party - send command to host instead
                    BannerBrosModule.LogMessage($"[MapClick] INTERCEPTED move to ({point.x:F1}, {point.y:F1})");
                    spectatorMgr.SendMoveCommand(point.x, point.y);
                    return false; // Don't execute local movement
                }
                else
                {
                    BannerBrosModule.LogMessage($"[MapClick] NOT intercepting - party mismatch");
                }
            }
            catch (Exception ex)
            {
                BannerBrosModule.LogMessage($"[MapClick] Error in patch: {ex.Message}");
            }

            return true;
        }
    }

    /// <summary>
    /// Alternative patch for SetMovePatrolAroundPoint.
    /// </summary>
    [HarmonyPatch(typeof(MobilePartyAi), "SetMovePatrolAroundPoint")]
    public static class SetMovePatrolAroundPointPatch
    {
        public static bool Prefix(MobilePartyAi __instance, Vec2 point)
        {
            try
            {
                if (!ShouldInterceptMovement()) return true;

                var party = GetPartyFromAi(__instance);
                var module = BannerBrosModule.Instance;
                var spectatorMgr = module?.SpectatorModeManager;

                if (spectatorMgr?.AssignedPartyId == party?.StringId)
                {
                    BannerBrosModule.LogMessage($"[MapClick] Intercepted patrol to ({point.x:F1}, {point.y:F1})");
                    spectatorMgr.SendMoveCommand(point.x, point.y);
                    return false;
                }
            }
            catch { }

            return true;
        }
    }

    /// <summary>
    /// Patch for MainParty movement specifically.
    /// The MainParty is hidden in spectator mode, but this catches any stray commands.
    /// </summary>
    [HarmonyPatch(typeof(MobileParty), "SetMoveModeHold")]
    public static class SetMoveModeHoldPatch
    {
        public static bool Prefix(MobileParty __instance)
        {
            try
            {
                if (!ShouldInterceptMovement()) return true;

                // If this is MainParty and we're in spectator mode, ignore the command
                if (__instance == MobileParty.MainParty)
                {
                    return false;
                }
            }
            catch { }

            return true;
        }
    }
}

/// <summary>
/// Patches to sync command results back to the client.
/// </summary>
public static class CommandResultPatches
{
    /// <summary>
    /// Handles the command result from the host.
    /// Updates local state based on success/failure.
    /// </summary>
    public static void HandleCommandResult(CommandResultPacket packet)
    {
        if (packet.Success)
        {
            BannerBrosModule.LogMessage($"[Command] {packet.CommandType} succeeded");
        }
        else
        {
            BannerBrosModule.LogMessage($"[Command] {packet.CommandType} failed: {packet.ErrorMessage}");
        }
    }
}
