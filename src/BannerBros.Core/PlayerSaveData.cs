using Newtonsoft.Json;
using TaleWorlds.CampaignSystem;

namespace BannerBros.Core;

/// <summary>
/// Stores player-to-hero mappings for save/load functionality.
/// Saved alongside the campaign save so players can reconnect to their characters.
/// </summary>
public class PlayerSaveData
{
    /// <summary>
    /// Maps player names to their saved character data.
    /// </summary>
    public Dictionary<string, SavedPlayerCharacter> PlayerCharacters { get; set; } = new();

    /// <summary>
    /// Gets the save file path for the current campaign.
    /// </summary>
    private static string GetSavePath()
    {
        var saveName = Campaign.Current?.UniqueGameId ?? "unknown";
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "Mount and Blade II Bannerlord",
            "Configs",
            $"BannerBros_Players_{saveName}.json"
        );
    }

    /// <summary>
    /// Saves the player data to disk.
    /// </summary>
    public void Save()
    {
        try
        {
            var path = GetSavePath();
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonConvert.SerializeObject(this, Formatting.Indented);
            File.WriteAllText(path, json);
            BannerBrosModule.LogMessage($"Player data saved ({PlayerCharacters.Count} players)");
        }
        catch (Exception ex)
        {
            BannerBrosModule.LogMessage($"Failed to save player data: {ex.Message}");
        }
    }

    /// <summary>
    /// Loads player data from disk for the current campaign.
    /// </summary>
    public static PlayerSaveData Load()
    {
        try
        {
            var path = GetSavePath();
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                var data = JsonConvert.DeserializeObject<PlayerSaveData>(json);
                if (data != null)
                {
                    BannerBrosModule.LogMessage($"Player data loaded ({data.PlayerCharacters.Count} players)");
                    return data;
                }
            }
        }
        catch (Exception ex)
        {
            BannerBrosModule.LogMessage($"Failed to load player data: {ex.Message}");
        }

        return new PlayerSaveData();
    }

    /// <summary>
    /// Registers a player's character for future reconnection.
    /// </summary>
    public void RegisterPlayer(string playerName, string heroId, string clanId, string partyId)
    {
        PlayerCharacters[playerName.ToLowerInvariant()] = new SavedPlayerCharacter
        {
            PlayerName = playerName,
            HeroId = heroId,
            ClanId = clanId,
            PartyId = partyId,
            LastSeen = DateTime.UtcNow
        };
    }

    /// <summary>
    /// Finds a saved character for a player name.
    /// </summary>
    public SavedPlayerCharacter? FindCharacter(string playerName)
    {
        return PlayerCharacters.TryGetValue(playerName.ToLowerInvariant(), out var character)
            ? character
            : null;
    }

    /// <summary>
    /// Checks if a hero is still valid in the current campaign.
    /// </summary>
    public static bool IsHeroValid(string heroId)
    {
        if (string.IsNullOrEmpty(heroId) || Campaign.Current == null)
            return false;

        var hero = Campaign.Current.CampaignObjectManager.Find<Hero>(heroId);
        return hero != null && hero.IsAlive;
    }
}

/// <summary>
/// Data for a saved player character.
/// </summary>
public class SavedPlayerCharacter
{
    public string PlayerName { get; set; } = "";
    public string HeroId { get; set; } = "";
    public string ClanId { get; set; } = "";
    public string PartyId { get; set; } = "";
    public DateTime LastSeen { get; set; }
}
