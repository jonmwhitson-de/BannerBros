using System;
using System.Linq;
using BannerBros.Network;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Party.PartyComponents;
using TaleWorlds.CampaignSystem.Roster;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Library;
using LiteNetLib;

namespace BannerBros.Core;

/// <summary>
/// Handles commands from clients in single authoritative campaign architecture.
/// Executes commands on the host's campaign and sends results back to clients.
/// </summary>
public class CommandHandler
{
    private readonly PlayerManager _playerManager;

    public CommandHandler(PlayerManager playerManager)
    {
        _playerManager = playerManager;
    }

    public void Initialize()
    {
        var networkManager = NetworkManager.Instance;
        if (networkManager == null || !networkManager.IsHost) return;

        networkManager.Messages.OnMoveCommandReceived += HandleMoveCommand;
        networkManager.Messages.OnEnterSettlementCommandReceived += HandleEnterSettlementCommand;
        networkManager.Messages.OnLeaveSettlementCommandReceived += HandleLeaveSettlementCommand;
        networkManager.Messages.OnAttackCommandReceived += HandleAttackCommand;
        networkManager.Messages.OnFollowCommandReceived += HandleFollowCommand;
        networkManager.Messages.OnSpectatorReadyReceived += HandleSpectatorReady;
    }

    public void Cleanup()
    {
        var networkManager = NetworkManager.Instance;
        if (networkManager == null) return;

        networkManager.Messages.OnMoveCommandReceived -= HandleMoveCommand;
        networkManager.Messages.OnEnterSettlementCommandReceived -= HandleEnterSettlementCommand;
        networkManager.Messages.OnLeaveSettlementCommandReceived -= HandleLeaveSettlementCommand;
        networkManager.Messages.OnAttackCommandReceived -= HandleAttackCommand;
        networkManager.Messages.OnFollowCommandReceived -= HandleFollowCommand;
        networkManager.Messages.OnSpectatorReadyReceived -= HandleSpectatorReady;
    }

    /// <summary>
    /// Handles a spectator client signaling they're ready for party assignment.
    /// </summary>
    private void HandleSpectatorReady(SpectatorReadyPacket packet, int peerId)
    {
        BannerBrosModule.LogMessage($"Player {packet.PlayerName} (ID: {packet.PlayerId}) is ready as spectator");

        var player = _playerManager.GetPlayer(packet.PlayerId);
        if (player == null)
        {
            BannerBrosModule.LogMessage($"Player {packet.PlayerId} not found!");
            return;
        }

        // Create a party for this player
        try
        {
            var partyResult = CreatePartyForClient(player);
            if (partyResult.Success)
            {
                // Update player info
                player.HeroId = partyResult.HeroId;
                player.ClanId = partyResult.ClanId;
                player.PartyId = partyResult.PartyId;
                player.MapPositionX = partyResult.SpawnX;
                player.MapPositionY = partyResult.SpawnY;
                player.State = PlayerState.OnMap;

                // Send assignment to client
                var assignmentPacket = new PartyAssignmentPacket
                {
                    PlayerId = packet.PlayerId,
                    PartyId = partyResult.PartyId ?? "",
                    HeroId = partyResult.HeroId ?? "",
                    ClanId = partyResult.ClanId ?? "",
                    MapX = partyResult.SpawnX,
                    MapY = partyResult.SpawnY
                };

                NetworkManager.Instance?.SendTo(peerId, assignmentPacket, DeliveryMethod.ReliableOrdered);

                BannerBrosModule.LogMessage($"Assigned party {partyResult.PartyId} to player {packet.PlayerName}");
            }
            else
            {
                BannerBrosModule.LogMessage($"Failed to create party for {packet.PlayerName}: {partyResult.ErrorMessage}");
            }
        }
        catch (Exception ex)
        {
            BannerBrosModule.LogMessage($"Error creating party for spectator: {ex.Message}");
        }
    }

    /// <summary>
    /// Creates a party for a client player.
    /// </summary>
    private SpawnResult CreatePartyForClient(CoopPlayer player)
    {
        if (Campaign.Current == null)
        {
            return new SpawnResult { Success = false, ErrorMessage = "No active campaign" };
        }

        try
        {
            // Get a culture - default to first main culture
            var culture = Campaign.Current.ObjectManager.GetObjectTypeList<CultureObject>()
                .FirstOrDefault(c => c.IsMainCulture);

            if (culture == null)
            {
                return new SpawnResult { Success = false, ErrorMessage = "No culture found" };
            }

            // Get spawn position near host
            float spawnX, spawnY;
            Settlement? spawnSettlement = null;

            if (MobileParty.MainParty != null)
            {
                var hostPos = MobileParty.MainParty.GetPosition2D;
                spawnX = hostPos.x + 2.0f;
                spawnY = hostPos.y + 2.0f;
            }
            else
            {
                spawnSettlement = Campaign.Current.Settlements.FirstOrDefault(s => s.IsTown);
                if (spawnSettlement != null)
                {
                    var pos = spawnSettlement.GatePosition;
                    spawnX = pos.X;
                    spawnY = pos.Y;
                }
                else
                {
                    spawnX = 100f;
                    spawnY = 100f;
                }
            }

            // Find a settlement for hero creation
            if (spawnSettlement == null)
            {
                spawnSettlement = Campaign.Current.Settlements.FirstOrDefault(s => s.IsTown && s.Culture == culture)
                    ?? Campaign.Current.Settlements.FirstOrDefault(s => s.IsTown);
            }

            // Create clan
            var clanId = $"coop_clan_{player.Name.ToLowerInvariant().Replace(" ", "_")}_{DateTime.Now.Ticks}";
            var clan = Clan.CreateClan(clanId);
            if (clan == null)
            {
                return new SpawnResult { Success = false, ErrorMessage = "Failed to create clan" };
            }
            clan.Culture = culture;
            clan.AddRenown(50);

            // Create hero using correct API
            var hero = HeroCreator.CreateSpecialHero(
                culture.BasicTroop,
                spawnSettlement,
                clan,
                null,
                25
            );

            if (hero == null)
            {
                return new SpawnResult { Success = false, ErrorMessage = "Failed to create hero" };
            }

            hero.SetName(
                new TaleWorlds.Localization.TextObject(player.Name),
                new TaleWorlds.Localization.TextObject(player.Name)
            );

            clan.SetLeader(hero);

            // Create party using LordPartyComponent (same pattern as SessionManager)
            MobileParty? party = null;
            try
            {
                var partyId = $"coop_party_{player.Name.ToLowerInvariant().Replace(" ", "_")}_{DateTime.Now.Ticks}";

                // Try to create LordPartyComponent via reflection
                var componentType = typeof(LordPartyComponent);
                var createMethod = componentType.GetMethod("CreateLordPartyComponent",
                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);

                if (createMethod != null)
                {
                    var component = createMethod.Invoke(null, new object[] { hero, hero }) as PartyComponent;
                    if (component != null)
                    {
                        party = MobileParty.CreateParty(partyId, component);
                    }
                }

                // Fallback: Create with null component and set up manually
                if (party == null)
                {
                    party = MobileParty.CreateParty(partyId, null);
                }

                if (party != null)
                {
                    // Initialize roster with hero
                    var memberRoster = TroopRoster.CreateDummyTroopRoster();
                    memberRoster.AddToCounts(hero.CharacterObject, 1);

                    // Try to initialize party
                    try
                    {
                        party.InitializeMobilePartyAroundPosition(memberRoster, TroopRoster.CreateDummyTroopRoster(),
                            new Vec2(spawnX, spawnY), 1.0f, 0.5f);
                    }
                    catch
                    {
                        // Fallback - add troops directly
                        party.MemberRoster.AddToCounts(hero.CharacterObject, 1);
                    }

                    // Set party ownership via reflection
                    try
                    {
                        var ownerProp = party.GetType().GetProperty("Owner");
                        ownerProp?.SetValue(party, hero);
                    }
                    catch { }

                    // Set clan
                    try { party.ActualClan = clan; } catch { }

                    // Set AI
                    try { party.Ai?.SetDoNotMakeNewDecisions(true); } catch { }
                    try { party.SetMoveModeHold(); } catch { }
                    try { party.IsVisible = true; } catch { }
                }
            }
            catch (Exception ex)
            {
                BannerBrosModule.LogMessage($"Party creation error: {ex.Message}");
            }

            if (party == null)
            {
                return new SpawnResult { Success = false, ErrorMessage = "Failed to create party" };
            }

            return new SpawnResult
            {
                Success = true,
                HeroId = hero.StringId,
                ClanId = clan.StringId,
                PartyId = party.StringId,
                SpawnX = spawnX,
                SpawnY = spawnY
            };
        }
        catch (Exception ex)
        {
            return new SpawnResult { Success = false, ErrorMessage = ex.Message };
        }
    }

    /// <summary>
    /// Handles move command from client.
    /// </summary>
    private void HandleMoveCommand(MoveCommandPacket packet, int peerId)
    {
        var player = _playerManager.GetPlayer(packet.PlayerId);
        if (player == null || string.IsNullOrEmpty(player.PartyId)) return;

        try
        {
            var party = Campaign.Current?.MobileParties
                .FirstOrDefault(p => p.StringId == player.PartyId);

            if (party == null)
            {
                SendCommandResult(peerId, packet.PlayerId, "Move", false, "Party not found");
                return;
            }

            // Set party to move to target position
            var targetPos = new Vec2(packet.TargetX, packet.TargetY);

            try
            {
                // Try SetMovePatrolAroundPoint or direct position set
                party.Ai?.SetMovePatrolAroundPoint(targetPos);
            }
            catch
            {
                // Fallback: directly set position (less smooth)
                try
                {
                    var posProp = party.GetType().GetProperty("Position2D");
                    posProp?.SetValue(party, targetPos);
                }
                catch { }
            }

            player.MapPositionX = packet.TargetX;
            player.MapPositionY = packet.TargetY;

            SendCommandResult(peerId, packet.PlayerId, "Move", true, null);
        }
        catch (Exception ex)
        {
            SendCommandResult(peerId, packet.PlayerId, "Move", false, ex.Message);
        }
    }

    /// <summary>
    /// Handles enter settlement command from client.
    /// </summary>
    private void HandleEnterSettlementCommand(EnterSettlementCommandPacket packet, int peerId)
    {
        var player = _playerManager.GetPlayer(packet.PlayerId);
        if (player == null || string.IsNullOrEmpty(player.PartyId)) return;

        try
        {
            var party = Campaign.Current?.MobileParties
                .FirstOrDefault(p => p.StringId == player.PartyId);

            var settlement = Campaign.Current?.Settlements
                .FirstOrDefault(s => s.StringId == packet.SettlementId);

            if (party == null || settlement == null)
            {
                SendCommandResult(peerId, packet.PlayerId, "EnterSettlement", false, "Party or settlement not found");
                return;
            }

            // Move party to settlement
            try
            {
                var settlementPos = settlement.GatePosition;
                party.Ai?.SetMovePatrolAroundPoint(new Vec2(settlementPos.X, settlementPos.Y));
            }
            catch { }

            SendCommandResult(peerId, packet.PlayerId, "EnterSettlement", true, null);
        }
        catch (Exception ex)
        {
            SendCommandResult(peerId, packet.PlayerId, "EnterSettlement", false, ex.Message);
        }
    }

    /// <summary>
    /// Handles leave settlement command from client.
    /// </summary>
    private void HandleLeaveSettlementCommand(LeaveSettlementCommandPacket packet, int peerId)
    {
        var player = _playerManager.GetPlayer(packet.PlayerId);
        if (player == null || string.IsNullOrEmpty(player.PartyId)) return;

        try
        {
            var party = Campaign.Current?.MobileParties
                .FirstOrDefault(p => p.StringId == player.PartyId);

            if (party == null)
            {
                SendCommandResult(peerId, packet.PlayerId, "LeaveSettlement", false, "Party not found");
                return;
            }

            // Leave settlement - hold position
            try
            {
                party.SetMoveModeHold();
            }
            catch { }

            SendCommandResult(peerId, packet.PlayerId, "LeaveSettlement", true, null);
        }
        catch (Exception ex)
        {
            SendCommandResult(peerId, packet.PlayerId, "LeaveSettlement", false, ex.Message);
        }
    }

    /// <summary>
    /// Handles attack command from client.
    /// </summary>
    private void HandleAttackCommand(AttackCommandPacket packet, int peerId)
    {
        var player = _playerManager.GetPlayer(packet.PlayerId);
        if (player == null || string.IsNullOrEmpty(player.PartyId)) return;

        try
        {
            var party = Campaign.Current?.MobileParties
                .FirstOrDefault(p => p.StringId == player.PartyId);

            var targetParty = Campaign.Current?.MobileParties
                .FirstOrDefault(p => p.StringId == packet.TargetPartyId);

            if (party == null || targetParty == null)
            {
                SendCommandResult(peerId, packet.PlayerId, "Attack", false, "Party not found");
                return;
            }

            // Set party to pursue target (attack when in range)
            try
            {
                var targetPos = targetParty.GetPosition2D;
                party.Ai?.SetMovePatrolAroundPoint(targetPos);
            }
            catch { }

            SendCommandResult(peerId, packet.PlayerId, "Attack", true, null);
        }
        catch (Exception ex)
        {
            SendCommandResult(peerId, packet.PlayerId, "Attack", false, ex.Message);
        }
    }

    /// <summary>
    /// Handles follow command from client.
    /// </summary>
    private void HandleFollowCommand(FollowCommandPacket packet, int peerId)
    {
        var player = _playerManager.GetPlayer(packet.PlayerId);
        if (player == null || string.IsNullOrEmpty(player.PartyId)) return;

        try
        {
            var party = Campaign.Current?.MobileParties
                .FirstOrDefault(p => p.StringId == player.PartyId);

            var targetParty = Campaign.Current?.MobileParties
                .FirstOrDefault(p => p.StringId == packet.TargetPartyId);

            if (party == null || targetParty == null)
            {
                SendCommandResult(peerId, packet.PlayerId, "Follow", false, "Party not found");
                return;
            }

            // Set party to follow target
            try
            {
                var targetPos = targetParty.GetPosition2D;
                party.Ai?.SetMovePatrolAroundPoint(targetPos);
            }
            catch { }

            SendCommandResult(peerId, packet.PlayerId, "Follow", true, null);
        }
        catch (Exception ex)
        {
            SendCommandResult(peerId, packet.PlayerId, "Follow", false, ex.Message);
        }
    }

    /// <summary>
    /// Sends command result back to client.
    /// </summary>
    private void SendCommandResult(int peerId, int playerId, string commandType, bool success, string? errorMessage)
    {
        var packet = new CommandResultPacket
        {
            PlayerId = playerId,
            CommandType = commandType,
            Success = success,
            ErrorMessage = errorMessage ?? ""
        };

        NetworkManager.Instance?.SendTo(peerId, packet, DeliveryMethod.ReliableOrdered);
    }
}
