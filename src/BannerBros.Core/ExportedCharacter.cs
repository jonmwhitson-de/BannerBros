using Newtonsoft.Json;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.CharacterDevelopment;
using TaleWorlds.Core;
using TaleWorlds.Library;

namespace BannerBros.Core;

/// <summary>
/// Holds all character data captured from Bannerlord's character creation.
/// This data can be sent to the server to recreate the character in co-op.
/// </summary>
public class ExportedCharacter
{
    public string Name { get; set; } = "";
    public bool IsFemale { get; set; }
    public string CultureId { get; set; } = "";
    public float Age { get; set; } = 25;

    /// <summary>
    /// BodyProperties as XML string - contains full appearance data.
    /// </summary>
    public string BodyPropertiesXml { get; set; } = "";

    /// <summary>
    /// Attributes: Vigor, Control, Endurance, Cunning, Social, Intelligence
    /// </summary>
    public Dictionary<string, int> Attributes { get; set; } = new();

    /// <summary>
    /// All skills and their levels.
    /// </summary>
    public Dictionary<string, int> Skills { get; set; } = new();

    /// <summary>
    /// Focus points per skill.
    /// </summary>
    public Dictionary<string, int> FocusPoints { get; set; } = new();

    /// <summary>
    /// Trait levels (e.g., Mercy, Valor, Honor, etc.)
    /// </summary>
    public Dictionary<string, int> Traits { get; set; } = new();

    /// <summary>
    /// Equipment item IDs for starting gear.
    /// </summary>
    public List<string> EquipmentIds { get; set; } = new();

    /// <summary>
    /// Starting gold amount.
    /// </summary>
    public int Gold { get; set; } = 1000;

    /// <summary>
    /// Timestamp when character was created.
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Unique identifier for this exported character.
    /// </summary>
    public string ExportId { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    /// Captures all data from the current MainHero.
    /// Call this after character creation is complete but before campaign fully starts.
    /// </summary>
    public static ExportedCharacter CaptureFromMainHero()
    {
        var hero = Hero.MainHero;
        if (hero == null)
        {
            throw new InvalidOperationException("MainHero is null - character creation may not be complete");
        }

        var exported = new ExportedCharacter
        {
            Name = hero.Name?.ToString() ?? "Unknown",
            IsFemale = hero.IsFemale,
            CultureId = hero.Culture?.StringId ?? "empire",
            Age = hero.Age,
            CreatedAt = DateTime.UtcNow
        };

        // Capture body properties (appearance)
        try
        {
            exported.BodyPropertiesXml = hero.BodyProperties.ToString();
        }
        catch (Exception ex)
        {
            BannerBrosModule.LogMessage($"Failed to capture BodyProperties: {ex.Message}");
        }

        // Capture attributes
        try
        {
            foreach (CharacterAttributesEnum attr in Enum.GetValues(typeof(CharacterAttributesEnum)))
            {
                if (attr == CharacterAttributesEnum.End) continue;
                exported.Attributes[attr.ToString()] = hero.GetAttributeValue(attr);
            }
        }
        catch (Exception ex)
        {
            BannerBrosModule.LogMessage($"Failed to capture attributes: {ex.Message}");
        }

        // Capture skills and focus points
        try
        {
            foreach (var skill in TaleWorlds.Core.Skills.All)
            {
                exported.Skills[skill.StringId] = hero.GetSkillValue(skill);
                exported.FocusPoints[skill.StringId] = hero.HeroDeveloper?.GetFocus(skill) ?? 0;
            }
        }
        catch (Exception ex)
        {
            BannerBrosModule.LogMessage($"Failed to capture skills: {ex.Message}");
        }

        // Capture traits
        try
        {
            foreach (var trait in DefaultTraits.Personality)
            {
                var level = hero.GetTraitLevel(trait);
                if (level != 0)
                {
                    exported.Traits[trait.StringId] = level;
                }
            }
        }
        catch (Exception ex)
        {
            BannerBrosModule.LogMessage($"Failed to capture traits: {ex.Message}");
        }

        // Capture equipment
        try
        {
            if (hero.BattleEquipment != null)
            {
                for (int i = 0; i < (int)EquipmentIndex.NumEquipmentSetSlots; i++)
                {
                    var item = hero.BattleEquipment[(EquipmentIndex)i];
                    if (!item.IsEmpty && item.Item != null)
                    {
                        exported.EquipmentIds.Add(item.Item.StringId);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            BannerBrosModule.LogMessage($"Failed to capture equipment: {ex.Message}");
        }

        // Capture gold
        try
        {
            exported.Gold = hero.Gold;
        }
        catch
        {
            exported.Gold = 1000;
        }

        BannerBrosModule.LogMessage($"Captured character: {exported.Name}, Culture: {exported.CultureId}");
        return exported;
    }

    /// <summary>
    /// Saves this character to a local file.
    /// </summary>
    public void SaveToFile(string? filename = null)
    {
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "Mount and Blade II Bannerlord",
                "Configs",
                "BannerBros_Characters"
            );

            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var file = filename ?? $"{Name}_{ExportId}.json";
            var path = Path.Combine(directory, file);

            var json = JsonConvert.SerializeObject(this, Formatting.Indented);
            File.WriteAllText(path, json);

            BannerBrosModule.LogMessage($"Character saved to: {path}");
        }
        catch (Exception ex)
        {
            BannerBrosModule.LogMessage($"Failed to save character: {ex.Message}");
        }
    }

    /// <summary>
    /// Loads a character from a file.
    /// </summary>
    public static ExportedCharacter? LoadFromFile(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var json = File.ReadAllText(path);
            return JsonConvert.DeserializeObject<ExportedCharacter>(json);
        }
        catch (Exception ex)
        {
            BannerBrosModule.LogMessage($"Failed to load character: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Gets all saved character files.
    /// </summary>
    public static List<string> GetSavedCharacterFiles()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "Mount and Blade II Bannerlord",
            "Configs",
            "BannerBros_Characters"
        );

        if (!Directory.Exists(directory))
        {
            return new List<string>();
        }

        return Directory.GetFiles(directory, "*.json").ToList();
    }

    /// <summary>
    /// Serializes to JSON for network transmission.
    /// </summary>
    public string ToJson()
    {
        return JsonConvert.SerializeObject(this);
    }

    /// <summary>
    /// Deserializes from JSON.
    /// </summary>
    public static ExportedCharacter? FromJson(string json)
    {
        try
        {
            return JsonConvert.DeserializeObject<ExportedCharacter>(json);
        }
        catch
        {
            return null;
        }
    }
}
