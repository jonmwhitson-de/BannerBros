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
                text => !string.IsNullOrWhiteSpace(text) && text.Length >= 2 && text.Length <= 30,
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
        // Get available cultures
        var cultures = GetAvailableCultures();

        if (cultures.Count == 0)
        {
            BannerBrosModule.LogMessage("No cultures available!");
            return;
        }

        var elements = new List<InquiryElement>();
        foreach (var culture in cultures.Take(6)) // Limit to 6 for UI
        {
            elements.Add(new InquiryElement(
                culture.StringId,
                culture.Name.ToString(),
                null,
                true,
                GetCultureDescription(culture)
            ));
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

    private static List<CultureObject> GetAvailableCultures()
    {
        var cultures = new List<CultureObject>();

        if (Campaign.Current != null)
        {
            cultures = Campaign.Current.ObjectManager
                .GetObjectTypeList<CultureObject>()
                .Where(c => c.IsMainCulture && !c.IsBandit)
                .ToList();
        }

        // Fallback to default cultures if campaign not available
        if (cultures.Count == 0)
        {
            var defaultCultures = new[] { "empire", "sturgia", "aserai", "vlandia", "khuzait", "battania" };
            foreach (var cultureId in defaultCultures)
            {
                var culture = MBObjectManager.Instance?.GetObject<CultureObject>(cultureId);
                if (culture != null)
                {
                    cultures.Add(culture);
                }
            }
        }

        return cultures;
    }

    private static string GetCultureDescription(CultureObject culture)
    {
        // Provide brief descriptions for each culture
        return culture.StringId switch
        {
            "empire" => "Disciplined legions, crossbows, heavy cavalry",
            "sturgia" => "Fierce axemen, skilled sailors, hardy warriors",
            "aserai" => "Swift cavalry, desert warriors, skilled merchants",
            "vlandia" => "Heavy knights, crossbowmen, feudal lords",
            "khuzait" => "Master horse archers, nomadic warriors",
            "battania" => "Forest guerrillas, longbowmen, druids",
            _ => culture.Name.ToString()
        };
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
        var culture = MBObjectManager.Instance?.GetObject<CultureObject>(_selectedCultureId);
        var cultureName = culture?.Name.ToString() ?? _selectedCultureId;

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
