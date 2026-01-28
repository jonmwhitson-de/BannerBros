using System;
using System.Collections.Generic;
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
        BannerBrosModule.LogMessage($"[SaveLoader] Full path: {savePath}");

        // Force refresh the save list to include newly written file
        ForceRefreshSaveList();

        // First try to load directly by path (faster, works for newly written files)
        if (TryLoadDirectlyByPath(savePath))
        {
            return true;
        }

        // Get the save file info object from game's save list
        var saveFileInfo = GetSaveFileInfo(saveName);

        if (saveFileInfo == null)
        {
            BannerBrosModule.LogMessage("[SaveLoader] Could not find save in game's save list!");
            BannerBrosModule.LogMessage("[SaveLoader] The file may be too new - try manual load");
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

    /// <summary>
    /// Forces the game to refresh its internal save file list.
    /// </summary>
    private static void ForceRefreshSaveList()
    {
        try
        {
            BannerBrosModule.LogMessage("[SaveLoader] Forcing save list refresh...");

            // Find MBSaveLoad type
            Type? mbSaveLoadType = typeof(MBSubModuleBase).Assembly.GetType("TaleWorlds.MountAndBlade.MBSaveLoad");
            if (mbSaveLoadType == null)
            {
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try
                    {
                        var type = assembly.GetType("TaleWorlds.MountAndBlade.MBSaveLoad")
                                   ?? assembly.GetType("TaleWorlds.Core.MBSaveLoad");
                        if (type != null)
                        {
                            mbSaveLoadType = type;
                            break;
                        }
                    }
                    catch { }
                }
            }

            if (mbSaveLoadType == null)
            {
                BannerBrosModule.LogMessage("[SaveLoader] MBSaveLoad not found for refresh");
                return;
            }

            // Try multiple refresh methods
            var methodNames = new[] { "RefreshSaveFiles", "InitializeSaveSystem", "Initialize", "Refresh" };
            foreach (var methodName in methodNames)
            {
                var method = mbSaveLoadType.GetMethod(methodName, BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);
                if (method != null)
                {
                    try
                    {
                        var parameters = method.GetParameters();
                        if (parameters.Length == 0)
                        {
                            method.Invoke(null, null);
                            BannerBrosModule.LogMessage($"[SaveLoader] Called {methodName}()");
                        }
                        else
                        {
                            // Try with default args
                            var args = new object?[parameters.Length];
                            for (int i = 0; i < parameters.Length; i++)
                            {
                                args[i] = parameters[i].HasDefaultValue ? parameters[i].DefaultValue : null;
                            }
                            method.Invoke(null, args);
                            BannerBrosModule.LogMessage($"[SaveLoader] Called {methodName}(defaults)");
                        }
                    }
                    catch (Exception ex)
                    {
                        BannerBrosModule.LogMessage($"[SaveLoader] {methodName} failed: {ex.Message}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            BannerBrosModule.LogMessage($"[SaveLoader] Refresh error: {ex.Message}");
        }
    }

    /// <summary>
    /// Try to load save directly by file path, bypassing the game's save list.
    /// </summary>
    private static bool TryLoadDirectlyByPath(string savePath)
    {
        try
        {
            BannerBrosModule.LogMessage("[SaveLoader] Trying direct path load...");

            // Verify the file exists and is valid
            if (!System.IO.File.Exists(savePath))
            {
                BannerBrosModule.LogMessage($"[SaveLoader] ERROR: File does not exist: {savePath}");
                return false;
            }

            var fileInfo = new System.IO.FileInfo(savePath);
            BannerBrosModule.LogMessage($"[SaveLoader] File size: {fileInfo.Length} bytes, modified: {fileInfo.LastWriteTime}");

            // Look for SaveGameFileInfo type
            var saveFileInfoType = AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(a => { try { return a.GetTypes(); } catch { return Array.Empty<Type>(); } })
                .FirstOrDefault(t => t.Name == "SaveGameFileInfo");

            if (saveFileInfoType != null)
            {
                BannerBrosModule.LogMessage($"[SaveLoader] Found SaveGameFileInfo: {saveFileInfoType.FullName}");

                // Log all properties for understanding the type
                var allProps = saveFileInfoType.GetProperties(BindingFlags.Public | BindingFlags.Instance);
                BannerBrosModule.LogMessage($"[SaveLoader] SaveGameFileInfo properties:");
                foreach (var prop in allProps)
                {
                    BannerBrosModule.LogMessage($"[SaveLoader]   {prop.Name} ({prop.PropertyType.Name}) CanWrite={prop.CanWrite}");
                }

                // Log all constructors
                var ctors = saveFileInfoType.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                BannerBrosModule.LogMessage($"[SaveLoader] SaveGameFileInfo has {ctors.Length} constructors:");

                object? createdSaveInfo = null;

                foreach (var ctor in ctors)
                {
                    var ctorParams = ctor.GetParameters();
                    BannerBrosModule.LogMessage($"[SaveLoader]   Ctor: ({string.Join(", ", ctorParams.Select(p => $"{p.ParameterType.Name} {p.Name}"))})");

                    // Try ctor with FileInfo parameter
                    if (ctorParams.Length == 1 && ctorParams[0].ParameterType == typeof(System.IO.FileInfo))
                    {
                        try
                        {
                            var saveInfo = ctor.Invoke(new object[] { fileInfo });
                            if (saveInfo != null)
                            {
                                BannerBrosModule.LogMessage("[SaveLoader] Created SaveGameFileInfo from FileInfo!");
                                if (TryLoadViaSandBoxHelper(saveInfo))
                                {
                                    return true;
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            BannerBrosModule.LogMessage($"[SaveLoader] FileInfo ctor failed: {ex.Message}");
                        }
                    }
                    // Try ctor with string parameter
                    else if (ctorParams.Length == 1 && ctorParams[0].ParameterType == typeof(string))
                    {
                        try
                        {
                            var saveInfo = ctor.Invoke(new object[] { savePath });
                            if (saveInfo != null)
                            {
                                BannerBrosModule.LogMessage("[SaveLoader] Created SaveGameFileInfo from path string!");
                                if (TryLoadViaSandBoxHelper(saveInfo))
                                {
                                    return true;
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            BannerBrosModule.LogMessage($"[SaveLoader] String ctor failed: {ex.Message}");
                        }
                    }
                    // Try parameterless ctor and set properties manually
                    else if (ctorParams.Length == 0 && createdSaveInfo == null)
                    {
                        try
                        {
                            createdSaveInfo = ctor.Invoke(null);
                            BannerBrosModule.LogMessage("[SaveLoader] Created empty SaveGameFileInfo, will try to populate");
                        }
                        catch (Exception ex)
                        {
                            BannerBrosModule.LogMessage($"[SaveLoader] Parameterless ctor failed: {ex.Message}");
                        }
                    }
                }

                // If we created an empty SaveGameFileInfo, try to populate it
                if (createdSaveInfo != null)
                {
                    if (TryPopulateSaveGameFileInfo(createdSaveInfo, savePath, fileInfo))
                    {
                        BannerBrosModule.LogMessage("[SaveLoader] Populated SaveGameFileInfo, attempting load...");
                        if (TryLoadViaSandBoxHelper(createdSaveInfo))
                        {
                            return true;
                        }
                    }
                }

                // Log all static methods for debugging
                var staticMethods = saveFileInfoType.GetMethods(BindingFlags.Public | BindingFlags.Static);
                BannerBrosModule.LogMessage($"[SaveLoader] SaveGameFileInfo has {staticMethods.Length} static methods:");
                foreach (var m in staticMethods.Where(x => x.DeclaringType == saveFileInfoType))
                {
                    var p = m.GetParameters();
                    BannerBrosModule.LogMessage($"[SaveLoader]   {m.Name}({string.Join(", ", p.Select(x => x.ParameterType.Name))})");
                }

                // Try static Create/FromPath methods
                var createMethods = saveFileInfoType.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic)
                    .Where(m => m.Name.Contains("Create") || m.Name.Contains("FromPath") || m.Name.Contains("FromFile") || m.Name.Contains("Load"))
                    .ToArray();

                foreach (var method in createMethods)
                {
                    var methodParams = method.GetParameters();
                    BannerBrosModule.LogMessage($"[SaveLoader] Trying SaveGameFileInfo.{method.Name}({string.Join(", ", methodParams.Select(p => p.ParameterType.Name))})...");

                    if (methodParams.Length >= 1 && (methodParams[0].ParameterType == typeof(string) || methodParams[0].ParameterType == typeof(System.IO.FileInfo)))
                    {
                        try
                        {
                            var args = new object?[methodParams.Length];
                            args[0] = methodParams[0].ParameterType == typeof(string) ? savePath : (object)fileInfo;
                            for (int i = 1; i < methodParams.Length; i++)
                            {
                                args[i] = methodParams[i].HasDefaultValue ? methodParams[i].DefaultValue : null;
                            }
                            var saveInfo = method.Invoke(null, args);
                            if (saveInfo != null)
                            {
                                BannerBrosModule.LogMessage($"[SaveLoader] Created via {method.Name}!");
                                if (TryLoadViaSandBoxHelper(saveInfo))
                                {
                                    return true;
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            BannerBrosModule.LogMessage($"[SaveLoader] {method.Name} failed: {ex.Message}");
                        }
                    }
                }
            }

            // Try SandBoxSaveHelper methods
            if (TryLoadViaSandBoxHelperByPath(savePath))
            {
                return true;
            }

            // Try SandBox.SaveLoad class
            if (TryLoadViaSandBoxSaveLoad(savePath))
            {
                return true;
            }

            // Try to start a campaign game manager with the save
            if (TryLoadViaCampaignGameManager(savePath))
            {
                return true;
            }
        }
        catch (Exception ex)
        {
            BannerBrosModule.LogMessage($"[SaveLoader] Direct path load error: {ex.Message}");
            BannerBrosModule.LogMessage($"[SaveLoader] Stack: {ex.StackTrace}");
        }

        return false;
    }

    /// <summary>
    /// Try to populate an empty SaveGameFileInfo with the file information.
    /// </summary>
    private static bool TryPopulateSaveGameFileInfo(object saveInfo, string savePath, System.IO.FileInfo fileInfo)
    {
        try
        {
            var type = saveInfo.GetType();
            var saveName = System.IO.Path.GetFileNameWithoutExtension(savePath);
            bool anySet = false;

            // Try to set Name property
            var nameProp = type.GetProperty("Name", BindingFlags.Public | BindingFlags.Instance);
            if (nameProp != null && nameProp.CanWrite)
            {
                nameProp.SetValue(saveInfo, saveName);
                BannerBrosModule.LogMessage($"[SaveLoader] Set Name = {saveName}");
                anySet = true;
            }
            else
            {
                // Try setting via backing field
                var nameField = type.GetField("_name", BindingFlags.NonPublic | BindingFlags.Instance)
                             ?? type.GetField("<Name>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance);
                if (nameField != null)
                {
                    nameField.SetValue(saveInfo, saveName);
                    BannerBrosModule.LogMessage($"[SaveLoader] Set Name field = {saveName}");
                    anySet = true;
                }
            }

            // Try to set various path-related properties
            var pathProps = new[] { "FilePath", "Path", "SavePath", "FullPath", "File" };
            foreach (var propName in pathProps)
            {
                var prop = type.GetProperty(propName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (prop != null && prop.CanWrite)
                {
                    if (prop.PropertyType == typeof(string))
                    {
                        prop.SetValue(saveInfo, savePath);
                        BannerBrosModule.LogMessage($"[SaveLoader] Set {propName} = {savePath}");
                        anySet = true;
                    }
                    else if (prop.PropertyType == typeof(System.IO.FileInfo))
                    {
                        prop.SetValue(saveInfo, fileInfo);
                        BannerBrosModule.LogMessage($"[SaveLoader] Set {propName} = FileInfo");
                        anySet = true;
                    }
                }
            }

            // Try fields too
            var fields = type.GetFields(BindingFlags.NonPublic | BindingFlags.Instance);
            foreach (var field in fields)
            {
                var fieldNameLower = field.Name.ToLowerInvariant();
                if (fieldNameLower.Contains("path") || fieldNameLower.Contains("file"))
                {
                    BannerBrosModule.LogMessage($"[SaveLoader] Found field: {field.Name} ({field.FieldType.Name})");
                    if (field.FieldType == typeof(string))
                    {
                        field.SetValue(saveInfo, savePath);
                        BannerBrosModule.LogMessage($"[SaveLoader] Set field {field.Name} = path");
                        anySet = true;
                    }
                    else if (field.FieldType == typeof(System.IO.FileInfo))
                    {
                        field.SetValue(saveInfo, fileInfo);
                        BannerBrosModule.LogMessage($"[SaveLoader] Set field {field.Name} = FileInfo");
                        anySet = true;
                    }
                }
            }

            // Log what the object looks like now
            BannerBrosModule.LogMessage("[SaveLoader] SaveGameFileInfo state after population:");
            foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                try
                {
                    var val = prop.GetValue(saveInfo);
                    BannerBrosModule.LogMessage($"[SaveLoader]   {prop.Name} = {val ?? "(null)"}");
                }
                catch { }
            }

            return anySet;
        }
        catch (Exception ex)
        {
            BannerBrosModule.LogMessage($"[SaveLoader] PopulateSaveGameFileInfo error: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Try loading via SandBoxSaveHelper with a file path directly.
    /// </summary>
    private static bool TryLoadViaSandBoxHelperByPath(string savePath)
    {
        try
        {
            var sandboxAssembly = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "SandBox");

            if (sandboxAssembly == null) return false;

            var helperType = sandboxAssembly.GetType("SandBox.SandBoxSaveHelper");
            if (helperType == null) return false;

            // Log all methods for debugging
            var allMethods = helperType.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);
            BannerBrosModule.LogMessage($"[SaveLoader] SandBoxSaveHelper has {allMethods.Length} methods:");
            foreach (var m in allMethods.Where(x => x.DeclaringType == helperType))
            {
                var p = m.GetParameters();
                BannerBrosModule.LogMessage($"[SaveLoader]   {m.Name}({string.Join(", ", p.Select(x => x.ParameterType.Name))})");
            }

            // Try methods that accept string path
            var loadMethods = allMethods.Where(m => m.Name.Contains("Load")).ToArray();
            foreach (var method in loadMethods)
            {
                var methodParams = method.GetParameters();
                if (methodParams.Length >= 1 && methodParams[0].ParameterType == typeof(string))
                {
                    BannerBrosModule.LogMessage($"[SaveLoader] Trying SandBoxSaveHelper.{method.Name}(string)...");
                    try
                    {
                        var args = new object?[methodParams.Length];
                        args[0] = savePath;
                        for (int i = 1; i < methodParams.Length; i++)
                        {
                            args[i] = methodParams[i].HasDefaultValue ? methodParams[i].DefaultValue : null;
                        }
                        method.Invoke(null, args);
                        BannerBrosModule.LogMessage("[SaveLoader] SandBoxSaveHelper path load initiated!");
                        return true;
                    }
                    catch (Exception ex)
                    {
                        BannerBrosModule.LogMessage($"[SaveLoader] {method.Name} failed: {ex.InnerException?.Message ?? ex.Message}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            BannerBrosModule.LogMessage($"[SaveLoader] SandBoxSaveHelper path error: {ex.Message}");
        }

        return false;
    }

    /// <summary>
    /// Try loading via SandBox.SaveLoad class.
    /// </summary>
    private static bool TryLoadViaSandBoxSaveLoad(string savePath)
    {
        try
        {
            var saveLoadType = AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(a => { try { return a.GetTypes(); } catch { return Array.Empty<Type>(); } })
                .FirstOrDefault(t => t.Name == "SaveLoad" && t.Namespace?.Contains("SandBox") == true);

            if (saveLoadType == null) return false;

            BannerBrosModule.LogMessage($"[SaveLoader] Found SandBox.SaveLoad: {saveLoadType.FullName}");

            // Log all methods
            var allMethods = saveLoadType.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);
            BannerBrosModule.LogMessage($"[SaveLoader] SaveLoad has {allMethods.Length} methods:");
            foreach (var m in allMethods.Where(x => x.DeclaringType == saveLoadType).Take(15))
            {
                var p = m.GetParameters();
                BannerBrosModule.LogMessage($"[SaveLoader]   {m.Name}({string.Join(", ", p.Select(x => x.ParameterType.Name))})");
            }

            // Try LoadGame with path
            var loadMethods = allMethods.Where(m => m.Name.Contains("Load") || m.Name.Contains("Start")).ToArray();
            foreach (var method in loadMethods)
            {
                var methodParams = method.GetParameters();
                if (methodParams.Length >= 1 && methodParams[0].ParameterType == typeof(string))
                {
                    BannerBrosModule.LogMessage($"[SaveLoader] Trying SaveLoad.{method.Name}(string)...");
                    try
                    {
                        var args = new object?[methodParams.Length];
                        args[0] = savePath;
                        for (int i = 1; i < methodParams.Length; i++)
                        {
                            args[i] = methodParams[i].HasDefaultValue ? methodParams[i].DefaultValue : null;
                        }
                        method.Invoke(null, args);
                        BannerBrosModule.LogMessage("[SaveLoader] SaveLoad path load initiated!");
                        return true;
                    }
                    catch (Exception ex)
                    {
                        BannerBrosModule.LogMessage($"[SaveLoader] {method.Name} failed: {ex.InnerException?.Message ?? ex.Message}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            BannerBrosModule.LogMessage($"[SaveLoader] SandBox.SaveLoad error: {ex.Message}");
        }

        return false;
    }

    /// <summary>
    /// Try loading via CampaignGameManager.
    /// </summary>
    private static bool TryLoadViaCampaignGameManager(string savePath)
    {
        try
        {
            var saveName = System.IO.Path.GetFileNameWithoutExtension(savePath);

            // Look for SandBoxSaveHelper.LoadSaveGame that might accept a name
            var sandboxAssembly = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "SandBox");

            if (sandboxAssembly != null)
            {
                // Try to find a way to start campaign from save file
                var gameManagerTypes = sandboxAssembly.GetTypes()
                    .Where(t => t.Name.Contains("GameManager") || t.Name.Contains("SaveLoad"))
                    .ToArray();

                foreach (var type in gameManagerTypes)
                {
                    BannerBrosModule.LogMessage($"[SaveLoader] Checking type: {type.Name}");
                    var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Static)
                        .Where(m => m.Name.Contains("Load") || m.Name.Contains("Start"))
                        .Take(5)
                        .ToArray();

                    foreach (var m in methods)
                    {
                        var p = m.GetParameters();
                        BannerBrosModule.LogMessage($"[SaveLoader]   {m.Name}({string.Join(", ", p.Select(x => x.ParameterType.Name))})");
                    }
                }
            }

            // Try MBGameManager.StartSavedGame or similar
            var mbGameManagerType = AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(a => { try { return a.GetTypes(); } catch { return Array.Empty<Type>(); } })
                .FirstOrDefault(t => t.Name == "MBGameManager" || t.Name == "SandBoxGameManager");

            if (mbGameManagerType != null)
            {
                BannerBrosModule.LogMessage($"[SaveLoader] Found: {mbGameManagerType.FullName}");

                var startMethods = mbGameManagerType.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic)
                    .Where(m => m.Name.Contains("Start") || m.Name.Contains("Load"))
                    .ToArray();

                foreach (var method in startMethods)
                {
                    var methodParams = method.GetParameters();
                    BannerBrosModule.LogMessage($"[SaveLoader]   {method.Name}({string.Join(", ", methodParams.Select(p => $"{p.ParameterType.Name} {p.Name}"))})");
                }
            }
        }
        catch (Exception ex)
        {
            BannerBrosModule.LogMessage($"[SaveLoader] CampaignGameManager error: {ex.Message}");
        }

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
                // Build args with appropriate values
                var args = new object?[getParams.Length];
                for (int i = 0; i < getParams.Length; i++)
                {
                    var paramType = getParams[i].ParameterType;

                    if (getParams[i].HasDefaultValue)
                    {
                        args[i] = getParams[i].DefaultValue;
                    }
                    else if (paramType == typeof(bool))
                    {
                        args[i] = false;
                    }
                    else if (paramType == typeof(string))
                    {
                        args[i] = "";
                    }
                    else if (paramType.Name.StartsWith("Func`"))
                    {
                        // Create a Func that returns true for all items (accept all saves)
                        // The Func is likely Func<SaveGameFileInfo, bool>
                        try
                        {
                            var genericArgs = paramType.GetGenericArguments();
                            if (genericArgs.Length == 2 && genericArgs[1] == typeof(bool))
                            {
                                // Create: (x) => true
                                var inputType = genericArgs[0];
                                var param = System.Linq.Expressions.Expression.Parameter(inputType, "x");
                                var trueExpr = System.Linq.Expressions.Expression.Constant(true);
                                var lambda = System.Linq.Expressions.Expression.Lambda(paramType, trueExpr, param);
                                args[i] = lambda.Compile();
                                BannerBrosModule.LogMessage($"[SaveLoader] Created Func filter for {inputType.Name}");
                            }
                            else
                            {
                                args[i] = null;
                            }
                        }
                        catch (Exception ex)
                        {
                            BannerBrosModule.LogMessage($"[SaveLoader] Failed to create Func: {ex.Message}");
                            args[i] = null;
                        }
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
            var allNames = new List<string>();

            foreach (var saveFile in saveFilesEnumerable)
            {
                var saveType = saveFile.GetType();
                var nameProperty = saveType.GetProperty("Name");
                var name = nameProperty?.GetValue(saveFile) as string ?? "(unknown)";
                count++;
                allNames.Add(name);

                // Try exact match
                if (name.Equals(saveName, StringComparison.OrdinalIgnoreCase))
                {
                    foundSave = saveFile;
                    BannerBrosModule.LogMessage($"[SaveLoader] Found exact match: {name}");
                }
                // Try partial match (e.g., CoOp_ prefix might be stripped)
                else if (name.Contains(saveName) || saveName.Contains(name))
                {
                    BannerBrosModule.LogMessage($"[SaveLoader] Found partial match: {name}");
                    if (foundSave == null)
                    {
                        foundSave = saveFile;
                    }
                }
            }

            BannerBrosModule.LogMessage($"[SaveLoader] Scanned {count} saves total");
            BannerBrosModule.LogMessage($"[SaveLoader] All save names: [{string.Join(", ", allNames)}]");

            if (foundSave != null)
            {
                return foundSave;
            }

            // File not in game's list - check if it exists on disk
            BannerBrosModule.LogMessage($"[SaveLoader] Save '{saveName}' not in game's list");

            // Check filesystem directly
            var documentsPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            var possibleDirs = new[]
            {
                System.IO.Path.Combine(documentsPath, "Mount and Blade II Bannerlord", "Game Saves", "Native"),
                System.IO.Path.Combine(documentsPath, "Mount and Blade II Bannerlord", "Game Saves"),
            };

            foreach (var dir in possibleDirs)
            {
                if (!System.IO.Directory.Exists(dir)) continue;
                var files = System.IO.Directory.GetFiles(dir, "*.sav");
                BannerBrosModule.LogMessage($"[SaveLoader] Directory {dir} has {files.Length} .sav files:");
                foreach (var f in files.Take(10))
                {
                    var fname = System.IO.Path.GetFileNameWithoutExtension(f);
                    var finfo = new System.IO.FileInfo(f);
                    BannerBrosModule.LogMessage($"[SaveLoader]   {fname} ({finfo.Length} bytes, {finfo.LastWriteTime})");
                }
            }
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
