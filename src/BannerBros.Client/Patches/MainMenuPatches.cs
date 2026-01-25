using HarmonyLib;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.Localization;
using TaleWorlds.MountAndBlade;
using TaleWorlds.MountAndBlade.ViewModelCollection;
using TaleWorlds.MountAndBlade.ViewModelCollection.InitialMenu;
using TaleWorlds.MountAndBlade.ViewModelCollection.EscapeMenu;
using BannerBros.Client.UI;
using BannerBros.Core;

namespace BannerBros.Client.Patches;

/// <summary>
/// Patches to add co-op options to the main menu.
/// </summary>
public static class MainMenuPatches
{
    /// <summary>
    /// Patch to add co-op buttons to initial menu.
    /// The initial menu is what appears when you first start the game.
    /// </summary>
    [HarmonyPatch(typeof(InitialMenuVM), "RefreshMenuOptions")]
    public static class InitialMenuRefreshPatch
    {
        public static void Postfix(InitialMenuVM __instance)
        {
            try
            {
                // Access the menu options via reflection since MenuOptions might be private
                var menuOptionsField = typeof(InitialMenuVM).GetProperty("MenuOptions");
                if (menuOptionsField?.GetValue(__instance) is MBBindingList<InitialMenuOptionVM> menuOptions)
                {
                    // Find where to insert (after "Campaign" option)
                    int insertIndex = 0;
                    for (int i = 0; i < menuOptions.Count; i++)
                    {
                        if (menuOptions[i].InitialStateOption?.Id == "Campaign" ||
                            menuOptions[i].InitialStateOption?.Id == "StoryMode")
                        {
                            insertIndex = i + 1;
                            break;
                        }
                    }

                    // Check if our options already exist
                    bool hasCoopOptions = menuOptions.Any(m =>
                        m.InitialStateOption?.Id == "BannerBros_Host" ||
                        m.InitialStateOption?.Id == "BannerBros_Join");

                    if (!hasCoopOptions)
                    {
                        // Add Host Co-op option
                        var hostOption = CreateMenuOption(
                            "BannerBros_Host",
                            "Host Co-op",
                            "Start a co-op campaign session for others to join",
                            () => MainMenuExtension.ShowHostDialog()
                        );

                        // Add Join Co-op option
                        var joinOption = CreateMenuOption(
                            "BannerBros_Join",
                            "Join Co-op",
                            "Join an existing co-op campaign session",
                            () => MainMenuExtension.ShowJoinDialog()
                        );

                        if (hostOption != null)
                            menuOptions.Insert(Math.Min(insertIndex, menuOptions.Count), hostOption);
                        if (joinOption != null)
                            menuOptions.Insert(Math.Min(insertIndex + 1, menuOptions.Count), joinOption);
                    }
                }
            }
            catch (Exception ex)
            {
                BannerBrosModule.LogMessage($"Failed to add menu options: {ex.Message}");
            }
        }

        private static InitialMenuOptionVM? CreateMenuOption(string id, string name, string description, Action action)
        {
            try
            {
                // Wrap the action in error handling
                Action safeAction = () =>
                {
                    try
                    {
                        BannerBrosModule.LogMessage($"Menu option '{name}' clicked");
                        action();
                    }
                    catch (Exception ex)
                    {
                        BannerBrosModule.LogMessage($"Error in menu action '{name}': {ex.Message}");
                    }
                };

                // Create the initial state option
                var stateOption = new InitialStateOption(
                    id,
                    new TextObject(name),
                    9000, // Sort order
                    safeAction,
                    () => (false, new TextObject("")) // Not disabled - value tuple for InitialStateOption
                );

                return new InitialMenuOptionVM(stateOption);
            }
            catch (Exception ex)
            {
                BannerBrosModule.LogMessage($"Error creating menu option '{name}': {ex.Message}");
                return null;
            }
        }
    }
}

/// <summary>
/// Patches to add co-op status to the escape menu during gameplay.
/// DISABLED: Causes crashes in some Bannerlord versions.
/// </summary>
public static class EscapeMenuPatches
{
    /// <summary>
    /// Adds co-op options to the in-game escape menu.
    /// </summary>
    [HarmonyPatch(typeof(EscapeMenuVM), MethodType.Constructor)]
    public static class EscapeMenuConstructorPatch
    {
        public static void Postfix(EscapeMenuVM __instance)
        {
            var module = BannerBrosModule.Instance;
            if (module?.IsConnected != true) return;

            // Add co-op menu items
            try
            {
                var items = __instance.MenuItems;
                if (items != null)
                {
                    // Find a good insertion point (before "Save" or at the start)
                    int insertIndex = 0;
                    for (int i = 0; i < items.Count; i++)
                    {
                        if (items[i].ActionText?.ToString()?.Contains("Save") == true)
                        {
                            insertIndex = i;
                            break;
                        }
                    }

                    // Add player list option
                    var playerListItem = new EscapeMenuItemVM(
                        new TextObject("Co-op Players"),
                        _ => CoopSessionMenu.ShowPlayerList(),
                        null,
                        () => new Tuple<bool, TextObject>(false, new TextObject(""))
                    );

                    // Add settings option (host only)
                    if (module.IsHost)
                    {
                        var settingsItem = new EscapeMenuItemVM(
                            new TextObject("Co-op Settings"),
                            _ => CoopSessionMenu.ShowSettings(),
                            null,
                            () => new Tuple<bool, TextObject>(false, new TextObject(""))
                        );
                        items.Insert(insertIndex, settingsItem);
                        insertIndex++;
                    }

                    items.Insert(insertIndex, playerListItem);

                    // Add disconnect option
                    var disconnectItem = new EscapeMenuItemVM(
                        new TextObject("Leave Co-op"),
                        _ => CoopSessionMenu.ShowDisconnectConfirm(),
                        null,
                        () => new Tuple<bool, TextObject>(false, new TextObject(""))
                    );
                    items.Add(disconnectItem);
                }
            }
            catch (Exception ex)
            {
                BannerBrosModule.LogMessage($"Failed to add escape menu options: {ex.Message}");
            }
        }
    }
}
