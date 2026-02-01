using System.IO;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.Localization;
using TaleWorlds.MountAndBlade;
using TaleWorlds.ScreenSystem;
using BannerBros.Core;
using BannerBros.Core.Patches;

namespace BannerBros.Client.UI;

/// <summary>
/// Extends the main menu with co-op options.
/// </summary>
public static class MainMenuExtension
{
    private static bool _initialized;

    public static void Initialize()
    {
        if (_initialized) return;
        _initialized = true;

        // Hook into initial screen setup
        // The actual menu injection happens in the module
    }

    public static void InjectMenuOptions()
    {
        // This would be called from a Harmony patch on the main menu
        // to add our custom options
    }

    public static void ShowHostDialog()
    {
        try
        {
            // First, show speed selection
            ShowHostSpeedSelection();
        }
        catch (Exception ex)
        {
            BannerBrosModule.LogMessage($"Error showing host dialog: {ex.Message}");
        }
    }

    private static void ShowHostSpeedSelection()
    {
        var module = BannerBrosModule.Instance;
        var currentSpeed = module?.Config.TimeSpeedMultiplier ?? 1.0f;

        var inquiry = new MultiSelectionInquiryData(
            "Host Co-op Session",
            "Select game speed for your co-op session:\n\n(This controls how fast time passes on the campaign map)",
            new List<InquiryElement>
            {
                new InquiryElement("0.5", "Slow (0.5x)", null, true, "Half speed - more time to react"),
                new InquiryElement("1", "Normal (1x)", null, true, "Standard campaign speed"),
                new InquiryElement("2", "Fast (2x)", null, true, "Double speed campaign"),
            },
            true,
            1,
            1,
            "Continue",
            "Cancel",
            OnHostSpeedSelected,
            null
        );

        MBInformationManager.ShowMultiSelectionInquiry(inquiry, true);
    }

    private static void OnHostSpeedSelected(List<InquiryElement> selected)
    {
        if (selected.Count == 0) return;

        var module = BannerBrosModule.Instance;
        if (module == null) return;

        var speedStr = selected[0].Identifier as string;
        if (float.TryParse(speedStr, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var speed))
        {
            module.Config.TimeSpeedMultiplier = speed;
        }
        else
        {
            module.Config.TimeSpeedMultiplier = 1.0f;
        }

        // Now show the confirmation dialog
        var speedText = module.Config.TimeSpeedMultiplier <= 0.5f ? "Slow (0.5x)" :
                        module.Config.TimeSpeedMultiplier >= 2.0f ? "Fast (2x)" : "Normal (1x)";
        var inquiry = new InquiryData(
            "Host Co-op Session",
            $"Start a new co-op campaign.\n\nGame Speed: {speedText}\nPort: {module.Config.DefaultPort}\n\nOther players will be able to join your game.",
            true,
            true,
            "Start Hosting",
            "Cancel",
            OnHostNewCampaign,
            null
        );

        InformationManager.ShowInquiry(inquiry, true);
    }

    // Store pending connection info during the join flow
    private static string _pendingServerAddress = "";
    private static int _pendingServerPort = 7777;

    public static void ShowJoinDialog()
    {
        // Direct join from main menu - enter IP, download save, then load it
        InformationManager.ShowTextInquiry(
            new TextInquiryData(
                "Join Co-op Session",
                "Enter the host's IP address:\n\nExample: 192.168.1.100 or 192.168.1.100:7777",
                true,
                true,
                "Connect",
                "Cancel",
                OnMainMenuJoinAddressEntered,
                null,
                false,
                text => new Tuple<bool, string>(!string.IsNullOrWhiteSpace(text), "Address cannot be empty"),
                "",
                BannerBrosModule.Instance?.Config.LastServerAddress ?? ""
            )
        );
    }

    private static void OnMainMenuJoinAddressEntered(string address)
    {
        // Parse address
        var parts = address.Split(':');
        _pendingServerAddress = parts[0];
        _pendingServerPort = parts.Length > 1 && int.TryParse(parts[1], out var p)
            ? p
            : BannerBrosModule.Instance?.Config.DefaultPort ?? 7777;

        // Save for later reconnection
        var module = BannerBrosModule.Instance;
        if (module != null)
        {
            module.Config.LastServerAddress = address;
            module.Config.Save();
        }

        BannerBrosModule.LogMessage($"Connecting to {_pendingServerAddress}:{_pendingServerPort} from main menu...");

        // Connect to download save file
        ConnectFromMainMenuForSaveDownload();
    }

    private static void ConnectFromMainMenuForSaveDownload()
    {
        var module = BannerBrosModule.Instance;
        if (module == null) return;

        // Initialize network for save download (no campaign yet)
        module.ConnectForSaveDownload(_pendingServerAddress, _pendingServerPort);

        // Show connecting dialog
        InformationManager.ShowInquiry(
            new InquiryData(
                "Connecting...",
                $"Connecting to {_pendingServerAddress}:{_pendingServerPort}...\n\n" +
                "The host's save file will be downloaded.\n" +
                "You'll then load it to join the game.",
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
    /// Shows the in-game join dialog when player is already in a campaign.
    /// This is the actual join flow for state sync architecture.
    /// </summary>
    public static void ShowInGameJoinDialog()
    {
        InformationManager.ShowTextInquiry(
            new TextInquiryData(
                "Join Co-op Session",
                "Enter the host's IP address:\n\nExample: 192.168.1.100 or 192.168.1.100:7777",
                true,
                true,
                "Connect",
                "Cancel",
                OnInGameJoinAddressEntered,
                null,
                false,
                text => new Tuple<bool, string>(!string.IsNullOrWhiteSpace(text), "Address cannot be empty"),
                "",
                BannerBrosModule.Instance?.Config.LastServerAddress ?? ""
            )
        );
    }

    private static void OnInGameJoinAddressEntered(string address)
    {
        // Parse address
        var parts = address.Split(':');
        _pendingServerAddress = parts[0];
        _pendingServerPort = parts.Length > 1 && int.TryParse(parts[1], out var p)
            ? p
            : BannerBrosModule.Instance?.Config.DefaultPort ?? 7777;

        // Save for next time
        if (BannerBrosModule.Instance != null)
        {
            BannerBrosModule.Instance.Config.LastServerAddress = address;
        }

        // Connect directly - we're already in a campaign with our character
        ConnectFromCampaign();
    }

    private static void ConnectFromCampaign()
    {
        var module = BannerBrosModule.Instance;
        if (module == null) return;

        BannerBrosModule.LogMessage($"Connecting to {_pendingServerAddress}:{_pendingServerPort}...");

        // Join the session - state will sync via network
        module.JoinSession(_pendingServerAddress, _pendingServerPort);

        // Show connecting dialog
        InformationManager.ShowInquiry(
            new InquiryData(
                "Connecting...",
                $"Connecting to {_pendingServerAddress}:{_pendingServerPort}...\n\n" +
                "Your party will sync with the host's world.\n" +
                "You'll see other players on the campaign map!",
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

    private static void OnJoinAddressEntered(string address)
    {
        // Parse address
        var parts = address.Split(':');
        _pendingServerAddress = parts[0];
        _pendingServerPort = parts.Length > 1 && int.TryParse(parts[1], out var p)
            ? p
            : BannerBrosModule.Instance?.Config.DefaultPort ?? 7777;

        // Save for next time
        if (BannerBrosModule.Instance != null)
        {
            BannerBrosModule.Instance.Config.LastServerAddress = address;
        }

        // Check for saved characters
        var savedFiles = ExportedCharacter.GetSavedCharacterFiles();
        if (savedFiles.Count > 0)
        {
            ShowCharacterListForJoin(savedFiles);
        }
        else
        {
            // No saved characters - show option to create one
            ShowNoCharacterDialog();
        }
    }

    private static void ShowCharacterListForJoin(List<string> savedFiles)
    {
        var elements = new List<InquiryElement>();
        foreach (var file in savedFiles)
        {
            var character = ExportedCharacter.LoadFromFile(file);
            if (character != null)
            {
                var desc = $"{character.CultureId} - Created {character.CreatedAt:MMM dd, yyyy}";
                elements.Add(new InquiryElement(file, character.Name, null, true, desc));
            }
        }

        if (elements.Count == 0)
        {
            ShowNoCharacterDialog();
            return;
        }

        var inquiry = new MultiSelectionInquiryData(
            "Select Character",
            $"Server: {_pendingServerAddress}:{_pendingServerPort}\n\nSelect your character:",
            elements,
            true,
            1,
            1,
            "Join",
            "Create New",
            OnJoinCharacterSelected,
            _ => OnNewCharacterSelected()
        );

        MBInformationManager.ShowMultiSelectionInquiry(inquiry, true);
    }

    private static void ShowNoCharacterDialog()
    {
        var inquiry = new InquiryData(
            "No Saved Characters",
            $"Server: {_pendingServerAddress}:{_pendingServerPort}\n\n" +
            "You have no saved characters.\n\n" +
            "Create a character first, then join.",
            true,
            true,
            "Create Character",
            "Cancel",
            OnNewCharacterSelected,
            null
        );
        InformationManager.ShowInquiry(inquiry, true);
    }

    private static void OnJoinCharacterSelected(List<InquiryElement> selected)
    {
        if (selected.Count == 0) return;

        var filePath = selected[0].Identifier as string;
        if (string.IsNullOrEmpty(filePath)) return;

        var character = ExportedCharacter.LoadFromFile(filePath);
        if (character == null)
        {
            BannerBrosModule.LogMessage("Failed to load character file");
            return;
        }

        ConnectWithCharacter(character);
    }

    private static void ConnectWithCharacter(ExportedCharacter character)
    {
        BannerBrosModule.LogMessage($"Joining as {character.Name}...");

        var module = BannerBrosModule.Instance;
        if (module != null)
        {
            module.PendingExportedCharacter = character;
            module.JoinSession(_pendingServerAddress, _pendingServerPort);
        }

        // Show connecting dialog
        InformationManager.ShowInquiry(
            new InquiryData(
                "Connecting...",
                $"Connecting as {character.Name}.\n\n" +
                "Your party will sync with the host's world.\n" +
                "You'll see other players on the campaign map!",
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
    /// Legacy join flow with character creation - kept for "Create New" option.
    /// </summary>
    public static void ShowJoinWithCharacterDialog()
    {
        // First, get the server address
        InformationManager.ShowTextInquiry(
            new TextInquiryData(
                "Join Co-op Session",
                "Enter the host's IP address:\n\nExample: 192.168.1.100 or 192.168.1.100:7777",
                true,
                true,
                "Next",
                "Cancel",
                OnServerAddressEntered,
                null,
                false,
                text => new Tuple<bool, string>(!string.IsNullOrWhiteSpace(text), "Address cannot be empty"),
                "",
                BannerBrosModule.Instance?.Config.LastServerAddress ?? ""
            )
        );
    }

    private static void OnServerAddressEntered(string address)
    {
        // Parse address
        var parts = address.Split(':');
        _pendingServerAddress = parts[0];
        _pendingServerPort = parts.Length > 1 && int.TryParse(parts[1], out var p)
            ? p
            : BannerBrosModule.Instance?.Config.DefaultPort ?? 7777;

        // Save for next time
        if (BannerBrosModule.Instance != null)
        {
            BannerBrosModule.Instance.Config.LastServerAddress = address;
        }

        // Now show the character selection dialog
        ShowCharacterSelectionDialog();
    }

    private static void ShowCharacterSelectionDialog()
    {
        // Check if there are any saved characters
        var savedFiles = ExportedCharacter.GetSavedCharacterFiles();
        var hasSavedCharacters = savedFiles.Count > 0;

        var message = hasSavedCharacters
            ? $"Server: {_pendingServerAddress}:{_pendingServerPort}\n\n" +
              "Choose how to join:\n\n" +
              "• New Character - Go through full character creation\n" +
              "• Load Character - Use a previously created character"
            : $"Server: {_pendingServerAddress}:{_pendingServerPort}\n\n" +
              "You'll need to create a new character to join.\n\n" +
              "This will start Bannerlord's character creation.\n" +
              "After creation, you'll automatically join the server.";

        if (hasSavedCharacters)
        {
            var inquiry = new InquiryData(
                "Join Co-op Session",
                message,
                true,
                true,
                "New Character",
                "Load Character",
                OnNewCharacterSelected,
                OnLoadCharacterSelected
            );
            InformationManager.ShowInquiry(inquiry, true);
        }
        else
        {
            var inquiry = new InquiryData(
                "Join Co-op Session",
                message,
                true,
                true,
                "Create Character",
                "Cancel",
                OnNewCharacterSelected,
                null
            );
            InformationManager.ShowInquiry(inquiry, true);
        }
    }

    private static void OnNewCharacterSelected()
    {
        BannerBrosModule.LogMessage("Starting character creation for co-op...");
        BannerBrosModule.LogMessage("Complete character creation, then you'll join automatically.");

        // Set up the co-op export mode
        CharacterCreationPatches.StartCoopCharacterCreation(_pendingServerAddress, _pendingServerPort);

        // Show instructions
        InformationManager.ShowInquiry(
            new InquiryData(
                "Create Your Character",
                "You'll now go through Bannerlord's character creation.\n\n" +
                "1. Click 'Campaign' on the main menu\n" +
                "2. Start a 'New Campaign' (Sandbox or Story)\n" +
                "3. Complete character creation normally\n" +
                "4. When the map loads, your character will be captured\n" +
                "5. You'll be prompted to join the co-op server\n\n" +
                "DO NOT SAVE the temporary campaign!",
                true,
                false,
                "Got it!",
                "",
                null,
                null
            ),
            true
        );
    }

    private static void OnLoadCharacterSelected()
    {
        var savedFiles = ExportedCharacter.GetSavedCharacterFiles();

        if (savedFiles.Count == 0)
        {
            BannerBrosModule.LogMessage("No saved characters found");
            ShowCharacterSelectionDialog();
            return;
        }

        // Build a list of saved characters
        var elements = new List<InquiryElement>();
        foreach (var file in savedFiles)
        {
            var character = ExportedCharacter.LoadFromFile(file);
            if (character != null)
            {
                var fileName = Path.GetFileNameWithoutExtension(file);
                var desc = $"{character.CultureId} - Created {character.CreatedAt:MMM dd, yyyy}";
                elements.Add(new InquiryElement(file, character.Name, null, true, desc));
            }
        }

        if (elements.Count == 0)
        {
            BannerBrosModule.LogMessage("No valid saved characters found");
            OnNewCharacterSelected();
            return;
        }

        var inquiry = new MultiSelectionInquiryData(
            "Select Character",
            "Choose a character to join with:",
            elements,
            true,
            1,
            1,
            "Join",
            "Back",
            OnCharacterFileSelected,
            _ => ShowCharacterSelectionDialog()
        );

        MBInformationManager.ShowMultiSelectionInquiry(inquiry, true);
    }

    private static void OnCharacterFileSelected(List<InquiryElement> selected)
    {
        if (selected.Count == 0) return;

        var filePath = selected[0].Identifier as string;
        if (string.IsNullOrEmpty(filePath)) return;

        var character = ExportedCharacter.LoadFromFile(filePath);
        if (character == null)
        {
            BannerBrosModule.LogMessage("Failed to load character file");
            return;
        }

        BannerBrosModule.LogMessage($"Joining as {character.Name}...");

        // Store the character and connect
        var module = BannerBrosModule.Instance;
        if (module != null)
        {
            module.PendingExportedCharacter = character;
            module.JoinSession(_pendingServerAddress, _pendingServerPort);
        }
    }

    // Legacy method - kept for compatibility
    private static void OnJoinConfirmed(string address)
    {
        OnServerAddressEntered(address);
    }

    private static void OnHostNewCampaign()
    {
        try
        {
            var module = BannerBrosModule.Instance;
            if (module == null)
            {
                BannerBrosModule.LogMessage("Error: BannerBrosModule.Instance is null");
                return;
            }

            BannerBrosModule.LogMessage("Starting host session...");

            // Start hosting
            module.HostSession(module.Config.DefaultPort);

            // Proceed to campaign creation
            BannerBrosModule.LogMessage("Hosting on port " + module.Config.DefaultPort);

            // The game's normal campaign start flow will continue
        }
        catch (Exception ex)
        {
            BannerBrosModule.LogMessage($"Error starting host: {ex.Message}");
        }
    }

}

/// <summary>
/// In-game co-op menu for managing session while playing.
/// </summary>
public static class CoopSessionMenu
{
    public static void ShowPlayerList()
    {
        var module = BannerBrosModule.Instance;
        if (module?.IsConnected != true)
        {
            BannerBrosModule.LogMessage("Not connected to a co-op session");
            return;
        }

        var players = module.PlayerManager.Players.Values.ToList();
        var lines = new List<string>
        {
            $"Connected Players ({players.Count}/{module.Config.MaxPlayers}):",
            ""
        };

        foreach (var player in players)
        {
            var status = player.State switch
            {
                PlayerState.InBattle => " (In Battle)",
                PlayerState.InDialogue => " (In Dialogue)",
                PlayerState.InTrade => " (Trading)",
                PlayerState.InTown => " (In Town)",
                _ => ""
            };

            var hostTag = player.IsHost ? " [HOST]" : "";
            lines.Add($"  {player.Name}{hostTag}{status} - {player.Ping}ms");
        }

        var message = string.Join("\n", lines);

        InformationManager.ShowInquiry(
            new InquiryData("Co-op Players", message, true, false, "Close", "", null, null),
            true
        );
    }

    public static void ShowSettings()
    {
        var module = BannerBrosModule.Instance;
        if (module?.IsHost != true)
        {
            BannerBrosModule.LogMessage("Only the host can change settings");
            return;
        }

        // Show time speed options
        var inquiry = new MultiSelectionInquiryData(
            "Co-op Settings",
            "Select game speed:",
            new List<InquiryElement>
            {
                new("0.5", "0.5x (Slow)", null),
                new("1.0", "1.0x (Normal)", null),
                new("2.0", "2.0x (Fast)", null),
                new("4.0", "4.0x (Very Fast)", null),
            },
            true,
            1,
            1,
            "Apply",
            "Cancel",
            OnSpeedSelected,
            null
        );

        MBInformationManager.ShowMultiSelectionInquiry(inquiry, true);
    }

    private static void OnSpeedSelected(List<InquiryElement> selected)
    {
        if (selected.Count == 0) return;

        var speedStr = selected[0].Identifier as string;
        if (float.TryParse(speedStr, out var speed))
        {
            var module = BannerBrosModule.Instance;
            if (module != null)
            {
                module.Config.TimeSpeedMultiplier = speed;
                BannerBrosModule.LogMessage($"Game speed set to {speed}x");
            }
        }
    }

    public static void ShowDisconnectConfirm()
    {
        var inquiry = new InquiryData(
            "Leave Session",
            "Are you sure you want to leave the co-op session?",
            true,
            true,
            "Leave",
            "Stay",
            () => BannerBrosModule.Instance?.Disconnect(),
            null
        );

        InformationManager.ShowInquiry(inquiry, true);
    }

    /// <summary>
    /// Shows the world sync option for clients to get host's save file.
    /// This ensures all NPCs, settlements, and world state match.
    /// </summary>
    public static void ShowWorldSyncDialog()
    {
        var module = BannerBrosModule.Instance;
        if (module?.IsConnected != true)
        {
            BannerBrosModule.LogMessage("Not connected to a co-op session");
            return;
        }

        if (module.IsHost)
        {
            BannerBrosModule.LogMessage("Host already has authoritative world state");
            return;
        }

        var inquiry = new InquiryData(
            "Sync World State",
            "This will download the host's save file to sync all NPCs, settlements, and world state.\n\n" +
            "After download, you will need to:\n" +
            "1. Exit to main menu\n" +
            "2. Load the 'coop_sync_*' save file\n" +
            "3. Reconnect to the host\n\n" +
            "Your visibility with other players will be preserved!",
            true,
            true,
            "Start Sync",
            "Cancel",
            OnWorldSyncConfirmed,
            null
        );

        InformationManager.ShowInquiry(inquiry, true);
    }

    private static void OnWorldSyncConfirmed()
    {
        var module = BannerBrosModule.Instance;
        if (module?.SessionManager == null) return;

        BannerBrosModule.LogMessage("Requesting world sync from host...");
        module.SessionManager.RequestSaveFileFromHost();

        InformationManager.ShowInquiry(
            new InquiryData(
                "Downloading...",
                "Downloading save file from host.\n\n" +
                "This may take a moment depending on the save size.\n" +
                "You'll see a notification when complete.",
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
}
