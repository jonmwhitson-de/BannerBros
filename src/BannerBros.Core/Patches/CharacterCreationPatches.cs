using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.GameState;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;
using System.Reflection;

namespace BannerBros.Core.Patches;

/// <summary>
/// Harmony patches to intercept character creation for co-op character export.
/// When a player creates a character for co-op, we capture the data and prevent
/// the campaign from actually starting.
/// </summary>
public static class CharacterCreationPatches
{
    /// <summary>
    /// Flag indicating we're creating a character for co-op export, not normal play.
    /// </summary>
    public static bool IsCreatingForCoopExport { get; set; }

    /// <summary>
    /// The server address to connect to after character creation.
    /// </summary>
    public static string? PendingServerAddress { get; set; }

    /// <summary>
    /// The server port to connect to after character creation.
    /// </summary>
    public static int PendingServerPort { get; set; } = 7777;

    /// <summary>
    /// The captured character data after creation completes.
    /// </summary>
    public static ExportedCharacter? CapturedCharacter { get; set; }

    /// <summary>
    /// Event fired when character creation is complete and captured.
    /// </summary>
    public static event Action<ExportedCharacter>? OnCharacterCaptured;

    /// <summary>
    /// Invoke the OnCharacterCaptured event (callable from other classes).
    /// </summary>
    public static void RaiseCharacterCaptured(ExportedCharacter character)
    {
        OnCharacterCaptured?.Invoke(character);
    }

    /// <summary>
    /// Call this to start character creation in co-op export mode.
    /// After creation, the character will be captured and the player
    /// will be redirected to join the co-op server.
    /// </summary>
    public static void StartCoopCharacterCreation(string serverAddress, int serverPort)
    {
        IsCreatingForCoopExport = true;
        PendingServerAddress = serverAddress;
        PendingServerPort = serverPort;
        CapturedCharacter = null;

        BannerBrosModule.LogMessage("=== CO-OP CHARACTER CREATION MODE ENABLED ===");
        BannerBrosModule.LogMessage($"Server: {serverAddress}:{serverPort}");
        BannerBrosModule.LogMessage("Go to Campaign > New Campaign to create your character.");
        BannerBrosModule.LogMessage("Your character will be captured when the map loads.");

        // Start a new campaign - this triggers character creation
        // The game handles showing the character creation screens
    }

    /// <summary>
    /// Resets the co-op export state.
    /// </summary>
    public static void ResetCoopExportState()
    {
        IsCreatingForCoopExport = false;
        PendingServerAddress = null;
        PendingServerPort = 7777;
        CapturedCharacter = null;
    }
}

/// <summary>
/// Patch for when character creation is finalized.
/// This fires when the player completes all character creation stages.
/// </summary>
[HarmonyPatch]
public static class CharacterCreationFinalizedPatch
{
    /// <summary>
    /// Try to find the method that gets called when character creation ends.
    /// Different Bannerlord versions may have different method names.
    /// </summary>
    static MethodBase? TargetMethod()
    {
        // Try to find CharacterCreationState via reflection since it may be in different namespaces
        var assembly = typeof(Campaign).Assembly;
        var stateTypeNames = new[]
        {
            "TaleWorlds.CampaignSystem.GameState.CharacterCreationState",
            "TaleWorlds.CampaignSystem.CharacterCreationState",
            "TaleWorlds.MountAndBlade.CharacterCreationState"
        };

        Type? stateType = null;
        foreach (var typeName in stateTypeNames)
        {
            stateType = assembly.GetType(typeName);
            if (stateType != null) break;
        }

        if (stateType == null)
        {
            return null;
        }

        // Try various method names that might exist
        var methodNames = new[]
        {
            "OnCharacterCreationFinalized",
            "FinalizeCharacterCreation",
            "OnFinalize",
            "Finalize"
        };

        foreach (var name in methodNames)
        {
            var method = stateType.GetMethod(name,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (method != null)
            {
                BannerBrosModule.LogMessage($"Found character creation method: {name}");
                return method;
            }
        }

        return null;
    }

    static bool Prepare()
    {
        // Only apply patch if we can find the target method
        return TargetMethod() != null;
    }

    static void Postfix()
    {
        if (!CharacterCreationPatches.IsCreatingForCoopExport)
        {
            return;
        }

        try
        {
            BannerBrosModule.LogMessage("Character creation finalized - capturing for co-op...");

            // Capture the character data
            var captured = ExportedCharacter.CaptureFromMainHero();
            CharacterCreationPatches.CapturedCharacter = captured;

            // Save locally as backup
            captured.SaveToFile();

            // Fire event
            CharacterCreationPatches.RaiseCharacterCaptured(captured);

            BannerBrosModule.LogMessage($"Character '{captured.Name}' captured successfully!");
            BannerBrosModule.LogMessage("Exiting to menu to join co-op server...");

            // Schedule exit to main menu and auto-connect
            // This needs to happen on the main thread after the current frame
            ScheduleExitAndConnect();
        }
        catch (Exception ex)
        {
            BannerBrosModule.LogMessage($"Failed to capture character: {ex.Message}");
            CharacterCreationPatches.ResetCoopExportState();
        }
    }

    private static void ScheduleExitAndConnect()
    {
        // We need to exit to main menu and then connect to the server
        // This is tricky because we need to let the current frame complete first

        // Store connection info
        var address = CharacterCreationPatches.PendingServerAddress;
        var port = CharacterCreationPatches.PendingServerPort;
        var character = CharacterCreationPatches.CapturedCharacter;

        // Reset state
        CharacterCreationPatches.ResetCoopExportState();

        // Mark that we should auto-connect after returning to menu
        if (address != null && character != null)
        {
            CoopConnectionManager.PendingConnection = new PendingCoopConnection
            {
                ServerAddress = address,
                ServerPort = port,
                CharacterData = character
            };
        }

        // Exit to main menu - don't save this throwaway campaign
        try
        {
            // Try to exit without saving using reflection to find the right state type
            var gameStateManager = Game.Current?.GameStateManager;
            if (gameStateManager != null)
            {
                // Try to find InitialState or similar menu state
                var createStateMethod = gameStateManager.GetType().GetMethod("CreateState");
                if (createStateMethod != null)
                {
                    // Look for available state types
                    var assembly = typeof(Game).Assembly;
                    var stateTypes = new[] { "InitialState", "MainMenuState", "LobbyState" };

                    foreach (var stateName in stateTypes)
                    {
                        var stateType = assembly.GetType($"TaleWorlds.MountAndBlade.{stateName}") ??
                                        assembly.GetType($"TaleWorlds.Core.{stateName}");
                        if (stateType != null)
                        {
                            var genericMethod = createStateMethod.MakeGenericMethod(stateType);
                            var newState = genericMethod.Invoke(gameStateManager, null);
                            if (newState != null)
                            {
                                var cleanAndPush = gameStateManager.GetType().GetMethod("CleanAndPushState");
                                cleanAndPush?.Invoke(gameStateManager, new[] { newState });
                                BannerBrosModule.LogMessage($"Exiting to {stateName}...");
                                return;
                            }
                        }
                    }
                }
            }

            BannerBrosModule.LogMessage("Could not auto-exit. Please exit to main menu manually.");
        }
        catch (Exception ex)
        {
            BannerBrosModule.LogMessage($"Exit to menu failed: {ex.Message}");
            // Fallback - just show a message
            BannerBrosModule.LogMessage("Please exit to main menu manually, then click Join Game.");
        }
    }
}

/// <summary>
/// Alternative patch that monitors the campaign map loading.
/// If we're in co-op export mode and the map loads, intercept it.
/// </summary>
[HarmonyPatch(typeof(MapState))]
[HarmonyPatch("OnActivate")]
public static class MapStateActivatePatch
{
    static void Postfix(MapState __instance)
    {
        // Always log to confirm patch is working
        BannerBrosModule.LogMessage($"[DEBUG] MapState.OnActivate fired. IsCreatingForCoopExport={CharacterCreationPatches.IsCreatingForCoopExport}");

        if (!CharacterCreationPatches.IsCreatingForCoopExport)
        {
            return;
        }

        // Character creation is done - we've entered the map
        // Capture and exit
        try
        {
            BannerBrosModule.LogMessage("=== CAPTURING CHARACTER FOR CO-OP ===");

            if (Hero.MainHero != null)
            {
                var captured = ExportedCharacter.CaptureFromMainHero();
                CharacterCreationPatches.CapturedCharacter = captured;
                captured.SaveToFile();

                CharacterCreationPatches.RaiseCharacterCaptured(captured);

                BannerBrosModule.LogMessage($"Character '{captured.Name}' captured!");

                // Store for auto-connect
                var address = CharacterCreationPatches.PendingServerAddress;
                var port = CharacterCreationPatches.PendingServerPort;

                CharacterCreationPatches.ResetCoopExportState();

                if (address != null)
                {
                    CoopConnectionManager.PendingConnection = new PendingCoopConnection
                    {
                        ServerAddress = address,
                        ServerPort = port,
                        CharacterData = captured
                    };

                    BannerBrosModule.LogMessage("Returning to menu to join server...");
                    BannerBrosModule.LogMessage("DO NOT SAVE when prompted!");
                }
            }
        }
        catch (Exception ex)
        {
            BannerBrosModule.LogMessage($"Map capture failed: {ex.Message}");
            CharacterCreationPatches.ResetCoopExportState();
        }
    }
}

/// <summary>
/// Manages pending co-op connections after character creation.
/// </summary>
public static class CoopConnectionManager
{
    /// <summary>
    /// Connection info waiting to be processed after returning to main menu.
    /// </summary>
    public static PendingCoopConnection? PendingConnection { get; set; }

    private static bool _hasLoggedPending = false;

    /// <summary>
    /// Checks if there's a pending connection and processes it.
    /// Call this when the main menu is shown.
    /// </summary>
    public static void CheckAndProcessPendingConnection()
    {
        if (PendingConnection == null)
        {
            _hasLoggedPending = false;
            return;
        }

        // Only log once to avoid spam
        if (!_hasLoggedPending)
        {
            BannerBrosModule.LogMessage($"Pending co-op connection found: {PendingConnection.ServerAddress}:{PendingConnection.ServerPort}");
            BannerBrosModule.LogMessage("Will auto-connect when ready...");
            _hasLoggedPending = true;
        }

        // Make sure we're not already connected and not in a campaign
        var module = BannerBrosModule.Instance;
        if (module == null || module.IsConnected)
        {
            return;
        }

        var pending = PendingConnection;
        PendingConnection = null;
        _hasLoggedPending = false;

        BannerBrosModule.LogMessage($"Auto-connecting to {pending.ServerAddress}:{pending.ServerPort}...");

        // Auto-join the server with the captured character
        if (pending.CharacterData != null)
        {
            // Store the character for the join flow
            module.PendingExportedCharacter = pending.CharacterData;
            BannerBrosModule.LogMessage($"Using character: {pending.CharacterData.Name}");

            // Connect to server
            module.JoinSession(pending.ServerAddress, pending.ServerPort);
        }
        else
        {
            BannerBrosModule.LogMessage("Error: No character data in pending connection");
        }
    }
}

/// <summary>
/// Data for a pending co-op connection.
/// </summary>
public class PendingCoopConnection
{
    public string ServerAddress { get; set; } = "";
    public int ServerPort { get; set; } = 7777;
    public ExportedCharacter? CharacterData { get; set; }
}
