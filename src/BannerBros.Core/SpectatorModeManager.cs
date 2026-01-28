using System;
using System.Linq;
using BannerBros.Network;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Roster;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Library;

namespace BannerBros.Core;

/// <summary>
/// Manages spectator mode for clients in single authoritative campaign architecture.
/// Handles disabling local MainHero and controlling assigned party via host commands.
/// </summary>
public class SpectatorModeManager
{
    public bool IsSpectatorMode { get; private set; }
    public string? AssignedPartyId { get; private set; }
    public string? AssignedHeroId { get; private set; }
    public MobileParty? AssignedParty { get; private set; }
    public Hero? AssignedHero { get; private set; }

    // Original MainHero info (before hiding)
    private string? _originalMainHeroId;
    private Vec2 _originalMainPartyPosition;

    public event Action? OnSpectatorModeEntered;
    public event Action<MobileParty>? OnPartyAssigned;

    public SpectatorModeManager()
    {
    }

    public void Initialize()
    {
        var networkManager = NetworkManager.Instance;
        if (networkManager == null)
        {
            BannerBrosModule.LogMessage("[Spectator] Initialize failed: NetworkManager is null");
            return;
        }

        BannerBrosModule.LogMessage("[Spectator] Initializing SpectatorModeManager");
        networkManager.Messages.OnPartyAssignmentReceived += HandlePartyAssignment;
        networkManager.Messages.OnCommandResultReceived += HandleCommandResult;
        BannerBrosModule.LogMessage("[Spectator] Party assignment and command result handlers registered");
    }

    /// <summary>
    /// Handles command result from host.
    /// </summary>
    private void HandleCommandResult(CommandResultPacket packet)
    {
        var module = BannerBrosModule.Instance;
        var localPlayer = module?.PlayerManager.GetLocalPlayer();

        // Only process results for our commands
        if (localPlayer == null || packet.PlayerId != localPlayer.NetworkId) return;

        if (packet.Success)
        {
            BannerBrosModule.LogMessage($"[Spectator] Command {packet.CommandType} executed successfully");
        }
        else
        {
            BannerBrosModule.LogMessage($"[Spectator] Command {packet.CommandType} failed: {packet.ErrorMessage}");
        }
    }

    public void Cleanup()
    {
        var networkManager = NetworkManager.Instance;
        if (networkManager == null) return;

        networkManager.Messages.OnPartyAssignmentReceived -= HandlePartyAssignment;
        networkManager.Messages.OnCommandResultReceived -= HandleCommandResult;

        ExitSpectatorMode();
    }

    /// <summary>
    /// Called when client loads the shared save file.
    /// Enters spectator mode and notifies host.
    /// </summary>
    public void EnterSpectatorMode()
    {
        BannerBrosModule.LogMessage("[Spectator] EnterSpectatorMode called");

        if (IsSpectatorMode)
        {
            BannerBrosModule.LogMessage("[Spectator] Already in spectator mode, skipping");
            return;
        }

        BannerBrosModule.LogMessage("[Spectator] Entering spectator mode...");

        try
        {
            // Store original hero info
            if (Hero.MainHero != null)
            {
                _originalMainHeroId = Hero.MainHero.StringId;
            }

            if (MobileParty.MainParty != null)
            {
                _originalMainPartyPosition = MobileParty.MainParty.GetPosition2D;
            }

            // Hide the main party (move it far away or make invisible)
            HideMainParty();

            IsSpectatorMode = true;
            OnSpectatorModeEntered?.Invoke();

            // Notify host that we're ready for party assignment
            BannerBrosModule.LogMessage("[Spectator] Notifying host we're ready...");
            NotifyHostSpectatorReady();

            BannerBrosModule.LogMessage("[Spectator] *** SPECTATOR MODE ACTIVE - Waiting for party assignment ***");
        }
        catch (Exception ex)
        {
            BannerBrosModule.LogMessage($"Error entering spectator mode: {ex.Message}");
        }
    }

    /// <summary>
    /// Exits spectator mode and restores original state.
    /// </summary>
    public void ExitSpectatorMode()
    {
        if (!IsSpectatorMode) return;

        try
        {
            // Restore main party visibility/position if needed
            RestoreMainParty();

            IsSpectatorMode = false;
            AssignedPartyId = null;
            AssignedHeroId = null;
            AssignedParty = null;
            AssignedHero = null;

            BannerBrosModule.LogMessage("Exited spectator mode");
        }
        catch (Exception ex)
        {
            BannerBrosModule.LogMessage($"Error exiting spectator mode: {ex.Message}");
        }
    }

    /// <summary>
    /// Hides the main party so it doesn't interfere with spectator mode.
    /// </summary>
    private void HideMainParty()
    {
        try
        {
            var mainParty = MobileParty.MainParty;
            if (mainParty == null) return;

            // Option 1: Move to edge of map
            // This keeps the party in the game but out of the way
            try
            {
                var posProp = mainParty.GetType().GetProperty("Position2D");
                if (posProp?.CanWrite == true)
                {
                    // Move to corner of map
                    posProp.SetValue(mainParty, new Vec2(0, 0));
                }
            }
            catch { }

            // Option 2: Make invisible (if API supports it)
            try
            {
                mainParty.IsVisible = false;
            }
            catch { }

            // Option 3: Disable AI
            try
            {
                mainParty.Ai?.SetDoNotMakeNewDecisions(true);
            }
            catch { }

            BannerBrosModule.LogMessage("Main party hidden for spectator mode");
        }
        catch (Exception ex)
        {
            BannerBrosModule.LogMessage($"Error hiding main party: {ex.Message}");
        }
    }

    /// <summary>
    /// Restores the main party after exiting spectator mode.
    /// </summary>
    private void RestoreMainParty()
    {
        try
        {
            var mainParty = MobileParty.MainParty;
            if (mainParty == null) return;

            // Restore position
            try
            {
                var posProp = mainParty.GetType().GetProperty("Position2D");
                if (posProp?.CanWrite == true)
                {
                    posProp.SetValue(mainParty, _originalMainPartyPosition);
                }
            }
            catch { }

            // Restore visibility
            try
            {
                mainParty.IsVisible = true;
            }
            catch { }

            BannerBrosModule.LogMessage("Main party restored");
        }
        catch (Exception ex)
        {
            BannerBrosModule.LogMessage($"Error restoring main party: {ex.Message}");
        }
    }

    /// <summary>
    /// Notifies host that this client has loaded the save and is ready for party assignment.
    /// </summary>
    private void NotifyHostSpectatorReady()
    {
        var networkManager = NetworkManager.Instance;
        var module = BannerBrosModule.Instance;

        if (networkManager == null)
        {
            BannerBrosModule.LogMessage("[Spectator] Cannot notify host: NetworkManager is null");
            return;
        }
        if (module == null)
        {
            BannerBrosModule.LogMessage("[Spectator] Cannot notify host: Module is null");
            return;
        }

        var localPlayer = module.PlayerManager.GetLocalPlayer();
        if (localPlayer == null)
        {
            BannerBrosModule.LogMessage("[Spectator] Cannot notify host: LocalPlayer is null");
            return;
        }

        BannerBrosModule.LogMessage($"[Spectator] CLIENT -> HOST: Sending SpectatorReady (PlayerId: {localPlayer.NetworkId}, Name: {localPlayer.Name})");

        var packet = new SpectatorReadyPacket
        {
            PlayerId = localPlayer.NetworkId,
            PlayerName = localPlayer.Name
        };

        networkManager.SendToServer(packet);
        BannerBrosModule.LogMessage("[Spectator] SpectatorReady packet sent");
    }

    /// <summary>
    /// Handles party assignment from host.
    /// </summary>
    private void HandlePartyAssignment(PartyAssignmentPacket packet)
    {
        BannerBrosModule.LogMessage($"[Spectator] CLIENT: Received PartyAssignment packet (PartyId: {packet.PartyId}, PlayerId: {packet.PlayerId})");

        var module = BannerBrosModule.Instance;
        if (module == null)
        {
            BannerBrosModule.LogMessage("[Spectator] Cannot process: Module is null");
            return;
        }

        var localPlayer = module.PlayerManager.GetLocalPlayer();
        if (localPlayer == null)
        {
            BannerBrosModule.LogMessage("[Spectator] Cannot process: LocalPlayer is null");
            return;
        }

        if (packet.PlayerId != localPlayer.NetworkId)
        {
            BannerBrosModule.LogMessage($"[Spectator] Ignoring: packet PlayerId {packet.PlayerId} != our ID {localPlayer.NetworkId}");
            return;
        }

        BannerBrosModule.LogMessage($"[Spectator] *** PARTY ASSIGNED: {packet.PartyId} ***");

        AssignedPartyId = packet.PartyId;
        AssignedHeroId = packet.HeroId;

        // Find the assigned party in the game
        try
        {
            // Log all parties for debugging
            var allParties = Campaign.Current?.MobileParties?.ToList();
            BannerBrosModule.LogMessage($"[Spectator] Looking for party '{packet.PartyId}' among {allParties?.Count ?? 0} parties");

            AssignedParty = allParties?.FirstOrDefault(p => p.StringId == packet.PartyId);

            // If party not found, it was created on host after save transfer - create it locally
            if (AssignedParty == null)
            {
                BannerBrosModule.LogMessage($"[Spectator] Party not found in save - creating locally...");
                AssignedParty = CreateLocalParty(packet, localPlayer);
            }

            if (AssignedParty != null)
            {
                AssignedHero = AssignedParty.LeaderHero;
                BannerBrosModule.LogMessage($"[Spectator] Party ready: {AssignedParty.Name} (StringId: {AssignedParty.StringId})");

                // Update local player info
                localPlayer.PartyId = AssignedParty.StringId; // Use actual StringId
                localPlayer.HeroId = packet.HeroId;
                localPlayer.ClanId = packet.ClanId;
                localPlayer.MapPositionX = packet.MapX;
                localPlayer.MapPositionY = packet.MapY;

                OnPartyAssigned?.Invoke(AssignedParty);

                // Center camera on assigned party
                CenterCameraOnParty(AssignedParty);
            }
            else
            {
                BannerBrosModule.LogMessage($"[Spectator] ERROR: Could not find or create party: {packet.PartyId}");
            }
        }
        catch (Exception ex)
        {
            BannerBrosModule.LogMessage($"Error handling party assignment: {ex.Message}");
        }
    }

    /// <summary>
    /// Creates a party locally on the client side to match what host created.
    /// This is needed because the party was created on host after save transfer.
    /// </summary>
    private MobileParty? CreateLocalParty(PartyAssignmentPacket packet, CoopPlayer localPlayer)
    {
        try
        {
            if (Campaign.Current == null)
            {
                BannerBrosModule.LogMessage("[Spectator] Cannot create party: No campaign");
                return null;
            }

            // Get a culture
            var culture = Campaign.Current.ObjectManager.GetObjectTypeList<CultureObject>()
                .FirstOrDefault(c => c.IsMainCulture);

            if (culture == null)
            {
                BannerBrosModule.LogMessage("[Spectator] Cannot create party: No culture found");
                return null;
            }

            // Find a settlement for hero creation
            var spawnSettlement = Campaign.Current.Settlements.FirstOrDefault(s => s.IsTown);

            // Create clan
            var clanId = $"coop_client_clan_{localPlayer.Name?.ToLowerInvariant().Replace(" ", "_") ?? "player"}_{DateTime.Now.Ticks}";
            var clan = Clan.CreateClan(clanId);
            if (clan == null)
            {
                BannerBrosModule.LogMessage("[Spectator] Failed to create clan");
                return null;
            }
            clan.Culture = culture;
            clan.AddRenown(50);

            // Create hero
            var hero = HeroCreator.CreateSpecialHero(
                culture.BasicTroop,
                spawnSettlement,
                clan,
                null,
                25
            );

            if (hero == null)
            {
                BannerBrosModule.LogMessage("[Spectator] Failed to create hero");
                return null;
            }

            hero.SetName(
                new TaleWorlds.Localization.TextObject(localPlayer.Name ?? "Co-op Player"),
                new TaleWorlds.Localization.TextObject(localPlayer.Name ?? "Co-op Player")
            );

            clan.SetLeader(hero);

            // Create party - use the same ID pattern as host for matching
            MobileParty? party = null;
            var partyId = packet.PartyId; // Use the ID the host sent

            try
            {
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

                // Fallback
                if (party == null)
                {
                    party = MobileParty.CreateParty(partyId, null);
                }

                if (party != null)
                {
                    // Initialize roster with hero
                    var memberRoster = TroopRoster.CreateDummyTroopRoster();
                    memberRoster.AddToCounts(hero.CharacterObject, 1);
                    var prisonerRoster = TroopRoster.CreateDummyTroopRoster();
                    var posVec2 = new Vec2(packet.MapX, packet.MapY);

                    party.InitializeMobilePartyAtPosition(memberRoster, prisonerRoster, posVec2);

                    // Configure party
                    try { party.IsVisible = true; } catch { }
                    try { party.Ai?.SetDoNotMakeNewDecisions(true); } catch { }

                    BannerBrosModule.LogMessage($"[Spectator] Created local party: {party.StringId} at ({packet.MapX:F1}, {packet.MapY:F1})");

                    // Update AssignedPartyId to match actual StringId
                    AssignedPartyId = party.StringId;

                    return party;
                }
            }
            catch (Exception ex)
            {
                BannerBrosModule.LogMessage($"[Spectator] Party creation error: {ex.Message}");
            }
        }
        catch (Exception ex)
        {
            BannerBrosModule.LogMessage($"[Spectator] CreateLocalParty error: {ex.Message}");
        }

        return null;
    }

    /// <summary>
    /// Centers the camera on the assigned party.
    /// </summary>
    private void CenterCameraOnParty(MobileParty party)
    {
        try
        {
            // Try to center the map camera on our assigned party
            // This is tricky because the game camera normally follows MainParty
            // We may need to use reflection or find a campaign camera API

            var pos = party.GetPosition2D;
            BannerBrosModule.LogMessage($"Camera should focus on ({pos.x}, {pos.y})");

            // TODO: Actually move camera - may need MapScreen access
            // For now, just log the position
        }
        catch (Exception ex)
        {
            BannerBrosModule.LogMessage($"Error centering camera: {ex.Message}");
        }
    }

    /// <summary>
    /// Gets the position of the assigned party.
    /// </summary>
    public Vec2 GetAssignedPartyPosition()
    {
        if (AssignedParty != null)
        {
            return AssignedParty.GetPosition2D;
        }
        return Vec2.Zero;
    }

    /// <summary>
    /// Sends a movement command to the host.
    /// Client controls their assigned party by sending commands, not direct control.
    /// </summary>
    public void SendMoveCommand(float targetX, float targetY)
    {
        var networkManager = NetworkManager.Instance;
        var module = BannerBrosModule.Instance;

        if (networkManager == null || module == null)
        {
            BannerBrosModule.LogMessage("[Spectator] Cannot send move: NetworkManager or Module is null");
            return;
        }

        if (networkManager.IsHost)
        {
            BannerBrosModule.LogMessage("[Spectator] Host doesn't need to send move commands");
            return;
        }

        var localPlayer = module.PlayerManager.GetLocalPlayer();
        if (localPlayer == null)
        {
            BannerBrosModule.LogMessage("[Spectator] Cannot send move: No local player");
            return;
        }

        BannerBrosModule.LogMessage($"[Spectator] Sending move command to ({targetX:F1}, {targetY:F1})");

        var packet = new MoveCommandPacket
        {
            PlayerId = localPlayer.NetworkId,
            TargetX = targetX,
            TargetY = targetY
        };

        networkManager.SendToServer(packet);
    }

    /// <summary>
    /// Sends an attack command to the host.
    /// </summary>
    public void SendAttackCommand(string targetPartyId)
    {
        var networkManager = NetworkManager.Instance;
        var module = BannerBrosModule.Instance;

        if (networkManager == null || module == null || networkManager.IsHost) return;

        var localPlayer = module.PlayerManager.GetLocalPlayer();
        if (localPlayer == null) return;

        BannerBrosModule.LogMessage($"[Spectator] Sending attack command on {targetPartyId}");

        var packet = new AttackCommandPacket
        {
            PlayerId = localPlayer.NetworkId,
            TargetPartyId = targetPartyId
        };

        networkManager.SendToServer(packet);
    }

    /// <summary>
    /// Sends a follow command to the host.
    /// </summary>
    public void SendFollowCommand(string targetPartyId)
    {
        var networkManager = NetworkManager.Instance;
        var module = BannerBrosModule.Instance;

        if (networkManager == null || module == null || networkManager.IsHost) return;

        var localPlayer = module.PlayerManager.GetLocalPlayer();
        if (localPlayer == null) return;

        BannerBrosModule.LogMessage($"[Spectator] Sending follow command for {targetPartyId}");

        var packet = new FollowCommandPacket
        {
            PlayerId = localPlayer.NetworkId,
            TargetPartyId = targetPartyId
        };

        networkManager.SendToServer(packet);
    }
}
