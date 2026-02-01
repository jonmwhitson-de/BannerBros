using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BannerBros.Network;
using LiteNetLib;
using Newtonsoft.Json;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.Library;
using TaleWorlds.Localization;

namespace BannerBros.Core.StateSync;

/// <summary>
/// Manages state synchronization between server and clients.
/// Instead of transferring save files, we sync individual state changes.
///
/// Architecture:
/// - Server is authoritative for all game state
/// - State changes are intercepted via Harmony patches
/// - Changes are broadcast to all clients
/// - Clients apply received state changes locally
/// </summary>
public class StateSyncManager
{
    private static StateSyncManager? _instance;
    public static StateSyncManager Instance => _instance ??= new StateSyncManager();

    private bool _initialized;
    private bool _isServer;

    // Track which entities are being synced
    private readonly HashSet<string> _syncedPartyIds = new();
    private readonly HashSet<string> _syncedHeroIds = new();

    // Track shadow parties created on client for other players
    private readonly Dictionary<string, MobileParty> _shadowParties = new();

    // Pending state updates to apply (client-side)
    private readonly Queue<StateUpdatePacket> _pendingUpdates = new();

    // Events
    public event Action<string, Vec2>? OnPartyPositionChanged;
    public event Action<string, int>? OnPartyStateChanged;

    public bool IsServer => _isServer;
    public bool IsInitialized => _initialized;

    private StateSyncManager() { }

    /// <summary>
    /// Initialize the state sync system.
    /// </summary>
    public void Initialize(bool isServer)
    {
        if (_initialized) return;

        _isServer = isServer;
        _initialized = true;

        // Subscribe to network events
        var networkManager = NetworkManager.Instance;
        if (networkManager != null)
        {
            networkManager.Messages.OnStateUpdateReceived += HandleStateUpdateReceived;
            networkManager.Messages.OnWorldPartyBatchReceived += HandleWorldPartyBatchReceived;
        }

        BannerBrosModule.LogMessage($"[StateSync] Initialized as {(isServer ? "SERVER" : "CLIENT")}");
    }

    /// <summary>
    /// Hides all local NPC parties on the client.
    /// The client should only see parties that the host broadcasts.
    /// </summary>
    public void HideAllLocalNpcParties()
    {
        if (_isServer) return;
        if (Campaign.Current == null) return;

        int hiddenCount = 0;
        var localMainPartyId = MobileParty.MainParty?.StringId;

        foreach (var party in Campaign.Current.MobileParties.ToList())
        {
            try
            {
                if (party == null) continue;

                // Don't hide the local player's party
                if (party.StringId == localMainPartyId) continue;
                if (party.IsMainParty) continue;

                // Hide all other parties
                party.IsVisible = false;
                hiddenCount++;
            }
            catch { }
        }

        BannerBrosModule.LogMessage($"[StateSync] Hidden {hiddenCount} local NPC parties (client will show host's parties only)");
    }

    /// <summary>
    /// Cleanup the state sync system.
    /// </summary>
    public void Cleanup()
    {
        var networkManager = NetworkManager.Instance;
        if (networkManager != null)
        {
            networkManager.Messages.OnStateUpdateReceived -= HandleStateUpdateReceived;
            networkManager.Messages.OnWorldPartyBatchReceived -= HandleWorldPartyBatchReceived;
        }

        _syncedPartyIds.Clear();
        _syncedHeroIds.Clear();
        _pendingUpdates.Clear();

        // Hide and clear shadow parties
        foreach (var party in _shadowParties.Values)
        {
            try
            {
                if (party != null)
                {
                    party.IsVisible = false;
                }
            }
            catch { }
        }
        _shadowParties.Clear();

        _initialized = false;
        _localPartiesHidden = false;
        _lastBatchSequence = -1;
        _partyLookupCache.Clear();
        _partyTargetPositions.Clear();
        _cacheRefreshCounter = 0;

        BannerBrosModule.LogMessage("[StateSync] Cleaned up");
    }

    /// <summary>
    /// Reset sync state for reconnection after loading a save.
    /// Forces cache refresh and resets batch tracking.
    /// Call this when client reconnects after loading host's save file.
    /// </summary>
    public void ResetForReconnection()
    {
        BannerBrosModule.LogMessage("[StateSync] Resetting for reconnection...");

        // Clear cached data - will be rebuilt from fresh campaign
        _partyLookupCache.Clear();
        _cacheRefreshCounter = 0;

        // Reset batch tracking
        _lastBatchSequence = -1;
        _batchLogCounter = 0;

        // Don't hide local parties - they should match host after save load
        _localPartiesHidden = false;

        // Clear shadow parties - we'll recreate if needed
        _shadowParties.Clear();

        // Clear pending updates and interpolation targets
        _pendingUpdates.Clear();
        _partyTargetPositions.Clear();

        // Force immediate cache refresh on next batch
        RefreshPartyLookupCache();

        BannerBrosModule.LogMessage($"[StateSync] Reset complete. Cache has {_partyLookupCache.Count} parties ready for sync.");
    }

    /// <summary>
    /// Register a party for state synchronization.
    /// </summary>
    public void RegisterParty(MobileParty party)
    {
        if (party == null) return;
        var alreadyRegistered = _syncedPartyIds.Contains(party.StringId);
        _syncedPartyIds.Add(party.StringId);
        var pos = party.GetPosition2D;
        BannerBrosModule.LogMessage($"[StateSync] Registered party: {party.StringId} at ({pos.x:F1}, {pos.y:F1}) - IsServer: {_isServer}, AlreadyRegistered: {alreadyRegistered}, Total: {_syncedPartyIds.Count}");
    }

    /// <summary>
    /// Register a hero for state synchronization.
    /// </summary>
    public void RegisterHero(Hero hero)
    {
        if (hero == null) return;
        _syncedHeroIds.Add(hero.StringId);
        BannerBrosModule.LogMessage($"[StateSync] Registered hero: {hero.StringId}");
    }

    #region Server-Side: Broadcast State Changes

    /// <summary>
    /// Called when a party's position changes on the server.
    /// Broadcasts the change to all clients.
    /// </summary>
    private static int _broadcastLogCounter = 0;

    public void OnServerPartyPositionChanged(string partyId, float x, float y)
    {
        if (!_isServer)
        {
            BannerBrosModule.LogMessage($"[StateSync] OnServerPartyPositionChanged SKIPPED: not server");
            return;
        }
        if (!_initialized)
        {
            BannerBrosModule.LogMessage($"[StateSync] OnServerPartyPositionChanged SKIPPED: not initialized");
            return;
        }
        if (!_syncedPartyIds.Contains(partyId))
        {
            BannerBrosModule.LogMessage($"[StateSync] OnServerPartyPositionChanged SKIPPED: party {partyId} not synced");
            return;
        }

        var packet = new StateUpdatePacket
        {
            UpdateType = (byte)StateUpdateType.PartyPosition,
            EntityId = partyId,
            FloatValue1 = x,
            FloatValue2 = y,
            Timestamp = DateTime.UtcNow.Ticks
        };

        BroadcastStateUpdate(packet);

        // Log occasionally to avoid spam
        _broadcastLogCounter++;
        if (_broadcastLogCounter % 100 == 1)
        {
            BannerBrosModule.LogMessage($"[StateSync] Broadcasting {partyId} pos ({x:F1}, {y:F1}) to clients");
        }
    }

    /// <summary>
    /// Called when a party's state changes on the server.
    /// </summary>
    public void OnServerPartyStateChanged(string partyId, int state)
    {
        if (!_isServer || !_initialized) return;
        if (!_syncedPartyIds.Contains(partyId)) return;

        var packet = new StateUpdatePacket
        {
            UpdateType = (byte)StateUpdateType.PartyState,
            EntityId = partyId,
            IntValue1 = state,
            Timestamp = DateTime.UtcNow.Ticks
        };

        BroadcastStateUpdate(packet);
    }

    /// <summary>
    /// Broadcasts a full world state snapshot to a specific client.
    /// Called when a new client joins.
    /// </summary>
    public void SendFullStateToClient(int peerId)
    {
        if (!_isServer || !_initialized)
        {
            BannerBrosModule.LogMessage($"[StateSync] SendFullStateToClient SKIPPED - IsServer: {_isServer}, Initialized: {_initialized}");
            return;
        }
        if (Campaign.Current == null)
        {
            BannerBrosModule.LogMessage($"[StateSync] SendFullStateToClient SKIPPED - No campaign");
            return;
        }

        BannerBrosModule.LogMessage($"[StateSync] Sending full state to peer {peerId} - {_syncedPartyIds.Count} synced parties");

        int sentCount = 0;
        // Send all synced party positions
        foreach (var partyId in _syncedPartyIds)
        {
            var party = Campaign.Current.MobileParties
                .FirstOrDefault(p => p.StringId == partyId);

            if (party != null)
            {
                var pos = party.GetPosition2D;
                var packet = new StateUpdatePacket
                {
                    UpdateType = (byte)StateUpdateType.PartyPosition,
                    EntityId = partyId,
                    FloatValue1 = pos.x,
                    FloatValue2 = pos.y,
                    Timestamp = DateTime.UtcNow.Ticks
                };

                NetworkManager.Instance?.SendTo(peerId, packet, DeliveryMethod.ReliableOrdered);
                sentCount++;
                BannerBrosModule.LogMessage($"[StateSync] Sent party {partyId} pos ({pos.x:F1}, {pos.y:F1}) to peer {peerId}");
            }
            else
            {
                BannerBrosModule.LogMessage($"[StateSync] WARNING: Party {partyId} not found in campaign!");
            }
        }

        // Send campaign time
        var timePacket = new StateUpdatePacket
        {
            UpdateType = (byte)StateUpdateType.CampaignTime,
            FloatValue1 = (float)CampaignTime.Now.ToHours,
            Timestamp = DateTime.UtcNow.Ticks
        };
        NetworkManager.Instance?.SendTo(peerId, timePacket, DeliveryMethod.ReliableOrdered);

        BannerBrosModule.LogMessage($"[StateSync] Full state sent to peer {peerId}: {sentCount} parties, time={CampaignTime.Now.ToHours:F1}h");
    }

    private void BroadcastStateUpdate(StateUpdatePacket packet)
    {
        NetworkManager.Instance?.SendToAll(packet, DeliveryMethod.ReliableOrdered);
    }

    #endregion

    #region Client-Side: Receive and Apply State Changes

    /// <summary>
    /// Handle incoming state update from server.
    /// </summary>
    private void HandleStateUpdateReceived(StateUpdatePacket packet)
    {
        if (_isServer) return; // Server doesn't receive these

        BannerBrosModule.LogMessage($"[StateSync] CLIENT received update: Type={packet.UpdateType}, Entity={packet.EntityId}, Pos=({packet.FloatValue1:F1}, {packet.FloatValue2:F1})");

        // Queue the update to be applied on the main thread
        lock (_pendingUpdates)
        {
            _pendingUpdates.Enqueue(packet);
            BannerBrosModule.LogMessage($"[StateSync] Queued update, pending count: {_pendingUpdates.Count}");
        }
    }

    private static int _applyLogCounter = 0;

    /// <summary>
    /// Apply pending state updates. Called from main game loop.
    /// </summary>
    public void ApplyPendingUpdates()
    {
        if (_isServer || !_initialized) return;
        if (Campaign.Current == null) return;

        StateUpdatePacket[] updates;
        lock (_pendingUpdates)
        {
            if (_pendingUpdates.Count == 0) return;
            updates = _pendingUpdates.ToArray();
            _pendingUpdates.Clear();
        }

        // Log every 10th call to avoid spam
        _applyLogCounter++;
        if (_applyLogCounter % 10 == 1)
        {
            BannerBrosModule.LogMessage($"[StateSync] Applying {updates.Length} pending updates");
        }

        foreach (var packet in updates)
        {
            ApplyStateUpdate(packet);
        }
    }

    private void ApplyStateUpdate(StateUpdatePacket packet)
    {
        try
        {
            BannerBrosModule.LogMessage($"[StateSync] ApplyStateUpdate: Type={(StateUpdateType)packet.UpdateType}, Entity={packet.EntityId}");

            switch ((StateUpdateType)packet.UpdateType)
            {
                case StateUpdateType.PartyPosition:
                    ApplyPartyPosition(packet.EntityId, packet.FloatValue1, packet.FloatValue2);
                    break;

                case StateUpdateType.PartyState:
                    ApplyPartyState(packet.EntityId, packet.IntValue1);
                    break;

                case StateUpdateType.CampaignTime:
                    // Campaign time sync - for future use
                    BannerBrosModule.LogMessage($"[StateSync] CampaignTime update received (not applied yet)");
                    break;

                default:
                    BannerBrosModule.LogMessage($"[StateSync] Unknown update type: {packet.UpdateType}");
                    break;
            }
        }
        catch (Exception ex)
        {
            BannerBrosModule.LogMessage($"[StateSync] Error applying update: {ex.Message}");
            BannerBrosModule.LogMessage($"[StateSync] Stack: {ex.StackTrace}");
        }
    }

    private static int _positionLogCounter = 0;

    private void ApplyPartyPosition(string partyId, float x, float y)
    {
        // Log entry for debugging
        var localPartyId = MobileParty.MainParty?.StringId;
        BannerBrosModule.LogMessage($"[StateSync] ApplyPartyPosition: partyId={partyId}, localPartyId={localPartyId ?? "null"}, pos=({x:F1}, {y:F1})");

        // Skip coop_party_ prefixed IDs - these are client representation parties on the host
        // Clients don't need to create shadows for these (they represent the client themselves)
        if (partyId.StartsWith("coop_party_"))
        {
            BannerBrosModule.LogMessage($"[StateSync] Skipping coop_party update (client representation): {partyId}");
            return;
        }

        // Check if this is the local player's party - if so, we need to use a shadow ID
        // because both host and client have "player_party"
        string effectivePartyId = partyId;

        if (partyId == localPartyId)
        {
            // This update is for a remote player whose party has same ID as ours
            // Use a shadow ID instead
            effectivePartyId = $"remote_{partyId}";
            BannerBrosModule.LogMessage($"[StateSync] Remapping {partyId} to {effectivePartyId} (conflicts with local party)");
        }

        var party = Campaign.Current?.MobileParties
            .FirstOrDefault(p => p.StringId == effectivePartyId);

        bool fromShadow = false;
        bool created = false;

        // Check shadow parties if not found
        if (party == null)
        {
            _shadowParties.TryGetValue(effectivePartyId, out party);
            fromShadow = party != null;
        }

        if (party == null)
        {
            // Party doesn't exist locally - create a shadow party
            BannerBrosModule.LogMessage($"[StateSync] Creating shadow party for {effectivePartyId} at ({x:F1}, {y:F1})");
            party = CreateShadowParty(effectivePartyId, x, y);
            created = true;
            if (party == null)
            {
                BannerBrosModule.LogMessage($"[StateSync] FAILED to create shadow party for {effectivePartyId}");
                return;
            }
        }

        // Apply position using CampaignVec2
        SetPartyPosition(party, x, y);

        var newPos = new Vec2(x, y);

        // Disable AI to prevent local movement
        try
        {
            party.Ai?.SetDoNotMakeNewDecisions(true);
        }
        catch { }

        // Log occasionally
        _positionLogCounter++;
        if (created || _positionLogCounter % 100 == 1)
        {
            var remapped = effectivePartyId != partyId ? $" (remapped from {partyId})" : "";
            BannerBrosModule.LogMessage($"[StateSync] Party {effectivePartyId}{remapped} -> ({x:F1}, {y:F1}) [shadow:{fromShadow}, created:{created}]");
        }

        OnPartyPositionChanged?.Invoke(effectivePartyId, newPos);
    }

    /// <summary>
    /// Creates a shadow party on the client to represent a remote player.
    /// </summary>
    private MobileParty? CreateShadowParty(string partyId, float x, float y)
    {
        try
        {
            if (Campaign.Current == null) return null;

            BannerBrosModule.LogMessage($"[StateSync] Creating shadow party: {partyId} at ({x}, {y})");

            // Create a minimal party to represent the remote player
            var party = MobileParty.CreateParty(partyId, null);

            if (party == null)
            {
                BannerBrosModule.LogMessage($"[StateSync] MobileParty.CreateParty returned null");
                return null;
            }

            // Set position using CampaignVec2
            SetPartyPosition(party, x, y);

            // Make visible on map
            try { party.IsVisible = true; } catch { }

            // Disable AI completely
            try
            {
                party.Ai?.SetDoNotMakeNewDecisions(true);
                party.Ai?.DisableAi();
            }
            catch { }

            // Set a clan to prevent null reference crashes
            try
            {
                var playerClan = Clan.PlayerClan;
                if (playerClan != null)
                {
                    party.ActualClan = playerClan;
                }
            }
            catch { }

            // Add a minimal member roster to prevent empty party crashes
            try
            {
                if (party.MemberRoster != null && party.MemberRoster.TotalManCount == 0)
                {
                    var basicTroop = CharacterObject.All.FirstOrDefault(c => c.IsBasicTroop && !c.IsHero);
                    if (basicTroop != null)
                    {
                        party.MemberRoster.AddToCounts(basicTroop, 1);
                    }
                }
            }
            catch { }

            // Track the shadow party
            _shadowParties[partyId] = party;
            _syncedPartyIds.Add(partyId);

            var finalPos = party.GetPosition2D;
            BannerBrosModule.LogMessage($"[StateSync] Shadow party created: {partyId} at ({finalPos.x:F1}, {finalPos.y:F1})");
            return party;
        }
        catch (Exception ex)
        {
            BannerBrosModule.LogMessage($"[StateSync] Error creating shadow party: {ex.Message}");
            return null;
        }
    }

    private void ApplyPartyState(string partyId, int state)
    {
        // Apply party state changes
        OnPartyStateChanged?.Invoke(partyId, state);
    }

    /// <summary>
    /// Sets a party's position using CampaignVec2 for API compatibility.
    /// </summary>
    private void SetPartyPosition(MobileParty party, float x, float y)
    {
        try
        {
            // Create CampaignVec2 using (Vec2, Boolean) constructor
            var campaignVec2Type = typeof(Campaign).Assembly.GetType("TaleWorlds.CampaignSystem.CampaignVec2");
            if (campaignVec2Type == null)
            {
                BannerBrosModule.LogMessage("[StateSync] CampaignVec2 type not found");
                return;
            }

            var vec2Pos = new Vec2(x, y);
            object? campaignVec2 = null;

            // Try (Vec2, Boolean) constructor - the one that exists in Bannerlord
            var ctor = campaignVec2Type.GetConstructor(new[] { typeof(Vec2), typeof(bool) });
            if (ctor != null)
            {
                campaignVec2 = ctor.Invoke(new object[] { vec2Pos, true }); // true = IsOnLand
            }

            if (campaignVec2 == null)
            {
                BannerBrosModule.LogMessage("[StateSync] Failed to create CampaignVec2");
                return;
            }

            // Try to set position via _position field
            var posField = typeof(MobileParty).GetField("_position",
                BindingFlags.NonPublic | BindingFlags.Instance);

            if (posField != null && posField.FieldType == campaignVec2Type)
            {
                posField.SetValue(party, campaignVec2);
                return;
            }

            // Try Position2D property
            var posProp = typeof(MobileParty).GetProperty("Position2D");
            if (posProp?.CanWrite == true)
            {
                // Check if it expects CampaignVec2 or Vec2
                if (posProp.PropertyType == campaignVec2Type)
                {
                    posProp.SetValue(party, campaignVec2);
                }
                else
                {
                    posProp.SetValue(party, vec2Pos);
                }
            }
        }
        catch (Exception ex)
        {
            BannerBrosModule.LogMessage($"[StateSync] SetPartyPosition error: {ex.Message}");
        }
    }

    #endregion

    #region World Party Batch Sync (Client-Side)

    private int _lastBatchSequence = -1;
    private static int _batchLogCounter = 0;
    private bool _localPartiesHidden = false;

    // Cache party lookup for performance - rebuilt periodically
    private Dictionary<string, MobileParty> _partyLookupCache = new();
    private int _cacheRefreshCounter = 0;
    private const int CacheRefreshInterval = 10; // Refresh every 10 batches
    private const int MaxCreationsPerBatch = 10; // Limit party creation per batch to avoid crashes

    // Interpolation for smooth movement
    private Dictionary<string, Vec2> _partyTargetPositions = new();
    private const float InterpolationSpeed = 8.0f; // Units per second - adjust for smoothness

    /// <summary>
    /// Handle incoming world party batch from server.
    /// Packets may arrive in multiple chunks to stay under network size limits.
    /// </summary>
    private void HandleWorldPartyBatchReceived(WorldPartyBatchPacket packet)
    {
        try
        {
            if (_isServer) return; // Only clients process this

            // Log chunk info
            if (packet.TotalChunks > 1)
            {
                BannerBrosModule.LogMessage($"[StateSync] CLIENT received chunk {packet.ChunkIndex + 1}/{packet.TotalChunks} ({packet.PartyCount} parties), seq={packet.SequenceNumber}");
            }
            else
            {
                BannerBrosModule.LogMessage($"[StateSync] CLIENT received WorldPartyBatch: {packet.PartyCount} parties, seq={packet.SequenceNumber}");
            }

            // Check for old sequence (but allow same sequence for different chunks)
            if (packet.SequenceNumber < _lastBatchSequence)
            {
                BannerBrosModule.LogMessage($"[StateSync] Skipping old batch (seq {packet.SequenceNumber} < {_lastBatchSequence})");
                return;
            }
            _lastBatchSequence = packet.SequenceNumber;

            // Hide local NPC parties on first batch so only host's parties are visible
            if (!_localPartiesHidden)
            {
                _localPartiesHidden = true;
                HideAllLocalNpcParties();
            }

            ApplyWorldPartyBatch(packet);
        }
        catch (Exception ex)
        {
            BannerBrosModule.LogMessage($"[StateSync] HandleWorldPartyBatch EXCEPTION: {ex.Message}");
            BannerBrosModule.LogMessage($"[StateSync] Stack: {ex.StackTrace}");
        }
    }

    /// <summary>
    /// Rebuild the party lookup cache from campaign parties.
    /// </summary>
    private void RefreshPartyLookupCache()
    {
        _partyLookupCache.Clear();
        if (Campaign.Current == null) return;

        foreach (var party in Campaign.Current.MobileParties)
        {
            if (party != null && !string.IsNullOrEmpty(party.StringId))
            {
                _partyLookupCache[party.StringId] = party;
            }
        }

        BannerBrosModule.LogMessage($"[StateSync] Refreshed party cache: {_partyLookupCache.Count} parties");
    }

    /// <summary>
    /// Apply a batch of party positions to the local campaign.
    /// OPTIMIZED: Updates existing parties directly instead of creating shadow parties.
    /// Only creates shadow parties for truly new parties (like remote player).
    /// </summary>
    private void ApplyWorldPartyBatch(WorldPartyBatchPacket packet)
    {
        if (Campaign.Current == null)
        {
            BannerBrosModule.LogMessage($"[StateSync] ApplyWorldPartyBatch: No campaign");
            return;
        }

        // Refresh cache periodically
        _cacheRefreshCounter++;
        if (_cacheRefreshCounter >= CacheRefreshInterval || _partyLookupCache.Count == 0)
        {
            _cacheRefreshCounter = 0;
            RefreshPartyLookupCache();
        }

        List<PartyPositionData>? parties = null;
        try
        {
            parties = JsonConvert.DeserializeObject<List<PartyPositionData>>(packet.PartiesJson);
        }
        catch (Exception ex)
        {
            BannerBrosModule.LogMessage($"[StateSync] JSON deserialize error: {ex.Message}");
            return;
        }

        if (parties == null || parties.Count == 0)
        {
            BannerBrosModule.LogMessage($"[StateSync] ApplyWorldPartyBatch: No parties in packet");
            return;
        }

        var localMainPartyId = MobileParty.MainParty?.StringId;
        int updated = 0;
        int created = 0;
        int notFound = 0;

        foreach (var partyData in parties)
        {
            try
            {
                if (string.IsNullOrEmpty(partyData.Id)) continue;

                // Skip coop_party_ IDs - these are client representations on host, not real parties
                if (partyData.Id.StartsWith("coop_party_")) continue;

                // Skip remote_player_ IDs - these are shadow parties we create
                if (partyData.Id.StartsWith("remote_player_")) continue;

                // Skip shadow_ IDs
                if (partyData.Id.StartsWith("shadow_")) continue;

                // Handle host's player_party - create as remote_player_party
                if (partyData.Id == localMainPartyId && partyData.T == 4)
                {
                    string remappedId = $"remote_{partyData.Id}";
                    var shadowParty = GetOrCreateRemotePlayerParty(remappedId, partyData);
                    if (shadowParty != null)
                    {
                        SetPartyPosition(shadowParty, partyData.X, partyData.Y);
                        shadowParty.IsVisible = true;
                        updated++;
                    }
                    continue;
                }

                // Skip our own party in the batch
                if (partyData.Id == localMainPartyId) continue;

                // Find existing party in client's campaign (fast lookup)
                MobileParty? party = null;
                if (!_partyLookupCache.TryGetValue(partyData.Id, out party))
                {
                    // Not in cache - check shadow parties
                    _shadowParties.TryGetValue(partyData.Id, out party);
                }

                if (party != null)
                {
                    // Store target position for smooth interpolation
                    _partyTargetPositions[partyData.Id] = new Vec2(partyData.X, partyData.Y);
                    party.IsVisible = partyData.V;
                    try { party.Ai?.SetDoNotMakeNewDecisions(true); } catch { }
                    updated++;
                }
                else
                {
                    // Party doesn't exist locally - skip creation for now
                    // Creating shadow parties causes crashes, so just count as not found
                    // They will sync naturally when the client's campaign has the same parties
                    notFound++;
                }
            }
            catch
            {
                // Skip this party on error
            }
        }

        // Log every batch for debugging
        _batchLogCounter++;
        if (_batchLogCounter % 5 == 1) // Log every 5th batch
        {
            BannerBrosModule.LogMessage($"[StateSync] Batch #{_batchLogCounter}: {updated} updated, {notFound} not synced, {packet.PartyCount} from host");
        }
    }

    /// <summary>
    /// Update party positions with smooth interpolation.
    /// Call this every frame from campaign behavior tick.
    /// </summary>
    public void UpdatePartyInterpolation(float deltaTime)
    {
        if (_isServer) return; // Only clients need interpolation
        if (Campaign.Current == null) return;
        if (_partyTargetPositions.Count == 0) return;

        var localMainPartyId = MobileParty.MainParty?.StringId;
        var toRemove = new List<string>();

        foreach (var kvp in _partyTargetPositions)
        {
            var partyId = kvp.Key;
            var targetPos = kvp.Value;

            // Skip our own party
            if (partyId == localMainPartyId) continue;

            // Find the party
            MobileParty? party = null;
            if (!_partyLookupCache.TryGetValue(partyId, out party))
            {
                _shadowParties.TryGetValue(partyId, out party);
            }

            if (party == null)
            {
                toRemove.Add(partyId);
                continue;
            }

            try
            {
                var currentPos = party.GetPosition2D;
                var distance = (targetPos - currentPos).Length;

                // If very close, snap to target
                if (distance < 0.1f)
                {
                    SetPartyPosition(party, targetPos.x, targetPos.y);
                    continue;
                }

                // If too far (teleport case), snap immediately
                if (distance > 50f)
                {
                    SetPartyPosition(party, targetPos.x, targetPos.y);
                    continue;
                }

                // Smoothly interpolate toward target
                var moveDistance = InterpolationSpeed * deltaTime;
                var t = Math.Min(1.0f, moveDistance / distance);
                var newPos = Vec2.Lerp(currentPos, targetPos, t);

                SetPartyPosition(party, newPos.x, newPos.y);
            }
            catch
            {
                // Skip on error
            }
        }

        // Clean up parties that no longer exist
        foreach (var id in toRemove)
        {
            _partyTargetPositions.Remove(id);
        }
    }

    /// <summary>
    /// Creates a shadow party for an NPC (bandit, lord, caravan, etc.) from host.
    /// </summary>
    private MobileParty? CreateShadowPartyForNPC(PartyPositionData data)
    {
        try
        {
            if (Campaign.Current == null || string.IsNullOrEmpty(data.Id)) return null;

            // Create the shadow party
            var party = MobileParty.CreateParty(data.Id, null);
            if (party == null) return null;

            // Set position
            SetPartyPosition(party, data.X, data.Y);

            // Make visible
            try { party.IsVisible = data.V; } catch { }

            // Disable AI completely
            try
            {
                party.Ai?.SetDoNotMakeNewDecisions(true);
                party.Ai?.DisableAi();
            }
            catch { }

            // Set a clan if we have faction info
            try
            {
                if (!string.IsNullOrEmpty(data.F))
                {
                    var faction = Campaign.Current.Factions.FirstOrDefault(f => f.StringId == data.F);
                    if (faction is Clan clan)
                    {
                        party.ActualClan = clan;
                    }
                }
                else
                {
                    // Default to player clan for bandits etc
                    var playerClan = Clan.PlayerClan;
                    if (playerClan != null)
                    {
                        party.ActualClan = playerClan;
                    }
                }
            }
            catch { }

            // Add troops based on party size
            try
            {
                if (party.MemberRoster != null && data.S > 0)
                {
                    var basicTroop = CharacterObject.All.FirstOrDefault(c => c.IsBasicTroop && !c.IsHero);
                    if (basicTroop != null)
                    {
                        party.MemberRoster.AddToCounts(basicTroop, Math.Min(data.S, 10)); // Cap at 10 for performance
                    }
                }
            }
            catch { }

            // Set name if provided
            if (!string.IsNullOrEmpty(data.N))
            {
                try
                {
                    var nameProp = party.GetType().GetProperty("Name");
                    if (nameProp?.CanWrite == true)
                    {
                        nameProp.SetValue(party, new TextObject(data.N));
                    }
                }
                catch { }
            }

            // Track the shadow party
            _shadowParties[data.Id] = party;
            _partyLookupCache[data.Id] = party;

            return party;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Gets or creates the remote player party (for host's player_party shown on client).
    /// </summary>
    private MobileParty? GetOrCreateRemotePlayerParty(string remotePartyId, PartyPositionData data)
    {
        // Check if we already have this shadow party
        if (_shadowParties.TryGetValue(remotePartyId, out var existingParty) && existingParty != null)
        {
            return existingParty;
        }

        // Check if it exists in the campaign
        if (_partyLookupCache.TryGetValue(remotePartyId, out var campaignParty) && campaignParty != null)
        {
            _shadowParties[remotePartyId] = campaignParty;
            return campaignParty;
        }

        // Create new shadow party for remote player only
        return CreateShadowPartyForRemotePlayer(remotePartyId, data);
    }

    /// <summary>
    /// Creates a shadow party specifically for a remote player.
    /// </summary>
    private MobileParty? CreateShadowPartyForRemotePlayer(string partyId, PartyPositionData data)
    {
        try
        {
            if (Campaign.Current == null) return null;

            BannerBrosModule.LogMessage($"[StateSync] Creating remote player shadow: {partyId} at ({data.X:F1}, {data.Y:F1})");

            var party = MobileParty.CreateParty(partyId, null);
            if (party == null)
            {
                BannerBrosModule.LogMessage($"[StateSync] Failed to create party {partyId}");
                return null;
            }

            // Set position
            SetPartyPosition(party, data.X, data.Y);

            // Make visible
            try { party.IsVisible = true; } catch { }

            // Disable AI completely - prevent any AI-driven behavior
            try
            {
                party.Ai?.SetDoNotMakeNewDecisions(true);
                party.Ai?.DisableAi();
            }
            catch { }

            // Set a clan to prevent null reference crashes during faction checks
            try
            {
                var playerClan = Clan.PlayerClan;
                if (playerClan != null)
                {
                    party.ActualClan = playerClan;
                }
            }
            catch { }

            // Add a minimal member roster to prevent empty roster crashes
            try
            {
                if (party.MemberRoster != null && party.MemberRoster.TotalManCount == 0)
                {
                    // Try to add a basic troop to prevent empty party crashes
                    var basicTroop = CharacterObject.All.FirstOrDefault(c => c.IsBasicTroop && !c.IsHero);
                    if (basicTroop != null)
                    {
                        party.MemberRoster.AddToCounts(basicTroop, 1);
                    }
                }
            }
            catch { }

            // Set name if provided
            if (!string.IsNullOrEmpty(data.N))
            {
                try
                {
                    var nameProp = party.GetType().GetProperty("Name");
                    if (nameProp?.CanWrite == true)
                    {
                        nameProp.SetValue(party, new TextObject(data.N));
                    }
                }
                catch { }
            }

            // Track the shadow party
            _shadowParties[partyId] = party;
            _partyLookupCache[partyId] = party;

            BannerBrosModule.LogMessage($"[StateSync] Created remote player shadow: {partyId}");
            return party;
        }
        catch (Exception ex)
        {
            BannerBrosModule.LogMessage($"[StateSync] CreateShadowPartyForRemotePlayer error: {ex.Message}");
            return null;
        }
    }

    #endregion

    #region Utility Methods

    /// <summary>
    /// Check if a party is being synced.
    /// </summary>
    public bool IsPartySynced(string partyId) => _syncedPartyIds.Contains(partyId);

    /// <summary>
    /// Get all synced party IDs.
    /// </summary>
    public IEnumerable<string> GetSyncedPartyIds() => _syncedPartyIds.ToList();

    #endregion
}

/// <summary>
/// Types of state updates that can be synchronized.
/// </summary>
public enum StateUpdateType : byte
{
    PartyPosition = 1,
    PartyState = 2,
    HeroState = 3,
    CampaignTime = 4,
    Settlement = 5,
    PartyCreated = 10,
    PartyDestroyed = 11,
    HeroCreated = 12,
    HeroDestroyed = 13
}
