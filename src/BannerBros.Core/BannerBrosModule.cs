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

            LogMessage("HostSession: Starting network host...");
            if (NetworkManager.Instance == null)
            {
                LogMessage("Warning: NetworkManager.Instance is null!");
            }
            NetworkManager.Instance?.StartHost(port, Config.MaxPlayers);

            LogMessage("HostSession: Initializing SessionManager...");
            SessionManager.Initialize();

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
        IsHost = false;
        SessionManager.Initialize();
        NetworkManager.Instance?.Connect(address, port);
        IsConnected = true;
        LogMessage($"Joining session at {address}:{port}");
    }

    public void Disconnect()
    {
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
    }
}
