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

            // Method 1: Try MBSaveLoad with SaveGameFileInfo (most reliable)
            if (TryLoadViaMBSaveLoadWithFileInfo(saveName))
            {
                return true;
            }

            // Method 2: Try SandBoxSaveHelper with SaveGameFileInfo
            if (TryLoadViaSandBoxWithFileInfo(saveName))
            {
                return true;
            }

            // Method 3: Try direct SandBoxSaveHelper call
            if (TryLoadViaSandBoxDirect(saveName))
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

    private static bool TryLoadViaMBSaveLoadWithFileInfo(string saveName)
    {
        try
        {
            BannerBrosModule.LogMessage("[SaveLoader] Trying MBSaveLoad with SaveGameFileInfo...");

            // Get MBSaveLoad type
            var mbSaveLoadType = typeof(MBSubModuleBase).Assembly.GetType("TaleWorlds.MountAndBlade.MBSaveLoad");
            if (mbSaveLoadType == null)
            {
                BannerBrosModule.LogMessage("[SaveLoader] MBSaveLoad type not found");
                return false;
            }

            // Get save files list
            var getSaveFilesMethod = mbSaveLoadType.GetMethod("GetSaveFiles",
                BindingFlags.Public | BindingFlags.Static);

            if (getSaveFilesMethod == null)
            {
                BannerBrosModule.LogMessage("[SaveLoader] GetSaveFiles method not found");
                return false;
            }

            var saveFiles = getSaveFilesMethod.Invoke(null, null);
            if (saveFiles == null)
            {
                BannerBrosModule.LogMessage("[SaveLoader] GetSaveFiles returned null");
                return false;
            }

            // Find our save file by name
            object? targetSaveFile = null;
            var enumerable = saveFiles as System.Collections.IEnumerable;
            if (enumerable != null)
            {
                foreach (var saveFile in enumerable)
                {
                    var nameProperty = saveFile.GetType().GetProperty("Name");
                    var name = nameProperty?.GetValue(saveFile) as string;
                    BannerBrosModule.LogMessage($"[SaveLoader] Found save: {name}");

                    if (name != null && name.Equals(saveName, StringComparison.OrdinalIgnoreCase))
                    {
                        targetSaveFile = saveFile;
                        break;
                    }
                }
            }

            if (targetSaveFile == null)
            {
                BannerBrosModule.LogMessage($"[SaveLoader] Save file '{saveName}' not found in save list");
                return false;
            }

            BannerBrosModule.LogMessage($"[SaveLoader] Found target save file: {saveName}");

            // Try to load using SandBoxSaveHelper.LoadSaveGame(SaveGameFileInfo, Action<LoadGameResult>)
            var sandboxAssembly = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "SandBox");

            if (sandboxAssembly != null)
            {
                var helperType = sandboxAssembly.GetType("SandBox.SandBoxSaveHelper");
                if (helperType != null)
                {
                    // Try LoadSaveGame with SaveGameFileInfo parameter
                    var loadMethods = helperType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                        .Where(m => m.Name == "LoadSaveGame")
                        .ToList();

                    foreach (var loadMethod in loadMethods)
                    {
                        var parameters = loadMethod.GetParameters();
                        BannerBrosModule.LogMessage($"[SaveLoader] Found LoadSaveGame with {parameters.Length} params: " +
                            string.Join(", ", parameters.Select(p => p.ParameterType.Name)));

                        if (parameters.Length >= 1 && parameters[0].ParameterType.Name.Contains("SaveGameFileInfo"))
                        {
                            try
                            {
                                if (parameters.Length == 1)
                                {
                                    loadMethod.Invoke(null, new object[] { targetSaveFile });
                                }
                                else if (parameters.Length == 2)
                                {
                                    // Second param is usually Action<LoadGameResult>
                                    loadMethod.Invoke(null, new object[] { targetSaveFile, null });
                                }
                                BannerBrosModule.LogMessage("[SaveLoader] LoadSaveGame invoked successfully!");
                                return true;
                            }
                            catch (Exception invokeEx)
                            {
                                BannerBrosModule.LogMessage($"[SaveLoader] LoadSaveGame invoke error: {invokeEx.Message}");
                            }
                        }
                    }
                }
            }

            // Fallback: Try MBSaveLoad.LoadSaveGameData
            var loadSaveGameDataMethod = mbSaveLoadType.GetMethod("LoadSaveGameData",
                BindingFlags.Public | BindingFlags.Static);

            if (loadSaveGameDataMethod != null)
            {
                BannerBrosModule.LogMessage("[SaveLoader] Trying LoadSaveGameData...");
                loadSaveGameDataMethod.Invoke(null, new object[] { targetSaveFile });
                BannerBrosModule.LogMessage("[SaveLoader] LoadSaveGameData invoked!");
                return true;
            }
        }
        catch (Exception ex)
        {
            BannerBrosModule.LogMessage($"[SaveLoader] MBSaveLoad error: {ex.Message}");
        }

        return false;
    }

    private static bool TryLoadViaSandBoxWithFileInfo(string saveName)
    {
        try
        {
            BannerBrosModule.LogMessage("[SaveLoader] Trying SandBox with SaveGameFileInfo...");

            var sandboxAssembly = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "SandBox");

            if (sandboxAssembly == null)
            {
                BannerBrosModule.LogMessage("[SaveLoader] SandBox assembly not found");
                return false;
            }

            // Try to find SaveGameFileInfo type and create it
            var saveGameFileInfoType = typeof(MBSubModuleBase).Assembly.GetType("TaleWorlds.SaveSystem.SaveGameFileInfo")
                ?? AppDomain.CurrentDomain.GetAssemblies()
                    .SelectMany(a => { try { return a.GetTypes(); } catch { return Array.Empty<Type>(); } })
                    .FirstOrDefault(t => t.Name == "SaveGameFileInfo");

            if (saveGameFileInfoType != null)
            {
                BannerBrosModule.LogMessage($"[SaveLoader] Found SaveGameFileInfo type: {saveGameFileInfoType.FullName}");

                // Try to create SaveGameFileInfo from save name
                var constructor = saveGameFileInfoType.GetConstructor(new[] { typeof(string) });
                if (constructor != null)
                {
                    var saveFileInfo = constructor.Invoke(new object[] { saveName });

                    var helperType = sandboxAssembly.GetType("SandBox.SandBoxSaveHelper");
                    if (helperType != null)
                    {
                        var loadMethods = helperType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                            .Where(m => m.Name == "LoadSaveGame");

                        foreach (var loadMethod in loadMethods)
                        {
                            try
                            {
                                var parameters = loadMethod.GetParameters();
                                if (parameters.Length == 2)
                                {
                                    loadMethod.Invoke(null, new object[] { saveFileInfo, null });
                                    BannerBrosModule.LogMessage("[SaveLoader] LoadSaveGame with constructed FileInfo succeeded!");
                                    return true;
                                }
                            }
                            catch { }
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            BannerBrosModule.LogMessage($"[SaveLoader] SandBox FileInfo error: {ex.Message}");
        }

        return false;
    }

    private static bool TryLoadViaSandBoxDirect(string saveName)
    {
        try
        {
            BannerBrosModule.LogMessage("[SaveLoader] Trying direct SandBox call...");

            var sandboxAssembly = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "SandBox");

            if (sandboxAssembly == null)
            {
                return false;
            }

            var helperType = sandboxAssembly.GetType("SandBox.SandBoxSaveHelper");
            if (helperType == null)
            {
                return false;
            }

            // Try any LoadSaveGame method that takes a string
            var loadMethods = helperType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(m => m.Name.Contains("Load"));

            foreach (var loadMethod in loadMethods)
            {
                var parameters = loadMethod.GetParameters();
                BannerBrosModule.LogMessage($"[SaveLoader] Trying {loadMethod.Name}({string.Join(", ", parameters.Select(p => p.ParameterType.Name))})");

                if (parameters.Length == 1 && parameters[0].ParameterType == typeof(string))
                {
                    try
                    {
                        loadMethod.Invoke(null, new object[] { saveName });
                        BannerBrosModule.LogMessage($"[SaveLoader] {loadMethod.Name} with string succeeded!");
                        return true;
                    }
                    catch (Exception e)
                    {
                        BannerBrosModule.LogMessage($"[SaveLoader] {loadMethod.Name} failed: {e.Message}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            BannerBrosModule.LogMessage($"[SaveLoader] Direct SandBox error: {ex.Message}");
        }

        return false;
    }
}
