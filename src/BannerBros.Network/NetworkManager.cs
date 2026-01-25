using LiteNetLib;
using LiteNetLib.Utils;

namespace BannerBros.Network;

/// <summary>
/// Core networking manager using LiteNetLib.
/// Handles both host (server) and client connections.
/// </summary>
public class NetworkManager : INetEventListener
{
    public static NetworkManager? Instance { get; private set; }

    private NetManager? _netManager;
    private readonly Dictionary<int, NetPeer> _peers = new();
    private readonly NetPacketProcessor _packetProcessor = new();
    private readonly MessageHandler _messageHandler;

    private bool _isHost;
    private int _maxPlayers = 4;
    private int _localPeerId = -1;
    private NetPeer? _serverPeer; // For clients: the server connection

    public bool IsRunning => _netManager?.IsRunning == true;
    public bool IsHost => _isHost;
    public int ConnectedPeers => _peers.Count;
    public int LocalPeerId => _localPeerId;
    public MessageHandler Messages => _messageHandler;

    public event Action<int>? PeerConnected;
    public event Action<int, DisconnectReason>? PeerDisconnected;

    private NetworkManager()
    {
        _messageHandler = new MessageHandler(this);
        RegisterPacketTypes();
    }

    public static void Initialize()
    {
        Instance = new NetworkManager();
    }

    public static void Shutdown()
    {
        Instance?.Stop();
        Instance = null;
    }

    private void RegisterPacketTypes()
    {
        // Register nested types for serialization
        _packetProcessor.RegisterNestedType<PlayerStatePacket>();
        _packetProcessor.RegisterNestedType<WorldSyncPacket>();
        _packetProcessor.RegisterNestedType<BattleEventPacket>();
        _packetProcessor.RegisterNestedType<ChatPacket>();
        _packetProcessor.RegisterNestedType<TradeRequestPacket>();
        _packetProcessor.RegisterNestedType<SessionPacket>();
        _packetProcessor.RegisterNestedType<JoinRequestPacket>();
        _packetProcessor.RegisterNestedType<JoinResponsePacket>();
        _packetProcessor.RegisterNestedType<CharacterCreationPacket>();
        _packetProcessor.RegisterNestedType<CharacterCreationResponsePacket>();
        _packetProcessor.RegisterNestedType<FullStateSyncPacket>();
        _packetProcessor.RegisterNestedType<ConnectedPlayerInfo>();
        _packetProcessor.RegisterNestedType<BattleInfo>();
        _packetProcessor.RegisterNestedType<DiplomacyState>();

        // Subscribe to packet handlers
        _packetProcessor.SubscribeReusable<PlayerStatePacket, NetPeer>(_messageHandler.HandlePlayerState);
        _packetProcessor.SubscribeReusable<WorldSyncPacket, NetPeer>(_messageHandler.HandleWorldSync);
        _packetProcessor.SubscribeReusable<BattleEventPacket, NetPeer>(_messageHandler.HandleBattleEvent);
        _packetProcessor.SubscribeReusable<ChatPacket, NetPeer>(_messageHandler.HandleChat);
        _packetProcessor.SubscribeReusable<TradeRequestPacket, NetPeer>(_messageHandler.HandleTradeRequest);
        _packetProcessor.SubscribeReusable<SessionPacket, NetPeer>(_messageHandler.HandleSession);
        _packetProcessor.SubscribeReusable<JoinRequestPacket, NetPeer>(_messageHandler.HandleJoinRequest);
        _packetProcessor.SubscribeReusable<JoinResponsePacket, NetPeer>(_messageHandler.HandleJoinResponse);
        _packetProcessor.SubscribeReusable<CharacterCreationPacket, NetPeer>(_messageHandler.HandleCharacterCreation);
        _packetProcessor.SubscribeReusable<CharacterCreationResponsePacket, NetPeer>(_messageHandler.HandleCharacterCreationResponse);
        _packetProcessor.SubscribeReusable<FullStateSyncPacket, NetPeer>(_messageHandler.HandleFullStateSync);
    }

    public void StartHost(int port, int maxPlayers = 4)
    {
        if (IsRunning) Stop();

        _isHost = true;
        _maxPlayers = maxPlayers;

        _netManager = new NetManager(this)
        {
            AutoRecycle = true,
            UpdateTime = 15, // 15ms update interval
            DisconnectTimeout = 10000
        };

        _netManager.Start(port);
        _localPeerId = 0; // Host is always peer 0

        Console.WriteLine($"[BannerBros.Network] Host started on port {port}");
    }

    public void Connect(string address, int port)
    {
        if (IsRunning) Stop();

        _isHost = false;

        _netManager = new NetManager(this)
        {
            AutoRecycle = true,
            UpdateTime = 15,
            DisconnectTimeout = 10000
        };

        _netManager.Start();
        _netManager.Connect(address, port, "BannerBros_v0.1");

        Console.WriteLine($"[BannerBros.Network] Connecting to {address}:{port}");
    }

    public void Stop()
    {
        _netManager?.Stop();
        _peers.Clear();
        _isHost = false;
        _localPeerId = -1;

        Console.WriteLine("[BannerBros.Network] Stopped");
    }

    public void Disconnect()
    {
        Stop();
    }

    public void Update(float dt)
    {
        _netManager?.PollEvents();
    }

    public void Send<T>(T packet, DeliveryMethod deliveryMethod = DeliveryMethod.ReliableOrdered) where T : class, new()
    {
        if (!IsRunning) return;

        var writer = new NetDataWriter();
        _packetProcessor.Write(writer, packet);

        foreach (var peer in _peers.Values)
        {
            peer.Send(writer, deliveryMethod);
        }
    }

    public void SendTo<T>(int peerId, T packet, DeliveryMethod deliveryMethod = DeliveryMethod.ReliableOrdered) where T : class, new()
    {
        if (!IsRunning || !_peers.TryGetValue(peerId, out var peer)) return;

        var writer = new NetDataWriter();
        _packetProcessor.Write(writer, packet);
        peer.Send(writer, deliveryMethod);
    }

    public void SendToAllExcept<T>(int excludePeerId, T packet, DeliveryMethod deliveryMethod = DeliveryMethod.ReliableOrdered) where T : class, new()
    {
        if (!IsRunning) return;

        var writer = new NetDataWriter();
        _packetProcessor.Write(writer, packet);

        foreach (var (peerId, peer) in _peers)
        {
            if (peerId != excludePeerId)
            {
                peer.Send(writer, deliveryMethod);
            }
        }
    }

    /// <summary>
    /// Sends a packet to the server (for clients only).
    /// </summary>
    public void SendToServer<T>(T packet, DeliveryMethod deliveryMethod = DeliveryMethod.ReliableOrdered) where T : class, new()
    {
        if (!IsRunning || _isHost || _serverPeer == null) return;

        var writer = new NetDataWriter();
        _packetProcessor.Write(writer, packet);
        _serverPeer.Send(writer, deliveryMethod);
    }

    /// <summary>
    /// Sets the local peer ID (used when joining a session).
    /// </summary>
    public void SetLocalPeerId(int peerId)
    {
        _localPeerId = peerId;
    }

    public int GetLocalPeerId() => _localPeerId;

    // INetEventListener implementation

    void INetEventListener.OnPeerConnected(NetPeer peer)
    {
        var peerId = peer.Id;
        _peers[peerId] = peer;

        // For clients, track the server connection
        if (!_isHost)
        {
            _serverPeer = peer;
        }

        Console.WriteLine($"[BannerBros.Network] Peer {peerId} connected from {peer.Address}");
        PeerConnected?.Invoke(peerId);
    }

    void INetEventListener.OnPeerDisconnected(NetPeer peer, DisconnectInfo disconnectInfo)
    {
        var peerId = peer.Id;
        _peers.Remove(peerId);

        var reason = disconnectInfo.Reason switch
        {
            LiteNetLib.DisconnectReason.ConnectionFailed => DisconnectReason.ConnectionFailed,
            LiteNetLib.DisconnectReason.Timeout => DisconnectReason.Timeout,
            LiteNetLib.DisconnectReason.RemoteConnectionClose => DisconnectReason.Kicked,
            _ => DisconnectReason.Unknown
        };

        Console.WriteLine($"[BannerBros.Network] Peer {peerId} disconnected: {disconnectInfo.Reason}");
        PeerDisconnected?.Invoke(peerId, reason);
    }

    void INetEventListener.OnNetworkError(System.Net.IPEndPoint endPoint, System.Net.Sockets.SocketError socketError)
    {
        Console.WriteLine($"[BannerBros.Network] Network error: {socketError}");
    }

    void INetEventListener.OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channelNumber, DeliveryMethod deliveryMethod)
    {
        _packetProcessor.ReadAllPackets(reader, peer);
    }

    void INetEventListener.OnNetworkReceiveUnconnected(System.Net.IPEndPoint remoteEndPoint, NetPacketReader reader, UnconnectedMessageType messageType)
    {
        // Handle unconnected messages (e.g., server discovery)
    }

    void INetEventListener.OnNetworkLatencyUpdate(NetPeer peer, int latency)
    {
        // Track latency for each peer
    }

    void INetEventListener.OnConnectionRequest(ConnectionRequest request)
    {
        if (_peers.Count < _maxPlayers - 1) // -1 because host counts as 1
        {
            request.AcceptIfKey("BannerBros_v0.1");
        }
        else
        {
            request.Reject();
        }
    }
}

public enum DisconnectReason
{
    Unknown,
    ConnectionFailed,
    Timeout,
    Kicked,
    ServerClosed
}
