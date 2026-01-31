using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
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
    public event Action<string>? ConnectionRejected;

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
        // Subscribe to packet handlers
        // NetPacketProcessor auto-serializes classes with public properties
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
        _packetProcessor.SubscribeReusable<ClientCampaignReadyPacket, NetPeer>(_messageHandler.HandleClientCampaignReady);

        // Save file transfer packets
        _packetProcessor.SubscribeReusable<SaveFileRequestPacket, NetPeer>(_messageHandler.HandleSaveFileRequest);
        _packetProcessor.SubscribeReusable<SaveFileStartPacket, NetPeer>(_messageHandler.HandleSaveFileStart);
        _packetProcessor.SubscribeReusable<SaveFileChunkPacket, NetPeer>(_messageHandler.HandleSaveFileChunk);
        _packetProcessor.SubscribeReusable<SaveFileCompletePacket, NetPeer>(_messageHandler.HandleSaveFileComplete);
        _packetProcessor.SubscribeReusable<SaveFileReceivedPacket, NetPeer>(_messageHandler.HandleSaveFileReceived);
        _packetProcessor.SubscribeReusable<SpectatorReadyPacket, NetPeer>(_messageHandler.HandleSpectatorReady);
        _packetProcessor.SubscribeReusable<PartyAssignmentPacket, NetPeer>(_messageHandler.HandlePartyAssignment);

        // Command packets
        _packetProcessor.SubscribeReusable<MoveCommandPacket, NetPeer>(_messageHandler.HandleMoveCommand);
        _packetProcessor.SubscribeReusable<EnterSettlementCommandPacket, NetPeer>(_messageHandler.HandleEnterSettlementCommand);
        _packetProcessor.SubscribeReusable<LeaveSettlementCommandPacket, NetPeer>(_messageHandler.HandleLeaveSettlementCommand);
        _packetProcessor.SubscribeReusable<AttackCommandPacket, NetPeer>(_messageHandler.HandleAttackCommand);
        _packetProcessor.SubscribeReusable<FollowCommandPacket, NetPeer>(_messageHandler.HandleFollowCommand);
        _packetProcessor.SubscribeReusable<CommandResultPacket, NetPeer>(_messageHandler.HandleCommandResult);

        // Debug log streaming
        _packetProcessor.SubscribeReusable<DebugLogPacket, NetPeer>(_messageHandler.HandleDebugLog);

        // State synchronization packets (new architecture - no save file transfer)
        _packetProcessor.SubscribeReusable<StateUpdatePacket, NetPeer>(_messageHandler.HandleStateUpdate);
        _packetProcessor.SubscribeReusable<StateSyncJoinRequestPacket, NetPeer>(_messageHandler.HandleStateSyncJoinRequest);
        _packetProcessor.SubscribeReusable<InitialStateSyncPacket, NetPeer>(_messageHandler.HandleInitialStateSync);

        // World party batch sync (efficient sync of all parties)
        _packetProcessor.SubscribeReusable<WorldPartyBatchPacket, NetPeer>(_messageHandler.HandleWorldPartyBatch);
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
        try
        {
            _netManager?.PollEvents();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[BannerBros.Network] ERROR in PollEvents: {ex.Message}");
            Console.WriteLine($"[BannerBros.Network] Stack trace: {ex.StackTrace}");
        }
    }

    public void Send<T>(T packet, DeliveryMethod deliveryMethod = DeliveryMethod.ReliableOrdered) where T : class, new()
    {
        if (!IsRunning) return;

        var writer = new NetDataWriter();
        _packetProcessor.Write(writer, packet);

        // ToList() to avoid collection modified exception if peer connects/disconnects
        foreach (var peer in _peers.Values.ToList())
        {
            try
            {
                peer.Send(writer, deliveryMethod);
            }
            catch
            {
                // Ignore send errors for individual peers
            }
        }
    }

    /// <summary>
    /// Sends a packet to all connected peers.
    /// </summary>
    public void SendToAll<T>(T packet, DeliveryMethod deliveryMethod = DeliveryMethod.ReliableOrdered) where T : class, new()
    {
        Send(packet, deliveryMethod);
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

        // ToList() to avoid collection modified exception
        foreach (var kvp in _peers.ToList())
        {
            if (kvp.Key != excludePeerId)
            {
                try
                {
                    kvp.Value.Send(writer, deliveryMethod);
                }
                catch
                {
                    // Ignore send errors for individual peers
                }
            }
        }
    }

    /// <summary>
    /// Sends a packet to the server (for clients only).
    /// </summary>
    public void SendToServer<T>(T packet, DeliveryMethod deliveryMethod = DeliveryMethod.ReliableOrdered) where T : class, new()
    {
        if (!IsRunning)
        {
            Console.WriteLine($"[BannerBros.Network] SendToServer FAILED: Not running");
            return;
        }
        if (_isHost)
        {
            Console.WriteLine($"[BannerBros.Network] SendToServer FAILED: We are host");
            return;
        }
        if (_serverPeer == null)
        {
            Console.WriteLine($"[BannerBros.Network] SendToServer FAILED: No server peer");
            return;
        }

        var writer = new NetDataWriter();
        _packetProcessor.Write(writer, packet);
        _serverPeer.Send(writer, deliveryMethod);
        Console.WriteLine($"[BannerBros.Network] Sent {typeof(T).Name} to server");
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
        try
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
        catch (Exception ex)
        {
            Console.WriteLine($"[BannerBros.Network] ERROR in OnPeerConnected: {ex.Message}");
        }
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
            LiteNetLib.DisconnectReason.ConnectionRejected => DisconnectReason.Rejected,
            LiteNetLib.DisconnectReason.InvalidProtocol => DisconnectReason.VersionMismatch,
            _ => DisconnectReason.Unknown
        };

        Console.WriteLine($"[BannerBros.Network] Peer {peerId} disconnected: {disconnectInfo.Reason}");

        // For clients, provide feedback on connection rejection
        if (!_isHost && reason == DisconnectReason.Rejected)
        {
            ConnectionRejected?.Invoke("Connection rejected - server may be full or version mismatch");
        }
        else if (!_isHost && reason == DisconnectReason.VersionMismatch)
        {
            ConnectionRejected?.Invoke("Version mismatch - please update your mod");
        }
        else if (!_isHost && reason == DisconnectReason.ConnectionFailed)
        {
            ConnectionRejected?.Invoke("Could not connect to server - check address and port");
        }
        else if (!_isHost && reason == DisconnectReason.Timeout)
        {
            ConnectionRejected?.Invoke("Connection timed out - server may be unreachable");
        }

        PeerDisconnected?.Invoke(peerId, reason);
    }

    void INetEventListener.OnNetworkError(System.Net.IPEndPoint endPoint, System.Net.Sockets.SocketError socketError)
    {
        Console.WriteLine($"[BannerBros.Network] Network error: {socketError}");
    }

    void INetEventListener.OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channelNumber, DeliveryMethod deliveryMethod)
    {
        try
        {
            _packetProcessor.ReadAllPackets(reader, peer);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[BannerBros.Network] ERROR processing packet from peer {peer.Id}: {ex.Message}");
            Console.WriteLine($"[BannerBros.Network] Stack trace: {ex.StackTrace}");
        }
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
    ServerClosed,
    Rejected,
    VersionMismatch
}
