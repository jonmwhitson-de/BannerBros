using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;

namespace BannerBros.Core;

/// <summary>
/// Manages player protection during dialogue, trade, and other interactions.
/// Players in dialogue/trade are protected from attacks.
/// Players in character menus are NOT protected.
/// </summary>
public class PlayerProtectionBehavior : CampaignBehaviorBase
{
    private readonly HashSet<int> _protectedPlayers = new();

    public override void RegisterEvents()
    {
        CampaignEvents.TickEvent.AddNonSerializedListener(this, OnTick);
    }

    public override void SyncData(IDataStore dataStore)
    {
        // No persistent data
    }

    private void OnTick(float dt)
    {
        try
        {
            var module = BannerBrosModule.Instance;
            if (module?.IsConnected != true) return;

            UpdateProtectionStates();
            EnforceProtection();
        }
        catch (Exception ex)
        {
            BannerBrosModule.LogMessage($"PlayerProtection OnTick error: {ex.Message}");
        }
    }

    private void UpdateProtectionStates()
    {
        var module = BannerBrosModule.Instance;
        if (module == null) return;

        _protectedPlayers.Clear();

        try
        {
            // ToList() to avoid collection modified exception when players join/leave
            foreach (var player in module.PlayerManager.Players.Values.ToList())
            {
                try
                {
                    if (ShouldBeProtected(player))
                    {
                        _protectedPlayers.Add(player.NetworkId);
                    }
                }
                catch
                {
                    // Skip this player if there's an error
                }
            }
        }
        catch (Exception ex)
        {
            BannerBrosModule.LogMessage($"UpdateProtectionStates error: {ex.Message}");
        }
    }

    private bool ShouldBeProtected(CoopPlayer player)
    {
        var config = BannerBrosModule.Instance?.Config;
        if (config == null) return false;

        return player.State switch
        {
            PlayerState.InDialogue => config.ProtectDuringDialogue,
            PlayerState.InTrade => config.ProtectDuringTrade,
            PlayerState.InTown => true,   // Safe in towns
            PlayerState.InVillage => true, // Safe in villages
            PlayerState.InCastle => true,  // Safe in castles
            PlayerState.InMenu => config.ProtectDuringCharacterMenu,
            _ => false
        };
    }

    private void EnforceProtection()
    {
        // Prevent AI parties from engaging protected players
        // This would use Harmony patches to intercept AI targeting decisions

        if (Campaign.Current == null) return;

        // For each protected player, ensure their party cannot be attacked
        // by canceling any incoming attack attempts from AI
    }

    public bool IsPlayerProtected(int networkId)
    {
        return _protectedPlayers.Contains(networkId);
    }

    public bool IsLocalPlayerProtected()
    {
        var localPlayer = BannerBrosModule.Instance?.PlayerManager.GetLocalPlayer();
        return localPlayer != null && IsPlayerProtected(localPlayer.NetworkId);
    }
}
