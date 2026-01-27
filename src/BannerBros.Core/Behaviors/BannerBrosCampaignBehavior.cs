using Newtonsoft.Json;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.CampaignSystem.Conversation;
using TaleWorlds.MountAndBlade;
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
    private const float SyncInterval = 0.1f; // 10 times per second
    private const float WorldSyncInterval = 1.0f; // Once per second

    private float _lastX;
    private float _lastY;
    private PlayerState _lastState;
    private bool _campaignReady;
    private float _readyCheckTimer;

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

        // Load saved player data for this campaign
        var module = BannerBrosModule.Instance;
        if (module != null)
        {
            module.PlayerSaveData = PlayerSaveData.Load();
        }
    }

    private void OnTick(float dt)
    {
        var module = BannerBrosModule.Instance;
        if (module?.IsConnected != true) return;

        // Wait for campaign to be fully ready before syncing
        if (!_campaignReady)
        {
            _readyCheckTimer += dt;
            // Check every 0.5 seconds to avoid spam, start checking after 1 second
            if (_readyCheckTimer >= 1.0f && (_readyCheckTimer - 1.0f) % 0.5f < dt)
            {
                if (IsCampaignReady())
                {
                    _campaignReady = true;
                    BannerBrosModule.LogMessage("Campaign ready - starting co-op sync");

                    // Link host player to main hero now that campaign is ready
                    if (module.IsHost)
                    {
                        LinkHostToMainHero();
                    }
                }
            }
            return;
        }

        // Accumulate time
        _syncTimer += dt;
        _worldSyncTimer += dt;

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

        // Host broadcasts comprehensive world state update
        BroadcastFullWorldSync();
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

            // Build battle info list
            var battles = new List<BattleInfo>();
            foreach (var battle in module.WorldStateManager.ActiveBattles.Values)
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

            // Build player states
            var playerStates = new List<PlayerStatePacket>();
            foreach (var player in module.PlayerManager.Players.Values)
            {
                playerStates.Add(new PlayerStatePacket
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
                });
            }

            // Build active battles
            var activeBattles = new List<BattleInfo>();
            foreach (var battle in module.WorldStateManager.ActiveBattles.Values)
            {
                activeBattles.Add(new BattleInfo
                {
                    BattleId = battle.BattleId,
                    MapPosition = battle.MapPosition,
                    InitiatorPlayerId = battle.InitiatorPlayerId
                });
            }

            // Build diplomacy states (wars, alliances)
            var diplomacyStates = new List<DiplomacyState>();
            if (Campaign.Current?.Kingdoms != null)
            {
                foreach (var kingdom1 in Campaign.Current.Kingdoms)
                {
                    foreach (var kingdom2 in Campaign.Current.Kingdoms)
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
                }
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
}
