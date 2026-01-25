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
}
