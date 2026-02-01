using HarmonyLib;
using LiteNetLib;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;
using TaleWorlds.CampaignSystem;
using BannerBros.Core.Patches;
using BannerBros.Core.StateSync;
using BannerBros.Network;

namespace BannerBros.Core;

/// <summary>
/// Main entry point for the BannerBros co-op mod.
/// Uses state synchronization - no save file transfer.
/// Each player runs their own campaign, state is synced via network.
/// </summary>
public class BannerBrosModule : MBSubModuleBase
{
    public const string HarmonyId = "com.bannerbros.coop";

    public static BannerBrosModule? Instance { get; private set; }

    private Harmony? _harmony;

    public BannerBrosConfig Config { get; private set; } = new();
    public PlayerManager PlayerManager { get; private set; } = null!;
    public WorldStateManager WorldStateManager { get; private set; } = null!;
    public SessionManager SessionManager { get; private set; } = null!;
    public PlayerSaveData PlayerSaveData { get; set; } = new();

    // Managers
    public SpectatorModeManager SpectatorModeManager { get; private set; } = null!;
    public CommandHandler CommandHandler { get; private set; } = null!;

    /// <summary>
    /// Character data captured from character creation, pending to be sent to server.
    /// </summary>
    public ExportedCharacter? PendingExportedCharacter { get; set; }

    /// <summary>
    /// Save file name received from host, pending to be loaded.
    /// When set, the client should load this save file to sync with host.
    /// </summary>
    public string? PendingCoopSaveToLoad { get; set; }

    public bool IsHost { get; private set; }
    public bool IsConnected { get; private set; }
    public SessionState SessionState => SessionManager?.State ?? SessionState.Disconnected;

    protected override void OnSubModuleLoad()
    {
        base.OnSubModuleLoad();
        Instance = this;

        Config = BannerBrosConfig.Load();
        PlayerManager = new PlayerManager();
        WorldStateManager = new WorldStateManager();
        SessionManager = new SessionManager(PlayerManager, WorldStateManager);

        SpectatorModeManager = new SpectatorModeManager();
        CommandHandler = new CommandHandler(PlayerManager);

        InitializeHarmony();

        LogMessage("BannerBros v0.2.0 loaded (State Sync)");
    }

    private void InitializeHarmony()
    {
        try
        {
            _harmony = new Harmony(HarmonyId);
            _harmony.PatchAll(typeof(BannerBrosModule).Assembly);
            LogMessage("Core Harmony patches applied");
        }
        catch (Exception ex)
        {
            LogMessage($"Failed to apply Harmony patches: {ex.Message}");
        }
    }

    protected override void OnSubModuleUnloaded()
    {
        base.OnSubModuleUnloaded();
        Config?.Save();
        Instance = null;
    }

    protected override void OnApplicationTick(float dt)
    {
        base.OnApplicationTick(dt);

        // Enforce time control even when game is paused
        if (IsConnected && Campaign.Current != null)
        {
            EnforceTimeControl();
        }

        // Check for pending co-op connections when on main menu
        if (!IsConnected && Campaign.Current == null)
        {
            Patches.CoopConnectionManager.CheckAndProcessPendingConnection();
        }

        // Process any queued join requests (host only)
        if (IsHost && IsConnected)
        {
            SessionManager?.ProcessPendingJoinRequests();
        }
    }

    private float _timeEnforceLogTimer = 0;

    private void EnforceTimeControl()
    {
        try
        {
            var campaign = Campaign.Current;
            if (campaign == null) return;

            _timeEnforceLogTimer += 0.016f;
            if (_timeEnforceLogTimer > 10f)
            {
                _timeEnforceLogTimer = 0;
                Patches.TimeControlPatches.ResetLogFlag();
            }

            // Force time to run
            if (campaign.TimeControlMode == CampaignTimeControlMode.Stop)
            {
                campaign.TimeControlMode = CampaignTimeControlMode.StoppablePlay;
            }

            var multiplier = Config.TimeSpeedMultiplier;
            if (multiplier >= 2.0f)
            {
                campaign.SetTimeSpeed(2);
            }
            else
            {
                campaign.SetTimeSpeed(1);
                try { campaign.SpeedUpMultiplier = multiplier; } catch { }
            }
        }
        catch { }
    }

    public void CycleTimeSpeed()
    {
        if (!IsHost) return;

        if (Config.TimeSpeedMultiplier >= 2.0f)
        {
            Config.TimeSpeedMultiplier = 1.0f;
            LogMessage("Time speed: Normal (1x)");
        }
        else
        {
            Config.TimeSpeedMultiplier = 2.0f;
            LogMessage("Time speed: Fast (2x)");
        }
    }

    protected override void OnBeforeInitialModuleScreenSetAsRoot()
    {
        base.OnBeforeInitialModuleScreenSetAsRoot();
        OnCoreModuleReady?.Invoke();
        Patches.CoopConnectionManager.CheckAndProcessPendingConnection();
    }

    public static event Action? OnCoreModuleReady;

    protected override void OnGameStart(Game game, IGameStarter gameStarterObject)
    {
        base.OnGameStart(game, gameStarterObject);

        if (game.GameType is Campaign campaign)
        {
            var starter = (CampaignGameStarter)gameStarterObject;
            InitializeCampaignBehaviors(starter);
        }
    }

    public override void OnGameEnd(Game game)
    {
        base.OnGameEnd(game);

        if (IsConnected)
        {
            Disconnect();
        }
    }

    private void InitializeCampaignBehaviors(CampaignGameStarter starter)
    {
        starter.AddBehavior(new BannerBrosCampaignBehavior());
        starter.AddBehavior(new TimeControlBehavior());
        starter.AddBehavior(new BattleJoinBehavior());
        starter.AddBehavior(new PlayerProtectionBehavior());
        LogMessage("Campaign behaviors loaded");
    }

    public void HostSession(int port = 7777)
    {
        try
        {
            DebugLog.Initialize(isHost: true);

            LogMessage("HostSession: Setting IsHost = true");
            IsHost = true;

            PlayerManager.Clear();

            LogMessage("HostSession: Starting network host...");
            NetworkManager.Instance?.StartHost(port, Config.MaxPlayers);

            if (NetworkManager.Instance?.Messages != null)
            {
                NetworkManager.Instance.Messages.OnDebugLogReceived += OnDebugLogReceived;
            }

            LogMessage("HostSession: Initializing managers...");
            SessionManager.Initialize();
            CommandHandler.Initialize();

            // Initialize state synchronization
            StateSyncManager.Instance.Initialize(isServer: true);
            StateSyncPatches.SetSyncEnabled(true);

            LogMessage("HostSession: Starting host session...");
            SessionManager.StartHostSession();

            IsConnected = true;
            LogMessage($"Hosting session on port {port}");
        }
        catch (Exception ex)
        {
            LogMessage($"HostSession error: {ex.Message}");
            LogMessage($"Stack trace: {ex.StackTrace}");
        }
    }

    private void OnDebugLogReceived(DebugLogPacket packet, int peerId)
    {
        var timestamp = new DateTime(packet.TimestampTicks);
        DebugLog.LogRemoteClient(packet.Message, packet.PlayerId, packet.PlayerName, timestamp);
    }

    /// <summary>
    /// Join a co-op session. Client should already be in their own campaign.
    /// State will be synchronized via network packets.
    /// </summary>
    public void JoinSession(string address, int port = 7777)
    {
        var streamLogs = Config.StreamClientLogs;
        DebugLog.Initialize(isHost: false, streamToServer: streamLogs, playerId: 0, playerName: Config.PlayerName);

        if (streamLogs)
        {
            DebugLog.SendToServerCallback = SendDebugLogToServer;
        }

        if (NetworkManager.Instance == null)
        {
            LogMessage("Error: NetworkManager not initialized!");
            return;
        }

        IsHost = false;
        LogMessage("JoinSession: Initializing managers...");
        SessionManager.Initialize();
        SpectatorModeManager.Initialize();

        // Initialize state synchronization (client mode)
        StateSyncManager.Instance.Initialize(isServer: false);
        ClientBlockingPatches.SetClientMode(true);

        // If reconnecting after loading host's save, reset sync state
        if (!string.IsNullOrEmpty(PendingCoopSaveToLoad))
        {
            LogMessage($"JoinSession: Reconnecting after loading save '{PendingCoopSaveToLoad}'");
            StateSyncManager.Instance.ResetForReconnection();
            PendingCoopSaveToLoad = null; // Clear the pending save flag
        }

        LogMessage("JoinSession: Connecting to server...");
        NetworkManager.Instance.Connect(address, port);
        IsConnected = true;
        LogMessage($"Joining session at {address}:{port}");
    }

    /// <summary>
    /// Flag indicating we're connected just to download the save file (from main menu).
    /// </summary>
    public bool IsDownloadingSave { get; private set; }

    /// <summary>
    /// Connect to host from main menu to download save file.
    /// No campaign is running - we just get the save, then disconnect.
    /// </summary>
    public void ConnectForSaveDownload(string address, int port = 7777)
    {
        LogMessage($"ConnectForSaveDownload: Connecting to {address}:{port}...");

        if (NetworkManager.Instance == null)
        {
            LogMessage("Error: NetworkManager not initialized!");
            return;
        }

        IsHost = false;
        IsDownloadingSave = true;

        // Initialize session manager for save transfer handling
        SessionManager.Initialize();

        // Connect to host
        NetworkManager.Instance.Connect(address, port);
        IsConnected = true;

        LogMessage("ConnectForSaveDownload: Connected, will request save file...");
    }

    /// <summary>
    /// Called when save file download is complete. Disconnects and prepares for manual load.
    /// </summary>
    public void OnSaveDownloadComplete(string saveName)
    {
        LogMessage($"Save download complete: {saveName}");

        // Store for auto-reconnect after load
        PendingCoopSaveToLoad = saveName;
        IsDownloadingSave = false;

        // Disconnect - client will reconnect after loading the save
        Disconnect();

        LogMessage("Disconnected. Load the save file to continue.");
    }

    private void SendDebugLogToServer(string message, int playerId, string playerName)
    {
        if (IsHost || NetworkManager.Instance == null) return;

        var packet = new DebugLogPacket
        {
            PlayerId = playerId,
            PlayerName = playerName,
            Message = message,
            TimestampTicks = DateTime.Now.Ticks
        };

        NetworkManager.Instance.SendToServer(packet, DeliveryMethod.ReliableUnordered);
    }

    public void Disconnect()
    {
        SpectatorModeManager?.Cleanup();
        CommandHandler?.Cleanup();

        StateSyncManager.Instance.Cleanup();
        StateSyncPatches.SetSyncEnabled(false);
        ClientBlockingPatches.SetClientMode(false);

        DebugLog.SendToServerCallback = null;
        if (NetworkManager.Instance?.Messages != null)
        {
            NetworkManager.Instance.Messages.OnDebugLogReceived -= OnDebugLogReceived;
        }

        NetworkManager.Instance?.Disconnect();
        SessionManager.Cleanup();
        PlayerManager.Clear();
        IsConnected = false;
        IsHost = false;
        LogMessage("Disconnected from session");
    }

    public static void LogMessage(string message)
    {
        InformationManager.DisplayMessage(new InformationMessage($"[BannerBros] {message}"));
        DebugLog.Log(message);
    }
}
