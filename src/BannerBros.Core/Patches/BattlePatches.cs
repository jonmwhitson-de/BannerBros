using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.MapEvents;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.MountAndBlade;
using BannerBros.Network;

namespace BannerBros.Core.Patches;

/// <summary>
/// Patches for battle start, join, and end events.
/// Enables multiple players to participate in the same battle.
/// </summary>
public static class BattlePatches
{
    /// <summary>
    /// Patch to intercept when a battle (MapEvent) starts.
    /// Notifies other players so they can potentially join.
    /// </summary>
    [HarmonyPatch(typeof(MapEvent), MethodType.Constructor)]
    public static class MapEventCreatedPatch
    {
        public static void Postfix(MapEvent __instance)
        {
            var module = BannerBrosModule.Instance;
            if (module?.IsConnected != true) return;

            // Check if local player is involved in this battle
            var localPlayer = module.PlayerManager.GetLocalPlayer();
            if (localPlayer == null) return;

            // Get the parties involved
            var attackerParty = __instance.AttackerSide?.LeaderParty?.MobileParty;
            var defenderParty = __instance.DefenderSide?.LeaderParty?.MobileParty;

            bool isLocalPlayerBattle = false;
            BattleSide localSide = BattleSide.Attacker;

            if (attackerParty?.StringId == localPlayer.PartyId)
            {
                isLocalPlayerBattle = true;
                localSide = BattleSide.Attacker;
            }
            else if (defenderParty?.StringId == localPlayer.PartyId)
            {
                isLocalPlayerBattle = true;
                localSide = BattleSide.Defender;
            }

            if (isLocalPlayerBattle)
            {
                // Create battle instance and broadcast
                var position = __instance.Position;
                var battleId = module.WorldStateManager.CreateBattle(
                    localPlayer.NetworkId,
                    $"{position.X},{position.Y}",
                    localSide
                );

                localPlayer.State = PlayerState.InBattle;
                localPlayer.CurrentBattleId = battleId;

                // Send network notification
                var packet = new BattleEventPacket
                {
                    EventType = (int)BattleEventType.Started,
                    BattleId = battleId,
                    PlayerId = localPlayer.NetworkId,
                    MapPosition = $"{position.X},{position.Y}",
                    Side = (int)localSide
                };
                NetworkManager.Instance?.Send(packet);

                BannerBrosModule.LogMessage($"Battle started at {position}");
            }
        }
    }

    /// <summary>
    /// Patch to intercept when a battle ends.
    /// Notifies other players and cleans up battle state.
    /// </summary>
    [HarmonyPatch(typeof(MapEvent), "FinalizeEvent")]
    public static class MapEventEndedPatch
    {
        public static void Postfix(MapEvent __instance)
        {
            var module = BannerBrosModule.Instance;
            if (module?.IsConnected != true) return;

            var localPlayer = module.PlayerManager.GetLocalPlayer();
            if (localPlayer?.CurrentBattleId == null) return;

            // End the battle
            module.WorldStateManager.EndBattle(localPlayer.CurrentBattleId);

            // Determine outcome
            var eventType = __instance.BattleState switch
            {
                BattleState.AttackerVictory => BattleEventType.VictoryAttacker,
                BattleState.DefenderVictory => BattleEventType.VictoryDefender,
                _ => BattleEventType.Ended
            };

            var packet = new BattleEventPacket
            {
                EventType = (int)eventType,
                BattleId = localPlayer.CurrentBattleId,
                PlayerId = localPlayer.NetworkId
            };
            NetworkManager.Instance?.Send(packet);

            localPlayer.State = PlayerState.OnMap;
            localPlayer.CurrentBattleId = null;
        }
    }

    /// <summary>
    /// Patch to allow joining an existing battle.
    /// Called when a player party enters the battle radius.
    /// </summary>
    [HarmonyPatch(typeof(MapEvent), "AddPartyToEvent")]
    public static class AddPartyToEventPatch
    {
        public static void Postfix(MapEvent __instance, MobileParty mobileParty, BattleSideEnum side)
        {
            var module = BannerBrosModule.Instance;
            if (module?.IsConnected != true) return;

            // Check if this is a co-op player joining
            var localPlayer = module.PlayerManager.GetLocalPlayer();
            if (localPlayer == null || mobileParty.StringId != localPlayer.PartyId) return;

            // Find the battle instance
            var position = __instance.Position;
            var battle = module.WorldStateManager.FindBattleAtPosition(position.X, position.Y, 2.0f);

            if (battle != null && battle.InitiatorPlayerId != localPlayer.NetworkId)
            {
                // Joining someone else's battle
                var battleSide = side == BattleSideEnum.Attacker ? BattleSide.Attacker : BattleSide.Defender;
                module.WorldStateManager.JoinBattle(battle.BattleId, localPlayer.NetworkId, battleSide);

                localPlayer.State = PlayerState.InBattle;
                localPlayer.CurrentBattleId = battle.BattleId;

                var packet = new BattleEventPacket
                {
                    EventType = (int)BattleEventType.PlayerJoined,
                    BattleId = battle.BattleId,
                    PlayerId = localPlayer.NetworkId,
                    Side = (int)battleSide
                };
                NetworkManager.Instance?.Send(packet);

                BannerBrosModule.LogMessage($"Joined battle as {battleSide}");
            }
        }
    }
}

/// <summary>
/// Patches for mission (actual battle scene) synchronization.
/// Handles spawning players in the correct positions.
/// </summary>
public static class MissionPatches
{
    /// <summary>
    /// Patch for when a battle mission starts.
    /// Sets up multiplayer battle synchronization.
    /// </summary>
    [HarmonyPatch(typeof(Mission), "Initialize")]
    public static class MissionInitializePatch
    {
        public static void Postfix(Mission __instance)
        {
            var module = BannerBrosModule.Instance;
            if (module?.IsConnected != true) return;

            var localPlayer = module.PlayerManager.GetLocalPlayer();
            if (localPlayer?.State != PlayerState.InBattle) return;

            // Mission started while in co-op battle
            // Additional setup for multiplayer battle sync would go here
        }
    }

    /// <summary>
    /// Patch to handle player retreat from battle.
    /// </summary>
    [HarmonyPatch(typeof(Mission), "RetreatMission")]
    public static class RetreatMissionPatch
    {
        public static void Prefix(Mission __instance)
        {
            var module = BannerBrosModule.Instance;
            if (module?.IsConnected != true) return;

            var localPlayer = module.PlayerManager.GetLocalPlayer();
            if (localPlayer?.CurrentBattleId == null) return;

            // Notify network of retreat
            var packet = new BattleEventPacket
            {
                EventType = (int)BattleEventType.Retreat,
                BattleId = localPlayer.CurrentBattleId,
                PlayerId = localPlayer.NetworkId
            };
            NetworkManager.Instance?.Send(packet);

            // Leave the battle
            var battle = module.WorldStateManager.GetBattle(localPlayer.CurrentBattleId);
            if (battle != null)
            {
                battle.PlayerSides.Remove(localPlayer.NetworkId);
            }

            localPlayer.CurrentBattleId = null;
            localPlayer.State = PlayerState.OnMap;
        }
    }
}
