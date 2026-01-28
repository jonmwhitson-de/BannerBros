using System;
using System.Linq;
using System.Reflection;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;
using TaleWorlds.Library;

namespace BannerBros.Core;

/// <summary>
/// Handles programmatic loading of save games for the co-op join flow.
/// </summary>
public static class SaveGameLoader
{
    public static bool LoadSaveFile(string savePath)
    {
        var saveName = System.IO.Path.GetFileNameWithoutExtension(savePath);
        BannerBrosModule.LogMessage($"[SaveLoader] === LOADING SAVE: {saveName} ===");

        // Get the save file info object first - we need this for most load methods
        var saveFileInfo = GetSaveFileInfo(saveName);

        if (saveFileInfo == null)
        {
            BannerBrosModule.LogMessage("[SaveLoader] Could not find save file info!");
            return false;
        }

        BannerBrosModule.LogMessage($"[SaveLoader] Found save file info: {saveFileInfo.GetType().Name}");

        // Try Method 1: SandBoxSaveHelper.LoadSaveGame (most common)
        if (TryLoadViaSandBoxHelper(saveFileInfo))
        {
            return true;
        }

        // Try Method 2: MBSaveLoad.LoadSaveGameData
        if (TryLoadViaMBSaveLoad(saveFileInfo))
        {
            return true;
        }

        // Try Method 3: GameStateManager approach
        if (TryLoadViaGameState(saveFileInfo))
        {
            return true;
        }

        // Try Method 4: MBGameManager
        if (TryLoadViaMBGameManager(saveFileInfo))
        {
            return true;
        }

        BannerBrosModule.LogMessage("[SaveLoader] All load methods failed!");
        return false;
    }

    private static object? GetSaveFileInfo(string saveName)
    {
        try
        {
            // Search all assemblies for MBSaveLoad type
            Type? mbSaveLoadType = null;

            // Try known locations first
            mbSaveLoadType = typeof(MBSubModuleBase).Assembly.GetType("TaleWorlds.MountAndBlade.MBSaveLoad");

            // If not found, search all loaded assemblies
            if (mbSaveLoadType == null)
            {
                BannerBrosModule.LogMessage("[SaveLoader] MBSaveLoad not in MBSubModuleBase assembly, searching all assemblies...");
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try
                    {
                        var type = assembly.GetType("TaleWorlds.MountAndBlade.MBSaveLoad");
                        if (type != null)
                        {
                            mbSaveLoadType = type;
                            BannerBrosModule.LogMessage($"[SaveLoader] Found MBSaveLoad in: {assembly.GetName().Name}");
                            break;
                        }
                    }
                    catch { }
                }
            }

            // Try alternate type names
            if (mbSaveLoadType == null)
            {
                var typeNames = new[] { "MBSaveLoad", "SaveLoad", "SaveManager", "GameSaveManager" };
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (mbSaveLoadType != null) break;
                    try
                    {
                        foreach (var type in assembly.GetTypes())
                        {
                            if (typeNames.Contains(type.Name) && type.Namespace?.Contains("TaleWorlds") == true)
                            {
                                BannerBrosModule.LogMessage($"[SaveLoader] Found candidate: {type.FullName}");
                                // Check if it has GetSaveFiles method
                                if (type.GetMethod("GetSaveFiles", BindingFlags.Public | BindingFlags.Static) != null)
                                {
                                    mbSaveLoadType = type;
                                    BannerBrosModule.LogMessage($"[SaveLoader] Using: {type.FullName}");
                                    break;
                                }
                            }
                        }
                    }
                    catch { }
                }
            }

            if (mbSaveLoadType == null)
            {
                BannerBrosModule.LogMessage("[SaveLoader] MBSaveLoad type not found in any assembly");
                return null;
            }

            // Try to refresh the save list first
            var refreshMethod = mbSaveLoadType.GetMethod("RefreshSaveFiles", BindingFlags.Public | BindingFlags.Static);
            if (refreshMethod != null)
            {
                BannerBrosModule.LogMessage("[SaveLoader] Refreshing save file list...");
                var refreshParams = refreshMethod.GetParameters();
                if (refreshParams.Length == 0)
                {
                    refreshMethod.Invoke(null, null);
                }
                else
                {
                    // Try with default parameters
                    var args = new object[refreshParams.Length];
                    for (int i = 0; i < refreshParams.Length; i++)
                    {
                        args[i] = refreshParams[i].HasDefaultValue ? refreshParams[i].DefaultValue : null;
                    }
                    refreshMethod.Invoke(null, args);
                }
            }

            // Find GetSaveFiles method - try different overloads
            var getSaveFilesMethod = mbSaveLoadType.GetMethod("GetSaveFiles", BindingFlags.Public | BindingFlags.Static);
            if (getSaveFilesMethod == null)
            {
                // Try getting all methods named GetSaveFiles
                var methods = mbSaveLoadType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .Where(m => m.Name == "GetSaveFiles")
                    .ToArray();
                BannerBrosModule.LogMessage($"[SaveLoader] Found {methods.Length} GetSaveFiles methods");
                getSaveFilesMethod = methods.FirstOrDefault();
            }

            if (getSaveFilesMethod == null)
            {
                BannerBrosModule.LogMessage("[SaveLoader] GetSaveFiles method not found");
                return null;
            }

            // Call with appropriate parameters
            var getParams = getSaveFilesMethod.GetParameters();
            BannerBrosModule.LogMessage($"[SaveLoader] GetSaveFiles has {getParams.Length} params: {string.Join(", ", getParams.Select(p => p.ParameterType.Name))}");

            object? saveFiles;
            if (getParams.Length == 0)
            {
                saveFiles = getSaveFilesMethod.Invoke(null, null);
            }
            else
            {
                // Build args with defaults
                var args = new object?[getParams.Length];
                for (int i = 0; i < getParams.Length; i++)
                {
                    if (getParams[i].HasDefaultValue)
                    {
                        args[i] = getParams[i].DefaultValue;
                    }
                    else if (getParams[i].ParameterType == typeof(bool))
                    {
                        args[i] = false;
                    }
                    else if (getParams[i].ParameterType == typeof(string))
                    {
                        args[i] = "";
                    }
                    else
                    {
                        args[i] = null;
                    }
                }
                saveFiles = getSaveFilesMethod.Invoke(null, args);
            }

            if (saveFiles == null)
            {
                BannerBrosModule.LogMessage("[SaveLoader] GetSaveFiles returned null");
                return null;
            }

            var saveFilesEnumerable = saveFiles as System.Collections.IEnumerable;
            if (saveFilesEnumerable == null)
            {
                BannerBrosModule.LogMessage($"[SaveLoader] GetSaveFiles returned non-enumerable: {saveFiles.GetType().Name}");
                return null;
            }

            // List all saves for debugging
            BannerBrosModule.LogMessage($"[SaveLoader] Looking for: '{saveName}'");

            object? foundSave = null;
            int count = 0;
            foreach (var saveFile in saveFilesEnumerable)
            {
                var nameProperty = saveFile.GetType().GetProperty("Name");
                var name = nameProperty?.GetValue(saveFile) as string ?? "(unknown)";
                count++;

                if (name.Equals(saveName, StringComparison.OrdinalIgnoreCase))
                {
                    foundSave = saveFile;
                    BannerBrosModule.LogMessage($"[SaveLoader] Found matching save: {name}");
                }
            }

            BannerBrosModule.LogMessage($"[SaveLoader] Scanned {count} saves");

            if (foundSave != null)
            {
                return foundSave;
            }

            BannerBrosModule.LogMessage($"[SaveLoader] Save '{saveName}' not in list - may need refresh");
        }
        catch (Exception ex)
        {
            BannerBrosModule.LogMessage($"[SaveLoader] GetSaveFileInfo error: {ex.Message}");
        }

        return null;
    }

    private static bool TryLoadViaSandBoxHelper(object saveFileInfo)
    {
        try
        {
            BannerBrosModule.LogMessage("[SaveLoader] Trying SandBoxSaveHelper.LoadSaveGame...");

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
                BannerBrosModule.LogMessage("[SaveLoader] SandBoxSaveHelper not found");
                return false;
            }

            // Find LoadSaveGame method
            var methods = helperType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(m => m.Name == "LoadSaveGame")
                .ToList();

            BannerBrosModule.LogMessage($"[SaveLoader] Found {methods.Count} LoadSaveGame methods");

            foreach (var method in methods)
            {
                var parameters = method.GetParameters();
                var paramStr = string.Join(", ", parameters.Select(p => p.ParameterType.Name));
                BannerBrosModule.LogMessage($"[SaveLoader] Trying LoadSaveGame({paramStr})");

                try
                {
                    if (parameters.Length == 1)
                    {
                        method.Invoke(null, new object[] { saveFileInfo });
                        BannerBrosModule.LogMessage("[SaveLoader] LoadSaveGame(1 param) called!");
                        return true;
                    }
                    else if (parameters.Length == 2)
                    {
                        // Second param is usually Action<LoadGameResult> callback
                        method.Invoke(null, new object?[] { saveFileInfo, null });
                        BannerBrosModule.LogMessage("[SaveLoader] LoadSaveGame(2 params) called!");
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    BannerBrosModule.LogMessage($"[SaveLoader] Method failed: {ex.InnerException?.Message ?? ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            BannerBrosModule.LogMessage($"[SaveLoader] SandBoxHelper error: {ex.Message}");
        }

        return false;
    }

    private static bool TryLoadViaMBSaveLoad(object saveFileInfo)
    {
        try
        {
            BannerBrosModule.LogMessage("[SaveLoader] Trying MBSaveLoad.LoadSaveGameData...");

            var mbSaveLoadType = typeof(MBSubModuleBase).Assembly.GetType("TaleWorlds.MountAndBlade.MBSaveLoad");
            if (mbSaveLoadType == null)
            {
                return false;
            }

            var loadMethod = mbSaveLoadType.GetMethod("LoadSaveGameData", BindingFlags.Public | BindingFlags.Static);
            if (loadMethod != null)
            {
                var parameters = loadMethod.GetParameters();
                BannerBrosModule.LogMessage($"[SaveLoader] LoadSaveGameData params: {string.Join(", ", parameters.Select(p => p.ParameterType.Name))}");

                loadMethod.Invoke(null, new object[] { saveFileInfo });
                BannerBrosModule.LogMessage("[SaveLoader] LoadSaveGameData called!");
                return true;
            }
        }
        catch (Exception ex)
        {
            BannerBrosModule.LogMessage($"[SaveLoader] MBSaveLoad error: {ex.InnerException?.Message ?? ex.Message}");
        }

        return false;
    }

    private static bool TryLoadViaGameState(object saveFileInfo)
    {
        try
        {
            BannerBrosModule.LogMessage("[SaveLoader] Trying GameStateManager approach...");

            var game = Game.Current;
            if (game == null)
            {
                BannerBrosModule.LogMessage("[SaveLoader] Game.Current is null");
                return false;
            }

            // Try to find and use SaveLoad class
            var saveLoadType = AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(a => { try { return a.GetTypes(); } catch { return Array.Empty<Type>(); } })
                .FirstOrDefault(t => t.Name == "SaveLoad" && t.Namespace?.Contains("SandBox") == true);

            if (saveLoadType != null)
            {
                BannerBrosModule.LogMessage($"[SaveLoader] Found SaveLoad type: {saveLoadType.FullName}");

                var loadMethod = saveLoadType.GetMethod("LoadGame", BindingFlags.Public | BindingFlags.Static);
                if (loadMethod != null)
                {
                    var parameters = loadMethod.GetParameters();
                    BannerBrosModule.LogMessage($"[SaveLoader] SaveLoad.LoadGame params: {string.Join(", ", parameters.Select(p => p.ParameterType.Name))}");

                    if (parameters.Length == 1)
                    {
                        loadMethod.Invoke(null, new object[] { saveFileInfo });
                        BannerBrosModule.LogMessage("[SaveLoader] SaveLoad.LoadGame called!");
                        return true;
                    }
                    else if (parameters.Length == 2)
                    {
                        loadMethod.Invoke(null, new object?[] { saveFileInfo, null });
                        BannerBrosModule.LogMessage("[SaveLoader] SaveLoad.LoadGame(2) called!");
                        return true;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            BannerBrosModule.LogMessage($"[SaveLoader] GameState error: {ex.InnerException?.Message ?? ex.Message}");
        }

        return false;
    }

    private static bool TryLoadViaMBGameManager(object saveFileInfo)
    {
        try
        {
            BannerBrosModule.LogMessage("[SaveLoader] Trying MBGameManager approach...");

            // Find MBGameManager type
            var gameManagerType = typeof(MBSubModuleBase).Assembly.GetType("TaleWorlds.MountAndBlade.MBGameManager");
            if (gameManagerType == null)
            {
                // Try alternate namespace
                gameManagerType = AppDomain.CurrentDomain.GetAssemblies()
                    .SelectMany(a => { try { return a.GetTypes(); } catch { return Array.Empty<Type>(); } })
                    .FirstOrDefault(t => t.Name == "MBGameManager");
            }

            if (gameManagerType != null)
            {
                BannerBrosModule.LogMessage($"[SaveLoader] Found MBGameManager: {gameManagerType.FullName}");

                // Look for StartNewGame or LoadGame methods
                var methods = gameManagerType.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance)
                    .Where(m => m.Name.Contains("Load") || m.Name.Contains("Start"))
                    .ToList();

                foreach (var method in methods)
                {
                    BannerBrosModule.LogMessage($"[SaveLoader] Found method: {method.Name}");
                }
            }

            // Also try CampaignGameManager
            var campaignManagerType = AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(a => { try { return a.GetTypes(); } catch { return Array.Empty<Type>(); } })
                .FirstOrDefault(t => t.Name == "CampaignGameManager");

            if (campaignManagerType != null)
            {
                BannerBrosModule.LogMessage($"[SaveLoader] Found CampaignGameManager: {campaignManagerType.FullName}");

                var loadMethod = campaignManagerType.GetMethod("LoadGame",
                    BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);

                if (loadMethod != null)
                {
                    BannerBrosModule.LogMessage("[SaveLoader] Trying CampaignGameManager.LoadGame...");
                    loadMethod.Invoke(null, new object[] { saveFileInfo });
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            BannerBrosModule.LogMessage($"[SaveLoader] MBGameManager error: {ex.InnerException?.Message ?? ex.Message}");
        }

        return false;
    }
}
