using HarmonyLib;
using TaleWorlds.Core;
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

    public bool IsHost { get; private set; }
    public bool IsConnected { get; private set; }

    protected override void OnSubModuleLoad()
    {
        base.OnSubModuleLoad();
        Instance = this;

        Config = BannerBrosConfig.Load();
        PlayerManager = new PlayerManager();
        WorldStateManager = new WorldStateManager();

        InitializeHarmony();

        LogMessage("BannerBros v0.1.0 loaded");
    }

    private void InitializeHarmony()
    {
        try
        {
            _harmony = new Harmony(HarmonyId);
            _harmony.PatchAll(typeof(BannerBrosModule).Assembly);
            LogMessage("Harmony patches applied");
        }
        catch (Exception ex)
        {
            LogMessage($"Failed to apply Harmony patches: {ex.Message}");
        }
    }

    protected override void OnSubModuleUnloaded()
    {
        base.OnSubModuleUnloaded();

        _harmony?.UnpatchAll(HarmonyId);
        Config.Save();
        Instance = null;

        LogMessage("BannerBros unloaded");
    }

    protected override void OnBeforeInitialModuleScreenSetAsRoot()
    {
        base.OnBeforeInitialModuleScreenSetAsRoot();
        // Initialize UI components here
    }

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
    }

    public void HostSession(int port = 7777)
    {
        IsHost = true;
        NetworkManager.Instance?.StartHost(port, Config.MaxPlayers);
        IsConnected = true;
        LogMessage($"Hosting session on port {port}");
    }

    public void JoinSession(string address, int port = 7777)
    {
        IsHost = false;
        NetworkManager.Instance?.Connect(address, port);
        IsConnected = true;
        LogMessage($"Joining session at {address}:{port}");
    }

    public void Disconnect()
    {
        NetworkManager.Instance?.Disconnect();
        IsConnected = false;
        IsHost = false;
        LogMessage("Disconnected from session");
    }

    public static void LogMessage(string message)
    {
        InformationManager.DisplayMessage(new InformationMessage($"[BannerBros] {message}"));
    }
}
