using TaleWorlds.Core;
using TaleWorlds.Library;

namespace BannerBros.Client;

/// <summary>
/// Manages co-op related menus and UI screens.
/// </summary>
public class CoopMenuManager
{
    public void InjectMainMenuOptions()
    {
        // Add "Host Co-op" and "Join Co-op" buttons to main menu
        // This requires integration with Bannerlord's menu system
    }

    public void ShowHostLobby()
    {
        // Show lobby screen for host
        // - Player list
        // - Start game button
        // - Settings (time multiplier, etc.)
        InformationManager.DisplayMessage(new InformationMessage("[BannerBros] Host lobby opened"));
    }

    public void ShowJoinDialog()
    {
        // Show dialog for entering server address
        InformationManager.DisplayMessage(new InformationMessage("[BannerBros] Join dialog opened"));
    }

    public void ShowServerBrowser()
    {
        // For future: LAN server discovery
    }

    public void ShowPlayerList()
    {
        // Show current connected players
        // Accessible during gameplay
    }

    public void ShowTradeWindow(int targetPlayerId)
    {
        // Show trade window with another player
    }

    public void ShowBattleJoinPrompt(string battleId, string initiatorName)
    {
        // Prompt to join a nearby battle
        // Options: Join as Ally, Join as Enemy, Ignore
    }
}
