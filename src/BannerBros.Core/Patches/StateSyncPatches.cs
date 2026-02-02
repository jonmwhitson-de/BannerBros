using System;
using System.Collections.Generic;
using System.Diagnostics;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Encounters;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.Library;
using BannerBros.Core.StateSync;

namespace BannerBros.Core.Patches;

/// <summary>
/// Harmony patches to intercept state changes for synchronization.
/// These patches detect when game state changes on the server
/// and triggers broadcasts to connected clients.
/// </summary>
public static class StateSyncPatches
{
    private static bool _syncEnabled;
    private static readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private static long _lastSyncTicks;
    private const long MinSyncIntervalTicks = 1000000; // 100ms in ticks (10,000 ticks per ms)

    /// <summary>
    /// Enable or disable state synchronization broadcasts.
    /// Should only be enabled on the server/host.
    /// </summary>
    public static void SetSyncEnabled(bool enabled)
    {
        _syncEnabled = enabled;
        BannerBrosModule.LogMessage($"[StateSyncPatches] Sync {(enabled ? "enabled" : "disabled")}");
    }

    /// <summary>
    /// Patch for MobileParty position changes.
    /// Intercepts when a party's position is set.
    /// </summary>
    [HarmonyPatch(typeof(MobileParty), "Position2D", MethodType.Setter)]
    public static class MobilePartyPosition2DPatch
    {
        public static void Postfix(MobileParty __instance, Vec2 value)
        {
            if (!_syncEnabled) return;

            try
            {
                // Throttle sync to avoid flooding network
                var now = _stopwatch.ElapsedTicks;
                if (now - _lastSyncTicks < MinSyncIntervalTicks) return;
                _lastSyncTicks = now;

                var partyId = __instance.StringId;
                if (string.IsNullOrEmpty(partyId)) return;

                // Only sync if this is a player's party
                var stateSyncManager = StateSyncManager.Instance;
                if (stateSyncManager?.IsPartySynced(partyId) == true)
                {
                    stateSyncManager.OnServerPartyPositionChanged(partyId, value.x, value.y);
                }
            }
            catch (Exception ex)
            {
                // Silently ignore - don't break game for sync errors
                Console.WriteLine($"[StateSyncPatches] Position sync error: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Patch for MobileParty.SetMoveGoToPoint - intercepts movement commands.
    /// This is called when AI or player sets a movement destination.
    /// </summary>
    [HarmonyPatch(typeof(MobileParty), "SetMoveGoToPoint")]
    public static class SetMoveGoToPointPatch
    {
        public static void Postfix(MobileParty __instance, Vec2 point)
        {
            if (!_syncEnabled) return;

            try
            {
                var partyId = __instance.StringId;
                if (string.IsNullOrEmpty(partyId)) return;

                var stateSyncManager = StateSyncManager.Instance;
                if (stateSyncManager?.IsPartySynced(partyId) == true)
                {
                    // Sync the movement target so clients know where party is going
                    var pos = __instance.GetPosition2D;
                    stateSyncManager.OnServerPartyPositionChanged(partyId, pos.x, pos.y);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[StateSyncPatches] Movement sync error: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Alternative patch using Tick - syncs position every frame for active parties.
    /// This is more reliable than patching setters.
    /// </summary>
    [HarmonyPatch(typeof(MobileParty), "Tick")]
    public static class MobilePartyTickPatch
    {
        // Per-party tick counters to ensure ALL synced parties get broadcast regularly
        private static readonly Dictionary<string, int> _partyTickCounters = new();
        private static int _logCounter;
        private const int SyncEveryNTicks = 6; // ~10Hz at 60fps

        public static void Postfix(MobileParty __instance)
        {
            if (!_syncEnabled) return;

            try
            {
                var partyId = __instance.StringId;
                if (string.IsNullOrEmpty(partyId)) return;

                var stateSyncManager = StateSyncManager.Instance;
                if (stateSyncManager?.IsPartySynced(partyId) != true) return;

                // Get or initialize per-party counter
                if (!_partyTickCounters.TryGetValue(partyId, out int counter))
                {
                    counter = 0;
                }

                // Only sync every N ticks for THIS party
                counter++;
                if (counter < SyncEveryNTicks)
                {
                    _partyTickCounters[partyId] = counter;
                    return;
                }
                _partyTickCounters[partyId] = 0;

                var pos = __instance.GetPosition2D;
                stateSyncManager.OnServerPartyPositionChanged(partyId, pos.x, pos.y);

                // Log occasionally
                _logCounter++;
                if (_logCounter % 600 == 1) // Every ~60 seconds at 10Hz
                {
                    BannerBrosModule.LogMessage($"[StateSyncPatch] Broadcasting {partyId} at ({pos.x:F1}, {pos.y:F1})");
                }
            }
            catch
            {
                // Silently ignore
            }
        }

        /// <summary>
        /// Clears the per-party tick counters. Call when session ends.
        /// </summary>
        public static void ClearCounters()
        {
            _partyTickCounters.Clear();
        }
    }
}

/// <summary>
/// Patches to prevent clients from modifying authoritative state.
/// On clients, certain operations should be blocked because
/// the server is the authority.
/// </summary>
public static class ClientBlockingPatches
{
    private static bool _isClient;

    /// <summary>
    /// Sets whether we're in client mode (blocks local state changes).
    /// </summary>
    public static void SetClientMode(bool isClient)
    {
        _isClient = isClient;
        BannerBrosModule.LogMessage($"[ClientBlockingPatches] Client mode: {isClient}");
    }

    /// <summary>
    /// Block clients from starting their own party movements.
    /// Movement should come from server state updates.
    /// </summary>
    [HarmonyPatch(typeof(MobileParty), "SetMoveGoToPoint")]
    public static class BlockClientMovementPatch
    {
        public static bool Prefix(MobileParty __instance)
        {
            if (!_isClient) return true;

            try
            {
                var stateSyncManager = StateSyncManager.Instance;
                if (stateSyncManager == null) return true;

                // Block movement for synced parties (except our own command requests)
                var partyId = __instance.StringId;
                if (stateSyncManager.IsPartySynced(partyId))
                {
                    // Allow if this is the local player's party and they initiated the move
                    // (will be sent as a command to server)
                    var module = BannerBrosModule.Instance;
                    var localPlayer = module?.PlayerManager?.GetLocalPlayer();
                    if (localPlayer?.PartyId == partyId)
                    {
                        // This is our party - let the command handler deal with it
                        return true;
                    }

                    // Block - this is another player's party
                    return false;
                }
            }
            catch
            {
                // On error, allow the action
            }

            return true;
        }
    }

    /// <summary>
    /// Block clients from starting encounters directly.
    /// Encounters cause crashes because client's local NPC state differs from host.
    /// </summary>
    [HarmonyPatch(typeof(EncounterManager), "StartPartyEncounter")]
    public static class BlockClientEncounterPatch
    {
        public static bool Prefix(PartyBase attackerParty, PartyBase defenderParty)
        {
            if (!_isClient) return true;

            try
            {
                // Block all encounters initiated by the client
                // The host should handle encounters and sync the results
                BannerBrosModule.LogMessage($"[ClientBlocking] Blocked encounter: {attackerParty?.Name} vs {defenderParty?.Name}");
                BannerBrosModule.LogMessage("[ClientBlocking] Clients cannot start battles - host controls engagements");

                // Show message to user
                TaleWorlds.Library.InformationManager.DisplayMessage(
                    new TaleWorlds.Library.InformationMessage(
                        "Cannot engage in co-op - only host can initiate battles with NPCs",
                        TaleWorlds.Library.Colors.Red));

                return false; // Block the encounter
            }
            catch (Exception ex)
            {
                BannerBrosModule.LogMessage($"[ClientBlocking] Encounter patch error: {ex.Message}");
            }

            return false; // Block on error too - safer than crashing
        }
    }

    /// <summary>
    /// Block clients from creating PlayerEncounter directly.
    /// </summary>
    [HarmonyPatch(typeof(PlayerEncounter), "Start")]
    public static class BlockClientPlayerEncounterPatch
    {
        public static bool Prefix()
        {
            if (!_isClient) return true;

            try
            {
                BannerBrosModule.LogMessage("[ClientBlocking] Blocked PlayerEncounter.Start on client");

                TaleWorlds.Library.InformationManager.DisplayMessage(
                    new TaleWorlds.Library.InformationMessage(
                        "Cannot start encounter in co-op - host controls world interactions",
                        TaleWorlds.Library.Colors.Red));

                return false;
            }
            catch (Exception ex)
            {
                BannerBrosModule.LogMessage($"[ClientBlocking] PlayerEncounter patch error: {ex.Message}");
            }

            return false;
        }
    }
}
