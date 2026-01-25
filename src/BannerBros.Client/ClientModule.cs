using HarmonyLib;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.InputSystem;
using TaleWorlds.MountAndBlade;
using BannerBros.Client.UI;
using BannerBros.Core;

namespace BannerBros.Client;

/// <summary>
/// Client-side module handling UI and local player interactions.
/// </summary>
public class ClientModule : MBSubModuleBase
{
    public const string HarmonyId = "com.bannerbros.client";

    public static ClientModule? Instance { get; private set; }

    private Harmony? _harmony;

    public CoopMenuManager MenuManager { get; private set; } = null!;
    public PlayerHUDManager HUDManager { get; private set; } = null!;
    public PlayerMapMarkers MapMarkers { get; private set; } = null!;

    protected override void OnSubModuleLoad()
    {
        base.OnSubModuleLoad();
        Instance = this;

        // RE-ENABLED: Client managers
        MenuManager = new CoopMenuManager();
        HUDManager = new PlayerHUDManager();
        MapMarkers = new PlayerMapMarkers();
        InitializeHarmony(); // RE-ENABLED - adds menu buttons
        SubscribeToSessionEvents(); // RE-ENABLED - join flow

        BannerBrosModule.LogMessage("Client module loaded");
    }

    private void SubscribeToSessionEvents()
    {
        // Subscribe to core module ready event
        BannerBrosModule.OnCoreModuleReady += OnCoreModuleReady;
    }

    public void OnCoreModuleReady()
    {
        var sessionManager = BannerBrosModule.Instance?.SessionManager;
        if (sessionManager != null)
        {
            sessionManager.OnCharacterCreationRequired += OnCharacterCreationRequired;
            sessionManager.OnPlayerSpawned += OnPlayerSpawned;
            sessionManager.OnJoinRejected += OnJoinRejected;
        }
    }

    private void OnCharacterCreationRequired()
    {
        BannerBrosModule.LogMessage("Character creation required - opening creator");
        CharacterCreationUI.Show();
    }

    private void OnPlayerSpawned(CoopPlayer player)
    {
        if (player.NetworkId == BannerBrosModule.Instance?.PlayerManager.LocalPlayerId)
        {
            BannerBrosModule.LogMessage($"Your character {player.Name} has spawned!");
            // TODO: Transition to campaign map view at spawn location
        }
    }

    private void OnJoinRejected(string reason)
    {
        InformationManager.ShowInquiry(
            new InquiryData(
                "Cannot Join",
                $"Failed to join session:\n{reason}",
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

    private void InitializeHarmony()
    {
        try
        {
            _harmony = new Harmony(HarmonyId);
            // RE-ENABLED - EscapeMenuPatch still disabled in patch file
            _harmony.PatchAll(typeof(ClientModule).Assembly);
            BannerBrosModule.LogMessage("Client Harmony patches applied");
        }
        catch (Exception ex)
        {
            BannerBrosModule.LogMessage($"Client patches failed: {ex.Message}");
        }
    }

    protected override void OnSubModuleUnloaded()
    {
        base.OnSubModuleUnloaded();

        // Only cleanup if initialized
        // var sessionManager = BannerBrosModule.Instance?.SessionManager;
        // if (sessionManager != null)
        // {
        //     sessionManager.OnCharacterCreationRequired -= OnCharacterCreationRequired;
        //     sessionManager.OnPlayerSpawned -= OnPlayerSpawned;
        //     sessionManager.OnJoinRejected -= OnJoinRejected;
        // }
        // _harmony?.UnpatchAll(HarmonyId);
        // MapMarkers?.Cleanup();
        Instance = null;
    }

    protected override void OnBeforeInitialModuleScreenSetAsRoot()
    {
        base.OnBeforeInitialModuleScreenSetAsRoot();

        // Initialize menu extension
        MainMenuExtension.Initialize();
    }

    protected override void OnApplicationTick(float dt)
    {
        base.OnApplicationTick(dt);

        // RE-ENABLED
        HUDManager.Update(dt);
        MapMarkers.Update(dt);
        HandleCoopInput();
    }

    private void HandleCoopInput()
    {
        var module = BannerBrosModule.Instance;
        if (module?.IsConnected != true) return;

        // J - Join nearby battle
        if (Input.IsKeyPressed(InputKey.J))
        {
            TryJoinNearbyBattle();
        }

        // P - Show player list
        if (Input.IsKeyPressed(InputKey.P))
        {
            CoopSessionMenu.ShowPlayerList();
        }

        // Escape menu additions are handled via Harmony patches
    }

    private void TryJoinNearbyBattle()
    {
        var module = BannerBrosModule.Instance;
        if (module == null) return;

        var localPlayer = module.PlayerManager.GetLocalPlayer();
        if (localPlayer == null || localPlayer.State != PlayerState.OnMap) return;

        var nearbyBattle = module.WorldStateManager.FindBattleAtPosition(
            localPlayer.MapPositionX,
            localPlayer.MapPositionY,
            3.0f
        );

        if (nearbyBattle != null)
        {
            ShowBattleJoinDialog(nearbyBattle);
        }
        else
        {
            BannerBrosModule.LogMessage("No battle nearby to join");
        }
    }

    private void ShowBattleJoinDialog(BattleInstance battle)
    {
        var module = BannerBrosModule.Instance;
        if (module == null) return;

        var initiator = module.PlayerManager.GetPlayer(battle.InitiatorPlayerId);
        var initiatorName = initiator?.Name ?? "Unknown";

        InformationManager.ShowInquiry(
            new InquiryData(
                "Join Battle",
                $"{initiatorName} is fighting nearby.\nWhich side do you want to join?",
                true,
                true,
                "Join as Ally",
                "Join as Enemy",
                () => JoinBattle(battle, true),
                () => JoinBattle(battle, false)
            ),
            true
        );
    }

    private void JoinBattle(BattleInstance battle, bool asAlly)
    {
        var module = BannerBrosModule.Instance;
        if (module == null) return;

        // Determine which side to join based on initiator's side
        var initiatorSide = battle.PlayerSides.TryGetValue(battle.InitiatorPlayerId, out var side) ? side : BattleSide.Attacker;
        var joinSide = asAlly ? initiatorSide :
            (initiatorSide == BattleSide.Attacker ? BattleSide.Defender : BattleSide.Attacker);

        // The actual battle joining is handled by the game's party encounter system
        // We just need to trigger movement towards the battle location
        BannerBrosModule.LogMessage($"Joining battle as {joinSide}");
    }
}
