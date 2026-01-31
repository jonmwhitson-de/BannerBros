using System;
using LiteNetLib;

namespace BannerBros.Network;

/// <summary>
/// Handles incoming network messages and dispatches them appropriately.
/// </summary>
public class MessageHandler
{
    private readonly NetworkManager _networkManager;

    // Existing events
    public event Action<PlayerStatePacket>? OnPlayerStateReceived;
    public event Action<WorldSyncPacket>? OnWorldSyncReceived;
    public event Action<BattleEventPacket>? OnBattleEventReceived;
    public event Action<ChatPacket>? OnChatReceived;
    public event Action<TradeRequestPacket>? OnTradeRequestReceived;

    // Session management events
    public event Action<SessionPacket>? OnSessionEventReceived;
    public event Action<JoinRequestPacket, int>? OnJoinRequestReceived; // packet, peerId
    public event Action<JoinResponsePacket>? OnJoinResponseReceived;
    public event Action<CharacterCreationPacket, int>? OnCharacterCreationReceived; // packet, peerId
    public event Action<CharacterCreationResponsePacket>? OnCharacterCreationResponseReceived;
    public event Action<FullStateSyncPacket>? OnFullStateSyncReceived;
    public event Action<ClientCampaignReadyPacket, int>? OnClientCampaignReadyReceived; // packet, peerId

    // Save file transfer events
    public event Action<SaveFileRequestPacket, int>? OnSaveFileRequestReceived;
    public event Action<SaveFileStartPacket>? OnSaveFileStartReceived;
    public event Action<SaveFileChunkPacket>? OnSaveFileChunkReceived;
    public event Action<SaveFileCompletePacket>? OnSaveFileCompleteReceived;
    public event Action<SaveFileReceivedPacket, int>? OnSaveFileReceivedReceived;
    public event Action<SpectatorReadyPacket, int>? OnSpectatorReadyReceived;
    public event Action<PartyAssignmentPacket>? OnPartyAssignmentReceived;

    // Command events
    public event Action<MoveCommandPacket, int>? OnMoveCommandReceived;
    public event Action<EnterSettlementCommandPacket, int>? OnEnterSettlementCommandReceived;
    public event Action<LeaveSettlementCommandPacket, int>? OnLeaveSettlementCommandReceived;
    public event Action<AttackCommandPacket, int>? OnAttackCommandReceived;
    public event Action<FollowCommandPacket, int>? OnFollowCommandReceived;
    public event Action<CommandResultPacket>? OnCommandResultReceived;

    // Debug log streaming
    public event Action<DebugLogPacket, int>? OnDebugLogReceived;

    // State synchronization events (new architecture - no save file transfer)
    public event Action<StateUpdatePacket>? OnStateUpdateReceived;
    public event Action<StateSyncJoinRequestPacket, int>? OnStateSyncJoinRequestReceived;
    public event Action<InitialStateSyncPacket>? OnInitialStateSyncReceived;

    // World party batch sync (efficient sync of all parties)
    public event Action<WorldPartyBatchPacket>? OnWorldPartyBatchReceived;

    public MessageHandler(NetworkManager networkManager)
    {
        _networkManager = networkManager;
    }

    public void HandlePlayerState(PlayerStatePacket packet, NetPeer peer)
    {
        Console.WriteLine($"[BannerBros.Network] Received player state from peer {peer.Id}: {packet.PlayerName} at ({packet.MapX}, {packet.MapY})");

        // If we're the host, relay to other clients
        if (_networkManager.IsHost)
        {
            _networkManager.SendToAllExcept(peer.Id, packet);
        }

        OnPlayerStateReceived?.Invoke(packet);
    }

    public void HandleWorldSync(WorldSyncPacket packet, NetPeer peer)
    {
        // Only clients should receive world sync (from host)
        if (_networkManager.IsHost) return;

        Console.WriteLine($"[BannerBros.Network] Received world sync: Time multiplier = {packet.TimeMultiplier}");

        OnWorldSyncReceived?.Invoke(packet);
    }

    public void HandleBattleEvent(BattleEventPacket packet, NetPeer peer)
    {
        var eventType = (BattleEventType)packet.EventType;
        Console.WriteLine($"[BannerBros.Network] Battle event: {eventType} for battle {packet.BattleId}");

        // If host, relay to other clients
        if (_networkManager.IsHost)
        {
            _networkManager.SendToAllExcept(peer.Id, packet);
        }

        OnBattleEventReceived?.Invoke(packet);
    }

    public void HandleChat(ChatPacket packet, NetPeer peer)
    {
        Console.WriteLine($"[BannerBros.Network] Chat from {packet.SenderName}: {packet.Message}");

        // If host, relay appropriately
        if (_networkManager.IsHost)
        {
            if (packet.Channel == 2 && packet.TargetPlayerId >= 0)
            {
                // Whisper - only send to target
                _networkManager.SendTo(packet.TargetPlayerId, packet);
            }
            else
            {
                // Broadcast to all except sender
                _networkManager.SendToAllExcept(peer.Id, packet);
            }
        }

        OnChatReceived?.Invoke(packet);
    }

    public void HandleTradeRequest(TradeRequestPacket packet, NetPeer peer)
    {
        var requestType = (TradeRequestType)packet.RequestType;
        Console.WriteLine($"[BannerBros.Network] Trade request: {requestType} from {packet.InitiatorId} to {packet.TargetId}");

        // If host, relay to target player
        if (_networkManager.IsHost)
        {
            if (requestType == TradeRequestType.Initiate ||
                requestType == TradeRequestType.UpdateOffer ||
                requestType == TradeRequestType.Cancel)
            {
                _networkManager.SendTo(packet.TargetId, packet);
            }
            else if (requestType == TradeRequestType.Accept ||
                     requestType == TradeRequestType.Reject)
            {
                _networkManager.SendTo(packet.InitiatorId, packet);
            }
            else if (requestType == TradeRequestType.Complete)
            {
                // Notify both parties
                _networkManager.SendTo(packet.InitiatorId, packet);
                _networkManager.SendTo(packet.TargetId, packet);
            }
        }

        OnTradeRequestReceived?.Invoke(packet);
    }

    public void HandleSession(SessionPacket packet, NetPeer peer)
    {
        var eventType = (SessionEventType)packet.EventType;
        Console.WriteLine($"[BannerBros.Network] Session event: {eventType} for player {packet.PlayerName}");

        // If host, relay session events to all clients
        if (_networkManager.IsHost)
        {
            _networkManager.SendToAllExcept(peer.Id, packet);
        }

        OnSessionEventReceived?.Invoke(packet);
    }

    public void HandleJoinRequest(JoinRequestPacket packet, NetPeer peer)
    {
        Console.WriteLine($"[BannerBros.Network] Join request from {packet.PlayerName} (mod version: {packet.ModVersion})");

        // Only host processes join requests
        if (!_networkManager.IsHost) return;

        OnJoinRequestReceived?.Invoke(packet, peer.Id);
    }

    public void HandleJoinResponse(JoinResponsePacket packet, NetPeer peer)
    {
        Console.WriteLine($"[BannerBros.Network] Join response: Accepted={packet.Accepted}, PlayerId={packet.AssignedPlayerId}");

        // Only clients receive join responses
        if (_networkManager.IsHost) return;

        OnJoinResponseReceived?.Invoke(packet);
    }

    public void HandleCharacterCreation(CharacterCreationPacket packet, NetPeer peer)
    {
        Console.WriteLine($"[BannerBros.Network] Character creation from player {packet.PlayerId}: {packet.CharacterName}");

        // Only host processes character creation
        if (!_networkManager.IsHost) return;

        OnCharacterCreationReceived?.Invoke(packet, peer.Id);
    }

    public void HandleCharacterCreationResponse(CharacterCreationResponsePacket packet, NetPeer peer)
    {
        Console.WriteLine($"[BannerBros.Network] Character creation response for player {packet.PlayerId}: Success={packet.Success}");

        // Only clients receive character creation responses
        if (_networkManager.IsHost) return;

        OnCharacterCreationResponseReceived?.Invoke(packet);
    }

    public void HandleFullStateSync(FullStateSyncPacket packet, NetPeer peer)
    {
        Console.WriteLine($"[BannerBros.Network] Full state sync received");

        // Only clients receive full state sync
        if (_networkManager.IsHost) return;

        OnFullStateSyncReceived?.Invoke(packet);
    }

    public void HandleClientCampaignReady(ClientCampaignReadyPacket packet, NetPeer peer)
    {
        Console.WriteLine($"[BannerBros.Network] ClientCampaignReady received: {packet.HeroName} (player {packet.PlayerId}) from peer {peer.Id}");

        // Only host processes campaign ready notifications
        if (!_networkManager.IsHost)
        {
            Console.WriteLine($"[BannerBros.Network] Not host, ignoring ClientCampaignReady");
            return;
        }

        Console.WriteLine($"[BannerBros.Network] Invoking OnClientCampaignReadyReceived, subscribers: {(OnClientCampaignReadyReceived != null ? "yes" : "NO")}");
        OnClientCampaignReadyReceived?.Invoke(packet, peer.Id);
    }

    // ========================================================================
    // Save File Transfer Handlers
    // ========================================================================

    public void HandleSaveFileRequest(SaveFileRequestPacket packet, NetPeer peer)
    {
        Console.WriteLine($"[BannerBros.Network] Save file request from player {packet.PlayerId}");
        if (!_networkManager.IsHost) return;
        OnSaveFileRequestReceived?.Invoke(packet, peer.Id);
    }

    public void HandleSaveFileStart(SaveFileStartPacket packet, NetPeer peer)
    {
        Console.WriteLine($"[BannerBros.Network] Save file transfer starting: {packet.SaveFileName} ({packet.TotalSize} bytes, {packet.TotalChunks} chunks)");
        if (_networkManager.IsHost) return; // Only clients receive this
        OnSaveFileStartReceived?.Invoke(packet);
    }

    public void HandleSaveFileChunk(SaveFileChunkPacket packet, NetPeer peer)
    {
        Console.WriteLine($"[BannerBros.Network] Save file chunk {packet.ChunkIndex + 1}/{packet.TotalChunks}");
        if (_networkManager.IsHost) return;
        OnSaveFileChunkReceived?.Invoke(packet);
    }

    public void HandleSaveFileComplete(SaveFileCompletePacket packet, NetPeer peer)
    {
        Console.WriteLine($"[BannerBros.Network] Save file transfer complete: {packet.SaveFileName}");
        if (_networkManager.IsHost) return;
        OnSaveFileCompleteReceived?.Invoke(packet);
    }

    public void HandleSaveFileReceived(SaveFileReceivedPacket packet, NetPeer peer)
    {
        Console.WriteLine($"[BannerBros.Network] Client {packet.PlayerId} received save file: Success={packet.Success}");
        if (!_networkManager.IsHost) return;
        OnSaveFileReceivedReceived?.Invoke(packet, peer.Id);
    }

    public void HandleSpectatorReady(SpectatorReadyPacket packet, NetPeer peer)
    {
        Console.WriteLine($"[BannerBros.Network] Spectator ready: {packet.PlayerName} (player {packet.PlayerId})");
        if (!_networkManager.IsHost) return;
        OnSpectatorReadyReceived?.Invoke(packet, peer.Id);
    }

    public void HandlePartyAssignment(PartyAssignmentPacket packet, NetPeer peer)
    {
        Console.WriteLine($"[BannerBros.Network] Party assignment for player {packet.PlayerId}: {packet.PartyId}");
        if (_networkManager.IsHost) return;
        OnPartyAssignmentReceived?.Invoke(packet);
    }

    // ========================================================================
    // Command Handlers (Client -> Host)
    // ========================================================================

    public void HandleMoveCommand(MoveCommandPacket packet, NetPeer peer)
    {
        Console.WriteLine($"[BannerBros.Network] Move command from player {packet.PlayerId} to ({packet.TargetX}, {packet.TargetY})");
        if (!_networkManager.IsHost) return;
        OnMoveCommandReceived?.Invoke(packet, peer.Id);
    }

    public void HandleEnterSettlementCommand(EnterSettlementCommandPacket packet, NetPeer peer)
    {
        Console.WriteLine($"[BannerBros.Network] Enter settlement command from player {packet.PlayerId}: {packet.SettlementId}");
        if (!_networkManager.IsHost) return;
        OnEnterSettlementCommandReceived?.Invoke(packet, peer.Id);
    }

    public void HandleLeaveSettlementCommand(LeaveSettlementCommandPacket packet, NetPeer peer)
    {
        Console.WriteLine($"[BannerBros.Network] Leave settlement command from player {packet.PlayerId}");
        if (!_networkManager.IsHost) return;
        OnLeaveSettlementCommandReceived?.Invoke(packet, peer.Id);
    }

    public void HandleAttackCommand(AttackCommandPacket packet, NetPeer peer)
    {
        Console.WriteLine($"[BannerBros.Network] Attack command from player {packet.PlayerId} targeting {packet.TargetPartyId}");
        if (!_networkManager.IsHost) return;
        OnAttackCommandReceived?.Invoke(packet, peer.Id);
    }

    public void HandleFollowCommand(FollowCommandPacket packet, NetPeer peer)
    {
        Console.WriteLine($"[BannerBros.Network] Follow command from player {packet.PlayerId} targeting {packet.TargetPartyId}");
        if (!_networkManager.IsHost) return;
        OnFollowCommandReceived?.Invoke(packet, peer.Id);
    }

    public void HandleCommandResult(CommandResultPacket packet, NetPeer peer)
    {
        Console.WriteLine($"[BannerBros.Network] Command result for player {packet.PlayerId}: {packet.CommandType} Success={packet.Success}");
        if (_networkManager.IsHost) return;
        OnCommandResultReceived?.Invoke(packet);
    }

    // ========================================================================
    // Debug Log Streaming
    // ========================================================================

    public void HandleDebugLog(DebugLogPacket packet, NetPeer peer)
    {
        // Only host receives debug logs from clients
        if (!_networkManager.IsHost) return;
        OnDebugLogReceived?.Invoke(packet, peer.Id);
    }

    // ========================================================================
    // State Synchronization Handlers (New Architecture)
    // ========================================================================

    public void HandleStateUpdate(StateUpdatePacket packet, NetPeer peer)
    {
        // Clients receive state updates from server
        // Server broadcasts state updates (so also receives from itself in relay scenarios)
        Console.WriteLine($"[BannerBros.Network] State update: Type={packet.UpdateType} Entity={packet.EntityId}");
        OnStateUpdateReceived?.Invoke(packet);
    }

    public void HandleStateSyncJoinRequest(StateSyncJoinRequestPacket packet, NetPeer peer)
    {
        Console.WriteLine($"[BannerBros.Network] State sync join request from player {packet.PlayerId}: {packet.PlayerName}");
        if (!_networkManager.IsHost) return;
        OnStateSyncJoinRequestReceived?.Invoke(packet, peer.Id);
    }

    public void HandleInitialStateSync(InitialStateSyncPacket packet, NetPeer peer)
    {
        Console.WriteLine($"[BannerBros.Network] Initial state sync received: {packet.AssignedPartyId}");
        if (_networkManager.IsHost) return; // Only clients receive this
        OnInitialStateSyncReceived?.Invoke(packet);
    }

    // ========================================================================
    // World Party Batch Sync Handler
    // ========================================================================

    public void HandleWorldPartyBatch(WorldPartyBatchPacket packet, NetPeer peer)
    {
        // Only clients receive world party batch (from host)
        if (_networkManager.IsHost) return;

        if (packet.TotalChunks > 1)
        {
            Console.WriteLine($"[BannerBros.Network] World party batch chunk {packet.ChunkIndex + 1}/{packet.TotalChunks}: {packet.PartyCount} parties, seq={packet.SequenceNumber}");
        }
        else
        {
            Console.WriteLine($"[BannerBros.Network] World party batch received: {packet.PartyCount} parties, seq={packet.SequenceNumber}");
        }
        OnWorldPartyBatchReceived?.Invoke(packet);
    }
}
