using System;
using System.Collections.Generic;
using System.Linq;
using BannerBros.Network;
using LiteNetLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.Library;

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
        }

        BannerBrosModule.LogMessage($"[StateSync] Initialized as {(isServer ? "SERVER" : "CLIENT")}");
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
        }

        _syncedPartyIds.Clear();
        _syncedHeroIds.Clear();
        _pendingUpdates.Clear();
        _initialized = false;

        BannerBrosModule.LogMessage("[StateSync] Cleaned up");
    }

    /// <summary>
    /// Register a party for state synchronization.
    /// </summary>
    public void RegisterParty(MobileParty party)
    {
        if (party == null) return;
        _syncedPartyIds.Add(party.StringId);
        BannerBrosModule.LogMessage($"[StateSync] Registered party: {party.StringId}");
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
    public void OnServerPartyPositionChanged(string partyId, float x, float y)
    {
        if (!_isServer || !_initialized) return;
        if (!_syncedPartyIds.Contains(partyId)) return;

        var packet = new StateUpdatePacket
        {
            UpdateType = (byte)StateUpdateType.PartyPosition,
            EntityId = partyId,
            FloatValue1 = x,
            FloatValue2 = y,
            Timestamp = DateTime.UtcNow.Ticks
        };

        BroadcastStateUpdate(packet);
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
        if (!_isServer || !_initialized) return;
        if (Campaign.Current == null) return;

        BannerBrosModule.LogMessage($"[StateSync] Sending full state to peer {peerId}");

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

        BannerBrosModule.LogMessage($"[StateSync] Full state sent to peer {peerId}");
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

        // Queue the update to be applied on the main thread
        lock (_pendingUpdates)
        {
            _pendingUpdates.Enqueue(packet);
        }
    }

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

        foreach (var packet in updates)
        {
            ApplyStateUpdate(packet);
        }
    }

    private void ApplyStateUpdate(StateUpdatePacket packet)
    {
        try
        {
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
                    break;
            }
        }
        catch (Exception ex)
        {
            BannerBrosModule.LogMessage($"[StateSync] Error applying update: {ex.Message}");
        }
    }

    private void ApplyPartyPosition(string partyId, float x, float y)
    {
        var party = Campaign.Current?.MobileParties
            .FirstOrDefault(p => p.StringId == partyId);

        if (party == null)
        {
            // Party doesn't exist locally - might need to create it
            BannerBrosModule.LogMessage($"[StateSync] Party {partyId} not found locally");
            return;
        }

        var newPos = new Vec2(x, y);

        // Apply position via reflection for API compatibility
        try
        {
            var posProp = party.GetType().GetProperty("Position2D");
            if (posProp?.CanWrite == true)
            {
                posProp.SetValue(party, newPos);
            }
        }
        catch { }

        // Disable AI to prevent local movement
        try
        {
            party.Ai?.SetDoNotMakeNewDecisions(true);
        }
        catch { }

        OnPartyPositionChanged?.Invoke(partyId, newPos);
    }

    private void ApplyPartyState(string partyId, int state)
    {
        // Apply party state changes
        OnPartyStateChanged?.Invoke(partyId, state);
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
