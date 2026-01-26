using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.Localization;
using TaleWorlds.MountAndBlade;
using TaleWorlds.ScreenSystem;
using BannerBros.Core;

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
        if (speedStr == "2")
        {
            module.Config.TimeSpeedMultiplier = 2.0f;
        }
        else
        {
            module.Config.TimeSpeedMultiplier = 1.0f;
        }

        // Now show the confirmation dialog
        var speedText = module.Config.TimeSpeedMultiplier >= 2.0f ? "Fast (2x)" : "Normal (1x)";
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

    public static void ShowJoinDialog()
    {
        // For now, use a text inquiry for the IP address
        // In a full implementation, this would be a proper UI screen
        var inquiry = new InquiryData(
            "Join Co-op Session",
            "Enter the host's IP address to join their game.\n\nExample: 192.168.1.100",
            true,
            true,
            "Join",
            "Cancel",
            () => ShowAddressInput(),
            null
        );

        InformationManager.ShowInquiry(inquiry, true);
    }

    private static void ShowAddressInput()
    {
        // In Bannerlord, we'd use a text input inquiry
        // For now, simulate with a default address
        InformationManager.ShowTextInquiry(
            new TextInquiryData(
                "Enter Host Address",
                "IP Address:",
                true,
                true,
                "Connect",
                "Cancel",
                OnJoinConfirmed,
                null,
                false,
                text => new Tuple<bool, string>(!string.IsNullOrWhiteSpace(text), "Address cannot be empty"),
                "",
                BannerBrosModule.Instance?.Config.LastServerAddress ?? ""
            )
        );
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

    private static void OnJoinConfirmed(string address)
    {
        var module = BannerBrosModule.Instance;
        if (module == null) return;

        // Parse address (could include port)
        var parts = address.Split(':');
        var host = parts[0];
        var port = parts.Length > 1 && int.TryParse(parts[1], out var p) ? p : module.Config.DefaultPort;

        // Save for next time
        module.Config.LastServerAddress = address;

        // Connect
        module.JoinSession(host, port);

        BannerBrosModule.LogMessage($"Connecting to {host}:{port}...");
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
}
