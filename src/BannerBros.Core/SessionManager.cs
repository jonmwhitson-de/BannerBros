using System.Reflection;
using Newtonsoft.Json;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.CharacterDevelopment;
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

    // Queue for pending join requests when host is busy
    private readonly Queue<(JoinRequestPacket packet, int peerId)> _pendingJoinRequests = new();
    private bool _processingJoinRequest = false;

    private void HandleJoinRequest(JoinRequestPacket packet, int peerId)
    {
        var networkManager = NetworkManager.Instance;
        if (networkManager == null || !networkManager.IsHost) return;

        try
        {
            // Check if host is in a state that can process joins
            if (!CanProcessJoinRequest())
            {
                BannerBrosModule.LogMessage($"Host busy - queuing join request from {packet.PlayerName}");
                _pendingJoinRequests.Enqueue((packet, peerId));
                return;
            }

            // Prevent concurrent join processing
            if (_processingJoinRequest)
            {
                BannerBrosModule.LogMessage($"Already processing a join - queuing request from {packet.PlayerName}");
                _pendingJoinRequests.Enqueue((packet, peerId));
                return;
            }

            _processingJoinRequest = true;
            ProcessJoinRequestInternal(packet, peerId);
        }
        catch (Exception ex)
        {
            BannerBrosModule.LogMessage($"HandleJoinRequest error: {ex.Message}");
            SendJoinResponse(peerId, false, "Server error - please try again");
        }
        finally
        {
            _processingJoinRequest = false;
        }
    }

    private bool CanProcessJoinRequest()
    {
        try
        {
            // Check if campaign exists
            if (Campaign.Current == null)
            {
                return false;
            }

            // Check if host's main hero is in a blocking state
            var mainHero = Hero.MainHero;
            if (mainHero != null)
            {
                // Check for dialogue
                if (Campaign.Current.ConversationManager?.IsConversationInProgress == true)
                {
                    return false;
                }

                // Check for active mission (battle, scene, etc.)
                if (TaleWorlds.MountAndBlade.Mission.Current != null)
                {
                    return false;
                }
            }

            return true;
        }
        catch
        {
            // If we can't determine state, assume it's safe
            return true;
        }
    }

    /// <summary>
    /// Process any queued join requests. Call this periodically or when host becomes available.
    /// </summary>
    public void ProcessPendingJoinRequests()
    {
        if (_pendingJoinRequests.Count == 0 || _processingJoinRequest) return;

        if (!CanProcessJoinRequest()) return;

        while (_pendingJoinRequests.Count > 0 && CanProcessJoinRequest())
        {
            var (packet, peerId) = _pendingJoinRequests.Dequeue();
            BannerBrosModule.LogMessage($"Processing queued join request from {packet.PlayerName}");

            try
            {
                _processingJoinRequest = true;
                ProcessJoinRequestInternal(packet, peerId);
            }
            catch (Exception ex)
            {
                BannerBrosModule.LogMessage($"Error processing queued join: {ex.Message}");
                SendJoinResponse(peerId, false, "Server error - please try again");
            }
            finally
            {
                _processingJoinRequest = false;
            }
        }
    }

    private void ProcessJoinRequestInternal(JoinRequestPacket packet, int peerId)
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

        // Check if client sent exported character data (from full character creation)
        ExportedCharacter? exportedCharacter = null;
        if (!string.IsNullOrEmpty(packet.ExportedCharacterJson))
        {
            exportedCharacter = ExportedCharacter.FromJson(packet.ExportedCharacterJson);
            if (exportedCharacter != null)
            {
                BannerBrosModule.LogMessage($"Received exported character: {exportedCharacter.Name}");
            }
        }

        // Check if this player has a saved character they can reclaim
        var savedCharacter = BannerBrosModule.Instance?.PlayerSaveData.FindCharacter(packet.PlayerName);
        bool hasValidSavedCharacter = savedCharacter != null && PlayerSaveData.IsHeroValid(savedCharacter.HeroId);

        // Player needs character creation if:
        // - No valid saved character AND no exported character AND hasn't indicated existing
        bool requiresCharacterCreation = !hasValidSavedCharacter && exportedCharacter == null && !packet.HasExistingCharacter;

        // Build response with current world state
        var networkManager = NetworkManager.Instance;
        var response = new JoinResponsePacket
        {
            Accepted = true,
            AssignedPlayerId = playerId,
            RequiresCharacterCreation = requiresCharacterCreation,
            ExistingPlayersJson = JsonConvert.SerializeObject(GetConnectedPlayerInfos())
        };

        // If player has a saved character, include that info
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

        // Send response first
        BannerBrosModule.LogMessage($"Sending JoinResponse to peer {peerId}: PlayerId={playerId}, RequiresCharCreate={requiresCharacterCreation}");
        networkManager?.SendTo(peerId, response);

        // Create player entry
        var player = new CoopPlayer
        {
            NetworkId = playerId,
            Name = packet.PlayerName,
            IsHost = false,
            State = requiresCharacterCreation ? PlayerState.InMenu : PlayerState.OnMap
        };

        // Handle the different cases:
        if (exportedCharacter != null)
        {
            try
            {
                // Client sent full character data - create hero from it
                BannerBrosModule.LogMessage($"Creating hero from exported character data...");
                var result = SpawnHeroFromExportedCharacter(exportedCharacter, player);

                if (result.Success)
                {
                    player.HeroId = result.HeroId;
                    player.ClanId = result.ClanId;
                    player.PartyId = result.PartyId;
                    player.MapPositionX = result.SpawnX;
                    player.MapPositionY = result.SpawnY;
                    player.State = PlayerState.OnMap;

                    // Register for future reconnection
                    try
                    {
                        BannerBrosModule.Instance?.PlayerSaveData.RegisterPlayer(
                            player.Name, result.HeroId ?? "", result.ClanId ?? "", result.PartyId ?? "");
                    }
                    catch (Exception regEx)
                    {
                        BannerBrosModule.LogMessage($"Warning: Failed to register player: {regEx.Message}");
                    }

                    // Send success response
                    try
                    {
                        SendCharacterCreationResponse(peerId, playerId, true, null,
                            result.HeroId, result.PartyId, result.ClanId, result.SpawnX, result.SpawnY);
                    }
                    catch (Exception respEx)
                    {
                        BannerBrosModule.LogMessage($"Warning: Failed to send creation response: {respEx.Message}");
                    }
                }
                else
                {
                    BannerBrosModule.LogMessage($"Failed to create hero: {result.ErrorMessage}");
                    // Fall back to requiring character creation
                    player.State = PlayerState.InMenu;
                }
            }
            catch (Exception heroEx)
            {
                BannerBrosModule.LogMessage($"ERROR creating hero: {heroEx.Message}");
                BannerBrosModule.LogMessage($"Stack: {heroEx.StackTrace}");
                player.State = PlayerState.InMenu;
            }
        }
        else if (hasValidSavedCharacter && savedCharacter != null)
        {
            try
            {
                // Reclaiming saved character
                player.HeroId = savedCharacter.HeroId;
                player.ClanId = savedCharacter.ClanId;
                player.PartyId = savedCharacter.PartyId;
                player.State = PlayerState.OnMap;
                UpdatePlayerPositionFromHero(player);
            }
            catch (Exception savedEx)
            {
                BannerBrosModule.LogMessage($"ERROR reclaiming saved character: {savedEx.Message}");
                player.State = PlayerState.InMenu;
            }
        }

        try
        {
            _playerManager.AddPlayer(player);
        }
        catch (Exception addEx)
        {
            BannerBrosModule.LogMessage($"ERROR adding player: {addEx.Message}");
        }

        // Notify other players
        try
        {
            BroadcastPlayerJoined(player);
        }
        catch (Exception broadcastEx)
        {
            BannerBrosModule.LogMessage($"Warning: Failed to broadcast player joined: {broadcastEx.Message}");
        }

        // Send full state sync
        try
        {
            SendFullStateSync(peerId);
        }
        catch (Exception syncEx)
        {
            BannerBrosModule.LogMessage($"Warning: Failed to send state sync: {syncEx.Message}");
        }

        BannerBrosModule.LogMessage($"Join processing complete for {packet.PlayerName} (Player count: {_playerManager.PlayerCount})");
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

        // ToList() to avoid collection modified exception
        foreach (var player in _playerManager.Players.Values.ToList())
        {
            try
            {
                infos.Add(new ConnectedPlayerInfo
                {
                    NetworkId = player.NetworkId,
                    Name = player.Name ?? "Unknown",
                    HeroId = player.HeroId,
                    ClanId = player.ClanId,
                    KingdomId = player.KingdomId,
                    MapX = player.MapPositionX,
                    MapY = player.MapPositionY,
                    IsHost = player.IsHost
                });
            }
            catch
            {
                // Skip player if there's an error
            }
        }

        return infos;
    }

    private void SendFullStateSync(int peerId)
    {
        try
        {
            var playerStates = new List<PlayerStatePacket>();
            // ToList() to avoid collection modified exception
            foreach (var player in _playerManager.Players.Values.ToList())
            {
                try
                {
                    playerStates.Add(CreatePlayerStatePacket(player));
                }
                catch
                {
                    // Skip player if there's an error creating packet
                }
            }

            var activeBattles = new List<BattleInfo>();
            // ToList() to avoid collection modified exception
            foreach (var battle in _worldStateManager.ActiveBattles.Values.ToList())
            {
                try
                {
                    activeBattles.Add(new BattleInfo
                    {
                        BattleId = battle.BattleId,
                        MapPosition = battle.MapPosition,
                        InitiatorPlayerId = battle.InitiatorPlayerId
                    });
                }
                catch
                {
                    // Skip battle if there's an error
                }
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

    /// <summary>
    /// Creates a hero from exported character data (from full character creation).
    /// This applies all the captured attributes, skills, traits, appearance, and equipment.
    /// </summary>
    private SpawnResult SpawnHeroFromExportedCharacter(ExportedCharacter exportedChar, CoopPlayer player)
    {
        if (Campaign.Current == null)
        {
            return new SpawnResult { Success = false, ErrorMessage = "No active campaign" };
        }

        try
        {
            BannerBrosModule.LogMessage($"Creating hero from exported character: {exportedChar.Name}");

            // Get the culture
            var culture = MBObjectManager.Instance.GetObject<CultureObject>(exportedChar.CultureId);
            if (culture == null)
            {
                culture = Campaign.Current.ObjectManager.GetObjectTypeList<CultureObject>()
                    .FirstOrDefault(c => c.IsMainCulture);
                BannerBrosModule.LogMessage($"Culture '{exportedChar.CultureId}' not found, using default: {culture?.StringId}");
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

            // Create a clan for this player
            var clan = CreatePlayerClan(culture, exportedChar.Name);
            if (clan == null)
            {
                return new SpawnResult { Success = false, ErrorMessage = "Failed to create clan" };
            }

            // Create the hero using HeroCreator
            var characterTemplate = culture.BasicTroop;
            var hero = HeroCreator.CreateSpecialHero(
                characterTemplate,
                spawnSettlement,
                clan,
                null,
                (int)exportedChar.Age
            );

            if (hero == null)
            {
                return new SpawnResult { Success = false, ErrorMessage = "Failed to create hero" };
            }

            // Set hero name
            hero.SetName(new TaleWorlds.Localization.TextObject(exportedChar.Name),
                         new TaleWorlds.Localization.TextObject(exportedChar.Name));

            // Set gender
            try
            {
                // Use reflection to set IsFemale if needed
                if (hero.IsFemale != exportedChar.IsFemale)
                {
                    var isFemaleField = typeof(Hero).GetField("_isFemale",
                        BindingFlags.NonPublic | BindingFlags.Instance);
                    isFemaleField?.SetValue(hero, exportedChar.IsFemale);
                }
            }
            catch (Exception ex)
            {
                BannerBrosModule.LogMessage($"Failed to set gender: {ex.Message}");
            }

            // Apply BodyProperties (appearance)
            ApplyBodyProperties(hero, exportedChar.BodyPropertiesXml);

            // Apply attributes
            ApplyAttributes(hero, exportedChar.Attributes);

            // Apply skills and focus points
            ApplySkillsAndFocus(hero, exportedChar.Skills, exportedChar.FocusPoints);

            // Apply traits
            ApplyTraits(hero, exportedChar.Traits);

            // Apply equipment
            ApplyEquipment(hero, exportedChar.EquipmentIds);

            // Set gold
            try
            {
                hero.ChangeHeroGold(exportedChar.Gold - hero.Gold);
            }
            catch (Exception ex)
            {
                BannerBrosModule.LogMessage($"Failed to set gold: {ex.Message}");
            }

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
                BannerBrosModule.LogMessage($"Created party {party.StringId} for player {exportedChar.Name}");
            }

            BannerBrosModule.LogMessage($"Successfully created hero {hero.Name} from exported character data");

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
            BannerBrosModule.LogMessage($"SpawnHeroFromExportedCharacter error: {ex}");
            return new SpawnResult { Success = false, ErrorMessage = ex.Message };
        }
    }

    private void ApplyBodyProperties(Hero hero, string bodyPropertiesXml)
    {
        if (string.IsNullOrEmpty(bodyPropertiesXml)) return;

        try
        {
            // Try to parse the BodyProperties string
            // Format is typically like: "<BodyProperties version=\"4\" age=\"25\" weight=\"0.5\" build=\"0.5\" key=\"..."
            // or just the key value

            if (BodyProperties.FromString(bodyPropertiesXml, out var bodyProps))
            {
                // Use reflection to set body properties
                var charObjField = typeof(Hero).GetProperty("BodyProperties",
                    BindingFlags.Public | BindingFlags.Instance);

                if (charObjField?.CanWrite == true)
                {
                    charObjField.SetValue(hero, bodyProps);
                    BannerBrosModule.LogMessage("Applied body properties to hero");
                }
                else
                {
                    // Try alternative method
                    var staticBodyField = typeof(Hero).GetField("_staticBodyProperties",
                        BindingFlags.NonPublic | BindingFlags.Instance);
                    if (staticBodyField != null)
                    {
                        staticBodyField.SetValue(hero, bodyProps.StaticProperties);
                        BannerBrosModule.LogMessage("Applied static body properties to hero");
                    }
                }
            }
            else
            {
                BannerBrosModule.LogMessage("Failed to parse body properties string");
            }
        }
        catch (Exception ex)
        {
            BannerBrosModule.LogMessage($"Failed to apply body properties: {ex.Message}");
        }
    }

    private void ApplyAttributes(Hero hero, Dictionary<string, int> attributes)
    {
        if (attributes == null || attributes.Count == 0) return;

        try
        {
            // Use reflection to handle CharacterAttributesEnum which may vary by version
            var enumType = typeof(Hero).Assembly.GetType("TaleWorlds.CampaignSystem.CharacterDevelopment.CharacterAttributesEnum");
            var getAttrMethod = hero.GetType().GetMethod("GetAttributeValue");
            var addAttrMethod = hero.HeroDeveloper?.GetType().GetMethod("AddAttribute");

            if (enumType == null || getAttrMethod == null)
            {
                BannerBrosModule.LogMessage("Could not find attribute methods via reflection");
                return;
            }

            int appliedCount = 0;
            foreach (var attr in attributes)
            {
                try
                {
                    // Use Enum.Parse instead of TryParse for runtime type
                    var attrEnum = Enum.Parse(enumType, attr.Key);
                    var currentValue = (int)(getAttrMethod.Invoke(hero, new object[] { attrEnum }) ?? 0);
                    var diff = attr.Value - currentValue;

                    if (diff != 0 && addAttrMethod != null && hero.HeroDeveloper != null)
                    {
                        addAttrMethod.Invoke(hero.HeroDeveloper, new object[] { attrEnum, diff, false });
                        appliedCount++;
                    }
                }
                catch { }
            }
            BannerBrosModule.LogMessage($"Applied {appliedCount} attributes to hero");
        }
        catch (Exception ex)
        {
            BannerBrosModule.LogMessage($"Failed to apply attributes: {ex.Message}");
        }
    }

    private void ApplySkillsAndFocus(Hero hero, Dictionary<string, int> skills, Dictionary<string, int> focusPoints)
    {
        try
        {
            // Get all skill objects from the game
            var skillObjects = MBObjectManager.Instance?.GetObjectTypeList<SkillObject>();
            if (skillObjects == null)
            {
                BannerBrosModule.LogMessage("Could not get skill objects");
                return;
            }

            // Apply skills
            if (skills != null && skills.Count > 0)
            {
                int appliedCount = 0;
                foreach (var skillEntry in skills)
                {
                    var skill = skillObjects.FirstOrDefault(s => s.StringId == skillEntry.Key);
                    if (skill != null)
                    {
                        try
                        {
                            var currentValue = hero.GetSkillValue(skill);
                            var diff = skillEntry.Value - currentValue;

                            if (diff > 0 && hero.HeroDeveloper != null)
                            {
                                hero.HeroDeveloper.ChangeSkillLevel(skill, diff, false);
                                appliedCount++;
                            }
                        }
                        catch { }
                    }
                }
                BannerBrosModule.LogMessage($"Applied {appliedCount} skills to hero");
            }

            // Apply focus points
            if (focusPoints != null && focusPoints.Count > 0 && hero.HeroDeveloper != null)
            {
                int appliedCount = 0;
                foreach (var focusEntry in focusPoints)
                {
                    var skill = skillObjects.FirstOrDefault(s => s.StringId == focusEntry.Key);
                    if (skill != null)
                    {
                        try
                        {
                            var currentFocus = hero.HeroDeveloper.GetFocus(skill);
                            var diff = focusEntry.Value - currentFocus;

                            if (diff > 0)
                            {
                                hero.HeroDeveloper.AddFocus(skill, diff, false);
                                appliedCount++;
                            }
                        }
                        catch { }
                    }
                }
                BannerBrosModule.LogMessage($"Applied {appliedCount} focus points to hero");
            }
        }
        catch (Exception ex)
        {
            BannerBrosModule.LogMessage($"Failed to apply skills/focus: {ex.Message}");
        }
    }

    private void ApplyTraits(Hero hero, Dictionary<string, int> traits)
    {
        if (traits == null || traits.Count == 0) return;

        try
        {
            // Get all trait objects from the game
            var traitObjects = MBObjectManager.Instance?.GetObjectTypeList<TraitObject>();
            if (traitObjects == null)
            {
                BannerBrosModule.LogMessage("Could not get trait objects");
                return;
            }

            int appliedCount = 0;
            foreach (var traitEntry in traits)
            {
                var trait = traitObjects.FirstOrDefault(t => t.StringId == traitEntry.Key);
                if (trait != null)
                {
                    try
                    {
                        var currentLevel = hero.GetTraitLevel(trait);
                        if (currentLevel != traitEntry.Value)
                        {
                            hero.SetTraitLevel(trait, traitEntry.Value);
                            appliedCount++;
                        }
                    }
                    catch { }
                }
            }
            BannerBrosModule.LogMessage($"Applied {traits.Count} traits to hero");
        }
        catch (Exception ex)
        {
            BannerBrosModule.LogMessage($"Failed to apply traits: {ex.Message}");
        }
    }

    private void ApplyEquipment(Hero hero, List<string> equipmentIds)
    {
        if (equipmentIds == null || equipmentIds.Count == 0) return;

        try
        {
            var equipment = hero.BattleEquipment;
            if (equipment == null) return;

            int slotIndex = 0;
            foreach (var itemId in equipmentIds)
            {
                if (slotIndex >= (int)EquipmentIndex.NumEquipmentSetSlots) break;

                var item = MBObjectManager.Instance.GetObject<ItemObject>(itemId);
                if (item != null)
                {
                    // Find appropriate slot for this item type
                    var slot = GetEquipmentSlotForItem(item);
                    if (slot != EquipmentIndex.None)
                    {
                        equipment[slot] = new EquipmentElement(item);
                    }
                }
                slotIndex++;
            }
            BannerBrosModule.LogMessage($"Applied {equipmentIds.Count} equipment items to hero");
        }
        catch (Exception ex)
        {
            BannerBrosModule.LogMessage($"Failed to apply equipment: {ex.Message}");
        }
    }

    private EquipmentIndex GetEquipmentSlotForItem(ItemObject item)
    {
        // Determine the appropriate equipment slot based on item type
        if (item.HasWeaponComponent)
        {
            // Weapons go in weapon slots 0-3
            return EquipmentIndex.Weapon0;
        }
        else if (item.HasHorseComponent)
        {
            return EquipmentIndex.Horse;
        }
        else if (item.HasArmorComponent)
        {
            var armorType = item.ArmorComponent?.BodyArmor;
            if (item.ItemType == ItemObject.ItemTypeEnum.HeadArmor)
                return EquipmentIndex.Head;
            else if (item.ItemType == ItemObject.ItemTypeEnum.BodyArmor)
                return EquipmentIndex.Body;
            else if (item.ItemType == ItemObject.ItemTypeEnum.LegArmor)
                return EquipmentIndex.Leg;
            else if (item.ItemType == ItemObject.ItemTypeEnum.HandArmor)
                return EquipmentIndex.Gloves;
            else if (item.ItemType == ItemObject.ItemTypeEnum.Cape)
                return EquipmentIndex.Cape;
        }

        return EquipmentIndex.None;
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

            // Method 4: Try direct party spawning via Actions
            if (party == null)
            {
                party = TrySpawnPartyDirectly(hero, clan, spawnSettlement);
            }

            // Method 5: Last resort - create minimal party manually
            if (party == null)
            {
                party = TryCreateMinimalParty(hero, clan, spawnSettlement);
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

                    // Initialize party position - use settlement's gate position
                    var gatePos = settlement.GatePosition;
                    var posVec2 = new Vec2(gatePos.X, gatePos.Y);

                    // Try to initialize the party at position using reflection for API compatibility
                    bool initialized = false;
                    try
                    {
                        // Try InitializeMobilePartyAroundPosition
                        var initMethod = party.GetType().GetMethod("InitializeMobilePartyAroundPosition");
                        if (initMethod != null)
                        {
                            initMethod.Invoke(party, new object[] { posVec2, 1f });
                            initialized = true;
                        }
                    }
                    catch { }

                    if (!initialized)
                    {
                        try
                        {
                            // Try InitializeMobilePartyAtPosition
                            var initMethod = party.GetType().GetMethod("InitializeMobilePartyAtPosition");
                            if (initMethod != null)
                            {
                                initMethod.Invoke(party, new object[] { memberRoster, prisonerRoster, posVec2 });
                                initialized = true;
                            }
                        }
                        catch { }
                    }

                    if (!initialized)
                    {
                        // Last resort - just set position via reflection
                        try
                        {
                            var posProp = party.GetType().GetProperty("Position2D");
                            posProp?.SetValue(party, posVec2);
                        }
                        catch { }
                    }

                    party.MemberRoster.Add(memberRoster);
                    party.PrisonRoster.Add(prisonerRoster);
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

    private MobileParty? TrySpawnPartyDirectly(Hero hero, Clan clan, Settlement settlement)
    {
        try
        {
            BannerBrosModule.LogMessage("Trying direct party spawn...");

            // Try using EnterSettlementAction then LeaveSettlementAction
            // This can trigger automatic party creation for lords
            try
            {
                EnterSettlementAction.ApplyForCharacterOnly(hero, settlement);
                BannerBrosModule.LogMessage($"Hero entered settlement {settlement.Name}");

                // Check if party was created
                if (hero.PartyBelongedTo != null)
                {
                    LeaveSettlementAction.ApplyForCharacterOnly(hero);
                    BannerBrosModule.LogMessage("Party created via settlement entry");
                    return hero.PartyBelongedTo;
                }
            }
            catch (Exception ex)
            {
                BannerBrosModule.LogMessage($"Settlement entry approach failed: {ex.Message}");
            }

            // Try using AddCompanionAction approach - make hero a "companion" temporarily
            // then promote to party leader
            try
            {
                // Look for SpawnLordParty method
                var assembly = typeof(MobileParty).Assembly;
                var helperType = assembly.GetType("TaleWorlds.CampaignSystem.Actions.HeroSpawnCampaignBehavior") ??
                                 assembly.GetType("TaleWorlds.CampaignSystem.CampaignBehaviors.HeroSpawnCampaignBehavior");

                if (helperType != null)
                {
                    var spawnMethod = helperType.GetMethod("SpawnLordParty",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance);

                    if (spawnMethod != null)
                    {
                        BannerBrosModule.LogMessage($"Found SpawnLordParty in {helperType.Name}");
                    }
                }
            }
            catch { }
        }
        catch (Exception ex)
        {
            BannerBrosModule.LogMessage($"Direct spawn failed: {ex.Message}");
        }
        return null;
    }

    private MobileParty? TryCreateMinimalParty(Hero hero, Clan clan, Settlement settlement)
    {
        try
        {
            BannerBrosModule.LogMessage("Trying minimal party creation...");

            // Create a party ID
            var partyId = $"coop_party_{hero.StringId}_{DateTime.Now.Ticks}";

            // Try to create party with no component (some versions allow this)
            try
            {
                var createMethod = typeof(MobileParty).GetMethod("CreateParty",
                    BindingFlags.Public | BindingFlags.Static,
                    null,
                    new[] { typeof(string), typeof(PartyComponent), typeof(Func<MobileParty, PartyComponent>) },
                    null);

                if (createMethod != null)
                {
                    // Create with a delegate that returns a warband component
                    Func<MobileParty, PartyComponent> componentDelegate = (party) =>
                    {
                        // Try to create a simple component
                        try
                        {
                            var warbandType = typeof(MobileParty).Assembly.GetType(
                                "TaleWorlds.CampaignSystem.Party.PartyComponents.WarPartyComponent");
                            if (warbandType != null)
                            {
                                var ctor = warbandType.GetConstructors(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance)
                                    .FirstOrDefault();
                                if (ctor != null)
                                {
                                    return ctor.Invoke(new object[] { clan, hero }) as PartyComponent;
                                }
                            }
                        }
                        catch { }
                        return null!;
                    };

                    var party = createMethod.Invoke(null, new object[] { partyId, null!, componentDelegate }) as MobileParty;
                    if (party != null)
                    {
                        // Initialize with hero
                        party.MemberRoster.AddToCounts(hero.CharacterObject, 1);
                        party.ActualClan = clan;

                        // Set position
                        var gatePos = settlement.GatePosition;
                        try
                        {
                            var setPosProp = party.GetType().GetProperty("Position2D");
                            setPosProp?.SetValue(party, new Vec2(gatePos.X, gatePos.Y));
                        }
                        catch { }

                        BannerBrosModule.LogMessage("Created minimal party");
                        return party;
                    }
                }
            }
            catch (Exception ex)
            {
                BannerBrosModule.LogMessage($"Minimal party via CreateParty failed: {ex.Message}");
            }

            // Last attempt: Use the simplest CreateParty overload
            try
            {
                var simpleCreateMethod = typeof(MobileParty).GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .Where(m => m.Name == "CreateParty")
                    .OrderBy(m => m.GetParameters().Length)
                    .FirstOrDefault();

                if (simpleCreateMethod != null)
                {
                    var parms = simpleCreateMethod.GetParameters();
                    BannerBrosModule.LogMessage($"Found CreateParty with {parms.Length} params: {string.Join(", ", parms.Select(p => p.ParameterType.Name))}");

                    // Try to invoke with appropriate args
                    object?[] args = new object?[parms.Length];
                    for (int i = 0; i < parms.Length; i++)
                    {
                        if (parms[i].ParameterType == typeof(string))
                            args[i] = partyId;
                        else if (parms[i].ParameterType.IsSubclassOf(typeof(PartyComponent)) || parms[i].ParameterType == typeof(PartyComponent))
                            args[i] = null;
                        else if (parms[i].ParameterType.IsGenericType)
                            args[i] = null;
                        else
                            args[i] = null;
                    }

                    var party = simpleCreateMethod.Invoke(null, args) as MobileParty;
                    if (party != null)
                    {
                        party.MemberRoster.AddToCounts(hero.CharacterObject, 1);
                        party.ActualClan = clan;
                        BannerBrosModule.LogMessage("Created party via simple CreateParty");
                        return party;
                    }
                }
            }
            catch (Exception ex)
            {
                BannerBrosModule.LogMessage($"Simple CreateParty failed: {ex.Message}");
            }
        }
        catch (Exception ex)
        {
            BannerBrosModule.LogMessage($"Minimal party creation failed: {ex.Message}");
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
        var module = BannerBrosModule.Instance;
        var config = module?.Config;

        // Check if we have exported character data to send
        var exportedChar = module?.PendingExportedCharacter;
        var hasExportedChar = exportedChar != null;

        var packet = new JoinRequestPacket
        {
            PlayerName = exportedChar?.Name ?? config?.PlayerName ?? "Player",
            ModVersion = ModVersion,
            HasExistingCharacter = hasExportedChar,
            ExportedCharacterJson = hasExportedChar ? exportedChar!.ToJson() : ""
        };

        NetworkManager.Instance?.SendToServer(packet);

        if (hasExportedChar)
        {
            BannerBrosModule.LogMessage($"Sent join request with character: {exportedChar!.Name}");
            // Clear pending character after sending to prevent re-send on reconnect
            if (module != null)
            {
                module.PendingExportedCharacter = null;
            }
        }
        else
        {
            BannerBrosModule.LogMessage("Sent join request to host");
        }
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
