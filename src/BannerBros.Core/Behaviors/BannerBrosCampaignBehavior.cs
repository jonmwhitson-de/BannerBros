using Newtonsoft.Json;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.CampaignSystem.Conversation;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;
using BannerBros.Core.StateSync;
using BannerBros.Network;
using LiteNetLib;

namespace BannerBros.Core;

/// <summary>
/// Main campaign behavior for BannerBros co-op functionality.
/// </summary>
public class BannerBrosCampaignBehavior : CampaignBehaviorBase
{
    private float _syncTimer;
    private float _worldSyncTimer;
    private float _partyBatchTimer;
    private const float SyncInterval = 0.1f; // 10 times per second
    private const float WorldSyncInterval = 1.0f; // Once per second
    private const float PartyBatchInterval = 0.5f; // Batch sync of all parties (2x per second)

    private float _lastX;
    private float _lastY;
    private PlayerState _lastState;
    private bool _campaignReady;
    private float _readyCheckTimer;
    private bool _joinPopupShown;
    private int _batchSequenceNumber;

    public override void RegisterEvents()
    {
        CampaignEvents.OnSessionLaunchedEvent.AddNonSerializedListener(this, OnSessionLaunched);
        CampaignEvents.TickEvent.AddNonSerializedListener(this, OnTick);
        CampaignEvents.HourlyTickEvent.AddNonSerializedListener(this, OnHourlyTick);
        CampaignEvents.DailyTickEvent.AddNonSerializedListener(this, OnDailyTick);
        CampaignEvents.OnBeforeSaveEvent.AddNonSerializedListener(this, OnBeforeSave);
    }

    public override void SyncData(IDataStore dataStore)
    {
        // SyncData is called during save/load
        // We use external JSON file instead of save data to avoid compatibility issues
    }

    private void OnBeforeSave()
    {
        // Save player data to external file when game saves
        var module = BannerBrosModule.Instance;
        if (module?.IsHost == true && module.IsConnected)
        {
            module.PlayerSaveData.Save();
            BannerBrosModule.LogMessage("Player data saved with game");
        }
    }

    private void OnSessionLaunched(CampaignGameStarter starter)
    {
        BannerBrosModule.LogMessage("Campaign session started");
        _syncTimer = 0;
        _worldSyncTimer = 0;
        _campaignReady = false;
        _readyCheckTimer = 0;
        _joinPopupShown = false;

        // Load saved player data for this campaign
        var module = BannerBrosModule.Instance;
        if (module != null)
        {
            module.PlayerSaveData = PlayerSaveData.Load();
        }
    }

    private void OnTick(float dt)
    {
        try
        {
            var module = BannerBrosModule.Instance;
            if (module == null) return;

            // Check if campaign is ready (needed for both connected and not connected states)
            if (!_campaignReady)
            {
                _readyCheckTimer += dt;
                // Check every 0.5 seconds to avoid spam, start checking after 1 second
                if (_readyCheckTimer >= 1.0f && (_readyCheckTimer - 1.0f) % 0.5f < dt)
                {
                    if (IsCampaignReady())
                    {
                        _campaignReady = true;
                        BannerBrosModule.LogMessage("Campaign ready");

                        if (module.IsConnected)
                        {
                            // Already connected - initialize co-op sync
                            BannerBrosModule.LogMessage("Starting co-op sync");
                            if (module.IsHost)
                            {
                                LinkHostToMainHero();
                            }
                            else
                            {
                                SendCampaignReadyToHost();
                            }
                        }
                        else if (!_joinPopupShown)
                        {
                            // Not connected - show join popup
                            _joinPopupShown = true;
                            ShowJoinCoopPopup();
                        }
                    }
                }

                // If not connected, don't do sync operations
                if (!module.IsConnected) return;
                return;
            }

            // Not connected - nothing else to do
            if (!module.IsConnected) return;

            // Accumulate time
            _syncTimer += dt;
            _worldSyncTimer += dt;
            _partyBatchTimer += dt;

            // Sync player states at fixed interval
            if (_syncTimer >= SyncInterval)
            {
                _syncTimer = 0;
                SyncLocalPlayerState();
            }

            // Host syncs world state less frequently
            if (module.IsHost && _worldSyncTimer >= WorldSyncInterval)
            {
                _worldSyncTimer = 0;
                SyncWorldState();
            }

            // Host broadcasts all party positions periodically (efficient batch sync)
            if (module.IsHost && _partyBatchTimer >= PartyBatchInterval)
            {
                _partyBatchTimer = 0;
                BroadcastWorldPartyBatch();
            }

            // Apply pending state updates (client only)
            if (!module.IsHost)
            {
                StateSyncManager.Instance.ApplyPendingUpdates();
            }

            // Ensure all co-op player parties are visible on the map
            EnsureCoopPartiesVisible();
        }
        catch (Exception ex)
        {
            BannerBrosModule.LogMessage($"OnTick error: {ex.Message}");
        }
    }

    /// <summary>
    /// Shows a popup asking the player if they want to join a co-op server.
    /// </summary>
    private void ShowJoinCoopPopup()
    {
        try
        {
            InformationManager.ShowInquiry(
                new InquiryData(
                    "Co-op Multiplayer",
                    "Would you like to join a co-op server?\n\n" +
                    "You can play with friends by connecting to a host's game. " +
                    "Both players will see each other on the campaign map!",
                    true,
                    true,
                    "Join Server",
                    "Play Solo",
                    OnJoinCoopAccepted,
                    null
                ),
                true
            );
        }
        catch (Exception ex)
        {
            BannerBrosModule.LogMessage($"Error showing join popup: {ex.Message}");
        }
    }

    private void OnJoinCoopAccepted()
    {
        // Show the IP entry dialog
        try
        {
            var lastAddress = BannerBrosModule.Instance?.Config.LastServerAddress ?? "";
            InformationManager.ShowTextInquiry(
                new TextInquiryData(
                    "Join Co-op Server",
                    "Enter the host's IP address:\n\nExample: 192.168.1.100 or 192.168.1.100:7777",
                    true,
                    true,
                    "Connect",
                    "Cancel",
                    OnServerAddressEntered,
                    null,
                    false,
                    text => new Tuple<bool, string>(!string.IsNullOrWhiteSpace(text), "Address cannot be empty"),
                    "",
                    lastAddress
                )
            );
        }
        catch (Exception ex)
        {
            BannerBrosModule.LogMessage($"Error showing IP dialog: {ex.Message}");
        }
    }

    private void OnServerAddressEntered(string address)
    {
        var module = BannerBrosModule.Instance;
        if (module == null) return;

        // Parse address
        var parts = address.Split(':');
        var serverAddress = parts[0];
        var serverPort = parts.Length > 1 && int.TryParse(parts[1], out var p)
            ? p
            : module.Config.DefaultPort;

        // Save for next time
        module.Config.LastServerAddress = address;

        BannerBrosModule.LogMessage($"Connecting to {serverAddress}:{serverPort}...");

        // Join the session
        module.JoinSession(serverAddress, serverPort);

        // Show connecting message
        InformationManager.ShowInquiry(
            new InquiryData(
                "Connecting...",
                $"Connecting to {serverAddress}:{serverPort}...\n\n" +
                "Your party will sync with the host's world.\n" +
                "You'll see other players on the campaign map!",
                true,
                false,
                "OK",
                "",
                null,
                null
            ),
            true
        );
    }

    private float _visibilityCheckTimer = 0;
    private const float VisibilityCheckInterval = 1.0f;

    /// <summary>
    /// Ensures all co-op player parties are visible on the campaign map.
    /// Also handles position synchronization between network data and local parties.
    /// </summary>
    private void EnsureCoopPartiesVisible()
    {
        _visibilityCheckTimer += SyncInterval;
        if (_visibilityCheckTimer < VisibilityCheckInterval) return;
        _visibilityCheckTimer = 0;

        var module = BannerBrosModule.Instance;
        if (module?.IsConnected != true) return;
        if (Campaign.Current == null) return;

        var isHost = module.IsHost;
        var localPlayer = module.PlayerManager.GetLocalPlayer();
        var localMainPartyId = MobileParty.MainParty?.StringId;

        foreach (var player in module.PlayerManager.Players.Values.ToList())
        {
            if (string.IsNullOrEmpty(player.PartyId)) continue;

            try
            {
                bool isLocalParty = (localPlayer != null && player.NetworkId == localPlayer.NetworkId);

                // For remote players on the client, we need to handle party ID conflicts
                // If the remote player's party ID matches our local party ID, use a remapped ID
                string effectivePartyId = player.PartyId;
                if (!isHost && !isLocalParty && player.PartyId == localMainPartyId)
                {
                    // This remote player has the same party ID as us - use shadow ID
                    effectivePartyId = $"remote_{player.PartyId}";
                }

                var party = Campaign.Current.MobileParties
                    .FirstOrDefault(p => p.StringId == effectivePartyId);

                // On client, create shadow party for remote players if not found
                if (party == null && !isHost && !isLocalParty)
                {
                    // Try to create shadow party for this remote player
                    party = CreateClientShadowPartyForPlayer(effectivePartyId, player);
                }

                if (party != null)
                {
                    // Ensure party is visible
                    if (!party.IsVisible)
                    {
                        try { party.IsVisible = true; }
                        catch { }
                    }

                    if (isLocalParty)
                    {
                        // This is OUR party - read position from party to player (local -> network)
                        var pos = party.GetPosition2D;
                        player.MapPositionX = pos.x;
                        player.MapPositionY = pos.y;
                    }
                    else
                    {
                        // This is ANOTHER player's party - apply network position to party
                        // (except on host where we have direct control)
                        if (!isHost)
                        {
                            // Use proper CampaignVec2 positioning
                            TeleportPartyToPosition(party, player.MapPositionX, player.MapPositionY);

                            // Disable AI to prevent local movement
                            try { party.Ai?.SetDoNotMakeNewDecisions(true); } catch { }
                        }
                        else
                        {
                            // Host: Read actual party position to player data (for broadcasting)
                            var pos = party.GetPosition2D;
                            player.MapPositionX = pos.x;
                            player.MapPositionY = pos.y;
                        }
                    }
                }
            }
            catch { }
        }
    }

    /// <summary>
    /// Creates a shadow party on the client to represent a remote player.
    /// </summary>
    private MobileParty? CreateClientShadowPartyForPlayer(string partyId, CoopPlayer player)
    {
        try
        {
            if (Campaign.Current == null) return null;

            BannerBrosModule.LogMessage($"[Sync] Creating shadow party: {partyId} for {player.Name} at ({player.MapPositionX:F1}, {player.MapPositionY:F1})");

            // Create a minimal party to represent the remote player
            var party = MobileParty.CreateParty(partyId, null);

            if (party == null)
            {
                BannerBrosModule.LogMessage($"[Sync] MobileParty.CreateParty returned null for {partyId}");
                return null;
            }

            // Set position using CampaignVec2
            TeleportPartyToPosition(party, player.MapPositionX, player.MapPositionY);

            // Make visible on map
            try { party.IsVisible = true; } catch { }

            // Disable AI
            try { party.Ai?.SetDoNotMakeNewDecisions(true); } catch { }

            // Set party name if possible
            try
            {
                var nameProp = party.GetType().GetProperty("Name");
                if (nameProp?.CanWrite == true)
                {
                    nameProp.SetValue(party, new TaleWorlds.Localization.TextObject($"{player.Name}'s Warband"));
                }
            }
            catch { }

            var finalPos = party.GetPosition2D;
            BannerBrosModule.LogMessage($"[Sync] Shadow party created: {partyId} at ({finalPos.x:F1}, {finalPos.y:F1})");
            return party;
        }
        catch (Exception ex)
        {
            BannerBrosModule.LogMessage($"[Sync] Error creating shadow party: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Teleports a party to a specific position using CampaignVec2.
    /// </summary>
    private void TeleportPartyToPosition(MobileParty party, float x, float y)
    {
        if (party == null) return;

        try
        {
            var vec2Pos = new Vec2(x, y);

            // Try to create CampaignVec2 and set position
            var campaignVec2Type = typeof(Campaign).Assembly.GetType("TaleWorlds.CampaignSystem.CampaignVec2");
            if (campaignVec2Type != null)
            {
                object? campaignVec2 = null;

                // Try (Vec2, Boolean) constructor first - common in Bannerlord
                var ctor = campaignVec2Type.GetConstructor(new[] { typeof(Vec2), typeof(bool) });
                if (ctor != null)
                {
                    campaignVec2 = ctor.Invoke(new object[] { vec2Pos, true }); // true = IsOnLand
                }
                else
                {
                    // Try (float, float) constructor
                    ctor = campaignVec2Type.GetConstructor(new[] { typeof(float), typeof(float) });
                    if (ctor != null)
                    {
                        campaignVec2 = ctor.Invoke(new object[] { x, y });
                    }
                }

                if (campaignVec2 != null)
                {
                    // Try setting _position field
                    var posField = typeof(MobileParty).GetField("_position",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

                    if (posField != null && posField.FieldType == campaignVec2Type)
                    {
                        posField.SetValue(party, campaignVec2);
                        return;
                    }

                    // Try Position2D property
                    var posProp = typeof(MobileParty).GetProperty("Position2D");
                    if (posProp?.CanWrite == true)
                    {
                        if (posProp.PropertyType == campaignVec2Type)
                        {
                            posProp.SetValue(party, campaignVec2);
                            return;
                        }
                    }
                }
            }

            // Fallback: try Position2D with Vec2
            try
            {
                var posProp = typeof(MobileParty).GetProperty("Position2D");
                if (posProp?.CanWrite == true && posProp.PropertyType == typeof(Vec2))
                {
                    posProp.SetValue(party, vec2Pos);
                }
            }
            catch { }
        }
        catch (Exception ex)
        {
            BannerBrosModule.LogMessage($"[Sync] TeleportPartyToPosition error: {ex.Message}");
        }
    }

    /// <summary>
    /// Checks if the campaign is fully initialized and safe to access game state.
    /// </summary>
    private bool IsCampaignReady()
    {
        try
        {
            // Check that essential campaign systems are available
            if (Campaign.Current == null) return false;
            if (Hero.MainHero == null) return false;
            if (MobileParty.MainParty == null) return false;

            // Try to access CampaignTime to ensure time system is ready
            var _ = CampaignTime.Now.ToHours;

            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Called on client when their campaign loads - notifies host so shadow hero can be created.
    /// </summary>
    private void SendCampaignReadyToHost()
    {
        try
        {
            var module = BannerBrosModule.Instance;
            var networkManager = NetworkManager.Instance;
            if (module == null || networkManager == null || !networkManager.IsRunning) return;

            var localPlayer = module.PlayerManager.GetLocalPlayer();
            if (localPlayer == null)
            {
                BannerBrosModule.LogMessage("Error: No local player found when sending campaign ready");
                return;
            }

            // Get info about our MainHero
            var hero = Hero.MainHero;
            var party = MobileParty.MainParty;

            if (hero == null)
            {
                BannerBrosModule.LogMessage("Error: No MainHero found when sending campaign ready");
                return;
            }

            var pos = party?.GetPosition2D ?? new TaleWorlds.Library.Vec2(0, 0);

            var packet = new ClientCampaignReadyPacket
            {
                PlayerId = localPlayer.NetworkId,
                HeroName = hero.Name?.ToString() ?? localPlayer.Name,
                HeroId = hero.StringId,
                ClanId = hero.Clan?.StringId ?? "",
                CultureId = hero.Culture?.StringId ?? "",
                MapX = pos.x,
                MapY = pos.y,
                IsFemale = hero.IsFemale,
                Age = (int)hero.Age
            };

            // Try to get body properties
            try
            {
                packet.BodyPropertiesXml = hero.BodyProperties.ToString();
            }
            catch { }

            BannerBrosModule.LogMessage($"Sending campaign ready: {packet.HeroName} at ({packet.MapX}, {packet.MapY})");
            networkManager.SendToServer(packet);

            // Update local player info
            localPlayer.HeroId = hero.StringId;
            localPlayer.ClanId = hero.Clan?.StringId;
            localPlayer.PartyId = party?.StringId;
            localPlayer.MapPositionX = pos.x;
            localPlayer.MapPositionY = pos.y;
        }
        catch (Exception ex)
        {
            BannerBrosModule.LogMessage($"SendCampaignReadyToHost error: {ex.Message}");
        }
    }

    /// <summary>
    /// Links the host player to the main hero after campaign is fully loaded.
    /// </summary>
    private void LinkHostToMainHero()
    {
        try
        {
            var module = BannerBrosModule.Instance;
            if (module == null) return;

            var hostPlayer = module.PlayerManager.GetPlayer(0);
            if (hostPlayer == null) return;

            hostPlayer.HeroId = Hero.MainHero.StringId;
            hostPlayer.ClanId = Hero.MainHero.Clan?.StringId;
            hostPlayer.KingdomId = Hero.MainHero.Clan?.Kingdom?.StringId;

            if (MobileParty.MainParty != null)
            {
                hostPlayer.PartyId = MobileParty.MainParty.StringId;
                var pos = MobileParty.MainParty.GetPosition2D;
                hostPlayer.MapPositionX = pos.x;
                hostPlayer.MapPositionY = pos.y;

                // Register host's party for state synchronization
                StateSyncManager.Instance.RegisterParty(MobileParty.MainParty);
                StateSyncManager.Instance.RegisterHero(Hero.MainHero);
            }

            BannerBrosModule.LogMessage($"Host linked to hero: {Hero.MainHero.Name}");
        }
        catch (Exception ex)
        {
            BannerBrosModule.LogMessage($"Error linking host to hero: {ex.Message}");
        }
    }

    private void OnHourlyTick()
    {
        // Don't sync until campaign is ready
        if (!_campaignReady) return;

        // Additional hourly sync for less time-critical data
        var module = BannerBrosModule.Instance;
        if (module?.IsHost != true) return;

        try
        {
            // Host broadcasts comprehensive world state update
            BroadcastFullWorldSync();
        }
        catch (Exception ex)
        {
            BannerBrosModule.LogMessage($"OnHourlyTick error: {ex.Message}");
        }
    }

    private void OnDailyTick()
    {
        // Don't sync until campaign is ready
        if (!_campaignReady) return;

        // Daily sync for diplomacy changes, etc.
    }

    private void SyncLocalPlayerState()
    {
        try
        {
            var module = BannerBrosModule.Instance;
            if (module?.IsConnected != true) return;

            var localPlayer = module.PlayerManager.GetLocalPlayer();
            if (localPlayer == null) return;

            // Get current state
            var mainParty = MobileParty.MainParty;
            float currentX = localPlayer.MapPositionX;
            float currentY = localPlayer.MapPositionY;
            var currentState = localPlayer.State;

            if (mainParty != null)
            {
                var pos = mainParty.GetPosition2D;
                currentX = pos.x;
                currentY = pos.y;
                localPlayer.MapPositionX = currentX;
                localPlayer.MapPositionY = currentY;
                localPlayer.PartyId = mainParty.StringId;
            }

            // Determine current state
            currentState = DeterminePlayerState();
            localPlayer.State = currentState;

            // Update hero/clan info if available
            if (Hero.MainHero != null)
            {
                localPlayer.HeroId = Hero.MainHero.StringId;
                localPlayer.ClanId = Hero.MainHero.Clan?.StringId;
                localPlayer.KingdomId = Hero.MainHero.Clan?.Kingdom?.StringId;
            }

            // Only send if something changed (delta compression)
            bool positionChanged = Math.Abs(currentX - _lastX) > 0.01f || Math.Abs(currentY - _lastY) > 0.01f;
            bool stateChanged = currentState != _lastState;

            if (positionChanged || stateChanged)
            {
                _lastX = currentX;
                _lastY = currentY;
                _lastState = currentState;

                SendPlayerStateUpdate(localPlayer, mainParty);
            }
        }
        catch (Exception ex)
        {
            // Log but don't crash - sync will retry next tick
            BannerBrosModule.LogMessage($"SyncLocalPlayerState error: {ex.Message}");
        }
    }

    private void SendPlayerStateUpdate(CoopPlayer player, MobileParty? party)
    {
        var networkManager = NetworkManager.Instance;
        if (networkManager == null || !networkManager.IsRunning) return;

        var packet = new PlayerStatePacket
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
            PartySize = party?.MemberRoster?.TotalManCount ?? 0,
            PartySpeed = party?.Speed ?? 0,
            IsInBattle = player.CurrentBattleId != null,
            BattleId = player.CurrentBattleId ?? ""
        };

        // Send with unreliable delivery for frequent position updates (reduces latency)
        if (networkManager.IsHost)
        {
            // Host sends to all clients
            networkManager.Send(packet, DeliveryMethod.Sequenced);
        }
        else
        {
            // Client sends to server
            networkManager.SendToServer(packet, DeliveryMethod.Sequenced);
        }
    }

    private PlayerState DeterminePlayerState()
    {
        if (Campaign.Current == null)
            return PlayerState.OnMap;

        // Check if in battle/mission
        if (Mission.Current != null)
            return PlayerState.InBattle;

        // Check if in dialogue
        if (Campaign.Current.ConversationManager?.IsConversationInProgress == true)
            return PlayerState.InDialogue;

        // Check if in settlement
        var currentSettlement = Settlement.CurrentSettlement;
        if (currentSettlement != null)
        {
            if (currentSettlement.IsTown)
                return PlayerState.InTown;
            if (currentSettlement.IsVillage)
                return PlayerState.InVillage;
            if (currentSettlement.IsCastle)
                return PlayerState.InCastle;
        }

        // Check if in trade screen (this would need specific state checking)
        // For now, we assume if we're not in any of the above, we're on the map

        return PlayerState.OnMap;
    }

    private void SyncWorldState()
    {
        try
        {
            var module = BannerBrosModule.Instance;
            var networkManager = NetworkManager.Instance;

            if (module?.IsHost != true || networkManager == null) return;

            // Update server time
            module.WorldStateManager.UpdateServerTime(CampaignTime.Now);

            // Build battle info list - ToList() to avoid collection modification
            var battles = new List<BattleInfo>();
            foreach (var battle in module.WorldStateManager.ActiveBattles.Values.ToList())
            {
                try
                {
                    battles.Add(new BattleInfo
                    {
                        BattleId = battle.BattleId,
                        MapPosition = battle.MapPosition,
                        InitiatorPlayerId = battle.InitiatorPlayerId,
                        AttackerPlayerIdsJson = JsonConvert.SerializeObject(battle.GetPlayersOnSide(BattleSide.Attacker).ToList()),
                        DefenderPlayerIdsJson = JsonConvert.SerializeObject(battle.GetPlayersOnSide(BattleSide.Defender).ToList())
                    });
                }
                catch
                {
                    // Skip battle if there's an error
                }
            }

            // Send lightweight world sync
            var packet = new WorldSyncPacket
            {
                CampaignTimeTicks = (long)(CampaignTime.Now.ToHours * 1000), // Convert to milliseconds for precision
                TimeMultiplier = module.Config.TimeSpeedMultiplier,
                Season = (int)CampaignTime.Now.GetSeasonOfYear,
                DayOfSeason = CampaignTime.Now.GetDayOfSeason,
                ActiveBattleCount = battles.Count,
                BattleDataJson = JsonConvert.SerializeObject(battles)
            };

            networkManager.Send(packet, DeliveryMethod.ReliableOrdered);
        }
        catch (Exception ex)
        {
            BannerBrosModule.LogMessage($"SyncWorldState error: {ex.Message}");
        }
    }

    private void BroadcastFullWorldSync()
    {
        try
        {
            var module = BannerBrosModule.Instance;
            var networkManager = NetworkManager.Instance;

            if (module?.IsHost != true || networkManager == null) return;

            // Build player states - with null protection
            var playerStates = new List<PlayerStatePacket>();
            foreach (var player in module.PlayerManager.Players.Values.ToList()) // ToList() to avoid collection modification
            {
                try
                {
                    playerStates.Add(new PlayerStatePacket
                    {
                        PlayerId = player.NetworkId,
                        PlayerName = player.Name ?? "Unknown",
                        MapX = player.MapPositionX,
                        MapY = player.MapPositionY,
                        State = (int)player.State,
                        HeroId = player.HeroId ?? "",
                        PartyId = player.PartyId ?? "",
                        ClanId = player.ClanId ?? "",
                        KingdomId = player.KingdomId ?? "",
                        IsInBattle = player.CurrentBattleId != null,
                        BattleId = player.CurrentBattleId ?? ""
                    });
                }
                catch (Exception playerEx)
                {
                    BannerBrosModule.LogMessage($"Warning: Failed to create state packet for player {player?.Name}: {playerEx.Message}");
                }
            }

            // Build active battles - ToList() to avoid collection modification
            var activeBattles = new List<BattleInfo>();
            foreach (var battle in module.WorldStateManager.ActiveBattles.Values.ToList())
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

            // Build diplomacy states (wars, alliances) - wrapped in try/catch for safety
            var diplomacyStates = new List<DiplomacyState>();
            try
            {
                if (Campaign.Current?.Kingdoms != null)
                {
                    // ToList() to avoid collection modification during iteration
                    var kingdoms = Campaign.Current.Kingdoms.ToList();
                    foreach (var kingdom1 in kingdoms)
                    {
                        foreach (var kingdom2 in kingdoms)
                        {
                            try
                            {
                                if (kingdom1.StringId.CompareTo(kingdom2.StringId) < 0) // Avoid duplicates
                                {
                                    var stance = kingdom1.GetStanceWith(kingdom2);
                                    // Check war status and alliance based on available API
                                    var isAtWar = stance.IsAtWar;
                                    // Alliance check - use IsNeutral as inverse indicator if IsAllied not available
                                    var isAllied = !stance.IsAtWar && !stance.IsNeutral;
                                    if (isAtWar || isAllied)
                                    {
                                        diplomacyStates.Add(new DiplomacyState
                                        {
                                            Faction1Id = kingdom1.StringId,
                                            Faction2Id = kingdom2.StringId,
                                            RelationType = isAtWar ? 1 : (isAllied ? 2 : 0)
                                        });
                                    }
                                }
                            }
                            catch
                            {
                                // Skip this kingdom pair if there's an error
                            }
                        }
                    }
                }
            }
            catch (Exception dipEx)
            {
                BannerBrosModule.LogMessage($"Warning: Diplomacy sync error: {dipEx.Message}");
            }

            var packet = new FullStateSyncPacket
            {
                CampaignTimeTicks = (long)(CampaignTime.Now.ToHours * 1000), // Convert to milliseconds for precision
                Year = CampaignTime.Now.GetYear,
                Season = (int)CampaignTime.Now.GetSeasonOfYear,
                TimeMultiplier = module.Config.TimeSpeedMultiplier,
                PlayerStatesJson = JsonConvert.SerializeObject(playerStates),
                ActiveBattlesJson = JsonConvert.SerializeObject(activeBattles),
                DiplomacyStatesJson = JsonConvert.SerializeObject(diplomacyStates)
            };

            networkManager.Send(packet, DeliveryMethod.ReliableOrdered);
        }
        catch (Exception ex)
        {
            BannerBrosModule.LogMessage($"BroadcastFullWorldSync error: {ex.Message}");
        }
    }

    /// <summary>
    /// Broadcasts all mobile party positions to clients in chunked batch packets.
    /// Chunks are needed because the full list exceeds network packet size limits (~32KB).
    /// </summary>
    private void BroadcastWorldPartyBatch()
    {
        try
        {
            var networkManager = NetworkManager.Instance;
            if (networkManager == null || !networkManager.IsRunning) return;
            if (Campaign.Current == null) return;

            // Collect all party positions
            var partyPositions = new List<PartyPositionData>();

            foreach (var party in Campaign.Current.MobileParties.ToList())
            {
                try
                {
                    if (party == null) continue;
                    if (string.IsNullOrEmpty(party.StringId)) continue;

                    var pos = party.GetPosition2D;

                    // Determine party type
                    int partyType = 5; // Default: Other
                    if (party.IsMainParty || party.StringId == "player_party")
                        partyType = 4; // Player
                    else if (party.IsLordParty)
                        partyType = 0; // Lord
                    else if (party.IsBandit)
                        partyType = 1; // Bandit
                    else if (party.IsCaravan)
                        partyType = 2; // Caravan
                    else if (party.IsVillager)
                        partyType = 3; // Villager

                    partyPositions.Add(new PartyPositionData
                    {
                        Id = party.StringId,
                        X = pos.x,
                        Y = pos.y,
                        V = party.IsVisible,
                        F = party.MapFaction?.StringId ?? "",
                        N = party.Name?.ToString() ?? "",
                        T = partyType,
                        S = party.MemberRoster?.TotalManCount ?? 0
                    });
                }
                catch
                {
                    // Skip party if there's an error
                }
            }

            // Add shadow parties (client representatives on host)
            var module = BannerBrosModule.Instance;
            if (module != null)
            {
                foreach (var player in module.PlayerManager.Players.Values.ToList())
                {
                    if (player.IsHost) continue;
                    if (string.IsNullOrEmpty(player.ShadowPartyId)) continue;

                    // Shadow party should already be in the MobileParties list,
                    // but ensure player position is up-to-date
                    var existingEntry = partyPositions.FirstOrDefault(p => p.Id == player.ShadowPartyId);
                    if (existingEntry != null)
                    {
                        existingEntry.X = player.MapPositionX;
                        existingEntry.Y = player.MapPositionY;
                        existingEntry.T = 4; // Mark as player type
                        existingEntry.N = $"{player.Name}'s Party";
                    }
                }
            }

            // Chunk the parties to stay under 32KB packet limit
            // ~200 parties per chunk is safe (~100-150 bytes per party serialized)
            const int ChunkSize = 200;
            int totalParties = partyPositions.Count;
            int totalChunks = (totalParties + ChunkSize - 1) / ChunkSize; // Ceiling division

            _batchSequenceNumber++;
            float campaignTime = (float)CampaignTime.Now.ToHours;

            for (int chunkIndex = 0; chunkIndex < totalChunks; chunkIndex++)
            {
                var chunkParties = partyPositions
                    .Skip(chunkIndex * ChunkSize)
                    .Take(ChunkSize)
                    .ToList();

                var packet = new WorldPartyBatchPacket
                {
                    CampaignTimeHours = campaignTime,
                    PartyCount = chunkParties.Count,
                    PartiesJson = JsonConvert.SerializeObject(chunkParties),
                    SequenceNumber = _batchSequenceNumber,
                    ChunkIndex = chunkIndex,
                    TotalChunks = totalChunks,
                    TotalParties = totalParties
                };

                // Send to all clients
                networkManager.Send(packet, DeliveryMethod.ReliableOrdered);
            }

            // Log once per batch (not per chunk)
            BannerBrosModule.LogMessage($"[WorldPartyBatch] HOST sent {totalParties} parties in {totalChunks} chunks, seq={_batchSequenceNumber}");
        }
        catch (Exception ex)
        {
            BannerBrosModule.LogMessage($"BroadcastWorldPartyBatch error: {ex.Message}");
            BannerBrosModule.LogMessage($"Stack: {ex.StackTrace}");
        }
    }
}
