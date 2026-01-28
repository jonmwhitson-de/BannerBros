using System;
using System.Linq;
using System.Reflection;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;

namespace BannerBros.Core;

/// <summary>
/// Handles programmatic loading of save games for the co-op join flow.
/// Uses reflection to support multiple Bannerlord versions.
/// </summary>
public static class SaveGameLoader
{
    /// <summary>
    /// Attempts to load a save file programmatically.
    /// Returns true if load was initiated successfully.
    /// </summary>
    public static bool LoadSaveFile(string savePath)
    {
        try
        {
            BannerBrosModule.LogMessage($"[SaveLoader] Attempting to load: {savePath}");

            // Extract save name from path (remove directory and extension)
            var saveName = System.IO.Path.GetFileNameWithoutExtension(savePath);
            BannerBrosModule.LogMessage($"[SaveLoader] Save name: {saveName}");

            // Method 1: Try SandBoxSaveHelper (newer versions)
            if (TryLoadViaSandBoxSaveHelper(saveName))
            {
                return true;
            }

            // Method 2: Try MBSaveLoad (older versions)
            if (TryLoadViaMBSaveLoad(saveName))
            {
                return true;
            }

            // Method 3: Try Game.Current.GameStateManager approach
            if (TryLoadViaGameStateManager(saveName))
            {
                return true;
            }

            // Method 4: Try Campaign.LoadGame
            if (TryLoadViaCampaign(saveName))
            {
                return true;
            }

            BannerBrosModule.LogMessage("[SaveLoader] All auto-load methods failed");
            return false;
        }
        catch (Exception ex)
        {
            BannerBrosModule.LogMessage($"[SaveLoader] Error: {ex.Message}");
            return false;
        }
    }

    private static bool TryLoadViaSandBoxSaveHelper(string saveName)
    {
        try
        {
            // Look for SandBoxSaveHelper in SandBox module
            var sandboxAssembly = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "SandBox");

            if (sandboxAssembly == null)
            {
                BannerBrosModule.LogMessage("[SaveLoader] SandBox assembly not found");
                return false;
            }

            var helperType = sandboxAssembly.GetType("SandBox.SandBoxSaveHelper");
            if (helperType == null)
            {
                BannerBrosModule.LogMessage("[SaveLoader] SandBoxSaveHelper type not found");
                return false;
            }

            // Try LoadSaveGame method
            var loadMethod = helperType.GetMethod("LoadSaveGame",
                BindingFlags.Public | BindingFlags.Static);

            if (loadMethod != null)
            {
                BannerBrosModule.LogMessage("[SaveLoader] Found LoadSaveGame method, invoking...");
                loadMethod.Invoke(null, new object[] { saveName });
                BannerBrosModule.LogMessage("[SaveLoader] LoadSaveGame invoked successfully");
                return true;
            }

            // Try TryLoadSave method
            var tryLoadMethod = helperType.GetMethod("TryLoadSave",
                BindingFlags.Public | BindingFlags.Static);

            if (tryLoadMethod != null)
            {
                BannerBrosModule.LogMessage("[SaveLoader] Found TryLoadSave method, invoking...");
                var result = tryLoadMethod.Invoke(null, new object[] { saveName });
                BannerBrosModule.LogMessage($"[SaveLoader] TryLoadSave result: {result}");
                return result is bool b && b;
            }

            BannerBrosModule.LogMessage("[SaveLoader] No suitable method found in SandBoxSaveHelper");
        }
        catch (Exception ex)
        {
            BannerBrosModule.LogMessage($"[SaveLoader] SandBoxSaveHelper error: {ex.Message}");
        }

        return false;
    }

    private static bool TryLoadViaMBSaveLoad(string saveName)
    {
        try
        {
            // Try MBSaveLoad.LoadSaveGameData
            var saveLoadType = typeof(MBSubModuleBase).Assembly
                .GetType("TaleWorlds.MountAndBlade.MBSaveLoad");

            if (saveLoadType == null)
            {
                BannerBrosModule.LogMessage("[SaveLoader] MBSaveLoad type not found");
                return false;
            }

            // Get the save game data first
            var getSaveMethod = saveLoadType.GetMethod("GetSaveFileWithName",
                BindingFlags.Public | BindingFlags.Static);

            if (getSaveMethod != null)
            {
                BannerBrosModule.LogMessage("[SaveLoader] Found GetSaveFileWithName method");
                var saveFile = getSaveMethod.Invoke(null, new object[] { saveName });
                if (saveFile != null)
                {
                    BannerBrosModule.LogMessage("[SaveLoader] Got save file object, looking for load method...");

                    // Now load it
                    var loadMethod = saveLoadType.GetMethod("LoadSaveGameData",
                        BindingFlags.Public | BindingFlags.Static);

                    if (loadMethod != null)
                    {
                        BannerBrosModule.LogMessage("[SaveLoader] Invoking LoadSaveGameData...");
                        loadMethod.Invoke(null, new object[] { saveFile });
                        BannerBrosModule.LogMessage("[SaveLoader] MBSaveLoad.LoadSaveGameData invoked");
                        return true;
                    }
                }
                else
                {
                    BannerBrosModule.LogMessage("[SaveLoader] GetSaveFileWithName returned null");
                }
            }
        }
        catch (Exception ex)
        {
            BannerBrosModule.LogMessage($"[SaveLoader] MBSaveLoad error: {ex.Message}");
        }

        return false;
    }

    private static bool TryLoadViaGameStateManager(string saveName)
    {
        try
        {
            var game = Game.Current;
            if (game == null)
            {
                BannerBrosModule.LogMessage("[SaveLoader] Game.Current is null");
                return false;
            }

            var gameStateManager = game.GameStateManager;
            if (gameStateManager == null)
            {
                BannerBrosModule.LogMessage("[SaveLoader] GameStateManager is null");
                return false;
            }

            // Try to find InitialState or MainMenuState and trigger load from there
            // Look for a LoadSave or similar method on the current game state
            var currentState = gameStateManager.ActiveState;
            if (currentState != null)
            {
                BannerBrosModule.LogMessage($"[SaveLoader] Current state: {currentState.GetType().Name}");

                // Try to find a LoadSave method on the current state
                var loadMethod = currentState.GetType().GetMethod("LoadSave",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

                if (loadMethod != null)
                {
                    BannerBrosModule.LogMessage("[SaveLoader] Found LoadSave on current state");
                    loadMethod.Invoke(currentState, new object[] { saveName });
                    return true;
                }
            }

            // Try to find SaveGameState type and create it
            var stateTypes = AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(a =>
                {
                    try { return a.GetTypes(); }
                    catch { return Array.Empty<Type>(); }
                })
                .Where(t => t.Name.Contains("SaveGame") || t.Name.Contains("LoadGame"))
                .ToList();

            foreach (var stateType in stateTypes)
            {
                BannerBrosModule.LogMessage($"[SaveLoader] Found potential type: {stateType.FullName}");
            }
        }
        catch (Exception ex)
        {
            BannerBrosModule.LogMessage($"[SaveLoader] GameStateManager error: {ex.Message}");
        }

        return false;
    }

    private static bool TryLoadViaCampaign(string saveName)
    {
        try
        {
            // Look for Campaign save loading methods
            var campaignType = AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(a =>
                {
                    try { return a.GetTypes(); }
                    catch { return Array.Empty<Type>(); }
                })
                .FirstOrDefault(t => t.FullName == "TaleWorlds.CampaignSystem.Campaign");

            if (campaignType == null)
            {
                BannerBrosModule.LogMessage("[SaveLoader] Campaign type not found");
                return false;
            }

            // Try Campaign.Load or similar static method
            var loadMethods = campaignType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(m => m.Name.Contains("Load"))
                .ToList();

            foreach (var method in loadMethods)
            {
                BannerBrosModule.LogMessage($"[SaveLoader] Found Campaign method: {method.Name}");
            }

            // Try SaveManager or similar
            var saveManagerType = AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(a =>
                {
                    try { return a.GetTypes(); }
                    catch { return Array.Empty<Type>(); }
                })
                .FirstOrDefault(t => t.Name == "SaveManager" || t.Name == "CampaignSaveSystem");

            if (saveManagerType != null)
            {
                BannerBrosModule.LogMessage($"[SaveLoader] Found: {saveManagerType.FullName}");

                var methods = saveManagerType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .Where(m => m.Name.Contains("Load"))
                    .ToList();

                foreach (var method in methods)
                {
                    BannerBrosModule.LogMessage($"[SaveLoader] Method: {method.Name}({string.Join(", ", method.GetParameters().Select(p => p.ParameterType.Name))})");
                }
            }
        }
        catch (Exception ex)
        {
            BannerBrosModule.LogMessage($"[SaveLoader] Campaign error: {ex.Message}");
        }

        return false;
    }
}
