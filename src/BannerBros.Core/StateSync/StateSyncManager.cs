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

        BannerBrosModule.LogMessage("[StateSync] Cleaned up");
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

            // Disable AI
            try { party.Ai?.SetDoNotMakeNewDecisions(true); } catch { }

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

    /// <summary>
    /// Handle incoming world party batch from server.
    /// </summary>
    private void HandleWorldPartyBatchReceived(WorldPartyBatchPacket packet)
    {
        if (_isServer) return; // Only clients process this

        BannerBrosModule.LogMessage($"[StateSync] CLIENT received WorldPartyBatch: {packet.PartyCount} parties, seq={packet.SequenceNumber}");

        // On first batch, hide all local NPC parties
        if (!_localPartiesHidden && Campaign.Current != null)
        {
            HideAllLocalNpcParties();
            _localPartiesHidden = true;
        }

        // Check for duplicate/out-of-order packets
        if (packet.SequenceNumber <= _lastBatchSequence)
        {
            BannerBrosModule.LogMessage($"[StateSync] Skipping old batch (seq {packet.SequenceNumber} <= {_lastBatchSequence})");
            return;
        }
        _lastBatchSequence = packet.SequenceNumber;

        try
        {
            ApplyWorldPartyBatch(packet);
        }
        catch (Exception ex)
        {
            BannerBrosModule.LogMessage($"[StateSync] HandleWorldPartyBatch error: {ex.Message}");
            BannerBrosModule.LogMessage($"[StateSync] Stack: {ex.StackTrace}");
        }
    }

    /// <summary>
    /// Apply a batch of party positions to the local campaign.
    /// Creates shadow parties for ALL host parties (client's local NPCs are hidden).
    /// </summary>
    private void ApplyWorldPartyBatch(WorldPartyBatchPacket packet)
    {
        if (Campaign.Current == null) return;

        var parties = JsonConvert.DeserializeObject<List<PartyPositionData>>(packet.PartiesJson);
        if (parties == null || parties.Count == 0)
        {
            BannerBrosModule.LogMessage($"[StateSync] ApplyWorldPartyBatch: No parties in packet");
            return;
        }

        var localMainPartyId = MobileParty.MainParty?.StringId;
        int updated = 0;
        int created = 0;
        int skipped = 0;

        foreach (var partyData in parties)
        {
            try
            {
                if (string.IsNullOrEmpty(partyData.Id)) continue;

                // Skip if this is our own party (client's main party)
                if (partyData.Id == localMainPartyId)
                {
                    // This is the host's main party OR our own party in the broadcast
                    // If it's a player type (4) and matches our ID, it's the host - create shadow
                    if (partyData.T == 4)
                    {
                        // Host's player_party - create as remote_player_party
                        string remappedId = $"remote_{partyData.Id}";
                        var shadowParty = GetOrCreateShadowParty(remappedId, partyData);
                        if (shadowParty != null)
                        {
                            SetPartyPosition(shadowParty, partyData.X, partyData.Y);
                            shadowParty.IsVisible = true;
                            updated++;
                        }
                    }
                    continue;
                }

                // For all other parties, create/update shadow
                var party = GetOrCreateShadowParty(partyData.Id, partyData);
                if (party != null)
                {
                    SetPartyPosition(party, partyData.X, partyData.Y);
                    party.IsVisible = partyData.V;
                    try { party.Ai?.SetDoNotMakeNewDecisions(true); } catch { }
                    updated++;
                }
                else
                {
                    skipped++;
                }
            }
            catch
            {
                // Skip this party on error
            }
        }

        // Log every batch for now (debugging)
        _batchLogCounter++;
        BannerBrosModule.LogMessage($"[StateSync] Applied batch #{_batchLogCounter}: {updated} updated, {skipped} skipped, {packet.PartyCount} total from host");
    }

    /// <summary>
    /// Gets an existing shadow party or creates a new one.
    /// </summary>
    private MobileParty? GetOrCreateShadowParty(string partyId, PartyPositionData data)
    {
        // Check if we already have this shadow party
        if (_shadowParties.TryGetValue(partyId, out var existingParty) && existingParty != null)
        {
            return existingParty;
        }

        // Check if it exists in the campaign (maybe created by another system)
        var campaignParty = Campaign.Current?.MobileParties
            .FirstOrDefault(p => p.StringId == partyId);
        if (campaignParty != null)
        {
            _shadowParties[partyId] = campaignParty;
            return campaignParty;
        }

        // Create new shadow party
        return CreateShadowPartyFromData(partyId, data);
    }

    /// <summary>
    /// Creates a shadow party from batch sync data.
    /// </summary>
    private MobileParty? CreateShadowPartyFromData(string partyId, PartyPositionData data)
    {
        try
        {
            if (Campaign.Current == null) return null;

            BannerBrosModule.LogMessage($"[StateSync] Creating shadow party from batch: {partyId} at ({data.X:F1}, {data.Y:F1})");

            var party = MobileParty.CreateParty(partyId, null);
            if (party == null) return null;

            // Set position
            SetPartyPosition(party, data.X, data.Y);

            // Set visibility
            try { party.IsVisible = data.V; } catch { }

            // Disable AI
            try { party.Ai?.SetDoNotMakeNewDecisions(true); } catch { }

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
            _syncedPartyIds.Add(partyId);

            return party;
        }
        catch (Exception ex)
        {
            BannerBrosModule.LogMessage($"[StateSync] CreateShadowPartyFromData error: {ex.Message}");
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
