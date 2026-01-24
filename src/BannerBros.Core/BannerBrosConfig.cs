using System.Text.Json;

namespace BannerBros.Core;

/// <summary>
/// Configuration settings for BannerBros.
/// </summary>
public class BannerBrosConfig
{
    private static readonly string ConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "Mount and Blade II Bannerlord",
        "Configs",
        "BannerBros.json"
    );

    /// <summary>
    /// Maximum number of players (1-4).
    /// </summary>
    public int MaxPlayers { get; set; } = 4;

    /// <summary>
    /// Default server port.
    /// </summary>
    public int DefaultPort { get; set; } = 7777;

    /// <summary>
    /// Campaign time speed multiplier (0.5 - 4.0).
    /// </summary>
    public float TimeSpeedMultiplier { get; set; } = 1.0f;

    /// <summary>
    /// Whether players are protected from attacks during NPC dialogue.
    /// </summary>
    public bool ProtectDuringDialogue { get; set; } = true;

    /// <summary>
    /// Whether players are protected from attacks during shop/trade menus.
    /// </summary>
    public bool ProtectDuringTrade { get; set; } = true;

    /// <summary>
    /// Whether players are protected during character menu (inventory, etc).
    /// </summary>
    public bool ProtectDuringCharacterMenu { get; set; } = false;

    /// <summary>
    /// Last used server address for quick reconnect.
    /// </summary>
    public string LastServerAddress { get; set; } = "";

    /// <summary>
    /// Player display name.
    /// </summary>
    public string PlayerName { get; set; } = "Player";

    public static BannerBrosConfig Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var json = File.ReadAllText(ConfigPath);
                return JsonSerializer.Deserialize<BannerBrosConfig>(json) ?? new BannerBrosConfig();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[BannerBros] Failed to load config: {ex.Message}");
        }

        return new BannerBrosConfig();
    }

    public void Save()
    {
        try
        {
            var directory = Path.GetDirectoryName(ConfigPath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var options = new JsonSerializerOptions { WriteIndented = true };
            var json = JsonSerializer.Serialize(this, options);
            File.WriteAllText(ConfigPath, json);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[BannerBros] Failed to save config: {ex.Message}");
        }
    }
}
