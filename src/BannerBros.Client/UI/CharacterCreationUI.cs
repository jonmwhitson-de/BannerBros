using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.Localization;
using TaleWorlds.MountAndBlade;
using TaleWorlds.ObjectSystem;
using TaleWorlds.CampaignSystem;
using BannerBros.Core;
using BannerBros.Network;

namespace BannerBros.Client.UI;

/// <summary>
/// Simplified character creation UI for joining players.
/// Uses Bannerlord's inquiry system for a basic but functional interface.
/// </summary>
public static class CharacterCreationUI
{
    private static string _characterName = "";
    private static string _selectedCultureId = "";
    private static bool _isFemale;
    private static int _startingAge = 25;

    public static void Show()
    {
        // Reset state
        _characterName = BannerBrosModule.Instance?.Config.PlayerName ?? "Adventurer";
        _selectedCultureId = "";
        _isFemale = false;
        _startingAge = 25;

        // Start the creation flow
        ShowNameInput();
    }

    private static void ShowNameInput()
    {
        InformationManager.ShowTextInquiry(
            new TextInquiryData(
                "Create Your Character",
                "Enter your character's name:",
                true,
                true,
                "Next",
                "Cancel",
                OnNameEntered,
                OnCreationCancelled,
                false,
                text => new Tuple<bool, string>(!string.IsNullOrWhiteSpace(text) && text.Length >= 2 && text.Length <= 30, "Name must be 2-30 characters"),
                "",
                _characterName
            )
        );
    }

    private static void OnNameEntered(string name)
    {
        _characterName = name;
        ShowGenderSelection();
    }

    private static void ShowGenderSelection()
    {
        var inquiry = new InquiryData(
            "Character Gender",
            $"Creating: {_characterName}\n\nSelect your character's gender:",
            true,
            true,
            "Male",
            "Female",
            () => { _isFemale = false; ShowCultureSelection(); },
            () => { _isFemale = true; ShowCultureSelection(); }
        );

        InformationManager.ShowInquiry(inquiry, true);
    }

    private static void ShowCultureSelection()
    {
        // Hardcoded culture list - client doesn't have campaign data loaded
        // The server will validate the culture ID
        var cultureData = new[]
        {
            ("empire", "Empire", "Disciplined legions, crossbows, heavy cavalry"),
            ("sturgia", "Sturgia", "Fierce axemen, skilled sailors, hardy warriors"),
            ("aserai", "Aserai", "Swift cavalry, desert warriors, skilled merchants"),
            ("vlandia", "Vlandia", "Heavy knights, crossbowmen, feudal lords"),
            ("khuzait", "Khuzait", "Master horse archers, nomadic warriors"),
            ("battania", "Battania", "Forest guerrillas, longbowmen, druids")
        };

        var elements = new List<InquiryElement>();
        foreach (var (id, name, desc) in cultureData)
        {
            elements.Add(new InquiryElement(id, name, null, true, desc));
        }

        var inquiry = new MultiSelectionInquiryData(
            "Select Culture",
            $"Creating: {_characterName} ({(_isFemale ? "Female" : "Male")})\n\nChoose your starting culture:",
            elements,
            true,
            1,
            1,
            "Create Character",
            "Back",
            OnCultureSelected,
            _ => ShowGenderSelection()
        );

        MBInformationManager.ShowMultiSelectionInquiry(inquiry, true);
    }

    private static void OnCultureSelected(List<InquiryElement> selected)
    {
        if (selected.Count == 0) return;

        _selectedCultureId = selected[0].Identifier as string ?? "";

        // Show confirmation
        ShowConfirmation();
    }

    private static void ShowConfirmation()
    {
        // Use friendly name for confirmation (client may not have culture objects loaded)
        var cultureName = _selectedCultureId switch
        {
            "empire" => "Empire",
            "sturgia" => "Sturgia",
            "aserai" => "Aserai",
            "vlandia" => "Vlandia",
            "khuzait" => "Khuzait",
            "battania" => "Battania",
            _ => _selectedCultureId
        };

        var message = $"Create this character?\n\n" +
                      $"Name: {_characterName}\n" +
                      $"Gender: {(_isFemale ? "Female" : "Male")}\n" +
                      $"Culture: {cultureName}\n" +
                      $"Age: {_startingAge}";

        var inquiry = new InquiryData(
            "Confirm Character",
            message,
            true,
            true,
            "Create",
            "Back",
            OnConfirmed,
            () => ShowCultureSelection()
        );

        InformationManager.ShowInquiry(inquiry, true);
    }

    private static void OnConfirmed()
    {
        // Build the character creation packet
        var packet = new CharacterCreationPacket
        {
            CharacterName = _characterName,
            CultureId = _selectedCultureId,
            IsFemale = _isFemale,
            StartingAge = _startingAge
        };

        // Submit to session manager
        BannerBrosModule.Instance?.SessionManager.SubmitCharacterCreation(packet);

        BannerBrosModule.LogMessage("Creating character...");
    }

    private static void OnCreationCancelled()
    {
        // Disconnect from session
        BannerBrosModule.Instance?.Disconnect();
        BannerBrosModule.LogMessage("Character creation cancelled, disconnected");
    }
}

/// <summary>
/// Screen state tracker for character creation.
/// </summary>
public class CharacterCreationState
{
    private static CharacterCreationState? _instance;
    public static CharacterCreationState Instance => _instance ??= new CharacterCreationState();

    public bool IsActive { get; private set; }
    public event Action? OnCharacterCreated;

    public void BeginCreation()
    {
        IsActive = true;
        CharacterCreationUI.Show();
    }

    public void EndCreation(bool success)
    {
        IsActive = false;
        if (success)
        {
            OnCharacterCreated?.Invoke();
        }
    }
}
