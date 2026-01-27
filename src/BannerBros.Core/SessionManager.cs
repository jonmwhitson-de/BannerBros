using System.Reflection;
using Newtonsoft.Json;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Party.PartyComponents;
using TaleWorlds.CampaignSystem.Roster;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.ObjectSystem;
using BannerBros.Network;

namespace BannerBros.Core;

/// <summary>
/// Manages multiplayer session state including join flow, player spawning,
/// and synchronization between host and clients.
/// </summary>
public class SessionManager
{
    public const string ModVersion = "0.1.0";

    private readonly PlayerManager _playerManager;
    private readonly WorldStateManager _worldStateManager;

    private int _nextPlayerId = 1; // 0 is reserved for host
    private SessionState _state = SessionState.Disconnected;
    private bool _awaitingCharacterCreation;

    public SessionState State => _state;
    public bool IsAwaitingCharacterCreation => _awaitingCharacterCreation;

    public event Action<SessionState>? OnStateChanged;
    public event Action<string>? OnJoinRejected;
    public event Action? OnCharacterCreationRequired;
    public event Action<CoopPlayer>? OnPlayerSpawned;
    public event Action<SavedCharacterInfo>? OnSavedCharacterFound;

    public SessionManager(PlayerManager playerManager, WorldStateManager worldStateManager)
    {
        _playerManager = playerManager;
        _worldStateManager = worldStateManager;
    }

    public void Initialize()
    {
        // Always cleanup first to prevent duplicate subscriptions
        Cleanup();

        var networkManager = NetworkManager.Instance;
        if (networkManager == null) return;

        // Subscribe to network events
        networkManager.Messages.OnJoinRequestReceived += HandleJoinRequest;
        networkManager.Messages.OnJoinResponseReceived += HandleJoinResponse;
        networkManager.Messages.OnCharacterCreationReceived += HandleCharacterCreation;
        networkManager.Messages.OnCharacterCreationResponseReceived += HandleCharacterCreationResponse;
        networkManager.Messages.OnFullStateSyncReceived += HandleFullStateSync;
        networkManager.Messages.OnPlayerStateReceived += HandlePlayerStateUpdate;
        networkManager.Messages.OnSessionEventReceived += HandleSessionEvent;

        networkManager.PeerConnected += OnPeerConnected;
        networkManager.PeerDisconnected += OnPeerDisconnected;
        networkManager.ConnectionRejected += OnConnectionRejected;
    }

    private void OnConnectionRejected(string reason)
    {
        BannerBrosModule.LogMessage($"Connection failed: {reason}");
        SetState(SessionState.Disconnected);
        OnJoinRejected?.Invoke(reason);
    }

    public void Cleanup()
    {
        var networkManager = NetworkManager.Instance;
        if (networkManager == null) return;

        networkManager.Messages.OnJoinRequestReceived -= HandleJoinRequest;
        networkManager.Messages.OnJoinResponseReceived -= HandleJoinResponse;
        networkManager.Messages.OnCharacterCreationReceived -= HandleCharacterCreation;
        networkManager.Messages.OnCharacterCreationResponseReceived -= HandleCharacterCreationResponse;
        networkManager.Messages.OnFullStateSyncReceived -= HandleFullStateSync;
        networkManager.Messages.OnPlayerStateReceived -= HandlePlayerStateUpdate;
        networkManager.Messages.OnSessionEventReceived -= HandleSessionEvent;

        networkManager.PeerConnected -= OnPeerConnected;
        networkManager.PeerDisconnected -= OnPeerDisconnected;
        networkManager.ConnectionRejected -= OnConnectionRejected;
    }

    #region Host Methods

    /// <summary>
    /// Called on host when starting a new session.
    /// </summary>
    public void StartHostSession()
    {
        try
        {
            // Reset player ID counter for new session
            _nextPlayerId = 1;

            BannerBrosModule.LogMessage("StartHostSession: Setting state to InSession");
            SetState(SessionState.InSession);

            // Add host as player 0
            var hostPlayer = new CoopPlayer
            {
                NetworkId = 0,
                Name = BannerBrosModule.Instance?.Config.PlayerName ?? "Host",
                IsHost = true,
                State = PlayerState.OnMap
            };

            BannerBrosModule.LogMessage("StartHostSession: Created host player");

            // Link host to the main hero (only if in campaign)
            try
            {
                if (Hero.MainHero != null)
                {
                    BannerBrosModule.LogMessage("StartHostSession: Linking to MainHero");
                    hostPlayer.HeroId = Hero.MainHero.StringId;
                    hostPlayer.ClanId = Hero.MainHero.Clan?.StringId;
                    hostPlayer.KingdomId = Hero.MainHero.Clan?.Kingdom?.StringId;

                    if (MobileParty.MainParty != null)
                    {
                        hostPlayer.PartyId = MobileParty.MainParty.StringId;
                        var pos = MobileParty.MainParty.GetPosition2D;
                        hostPlayer.MapPositionX = pos.x;
                        hostPlayer.MapPositionY = pos.y;
                    }
                }
                else
                {
                    BannerBrosModule.LogMessage("StartHostSession: No MainHero yet (will link after campaign starts)");
                }
            }
            catch (Exception ex)
            {
                BannerBrosModule.LogMessage($"StartHostSession: Error linking hero (safe to ignore before campaign): {ex.Message}");
            }

            _playerManager.LocalPlayerId = 0;
            _playerManager.AddPlayer(hostPlayer);
            BannerBrosModule.LogMessage("StartHostSession: Host player added successfully");
        }
        catch (Exception ex)
        {
            BannerBrosModule.LogMessage($"StartHostSession error: {ex.Message}");
        }
    }

    private void HandleJoinRequest(JoinRequestPacket packet, int peerId)
    {
        var networkManager = NetworkManager.Instance;
        if (networkManager == null || !networkManager.IsHost) return;

        try
        {
            BannerBrosModule.LogMessage($"Processing join request from {packet.PlayerName}");

            // Validate version
            if (packet.ModVersion != ModVersion)
            {
                SendJoinResponse(peerId, false, $"Version mismatch. Host: {ModVersion}, Client: {packet.ModVersion}");
                return;
            }

            // Check player limit
            if (_playerManager.PlayerCount >= (BannerBrosModule.Instance?.Config.MaxPlayers ?? 4))
            {
                SendJoinResponse(peerId, false, "Server is full");
                return;
            }

            // Assign player ID
            var playerId = _nextPlayerId++;

            // Check if this player has a saved character they can reclaim
            var savedCharacter = BannerBrosModule.Instance?.PlayerSaveData.FindCharacter(packet.PlayerName);
            bool hasValidSavedCharacter = savedCharacter != null && PlayerSaveData.IsHeroValid(savedCharacter.HeroId);

            // Player needs character creation if they don't have a valid saved character
            // and haven't indicated they have an existing one
            bool requiresCharacterCreation = !hasValidSavedCharacter && !packet.HasExistingCharacter;

            // Build response with current world state
            var response = new JoinResponsePacket
            {
                Accepted = true,
                AssignedPlayerId = playerId,
                RequiresCharacterCreation = requiresCharacterCreation,
                ExistingPlayersJson = JsonConvert.SerializeObject(GetConnectedPlayerInfos())
            };

            // If player has a saved character, include that info so they can reclaim it
            if (hasValidSavedCharacter && savedCharacter != null)
            {
                response.WorldStateData = JsonConvert.SerializeObject(new SavedCharacterInfo
                {
                    HeroId = savedCharacter.HeroId,
                    ClanId = savedCharacter.ClanId,
                    PartyId = savedCharacter.PartyId,
                    HeroName = GetHeroName(savedCharacter.HeroId)
                });
                BannerBrosModule.LogMessage($"Player {packet.PlayerName} has saved character: {savedCharacter.HeroId}");
            }

            // Send response
            BannerBrosModule.LogMessage($"Sending JoinResponse to peer {peerId}: PlayerId={playerId}, RequiresCharCreate={requiresCharacterCreation}, HasSaved={hasValidSavedCharacter}");
            networkManager.SendTo(peerId, response);

            // Create player entry (not fully initialized until character is created/loaded)
            var player = new CoopPlayer
            {
                NetworkId = playerId,
                Name = packet.PlayerName,
                IsHost = false,
                State = requiresCharacterCreation ? PlayerState.InMenu : PlayerState.OnMap
            };

            // If reclaiming saved character, link them now
            if (hasValidSavedCharacter && savedCharacter != null)
            {
                player.HeroId = savedCharacter.HeroId;
                player.ClanId = savedCharacter.ClanId;
                player.PartyId = savedCharacter.PartyId;
                player.State = PlayerState.OnMap;

                // Update their position from the hero's party
                UpdatePlayerPositionFromHero(player);
            }

            _playerManager.AddPlayer(player);

            // Notify other players
            BroadcastPlayerJoined(player);

            // Send full state sync
            SendFullStateSync(peerId);
        }
        catch (Exception ex)
        {
            BannerBrosModule.LogMessage($"HandleJoinRequest error: {ex.Message}");
            SendJoinResponse(peerId, false, "Server error processing join request");
        }
    }

    private string GetHeroName(string heroId)
    {
        try
        {
            var hero = Campaign.Current?.CampaignObjectManager.Find<Hero>(heroId);
            return hero?.Name?.ToString() ?? "Unknown";
        }
        catch
        {
            return "Unknown";
        }
    }

    private void UpdatePlayerPositionFromHero(CoopPlayer player)
    {
        try
        {
            if (string.IsNullOrEmpty(player.HeroId)) return;

            var hero = Campaign.Current?.CampaignObjectManager.Find<Hero>(player.HeroId);
            if (hero?.PartyBelongedTo != null)
            {
                var pos = hero.PartyBelongedTo.GetPosition2D;
                player.MapPositionX = pos.x;
                player.MapPositionY = pos.y;
                player.PartyId = hero.PartyBelongedTo.StringId;
            }
        }
        catch (Exception ex)
        {
            BannerBrosModule.LogMessage($"Error updating player position: {ex.Message}");
        }
    }

    private void SendJoinResponse(int peerId, bool accepted, string? rejectionReason = null)
    {
        var response = new JoinResponsePacket
        {
            Accepted = accepted,
            RejectionReason = rejectionReason
        };

        NetworkManager.Instance?.SendTo(peerId, response);
    }

    private List<ConnectedPlayerInfo> GetConnectedPlayerInfos()
    {
        var infos = new List<ConnectedPlayerInfo>();

        foreach (var player in _playerManager.Players.Values)
        {
            infos.Add(new ConnectedPlayerInfo
            {
                NetworkId = player.NetworkId,
                Name = player.Name,
                HeroId = player.HeroId,
                ClanId = player.ClanId,
                KingdomId = player.KingdomId,
                MapX = player.MapPositionX,
                MapY = player.MapPositionY,
                IsHost = player.IsHost
            });
        }

        return infos;
    }

    private void SendFullStateSync(int peerId)
    {
        try
        {
            var playerStates = new List<PlayerStatePacket>();
            foreach (var player in _playerManager.Players.Values)
            {
                playerStates.Add(CreatePlayerStatePacket(player));
            }

            var activeBattles = new List<BattleInfo>();
            foreach (var battle in _worldStateManager.ActiveBattles.Values)
            {
                activeBattles.Add(new BattleInfo
                {
                    BattleId = battle.BattleId,
                    MapPosition = battle.MapPosition,
                    InitiatorPlayerId = battle.InitiatorPlayerId
                });
            }

            // Safely get campaign time - may not exist if joining before campaign starts
            long campaignTimeTicks = 0;
            try
            {
                if (Campaign.Current != null)
                {
                    campaignTimeTicks = (long)(CampaignTime.Now.ToHours * 1000);
                }
            }
            catch
            {
                // Campaign time not available yet
            }

            var packet = new FullStateSyncPacket
            {
                CampaignTimeTicks = campaignTimeTicks,
                TimeMultiplier = BannerBrosModule.Instance?.Config.TimeSpeedMultiplier ?? 1.0f,
                PlayerStatesJson = JsonConvert.SerializeObject(playerStates),
                ActiveBattlesJson = JsonConvert.SerializeObject(activeBattles)
            };

            NetworkManager.Instance?.SendTo(peerId, packet);
        }
        catch (Exception ex)
        {
            BannerBrosModule.LogMessage($"SendFullStateSync error: {ex.Message}");
        }
    }

    private void HandleCharacterCreation(CharacterCreationPacket packet, int peerId)
    {
        var networkManager = NetworkManager.Instance;
        if (networkManager == null || !networkManager.IsHost) return;

        BannerBrosModule.LogMessage($"Processing character creation for player {packet.PlayerId}: {packet.CharacterName}");

        var player = _playerManager.GetPlayer(packet.PlayerId);
        if (player == null)
        {
            SendCharacterCreationResponse(peerId, packet.PlayerId, false, "Player not found");
            return;
        }

        try
        {
            // Spawn the new hero and party
            var result = SpawnPlayerHero(packet, player);

            if (result.Success)
            {
                player.HeroId = result.HeroId;
                player.PartyId = result.PartyId;
                player.ClanId = result.ClanId;
                player.MapPositionX = result.SpawnX;
                player.MapPositionY = result.SpawnY;
                player.State = PlayerState.OnMap;

                // Save the player-character mapping for reconnection
                BannerBrosModule.Instance?.PlayerSaveData.RegisterPlayer(
                    player.Name,
                    result.HeroId ?? "",
                    result.ClanId ?? "",
                    result.PartyId ?? ""
                );
                BannerBrosModule.LogMessage($"Registered {player.Name} -> {result.HeroId} for reconnection");

                SendCharacterCreationResponse(peerId, packet.PlayerId, true, null,
                    result.HeroId, result.PartyId, result.ClanId, result.SpawnX, result.SpawnY);

                // Notify all players of the new hero
                BroadcastPlayerStateUpdate(player);
            }
            else
            {
                SendCharacterCreationResponse(peerId, packet.PlayerId, false, result.ErrorMessage);
            }
        }
        catch (Exception ex)
        {
            BannerBrosModule.LogMessage($"Character creation failed: {ex.Message}");
            SendCharacterCreationResponse(peerId, packet.PlayerId, false, $"Error: {ex.Message}");
        }
    }

    private SpawnResult SpawnPlayerHero(CharacterCreationPacket packet, CoopPlayer player)
    {
        if (Campaign.Current == null)
        {
            return new SpawnResult { Success = false, ErrorMessage = "No active campaign" };
        }

        try
        {
            // Get the culture
            var culture = MBObjectManager.Instance.GetObject<CultureObject>(packet.CultureId);
            if (culture == null)
            {
                // Default to first available culture
                culture = Campaign.Current.ObjectManager.GetObjectTypeList<CultureObject>()
                    .FirstOrDefault(c => c.IsMainCulture);
            }

            if (culture == null)
            {
                return new SpawnResult { Success = false, ErrorMessage = "No valid culture found" };
            }

            // Find a spawn settlement - prefer a town of matching culture
            var spawnSettlement = Campaign.Current.Settlements
                .Where(s => s.IsTown && s.Culture == culture)
                .FirstOrDefault() ??
                Campaign.Current.Settlements.FirstOrDefault(s => s.IsTown);

            if (spawnSettlement == null)
            {
                return new SpawnResult { Success = false, ErrorMessage = "No valid spawn location found" };
            }

            // Create a clan for this player FIRST (hero needs a clan for party creation)
            var clan = CreatePlayerClan(culture, packet.CharacterName);
            if (clan == null)
            {
                return new SpawnResult { Success = false, ErrorMessage = "Failed to create clan" };
            }

            // Create the hero using HeroCreator
            var characterTemplate = culture.BasicTroop;
            var hero = HeroCreator.CreateSpecialHero(
                characterTemplate,
                spawnSettlement,
                clan, // Pass clan to hero creation
                null,
                packet.StartingAge
            );

            if (hero == null)
            {
                return new SpawnResult { Success = false, ErrorMessage = "Failed to create hero" };
            }

            // Set hero name
            hero.SetName(new TaleWorlds.Localization.TextObject(packet.CharacterName),
                         new TaleWorlds.Localization.TextObject(packet.CharacterName));

            // Make this hero the clan leader
            if (clan.Leader != hero)
            {
                clan.SetLeader(hero);
            }

            // Get spawn position from settlement
            var spawnPos = spawnSettlement.GatePosition;
            var spawnX = spawnPos.X;
            var spawnY = spawnPos.Y;

            // Create mobile party for the hero
            MobileParty? party = CreatePlayerParty(hero, clan, spawnSettlement);

            if (party == null)
            {
                BannerBrosModule.LogMessage("Warning: Party creation returned null, hero may not appear on map");
            }
            else
            {
                BannerBrosModule.LogMessage($"Created party {party.StringId} for player {packet.CharacterName}");
            }

            return new SpawnResult
            {
                Success = true,
                HeroId = hero.StringId,
                PartyId = party?.StringId ?? "",
                ClanId = clan.StringId,
                SpawnX = spawnX,
                SpawnY = spawnY
            };
        }
        catch (Exception ex)
        {
            BannerBrosModule.LogMessage($"SpawnPlayerHero error: {ex}");
            return new SpawnResult { Success = false, ErrorMessage = ex.Message };
        }
    }

    private MobileParty? CreatePlayerParty(Hero hero, Clan clan, Settlement spawnSettlement)
    {
        try
        {
            // Check if hero already has a party
            if (hero.PartyBelongedTo != null)
            {
                BannerBrosModule.LogMessage("Hero already has a party");
                return hero.PartyBelongedTo;
            }

            // Method 1: Try using Clan.CreateNewMobileParty (most reliable)
            MobileParty? party = TryCreatePartyViaClan(hero, clan, spawnSettlement);

            // Method 2: Try using MobileParty.CreateParty with component
            if (party == null)
            {
                party = TryCreatePartyWithComponent(hero, clan, spawnSettlement);
            }

            // Method 3: Try using MobilePartyHelper
            if (party == null)
            {
                party = TryCreatePartyViaHelper(hero, clan, spawnSettlement);
            }

            if (party != null)
            {
                BannerBrosModule.LogMessage($"Party created for {hero.Name} with {party.MemberRoster?.TotalManCount ?? 0} troops");
            }
            else
            {
                BannerBrosModule.LogMessage("All party creation methods failed - hero has no map presence");
            }

            return party;
        }
        catch (Exception ex)
        {
            BannerBrosModule.LogMessage($"CreatePlayerParty error: {ex.Message}");
            return hero.PartyBelongedTo;
        }
    }

    private MobileParty? TryCreatePartyViaClan(Hero hero, Clan clan, Settlement spawnSettlement)
    {
        try
        {
            // Try Clan.CreateNewMobileParty - available in some Bannerlord versions
            var createMethod = typeof(Clan).GetMethod("CreateNewMobileParty",
                BindingFlags.Public | BindingFlags.Instance);

            if (createMethod != null)
            {
                var parameters = createMethod.GetParameters();
                object?[]? args = parameters.Length switch
                {
                    1 when parameters[0].ParameterType == typeof(Hero) => new object[] { hero },
                    2 => new object[] { hero, spawnSettlement },
                    _ => null
                };

                if (args != null)
                {
                    var party = createMethod.Invoke(clan, args) as MobileParty;
                    if (party != null)
                    {
                        BannerBrosModule.LogMessage("Created party via Clan.CreateNewMobileParty");
                        return party;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            BannerBrosModule.LogMessage($"Clan.CreateNewMobileParty failed: {ex.Message}");
        }
        return null;
    }

    private MobileParty? TryCreatePartyWithComponent(Hero hero, Clan clan, Settlement settlement)
    {
        try
        {
            // Try to find a static CreateLordPartyComponent method
            var createMethod = typeof(LordPartyComponent).GetMethod("CreateLordPartyComponent",
                BindingFlags.Public | BindingFlags.Static);

            PartyComponent? component = null;

            if (createMethod != null)
            {
                var parameters = createMethod.GetParameters();
                object?[]? args = null;

                // Match parameter types dynamically
                if (parameters.Length >= 1 && parameters[0].ParameterType == typeof(Hero))
                {
                    if (parameters.Length == 1)
                        args = new object?[] { hero };
                    else if (parameters.Length == 2 && parameters[1].ParameterType == typeof(Hero))
                        args = new object?[] { hero, hero };
                    else if (parameters.Length == 2 && parameters[1].ParameterType == typeof(Settlement))
                        args = new object?[] { hero, settlement };
                }

                if (args != null)
                {
                    component = createMethod.Invoke(null, args) as PartyComponent;
                    BannerBrosModule.LogMessage($"Created component via static method with {args.Length} args");
                }
            }

            // Try constructors if static method didn't work
            if (component == null)
            {
                var constructors = typeof(LordPartyComponent).GetConstructors(
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

                foreach (var ctor in constructors)
                {
                    try
                    {
                        var ctorParams = ctor.GetParameters();
                        object?[]? ctorArgs = null;

                        if (ctorParams.Length == 0)
                            ctorArgs = Array.Empty<object?>();
                        else if (ctorParams.Length == 1 && ctorParams[0].ParameterType.IsAssignableFrom(typeof(Hero)))
                            ctorArgs = new object?[] { hero };
                        else if (ctorParams.Length == 2 &&
                                 ctorParams[0].ParameterType.IsAssignableFrom(typeof(Hero)) &&
                                 ctorParams[1].ParameterType.IsAssignableFrom(typeof(Hero)))
                            ctorArgs = new object?[] { hero, hero };

                        if (ctorArgs != null)
                        {
                            component = ctor.Invoke(ctorArgs) as PartyComponent;
                            if (component != null)
                            {
                                BannerBrosModule.LogMessage($"Created component via constructor with {ctorArgs.Length} args");
                                break;
                            }
                        }
                    }
                    catch { }
                }
            }

            if (component != null)
            {
                var partyId = $"coop_party_{hero.StringId}";
                var party = MobileParty.CreateParty(partyId, component);

                if (party != null)
                {
                    var memberRoster = TroopRoster.CreateDummyTroopRoster();
                    memberRoster.AddToCounts(hero.CharacterObject, 1);

                    if (clan.BasicTroop != null)
                        memberRoster.AddToCounts(clan.BasicTroop, 5);
                    if (clan.Culture?.EliteBasicTroop != null)
                        memberRoster.AddToCounts(clan.Culture.EliteBasicTroop, 2);

                    var prisonerRoster = TroopRoster.CreateDummyTroopRoster();

                    // Use settlement position
                    var position = settlement.GetPosition2D;
                    party.InitializeMobilePartyAtPosition(memberRoster, prisonerRoster, position);
                    party.ActualClan = clan;

                    BannerBrosModule.LogMessage("Created party via component");
                    return party;
                }
            }
        }
        catch (Exception ex)
        {
            BannerBrosModule.LogMessage($"Component party creation failed: {ex.Message}");
        }
        return null;
    }

    private MobileParty? TryCreatePartyViaHelper(Hero hero, Clan clan, Settlement settlement)
    {
        try
        {
            // Try to find and use MobilePartyHelper or similar
            var assembly = typeof(MobileParty).Assembly;

            // Look for helper classes that might have party creation methods
            var helperTypeNames = new[]
            {
                "TaleWorlds.CampaignSystem.Actions.MobilePartyHelper",
                "TaleWorlds.CampaignSystem.MobilePartyHelper",
                "TaleWorlds.CampaignSystem.ClanActions"
            };

            foreach (var typeName in helperTypeNames)
            {
                var helperType = assembly.GetType(typeName);
                if (helperType != null)
                {
                    var methods = helperType.GetMethods(BindingFlags.Public | BindingFlags.Static);
                    foreach (var method in methods)
                    {
                        if (method.Name.Contains("CreateNew") || method.Name.Contains("Spawn"))
                        {
                            BannerBrosModule.LogMessage($"Found potential helper: {typeName}.{method.Name}");
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            BannerBrosModule.LogMessage($"Helper party creation failed: {ex.Message}");
        }
        return null;
    }

    private Clan? CreatePlayerClan(CultureObject culture, string playerName)
    {
        try
        {
            // First, try to find an unused minor faction of matching culture
            // This is the safest approach as it uses existing game objects
            var minorClan = Clan.All.FirstOrDefault(c =>
                c.Culture == culture &&
                !c.IsEliminated &&
                c.IsMinorFaction &&
                (c.Heroes.Count == 0 || c.Leader == null)); // Prefer clans with no active heroes

            if (minorClan != null)
            {
                BannerBrosModule.LogMessage($"Using existing minor clan: {minorClan.Name}");
                return minorClan;
            }

            // Try any minor clan of matching culture
            minorClan = Clan.All.FirstOrDefault(c =>
                c.Culture == culture &&
                !c.IsEliminated &&
                c.IsMinorFaction);

            if (minorClan != null)
            {
                BannerBrosModule.LogMessage($"Assigned player to minor clan: {minorClan.Name}");
                return minorClan;
            }

            // Try any non-eliminated minor clan
            minorClan = Clan.All.FirstOrDefault(c =>
                !c.IsEliminated &&
                c.IsMinorFaction);

            if (minorClan != null)
            {
                BannerBrosModule.LogMessage($"Using minor clan (different culture): {minorClan.Name}");
                return minorClan;
            }

            // Last resort: try to create a new clan using available API
            try
            {
                var clanId = $"coop_clan_{playerName.ToLowerInvariant().Replace(" ", "_")}_{_nextPlayerId}";
                var newClan = Clan.CreateClan(clanId);

                if (newClan != null)
                {
                    // Set basic properties directly
                    newClan.Culture = culture;
                    newClan.AddRenown(50);
                    BannerBrosModule.LogMessage($"Created new clan: {clanId}");
                    return newClan;
                }
            }
            catch (Exception ex)
            {
                BannerBrosModule.LogMessage($"Clan creation failed: {ex.Message}");
            }

            BannerBrosModule.LogMessage("Warning: Could not create or find suitable clan");
            return null;
        }
        catch (Exception ex)
        {
            BannerBrosModule.LogMessage($"CreatePlayerClan error: {ex.Message}");
            return null;
        }
    }

    private Vec2 GetSafeSpawnPosition()
    {
        // Find a safe spawn location - prefer a town near the host
        var hostPlayer = _playerManager.GetPlayer(0);
        if (hostPlayer != null)
        {
            // Spawn near host with some offset
            var offsetX = MBRandom.RandomFloat * 10 - 5;
            var offsetY = MBRandom.RandomFloat * 10 - 5;
            return new Vec2(hostPlayer.MapPositionX + offsetX, hostPlayer.MapPositionY + offsetY);
        }

        // Fallback: spawn at a random town
        var town = Campaign.Current?.Settlements.FirstOrDefault(s => s.IsTown);
        if (town != null)
        {
            // GatePosition returns CampaignVec2, extract X and Y for Vec2
            var gatePos = town.GatePosition;
            return new Vec2(gatePos.X, gatePos.Y);
        }

        // Last resort: center of map
        return new Vec2(500, 500);
    }

    private void SendCharacterCreationResponse(int peerId, int playerId, bool success, string? error,
        string? heroId = null, string? partyId = null, string? clanId = null,
        float spawnX = 0, float spawnY = 0)
    {
        var response = new CharacterCreationResponsePacket
        {
            PlayerId = playerId,
            Success = success,
            ErrorMessage = error,
            HeroId = heroId,
            PartyId = partyId,
            ClanId = clanId,
            SpawnX = spawnX,
            SpawnY = spawnY
        };

        NetworkManager.Instance?.SendTo(peerId, response);
    }

    private void BroadcastPlayerJoined(CoopPlayer player)
    {
        var packet = new SessionPacket
        {
            EventType = (int)SessionEventType.PlayerJoined,
            PlayerId = player.NetworkId,
            PlayerName = player.Name
        };

        NetworkManager.Instance?.Send(packet);
    }

    private void BroadcastPlayerStateUpdate(CoopPlayer player)
    {
        var packet = CreatePlayerStatePacket(player);
        NetworkManager.Instance?.Send(packet);
    }

    #endregion

    #region Client Methods

    /// <summary>
    /// Called on client when connection to host is established.
    /// </summary>
    private void OnPeerConnected(int peerId)
    {
        var networkManager = NetworkManager.Instance;
        if (networkManager == null) return;

        // If we're a client and just connected to the host, send join request
        if (!networkManager.IsHost)
        {
            SetState(SessionState.Joining);
            SendJoinRequest();
        }
    }

    private void OnPeerDisconnected(int peerId, DisconnectReason reason)
    {
        var networkManager = NetworkManager.Instance;
        if (networkManager == null) return;

        if (networkManager.IsHost)
        {
            // Host: remove the disconnected player
            _playerManager.RemovePlayer(peerId);

            // Notify other players
            var packet = new SessionPacket
            {
                EventType = (int)SessionEventType.PlayerLeft,
                PlayerId = peerId
            };
            networkManager.Send(packet);
        }
        else
        {
            // Client: we disconnected from host
            SetState(SessionState.Disconnected);
            _playerManager.Clear();
        }
    }

    private void SendJoinRequest()
    {
        var config = BannerBrosModule.Instance?.Config;
        var packet = new JoinRequestPacket
        {
            PlayerName = config?.PlayerName ?? "Player",
            ModVersion = ModVersion,
            HasExistingCharacter = false // TODO: check if we have saved character data
        };

        NetworkManager.Instance?.SendToServer(packet);
        BannerBrosModule.LogMessage("Sent join request to host");
    }

    private void HandleJoinResponse(JoinResponsePacket packet)
    {
        if (!packet.Accepted)
        {
            BannerBrosModule.LogMessage($"Join rejected: {packet.RejectionReason}");
            SetState(SessionState.Disconnected);
            OnJoinRejected?.Invoke(packet.RejectionReason ?? "Unknown reason");
            NetworkManager.Instance?.Disconnect();
            return;
        }

        BannerBrosModule.LogMessage($"Join accepted! Player ID: {packet.AssignedPlayerId}");

        // Set our local player ID
        NetworkManager.Instance?.SetLocalPeerId(packet.AssignedPlayerId);
        _playerManager.LocalPlayerId = packet.AssignedPlayerId;

        // Add existing players
        if (!string.IsNullOrEmpty(packet.ExistingPlayersJson))
        {
            var existingPlayers = JsonConvert.DeserializeObject<List<ConnectedPlayerInfo>>(packet.ExistingPlayersJson);
            if (existingPlayers != null)
            {
                foreach (var playerInfo in existingPlayers)
                {
                    var player = new CoopPlayer
                    {
                        NetworkId = playerInfo.NetworkId,
                        Name = playerInfo.Name,
                        HeroId = playerInfo.HeroId,
                        ClanId = playerInfo.ClanId,
                        KingdomId = playerInfo.KingdomId,
                        MapPositionX = playerInfo.MapX,
                        MapPositionY = playerInfo.MapY,
                        IsHost = playerInfo.IsHost
                    };
                    _playerManager.AddPlayer(player);
                }
            }
        }

        // Check if we have a saved character we can reclaim
        if (!string.IsNullOrEmpty(packet.WorldStateData))
        {
            try
            {
                var savedChar = JsonConvert.DeserializeObject<SavedCharacterInfo>(packet.WorldStateData);
                if (savedChar != null && !string.IsNullOrEmpty(savedChar.HeroId))
                {
                    BannerBrosModule.LogMessage($"Found saved character: {savedChar.HeroName}");
                    // Notify UI to show reclaim option
                    OnSavedCharacterFound?.Invoke(savedChar);
                    SetState(SessionState.InSession);
                    return;
                }
            }
            catch (Exception ex)
            {
                BannerBrosModule.LogMessage($"Error parsing saved character: {ex.Message}");
            }
        }

        if (packet.RequiresCharacterCreation)
        {
            SetState(SessionState.CharacterCreation);
            _awaitingCharacterCreation = true;
            OnCharacterCreationRequired?.Invoke();
        }
        else
        {
            SetState(SessionState.InSession);
        }
    }

    /// <summary>
    /// Called by UI to submit character creation data.
    /// </summary>
    public void SubmitCharacterCreation(CharacterCreationPacket packet)
    {
        if (_state != SessionState.CharacterCreation) return;

        packet.PlayerId = _playerManager.LocalPlayerId;
        NetworkManager.Instance?.SendToServer(packet);
        BannerBrosModule.LogMessage("Submitted character creation to host");
    }

    private void HandleCharacterCreationResponse(CharacterCreationResponsePacket packet)
    {
        if (packet.PlayerId != _playerManager.LocalPlayerId) return;

        _awaitingCharacterCreation = false;

        if (!packet.Success)
        {
            BannerBrosModule.LogMessage($"Character creation failed: {packet.ErrorMessage}");
            // Stay in character creation state to retry
            return;
        }

        BannerBrosModule.LogMessage("Character created successfully!");

        // Update local player info
        var localPlayer = _playerManager.GetLocalPlayer();
        if (localPlayer != null)
        {
            localPlayer.HeroId = packet.HeroId;
            localPlayer.PartyId = packet.PartyId;
            localPlayer.ClanId = packet.ClanId;
            localPlayer.MapPositionX = packet.SpawnX;
            localPlayer.MapPositionY = packet.SpawnY;
            localPlayer.State = PlayerState.OnMap;

            OnPlayerSpawned?.Invoke(localPlayer);
        }

        SetState(SessionState.InSession);
    }

    private void HandleFullStateSync(FullStateSyncPacket packet)
    {
        // Deserialize player states from JSON
        var playerStates = new List<PlayerStatePacket>();
        if (!string.IsNullOrEmpty(packet.PlayerStatesJson))
        {
            try
            {
                playerStates = JsonConvert.DeserializeObject<List<PlayerStatePacket>>(packet.PlayerStatesJson)
                    ?? new List<PlayerStatePacket>();
            }
            catch (Exception ex)
            {
                BannerBrosModule.LogMessage($"Failed to parse player states: {ex.Message}");
            }
        }

        BannerBrosModule.LogMessage($"Received full state sync: {playerStates.Count} players");

        // Update player states
        foreach (var playerState in playerStates)
        {
            var player = _playerManager.GetPlayer(playerState.PlayerId);
            if (player != null)
            {
                player.MapPositionX = playerState.MapX;
                player.MapPositionY = playerState.MapY;
                player.State = (PlayerState)playerState.State;
                player.HeroId = playerState.HeroId;
                player.PartyId = playerState.PartyId;
                player.ClanId = playerState.ClanId;
                player.KingdomId = playerState.KingdomId;
            }
        }

        // Sync time multiplier
        if (BannerBrosModule.Instance != null)
        {
            BannerBrosModule.Instance.Config.TimeSpeedMultiplier = packet.TimeMultiplier;
        }
    }

    private void HandlePlayerStateUpdate(PlayerStatePacket packet)
    {
        // Update the player's state in our local manager
        var player = _playerManager.GetPlayer(packet.PlayerId);
        if (player != null)
        {
            player.MapPositionX = packet.MapX;
            player.MapPositionY = packet.MapY;
            player.State = (PlayerState)packet.State;
            player.HeroId = packet.HeroId;
            player.PartyId = packet.PartyId;
            player.ClanId = packet.ClanId;
            player.KingdomId = packet.KingdomId;
            player.PartySize = packet.PartySize;
            player.PartySpeed = packet.PartySpeed;
        }
    }

    private void HandleSessionEvent(SessionPacket packet)
    {
        var eventType = (SessionEventType)packet.EventType;

        switch (eventType)
        {
            case SessionEventType.PlayerJoined:
                if (_playerManager.GetPlayer(packet.PlayerId) == null)
                {
                    var player = new CoopPlayer
                    {
                        NetworkId = packet.PlayerId,
                        Name = packet.PlayerName
                    };
                    _playerManager.AddPlayer(player);
                }
                break;

            case SessionEventType.PlayerLeft:
            case SessionEventType.PlayerKicked:
                _playerManager.RemovePlayer(packet.PlayerId);
                break;
        }
    }

    #endregion

    #region Helpers

    private void SetState(SessionState newState)
    {
        if (_state != newState)
        {
            _state = newState;
            OnStateChanged?.Invoke(newState);
        }
    }

    private PlayerStatePacket CreatePlayerStatePacket(CoopPlayer player)
    {
        return new PlayerStatePacket
        {
            PlayerId = player.NetworkId,
            PlayerName = player.Name,
            MapX = player.MapPositionX,
            MapY = player.MapPositionY,
            State = (int)player.State,
            HeroId = player.HeroId ?? "",
            PartyId = player.PartyId ?? "",
            ClanId = player.ClanId ?? "",
            KingdomId = player.KingdomId ?? "",
            IsInBattle = player.CurrentBattleId != null,
            BattleId = player.CurrentBattleId ?? ""
        };
    }

    #endregion

    private class SpawnResult
    {
        public bool Success { get; set; }
        public string? ErrorMessage { get; set; }
        public string? HeroId { get; set; }
        public string? PartyId { get; set; }
        public string? ClanId { get; set; }
        public float SpawnX { get; set; }
        public float SpawnY { get; set; }
    }
}

public enum SessionState
{
    Disconnected,
    Joining,
    CharacterCreation,
    InSession
}
