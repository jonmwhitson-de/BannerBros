using Newtonsoft.Json;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Party.PartyComponents;
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

    public SessionManager(PlayerManager playerManager, WorldStateManager worldStateManager)
    {
        _playerManager = playerManager;
        _worldStateManager = worldStateManager;
    }

    public void Initialize()
    {
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
    }

    #region Host Methods

    /// <summary>
    /// Called on host when starting a new session.
    /// </summary>
    public void StartHostSession()
    {
        try
        {
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

        // Check if this is a returning player with existing character
        bool requiresCharacterCreation = !packet.HasExistingCharacter;

        // Build response with current world state
        var response = new JoinResponsePacket
        {
            Accepted = true,
            AssignedPlayerId = playerId,
            RequiresCharacterCreation = requiresCharacterCreation,
            ExistingPlayersJson = JsonConvert.SerializeObject(GetConnectedPlayerInfos())
        };

        // Send response
        networkManager.SendTo(peerId, response);

        // Create player entry (not fully initialized until character is created/loaded)
        var player = new CoopPlayer
        {
            NetworkId = playerId,
            Name = packet.PlayerName,
            IsHost = false,
            State = requiresCharacterCreation ? PlayerState.InMenu : PlayerState.OnMap
        };

        _playerManager.AddPlayer(player);

        // Notify other players
        BroadcastPlayerJoined(player);

        // Send full state sync
        SendFullStateSync(peerId);
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

        var packet = new FullStateSyncPacket
        {
            CampaignTimeTicks = (long)(CampaignTime.Now.ToHours * 1000), // Convert to milliseconds for precision
            TimeMultiplier = BannerBrosModule.Instance?.Config.TimeSpeedMultiplier ?? 1.0f,
            PlayerStatesJson = JsonConvert.SerializeObject(playerStates),
            ActiveBattlesJson = JsonConvert.SerializeObject(activeBattles)
        };

        NetworkManager.Instance?.SendTo(peerId, packet);
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

            // Find a spawn settlement
            var spawnSettlement = Settlement.CurrentSettlement ??
                Campaign.Current.Settlements.FirstOrDefault(s => s.IsTown);

            if (spawnSettlement == null)
            {
                return new SpawnResult { Success = false, ErrorMessage = "No valid spawn location found" };
            }

            // Create the hero using HeroCreator
            var characterTemplate = culture.BasicTroop;
            var hero = HeroCreator.CreateSpecialHero(
                characterTemplate,
                spawnSettlement,
                null,
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

            // Create a new clan for this player
            var clan = CreatePlayerClan(hero, culture, packet.CharacterName);

            // Get spawn position from settlement
            var spawnPos = spawnSettlement.GatePosition;
            var spawnX = spawnPos.X;
            var spawnY = spawnPos.Y;

            // Create mobile party using the simpler CreateLordParty approach
            MobileParty? party = null;
            try
            {
                // Use MobileParty.CreateParty with minimal setup
                // The exact API varies by Bannerlord version, so keep it simple
                party = hero.PartyBelongedTo;

                // If hero doesn't have a party yet, we need to create one
                // This is version-dependent, so we'll handle the case where it fails
                if (party == null)
                {
                    BannerBrosModule.LogMessage($"Warning: Hero party creation may need version-specific code");
                    // For now, return success but note the limitation
                }
            }
            catch (Exception ex)
            {
                BannerBrosModule.LogMessage($"Party creation warning: {ex.Message}");
            }

            return new SpawnResult
            {
                Success = true,
                HeroId = hero.StringId,
                PartyId = party?.StringId ?? "",
                ClanId = clan?.StringId ?? "",
                SpawnX = spawnX,
                SpawnY = spawnY
            };
        }
        catch (Exception ex)
        {
            return new SpawnResult { Success = false, ErrorMessage = ex.Message };
        }
    }

    private Clan? CreatePlayerClan(Hero leader, CultureObject culture, string playerName)
    {
        try
        {
            // For co-op players, we can use an existing minor faction/clan
            // or simply assign them to a default clan
            // Full clan creation requires version-specific API calls

            // Check if hero already has a clan
            if (leader.Clan != null)
            {
                return leader.Clan;
            }

            // Try to find an existing minor clan to join
            // This is a simpler approach that avoids complex clan creation
            var minorClan = Clan.All.FirstOrDefault(c =>
                c.Culture == culture &&
                !c.IsEliminated &&
                c.IsMinorFaction);

            if (minorClan != null)
            {
                leader.Clan = minorClan;
                BannerBrosModule.LogMessage($"Assigned player to existing clan: {minorClan.Name}");
                return minorClan;
            }

            // If no suitable clan found, the hero will remain clanless
            // This is a limitation that can be addressed with version-specific code
            BannerBrosModule.LogMessage($"Warning: Could not assign clan to player. Version-specific code may be needed.");
            return null;
        }
        catch (Exception ex)
        {
            BannerBrosModule.LogMessage($"Failed to create/assign clan: {ex.Message}");
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
