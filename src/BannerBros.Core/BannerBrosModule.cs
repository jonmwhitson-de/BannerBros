using HarmonyLib;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;
using TaleWorlds.CampaignSystem;
using BannerBros.Core.Patches;
using BannerBros.Network;

namespace BannerBros.Core;

/// <summary>
/// Main entry point for the BannerBros co-op mod.
/// Manages core functionality and coordinates between network and client modules.
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

    // Single Authoritative Campaign managers
    public SaveFileTransferManager SaveFileTransferManager { get; private set; } = null!;
    public SpectatorModeManager SpectatorModeManager { get; private set; } = null!;
    public CommandHandler CommandHandler { get; private set; } = null!;

    /// <summary>
    /// Character data captured from character creation, pending to be sent to server.
    /// </summary>
    public ExportedCharacter? PendingExportedCharacter { get; set; }

    /// <summary>
    /// Path to save file received from host, waiting to be loaded.
    /// </summary>
    public string? PendingSaveFilePath { get; set; }

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

        // Initialize single authoritative campaign managers
        SaveFileTransferManager = new SaveFileTransferManager(SessionManager);
        SpectatorModeManager = new SpectatorModeManager();
        CommandHandler = new CommandHandler(PlayerManager);

        InitializeHarmony();

        LogMessage("BannerBros v0.1.0 loaded");
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

        // SessionManager?.Cleanup(); // Disabled
        // _harmony?.UnpatchAll(HarmonyId); // Disabled
        Config?.Save();
        Instance = null;
    }

    private float _timeCheckTimer;
    private bool _hasLoggedTimeMode;

    protected override void OnApplicationTick(float dt)
    {
        base.OnApplicationTick(dt);

        // Enforce time control even when game is paused
        if (IsConnected && Campaign.Current != null)
        {
            EnforceTimeControl();
        }

        // Check for pending co-op connections when on main menu
        // This handles the case where player returns to menu after character creation
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

    private void EnforceTimeControl()
    {
        try
        {
            var campaign = Campaign.Current;
            if (campaign == null) return;

            // Force time to run EVERY frame - game constantly tries to pause
            // SetTimeSpeed: 0=pause, 1=play, 2=fast forward
            var multiplier = Config.TimeSpeedMultiplier;

            if (multiplier >= 2.0f)
            {
                // Fast forward
                campaign.SetTimeSpeed(2);
            }
            else
            {
                // Normal or slow - use SetTimeSpeed(1) then adjust multiplier
                campaign.SetTimeSpeed(1);

                // Try to set the speed multiplier for fine control
                try
                {
                    campaign.SpeedUpMultiplier = multiplier;
                }
                catch
                {
                    // SpeedUpMultiplier might not work, that's ok
                }
            }
        }
        catch
        {
            // SetTimeSpeed might not exist, try direct property
            try
            {
                var campaign = Campaign.Current;
                if (campaign != null)
                {
                    campaign.TimeControlMode = CampaignTimeControlMode.StoppablePlay;
                }
            }
            catch
            {
                // Ignore
            }
        }
    }

    /// <summary>
    /// Cycles time speed between normal and fast (host only).
    /// </summary>
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
        // Notify other modules that core is ready
        OnCoreModuleReady?.Invoke();

        // Check for pending co-op connections (after character creation)
        Patches.CoopConnectionManager.CheckAndProcessPendingConnection();
    }

    /// <summary>
    /// Event fired when core module is fully initialized.
    /// Other modules can subscribe to this to know when to wire up dependencies.
    /// </summary>
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

        // Cleanup network connections when game ends
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
        LogMessage("All campaign behaviors loaded");
    }

    public void HostSession(int port = 7777)
    {
        try
        {
            LogMessage("HostSession: Setting IsHost = true");
            IsHost = true;

            // Clear any existing players from previous sessions
            PlayerManager.Clear();

            LogMessage("HostSession: Starting network host...");
            if (NetworkManager.Instance == null)
            {
                LogMessage("Warning: NetworkManager.Instance is null!");
            }
            NetworkManager.Instance?.StartHost(port, Config.MaxPlayers);

            LogMessage("HostSession: Initializing SessionManager...");
            SessionManager.Initialize();

            // Initialize host-side managers
            SaveFileTransferManager.Initialize();
            CommandHandler.Initialize();

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

    public void JoinSession(string address, int port = 7777)
    {
        if (NetworkManager.Instance == null)
        {
            LogMessage("Error: NetworkManager not initialized!");
            return;
        }

        IsHost = false;
        LogMessage("JoinSession: Initializing SessionManager...");
        SessionManager.Initialize();

        // Initialize client-side managers
        SaveFileTransferManager.Initialize();
        SpectatorModeManager.Initialize();

        // Set up save file transfer callback
        SaveFileTransferManager.OnSaveFileReady += OnSaveFileReadyToLoad;
        SaveFileTransferManager.OnTransferProgress += OnSaveTransferProgress;

        LogMessage("JoinSession: Connecting to server...");
        NetworkManager.Instance.Connect(address, port);
        IsConnected = true;
        LogMessage($"Joining session at {address}:{port}");
    }

    /// <summary>
    /// Called when save file has been received and is ready to load.
    /// </summary>
    private void OnSaveFileReadyToLoad(string savePath)
    {
        LogMessage($"Save file ready at: {savePath}");
        PendingSaveFilePath = savePath;

        // Attempt auto-load after a small delay for file system to settle
        LogMessage("Attempting to auto-load save file...");

        System.Threading.Tasks.Task.Run(async () =>
        {
            await System.Threading.Tasks.Task.Delay(500);

            // Must run on main thread via game's action queue
            try
            {
                // Try to queue on main thread
                var utilitiesType = typeof(TaleWorlds.Engine.Utilities);
                var enqueueMethod = utilitiesType.GetMethod("EnqueueAction",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);

                if (enqueueMethod != null)
                {
                    enqueueMethod.Invoke(null, new object[] { (Action)(() => TryAutoLoadSave(savePath)) });
                }
                else
                {
                    // Fallback: try directly (may cause threading issues)
                    TryAutoLoadSave(savePath);
                }
            }
            catch (Exception ex)
            {
                LogMessage($"Error queuing auto-load: {ex.Message}");
                ShowManualLoadInstructions(savePath);
            }
        });
    }

    /// <summary>
    /// Attempts to auto-load the save file.
    /// </summary>
    private void TryAutoLoadSave(string savePath)
    {
        try
        {
            if (SaveGameLoader.LoadSaveFile(savePath))
            {
                LogMessage("Save file auto-load initiated!");
            }
            else
            {
                LogMessage("Auto-load failed - showing manual instructions");
                ShowManualLoadInstructions(savePath);
            }
        }
        catch (Exception ex)
        {
            LogMessage($"Auto-load error: {ex.Message}");
            ShowManualLoadInstructions(savePath);
        }
    }

    /// <summary>
    /// Shows instructions for manual save loading when auto-load fails.
    /// </summary>
    private void ShowManualLoadInstructions(string savePath)
    {
        var saveName = System.IO.Path.GetFileNameWithoutExtension(savePath);
        InformationManager.ShowInquiry(
            new InquiryData(
                "Load Save Manually",
                $"Auto-load not available. Please:\n\n" +
                $"1. Go to 'Load Game' on the main menu\n" +
                $"2. Load the save named '{saveName}'\n" +
                $"3. You'll enter spectator mode automatically",
                true,
                false,
                "OK",
                "",
                null,
                null
            ),
            true
        );
    }

    /// <summary>
    /// Called during save file transfer to show progress.
    /// </summary>
    private void OnSaveTransferProgress(float progress)
    {
        if ((int)(progress * 100) % 20 == 0) // Log every 20%
        {
            LogMessage($"Save file transfer: {progress * 100:F0}%");
        }
    }

    public void Disconnect()
    {
        // Cleanup managers
        SaveFileTransferManager?.Cleanup();
        SpectatorModeManager?.Cleanup();
        CommandHandler?.Cleanup();

        NetworkManager.Instance?.Disconnect();
        SessionManager.Cleanup();
        PlayerManager.Clear();
        PendingSaveFilePath = null;
        IsConnected = false;
        IsHost = false;
        LogMessage("Disconnected from session");
    }

    public static void LogMessage(string message)
    {
        InformationManager.DisplayMessage(new InformationMessage($"[BannerBros] {message}"));
    }
}
