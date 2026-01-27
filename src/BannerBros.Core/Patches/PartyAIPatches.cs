using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Conversation;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Party.PartyComponents;

namespace BannerBros.Core.Patches;

/// <summary>
/// Patches to protect players during dialogue, trade, and other interactions.
/// Prevents AI parties from attacking protected player parties.
/// </summary>
public static class PartyAIPatches
{
    /// <summary>
    /// Checks if a party belongs to a protected co-op player.
    /// </summary>
    public static bool IsPartyProtected(MobileParty? party)
    {
        if (party == null) return false;

        var module = BannerBrosModule.Instance;
        if (module?.IsConnected != true) return false;

        try
        {
            // Check if this party belongs to any connected player who is protected
            // ToList() to avoid collection modified exception
            foreach (var player in module.PlayerManager.Players.Values.ToList())
            {
                if (player.PartyId == party.StringId)
                {
                    return IsPlayerProtected(player);
                }
            }
        }
        catch
        {
            // Ignore errors during iteration - defensive for Harmony patches
        }

        return false;
    }

    /// <summary>
    /// Determines if a player should be protected based on their state and config.
    /// </summary>
    private static bool IsPlayerProtected(CoopPlayer player)
    {
        var config = BannerBrosModule.Instance?.Config;
        if (config == null) return false;

        return player.State switch
        {
            PlayerState.InDialogue => config.ProtectDuringDialogue,
            PlayerState.InTrade => config.ProtectDuringTrade,
            PlayerState.InTown => true,
            PlayerState.InVillage => true,
            PlayerState.InCastle => true,
            PlayerState.InMenu => config.ProtectDuringCharacterMenu,
            _ => false
        };
    }

    /// <summary>
    /// Patch for MobileParty.Ai.SetMoveEngageParty.
    /// Prevents AI from engaging protected player parties.
    /// </summary>
    [HarmonyPatch(typeof(MobilePartyAi), "SetMoveEngageParty")]
    public static class SetMoveEngagePartyPatch
    {
        public static bool Prefix(MobileParty targetParty)
        {
            try
            {
                if (IsPartyProtected(targetParty))
                {
                    // Cancel the engage action - party is protected
                    return false;
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
    /// Patch for checking if a party can be attacked.
    /// Used in various AI decision points.
    /// </summary>
    [HarmonyPatch(typeof(MobileParty), "CanAttack")]
    public static class CanAttackPatch
    {
        public static void Postfix(MobileParty targetParty, ref bool __result)
        {
            try
            {
                if (__result && IsPartyProtected(targetParty))
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
    /// Patch for AI thinking about attacking parties.
    /// Filters out protected parties from potential targets.
    /// </summary>
    [HarmonyPatch(typeof(MobilePartyAi), "CalculateTargetScoreForParty")]
    public static class CalculateTargetScorePatch
    {
        public static void Postfix(MobileParty targetParty, ref float __result)
        {
            try
            {
                if (IsPartyProtected(targetParty))
                {
                    // Set score to very negative so AI won't consider attacking
                    __result = float.MinValue;
                }
            }
            catch
            {
                // Ignore errors
            }
        }
    }
}

/// <summary>
/// Patches for party encounter prevention.
/// Stops map encounters with protected parties.
/// </summary>
public static class EncounterPatches
{
    /// <summary>
    /// Patch to prevent initiating encounters with protected parties.
    /// </summary>
    [HarmonyPatch(typeof(MobileParty), "StartEncounterWithParty")]
    public static class StartEncounterPatch
    {
        public static bool Prefix(MobileParty __instance, MobileParty other)
        {
            try
            {
                // Check if either party is a protected player
                if (PartyAIPatches.IsPartyProtected(other))
                {
                    BannerBrosModule.LogMessage($"Cannot engage - player is protected");
                    return false;
                }

                // Also prevent the encounter if WE are the protected party
                // (shouldn't happen but defensive check)
                if (PartyAIPatches.IsPartyProtected(__instance))
                {
                    return false;
                }
            }
            catch
            {
                // Ignore errors
            }

            return true;
        }
    }
}

/// <summary>
/// Patches for player state detection.
/// Helps determine when a player is in a protected state.
/// </summary>
public static class PlayerStatePatches
{
    /// <summary>
    /// Patch to detect when player enters dialogue.
    /// </summary>
    [HarmonyPatch(typeof(ConversationManager), "BeginConversation")]
    public static class BeginConversationPatch
    {
        public static void Postfix()
        {
            try
            {
                UpdateLocalPlayerState(PlayerState.InDialogue);
            }
            catch
            {
                // Ignore errors
            }
        }
    }

    /// <summary>
    /// Patch to detect when player exits dialogue.
    /// </summary>
    [HarmonyPatch(typeof(ConversationManager), "EndConversation")]
    public static class EndConversationPatch
    {
        public static void Postfix()
        {
            try
            {
                UpdateLocalPlayerState(PlayerState.OnMap);
            }
            catch
            {
                // Ignore errors
            }
        }
    }

    private static void UpdateLocalPlayerState(PlayerState newState)
    {
        var module = BannerBrosModule.Instance;
        if (module?.IsConnected != true) return;

        var localPlayer = module.PlayerManager.GetLocalPlayer();
        if (localPlayer != null)
        {
            localPlayer.State = newState;
            // Network sync will happen in the behavior tick
        }
    }
}
