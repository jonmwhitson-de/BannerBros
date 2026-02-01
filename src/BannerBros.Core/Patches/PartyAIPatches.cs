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
    /// Checks if a party belongs to a protected co-op player or is a shadow party.
    /// Shadow parties are always protected to prevent encounter crashes.
    /// </summary>
    public static bool IsPartyProtected(MobileParty? party)
    {
        if (party == null) return false;

        var module = BannerBrosModule.Instance;
        if (module?.IsConnected != true) return false;

        try
        {
            var partyId = party.StringId;

            // Shadow parties are ALWAYS protected to prevent encounter crashes
            // These are client-side representations of remote players
            if (IsShadowParty(partyId))
            {
                return true;
            }

            // Check if this party belongs to any connected player who is protected
            // ToList() to avoid collection modified exception
            foreach (var player in module.PlayerManager.Players.Values.ToList())
            {
                if (player.PartyId == partyId)
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
    /// Checks if a party ID indicates it's a shadow party (remote player representation).
    /// Shadow parties have minimal components and will crash if encounters are triggered.
    /// </summary>
    public static bool IsShadowParty(string? partyId)
    {
        if (string.IsNullOrEmpty(partyId)) return false;

        // remote_player_party - host's party visible on client
        if (partyId.StartsWith("remote_player_")) return true;

        // coop_party_* - client representation parties on host
        if (partyId.StartsWith("coop_party_")) return true;

        // shadow_* - generic shadow party prefix
        if (partyId.StartsWith("shadow_")) return true;

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
/// Stops map encounters with protected parties and shadow parties.
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
                // Check if either party is a protected player or shadow party
                if (PartyAIPatches.IsPartyProtected(other))
                {
                    return false;
                }

                // Also prevent the encounter if WE are the protected party
                if (PartyAIPatches.IsPartyProtected(__instance))
                {
                    return false;
                }

                // Additional safety: block if either party is a shadow party by ID
                if (PartyAIPatches.IsShadowParty(__instance?.StringId) ||
                    PartyAIPatches.IsShadowParty(other?.StringId))
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

    // Note: Removed CheckPartyNeedsUpdatePatch and MobilePartyAiHourlyTickPatch
    // as they may target non-existent methods and cause Harmony issues.
    // Core encounter protection is in StartEncounterPatch above.
}

// Note: Removed ShadowPartyProtectionPatches (IsActivePatch, PartyComponentPatch) as they
// may interfere with normal party operations. The core protection is in EncounterPatches.

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
