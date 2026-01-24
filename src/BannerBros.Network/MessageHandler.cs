using LiteNetLib;

namespace BannerBros.Network;

/// <summary>
/// Handles incoming network messages and dispatches them appropriately.
/// </summary>
public class MessageHandler
{
    private readonly NetworkManager _networkManager;

    public event Action<PlayerStatePacket>? OnPlayerStateReceived;
    public event Action<WorldSyncPacket>? OnWorldSyncReceived;
    public event Action<BattleEventPacket>? OnBattleEventReceived;
    public event Action<ChatPacket>? OnChatReceived;
    public event Action<TradeRequestPacket>? OnTradeRequestReceived;

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
            if (packet.Channel == 2 && packet.TargetPlayerId.HasValue)
            {
                // Whisper - only send to target
                _networkManager.SendTo(packet.TargetPlayerId.Value, packet);
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
}
