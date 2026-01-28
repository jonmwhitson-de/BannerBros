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

        // FIRST try to get save from game's internal list (has proper file references)
        var saveFileInfo = GetSaveFileInfo(saveName);

        if (saveFileInfo != null)
        {
            BannerBrosModule.LogMessage($"[SaveLoader] Found save in game's list: {saveFileInfo.GetType().Name}");

            // Try to load with proper SaveGameFileInfo
            if (TryLoadViaSandBoxHelper(saveFileInfo))
            {
                return true;
            }
            if (TryLoadViaMBSaveLoad(saveFileInfo))
            {
                return true;
            }
            if (TryLoadViaGameState(saveFileInfo))
            {
                return true;
            }
            if (TryLoadViaMBGameManager(saveFileInfo))
            {
                return true;
            }

            BannerBrosModule.LogMessage("[SaveLoader] All load methods failed with game's SaveGameFileInfo");
        }
        else
        {
            BannerBrosModule.LogMessage("[SaveLoader] Save not in game's list, trying direct path load...");
        }

        // FALLBACK: Try to load directly by path (creates empty SaveGameFileInfo)
        if (TryLoadDirectlyByPath(savePath))
        {
            return true;
        }

        BannerBrosModule.LogMessage("[SaveLoader] Could not load save by any method!");
        BannerBrosModule.LogMessage("[SaveLoader] Try loading manually from Load Game menu");
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

                // If we created an empty SaveGameFileInfo, try to populate it and load
                if (createdSaveInfo != null)
                {
                    TryPopulateSaveGameFileInfo(createdSaveInfo, savePath, fileInfo);

                    // Try loading even if population failed - the game might handle it
                    BannerBrosModule.LogMessage("[SaveLoader] Attempting load with SaveGameFileInfo...");
                    if (TryLoadViaSandBoxHelper(createdSaveInfo))
                    {
                        return true;
                    }
                    BannerBrosModule.LogMessage("[SaveLoader] Direct SaveGameFileInfo load failed");
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
            object? newestSave = null;
            DateTime newestTime = DateTime.MinValue;
            int count = 0;
            var allNames = new List<string>();

            // Log properties available on first save
            bool loggedProps = false;

            foreach (var saveFile in saveFilesEnumerable)
            {
                var saveType = saveFile.GetType();
                count++;

                // Log ALL members on first save (including private fields)
                if (!loggedProps)
                {
                    loggedProps = true;
                    BannerBrosModule.LogMessage($"[SaveLoader] SaveGameFileInfo ALL members:");

                    // Log properties (public and non-public)
                    var allProps = saveType.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    BannerBrosModule.LogMessage($"[SaveLoader]   Properties ({allProps.Length}):");
                    foreach (var prop in allProps)
                    {
                        try
                        {
                            var val = prop.GetValue(saveFile);
                            BannerBrosModule.LogMessage($"[SaveLoader]     {prop.Name} = {val ?? "(null)"}");
                        }
                        catch (Exception ex)
                        {
                            BannerBrosModule.LogMessage($"[SaveLoader]     {prop.Name} = (error: {ex.Message})");
                        }
                    }

                    // Log fields (including private)
                    var allFields = saveType.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    BannerBrosModule.LogMessage($"[SaveLoader]   Fields ({allFields.Length}):");
                    foreach (var field in allFields)
                    {
                        try
                        {
                            var val = field.GetValue(saveFile);
                            BannerBrosModule.LogMessage($"[SaveLoader]     {field.Name} = {val ?? "(null)"}");
                        }
                        catch (Exception ex)
                        {
                            BannerBrosModule.LogMessage($"[SaveLoader]     {field.Name} = (error: {ex.Message})");
                        }
                    }
                }

                // Get name
                var nameProperty = saveType.GetProperty("Name");
                var name = nameProperty?.GetValue(saveFile) as string ?? "(unknown)";
                allNames.Add(name);

                // Try to get file path
                var filePathProp = saveType.GetProperty("FilePath") ?? saveType.GetProperty("Path") ?? saveType.GetProperty("FullPath");
                var filePath = filePathProp?.GetValue(saveFile) as string;

                // Try to get modification time
                var modTimeProp = saveType.GetProperty("ModificationTime") ?? saveType.GetProperty("LastWriteTime") ?? saveType.GetProperty("SaveTime");
                var modTime = DateTime.MinValue;
                if (modTimeProp != null)
                {
                    var modVal = modTimeProp.GetValue(saveFile);
                    if (modVal is DateTime dt) modTime = dt;
                }

                // Track newest save (we just wrote ours, so it should be newest)
                if (modTime > newestTime)
                {
                    newestTime = modTime;
                    newestSave = saveFile;
                }

                // Try exact name match
                if (name.Equals(saveName, StringComparison.OrdinalIgnoreCase))
                {
                    foundSave = saveFile;
                    BannerBrosModule.LogMessage($"[SaveLoader] Found exact name match: {name}");
                }
                // Try partial name match
                else if (!string.IsNullOrEmpty(name) && name != "(unknown)" &&
                         (name.Contains(saveName) || saveName.Contains(name)))
                {
                    BannerBrosModule.LogMessage($"[SaveLoader] Found partial name match: {name}");
                    if (foundSave == null) foundSave = saveFile;
                }
                // Try path match
                else if (!string.IsNullOrEmpty(filePath) && filePath.Contains(saveName))
                {
                    BannerBrosModule.LogMessage($"[SaveLoader] Found path match: {filePath}");
                    if (foundSave == null) foundSave = saveFile;
                }
            }

            BannerBrosModule.LogMessage($"[SaveLoader] Scanned {count} saves total");
            BannerBrosModule.LogMessage($"[SaveLoader] All save names: [{string.Join(", ", allNames)}]");

            if (foundSave != null)
            {
                return foundSave;
            }

            // If no match found but we have saves, try the newest one (we just wrote it)
            if (newestSave != null && newestTime > DateTime.Now.AddMinutes(-2))
            {
                BannerBrosModule.LogMessage($"[SaveLoader] Using newest save (modified {newestTime})");
                return newestSave;
            }

            // FALLBACK: Try to find by iterating and checking all fields/properties for our filename
            BannerBrosModule.LogMessage($"[SaveLoader] Trying to find save by deep inspection...");
            foreach (var saveFile in saveFilesEnumerable)
            {
                var saveType = saveFile.GetType();

                // Check all fields and properties for our save name
                foreach (var field in saveType.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    try
                    {
                        var val = field.GetValue(saveFile)?.ToString() ?? "";
                        if (val.Contains(saveName) || val.Contains("CoOp_"))
                        {
                            BannerBrosModule.LogMessage($"[SaveLoader] Found via field {field.Name} = {val}");
                            return saveFile;
                        }
                    }
                    catch { }
                }

                foreach (var prop in saveType.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    try
                    {
                        var val = prop.GetValue(saveFile)?.ToString() ?? "";
                        if (val.Contains(saveName) || val.Contains("CoOp_"))
                        {
                            BannerBrosModule.LogMessage($"[SaveLoader] Found via property {prop.Name} = {val}");
                            return saveFile;
                        }
                    }
                    catch { }
                }
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
            BannerBrosModule.LogMessage("[SaveLoader] Trying SandBoxSaveHelper methods...");

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

            // Log ALL methods for debugging
            var allMethods = helperType.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);
            BannerBrosModule.LogMessage($"[SaveLoader] SandBoxSaveHelper ALL methods ({allMethods.Length}):");
            foreach (var m in allMethods.Where(x => x.DeclaringType == helperType))
            {
                var p = m.GetParameters();
                BannerBrosModule.LogMessage($"[SaveLoader]   {m.Name}({string.Join(", ", p.Select(x => x.ParameterType.Name))})");
            }

            // Check if save is disabled first
            var isDisabledMethod = helperType.GetMethod("GetIsDisabledWithReason", BindingFlags.Public | BindingFlags.Static);
            if (isDisabledMethod != null)
            {
                try
                {
                    var disabledParams = isDisabledMethod.GetParameters();
                    BannerBrosModule.LogMessage($"[SaveLoader] GetIsDisabledWithReason params: {string.Join(", ", disabledParams.Select(p => p.ParameterType.Name))}");

                    // Call with out parameter
                    var args = new object?[disabledParams.Length];
                    args[0] = saveFileInfo;
                    for (int i = 1; i < disabledParams.Length; i++)
                    {
                        args[i] = null; // Out parameters
                    }

                    var isDisabled = isDisabledMethod.Invoke(null, args);
                    BannerBrosModule.LogMessage($"[SaveLoader] GetIsDisabledWithReason returned: {isDisabled}");

                    // Check the out parameter (reason text)
                    if (args.Length > 1 && args[1] != null)
                    {
                        BannerBrosModule.LogMessage($"[SaveLoader] Disabled reason: {args[1]}");
                    }
                }
                catch (Exception ex)
                {
                    BannerBrosModule.LogMessage($"[SaveLoader] GetIsDisabledWithReason error: {ex.Message}");
                }
            }

            // Try TryLoadSave FIRST - it handles module mismatch dialogs properly
            // LoadGameAction skips dialogs and may crash on module mismatch
            var methodNames = new[] { "TryLoadSave", "LoadGameAction", "LoadSaveGame" };
            var methods = allMethods
                .Where(m => methodNames.Contains(m.Name))
                .OrderBy(m => Array.IndexOf(methodNames, m.Name)) // Priority order
                .ToList();

            BannerBrosModule.LogMessage($"[SaveLoader] Found {methods.Count} load methods matching our targets");

            foreach (var method in methods)
            {
                var parameters = method.GetParameters();
                var paramStr = string.Join(", ", parameters.Select(p => p.ParameterType.Name));
                BannerBrosModule.LogMessage($"[SaveLoader] Trying {method.Name}({paramStr})...");

                // TryLoadSave handles everything including dialogs - use simple callbacks
                // LoadGameAction needs us to manually start the game
                bool isTryLoadSave = method.Name == "TryLoadSave";

                try
                {
                    object? result = null;
                    if (parameters.Length == 1)
                    {
                        result = method.Invoke(null, new object[] { saveFileInfo });
                        BannerBrosModule.LogMessage($"[SaveLoader] {method.Name}(1 param) called! Result: {result}");
                        return true;
                    }
                    else if (parameters.Length == 2)
                    {
                        // Second param is usually Action<LoadGameResult> callback
                        var callback = isTryLoadSave
                            ? CreateSimpleLogCallback(parameters[1].ParameterType, "TryLoadSave callback")
                            : CreateLoadResultCallback(parameters[1].ParameterType);
                        result = method.Invoke(null, new object?[] { saveFileInfo, callback });
                        BannerBrosModule.LogMessage($"[SaveLoader] {method.Name}(2 params) called! Result: {result}");
                        return true;
                    }
                    else if (parameters.Length == 3)
                    {
                        // TryLoadSave(SaveGameFileInfo, Action<LoadResult>, Action) or similar
                        object? callback1;
                        Action callback2;

                        if (isTryLoadSave)
                        {
                            // TryLoadSave handles the game start - just log
                            callback1 = CreateSimpleLogCallback(parameters[1].ParameterType, "TryLoadSave result");
                            callback2 = () => BannerBrosModule.LogMessage("[SaveLoader] TryLoadSave OnComplete - game should be starting!");
                        }
                        else
                        {
                            // LoadGameAction - we need to start the game manually
                            callback1 = CreateLoadResultCallback(parameters[1].ParameterType);
                            callback2 = () => BannerBrosModule.LogMessage("[SaveLoader] LoadGameAction OnComplete callback fired!");
                        }

                        BannerBrosModule.LogMessage($"[SaveLoader] Calling {method.Name} with callbacks...");
                        result = method.Invoke(null, new object?[] { saveFileInfo, callback1, callback2 });
                        BannerBrosModule.LogMessage($"[SaveLoader] {method.Name}(3 params) called! Result: {result}");

                        // Don't return immediately - check if it actually worked
                        if (result is bool b && !b)
                        {
                            BannerBrosModule.LogMessage($"[SaveLoader] {method.Name} returned false, trying next method...");
                            continue;
                        }
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    BannerBrosModule.LogMessage($"[SaveLoader] {method.Name} failed: {ex.InnerException?.Message ?? ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            BannerBrosModule.LogMessage($"[SaveLoader] SandBoxHelper error: {ex.Message}");
        }

        return false;
    }

    /// <summary>
    /// Creates a simple callback that just logs - doesn't try to start the game.
    /// Used for TryLoadSave which handles game start internally.
    /// </summary>
    private static object? CreateSimpleLogCallback(Type parameterType, string context)
    {
        try
        {
            if (!parameterType.IsGenericType || parameterType.GetGenericTypeDefinition() != typeof(Action<>))
            {
                return null;
            }

            var genericArg = parameterType.GetGenericArguments()[0];

            // Create: (result) => BannerBrosModule.LogMessage($"[SaveLoader] {context}: {result}")
            var param = System.Linq.Expressions.Expression.Parameter(genericArg, "result");
            var logMethod = typeof(BannerBrosModule).GetMethod("LogMessage", BindingFlags.Public | BindingFlags.Static);

            var prefix = System.Linq.Expressions.Expression.Constant($"[SaveLoader] {context}: ");
            var toStringMethod = typeof(object).GetMethod("ToString");
            var resultToString = System.Linq.Expressions.Expression.Call(
                System.Linq.Expressions.Expression.Convert(param, typeof(object)),
                toStringMethod!);
            var concatMethod = typeof(string).GetMethod("Concat", new[] { typeof(string), typeof(string) });
            var message = System.Linq.Expressions.Expression.Call(concatMethod!, prefix, resultToString);
            var callLog = System.Linq.Expressions.Expression.Call(logMethod!, message);

            var lambda = System.Linq.Expressions.Expression.Lambda(parameterType, callLog, param);
            return lambda.Compile();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Creates a callback delegate for load result types (Action&lt;T&gt;).
    /// </summary>
    private static object? CreateLoadResultCallback(Type parameterType)
    {
        try
        {
            // Check if it's an Action<T> type
            if (!parameterType.IsGenericType || parameterType.GetGenericTypeDefinition() != typeof(Action<>))
            {
                BannerBrosModule.LogMessage($"[SaveLoader] Parameter is not Action<T>: {parameterType.Name}");
                return null;
            }

            var genericArg = parameterType.GetGenericArguments()[0];
            BannerBrosModule.LogMessage($"[SaveLoader] Creating callback for Action<{genericArg.Name}>");

            // Create a delegate that calls our detailed logging method
            // We use a wrapper that takes object and casts it
            var callbackMethod = typeof(SaveGameLoader).GetMethod("LogLoadResult", BindingFlags.NonPublic | BindingFlags.Static);
            if (callbackMethod == null)
            {
                BannerBrosModule.LogMessage("[SaveLoader] LogLoadResult method not found");
                return null;
            }

            // Build: LogLoadResult(result)
            var param = System.Linq.Expressions.Expression.Parameter(genericArg, "result");
            var convertedParam = System.Linq.Expressions.Expression.Convert(param, typeof(object));
            var callExpr = System.Linq.Expressions.Expression.Call(callbackMethod, convertedParam);

            var lambda = System.Linq.Expressions.Expression.Lambda(parameterType, callExpr, param);
            var callback = lambda.Compile();

            BannerBrosModule.LogMessage($"[SaveLoader] Created callback delegate successfully");
            return callback;
        }
        catch (Exception ex)
        {
            BannerBrosModule.LogMessage($"[SaveLoader] Failed to create callback: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Logs detailed information about a LoadResult object.
    /// </summary>
    private static void LogLoadResult(object result)
    {
        try
        {
            BannerBrosModule.LogMessage($"[SaveLoader] === LOAD RESULT RECEIVED ===");
            BannerBrosModule.LogMessage($"[SaveLoader] Type: {result?.GetType().FullName ?? "(null)"}");

            if (result == null)
            {
                BannerBrosModule.LogMessage("[SaveLoader] Result is null!");
                return;
            }

            var type = result.GetType();

            // Log all properties
            var props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);
            BannerBrosModule.LogMessage($"[SaveLoader] Properties ({props.Length}):");
            foreach (var prop in props)
            {
                try
                {
                    var val = prop.GetValue(result);
                    BannerBrosModule.LogMessage($"[SaveLoader]   {prop.Name} = {val ?? "(null)"}");
                }
                catch (Exception ex)
                {
                    BannerBrosModule.LogMessage($"[SaveLoader]   {prop.Name} = (error: {ex.Message})");
                }
            }

            // Log all fields
            var fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            BannerBrosModule.LogMessage($"[SaveLoader] Fields ({fields.Length}):");
            foreach (var field in fields)
            {
                try
                {
                    var val = field.GetValue(result);
                    BannerBrosModule.LogMessage($"[SaveLoader]   {field.Name} = {val ?? "(null)"}");
                }
                catch (Exception ex)
                {
                    BannerBrosModule.LogMessage($"[SaveLoader]   {field.Name} = (error: {ex.Message})");
                }
            }

            // Check for common success/error indicators
            var successProp = type.GetProperty("Successful") ?? type.GetProperty("Success") ?? type.GetProperty("IsSuccess");
            if (successProp != null)
            {
                var success = successProp.GetValue(result);
                BannerBrosModule.LogMessage($"[SaveLoader] *** SUCCESS STATUS: {success} ***");
            }

            var errorProp = type.GetProperty("ErrorMessage") ?? type.GetProperty("Error") ?? type.GetProperty("Message");
            if (errorProp != null)
            {
                var error = errorProp.GetValue(result);
                if (error != null && !string.IsNullOrEmpty(error.ToString()))
                {
                    BannerBrosModule.LogMessage($"[SaveLoader] *** ERROR: {error} ***");
                }
            }

            // If successful, try to start the loaded game!
            if (successProp != null && (bool)(successProp.GetValue(result) ?? false))
            {
                BannerBrosModule.LogMessage($"[SaveLoader] Load successful! Attempting to start the game...");

                // BEST APPROACH: Create SandBoxGameManager with LoadResult constructor
                if (TryStartWithLoadResultConstructor(result))
                {
                    BannerBrosModule.LogMessage($"[SaveLoader] Game start initiated via LoadResult constructor!");
                    return;
                }

                // Fallback: Try manual initialization
                TryStartLoadedGame(result);
            }

            BannerBrosModule.LogMessage($"[SaveLoader] === END LOAD RESULT ===");
        }
        catch (Exception ex)
        {
            BannerBrosModule.LogMessage($"[SaveLoader] Error logging LoadResult: {ex.Message}");
        }
    }

    /// <summary>
    /// Creates SandBoxGameManager with LoadResult constructor and starts the game.
    /// This is the cleanest approach - lets the game handle all initialization.
    /// </summary>
    private static bool TryStartWithLoadResultConstructor(object loadResult)
    {
        try
        {
            BannerBrosModule.LogMessage("[SaveLoader] Trying SandBoxGameManager(LoadResult) constructor...");

            var sandboxAssembly = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "SandBox");

            if (sandboxAssembly == null)
            {
                BannerBrosModule.LogMessage("[SaveLoader] SandBox assembly not found");
                return false;
            }

            var gameManagerType = sandboxAssembly.GetType("SandBox.SandBoxGameManager");
            if (gameManagerType == null)
            {
                BannerBrosModule.LogMessage("[SaveLoader] SandBoxGameManager not found");
                return false;
            }

            // Find the constructor that takes LoadResult
            var loadResultType = loadResult.GetType();
            var ctor = gameManagerType.GetConstructor(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                null, new[] { loadResultType }, null);

            if (ctor == null)
            {
                BannerBrosModule.LogMessage("[SaveLoader] Ctor(LoadResult) not found, trying base type...");

                // Try with base type (LoadResult might be a derived type)
                var baseType = loadResultType.BaseType;
                while (baseType != null && ctor == null)
                {
                    ctor = gameManagerType.GetConstructor(
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                        null, new[] { baseType }, null);
                    baseType = baseType.BaseType;
                }
            }

            if (ctor == null)
            {
                BannerBrosModule.LogMessage("[SaveLoader] Could not find LoadResult constructor");
                return false;
            }

            BannerBrosModule.LogMessage($"[SaveLoader] Found constructor: {ctor}");
            BannerBrosModule.LogMessage("[SaveLoader] Creating SandBoxGameManager with LoadResult...");

            var sandBoxGameManager = ctor.Invoke(new[] { loadResult });

            if (sandBoxGameManager == null)
            {
                BannerBrosModule.LogMessage("[SaveLoader] Failed to create SandBoxGameManager");
                return false;
            }

            BannerBrosModule.LogMessage("[SaveLoader] SandBoxGameManager created successfully!");

            // Now call MBGameManager.StartNewGame(sandBoxGameManager)
            var mbGameManagerType = typeof(MBSubModuleBase).Assembly.GetType("TaleWorlds.MountAndBlade.MBGameManager");
            if (mbGameManagerType == null)
            {
                mbGameManagerType = AppDomain.CurrentDomain.GetAssemblies()
                    .SelectMany(a => { try { return a.GetTypes(); } catch { return Array.Empty<Type>(); } })
                    .FirstOrDefault(t => t.Name == "MBGameManager");
            }

            if (mbGameManagerType == null)
            {
                BannerBrosModule.LogMessage("[SaveLoader] MBGameManager not found");
                return false;
            }

            var startNewGameMethod = mbGameManagerType.GetMethod("StartNewGame",
                BindingFlags.Public | BindingFlags.Static,
                null, new[] { mbGameManagerType }, null);

            if (startNewGameMethod == null)
            {
                BannerBrosModule.LogMessage("[SaveLoader] StartNewGame(MBGameManager) not found");

                // List all static methods
                var staticMethods = mbGameManagerType.GetMethods(BindingFlags.Public | BindingFlags.Static);
                BannerBrosModule.LogMessage($"[SaveLoader] MBGameManager static methods:");
                foreach (var m in staticMethods.Where(x => x.DeclaringType == mbGameManagerType))
                {
                    BannerBrosModule.LogMessage($"[SaveLoader]   {m.Name}({string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name))})");
                }
                return false;
            }

            BannerBrosModule.LogMessage("[SaveLoader] Calling MBGameManager.StartNewGame(SandBoxGameManager)...");
            startNewGameMethod.Invoke(null, new[] { sandBoxGameManager });
            BannerBrosModule.LogMessage("[SaveLoader] *** GAME STARTING! ***");

            return true;
        }
        catch (TargetInvocationException tie)
        {
            var inner = tie.InnerException;
            BannerBrosModule.LogMessage($"[SaveLoader] TryStartWithLoadResultConstructor FAILED: {inner?.GetType().Name}: {inner?.Message}");
            BannerBrosModule.LogMessage($"[SaveLoader] Inner stack: {inner?.StackTrace?.Split('\n').FirstOrDefault()}");
            return false;
        }
        catch (Exception ex)
        {
            BannerBrosModule.LogMessage($"[SaveLoader] TryStartWithLoadResultConstructor error: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Attempts to start the game with the loaded data from LoadResult.
    /// </summary>
    private static void TryStartLoadedGame(object loadResult)
    {
        try
        {
            var type = loadResult.GetType();

            // Get the LoadCallbackInitializator
            var initializatorField = type.GetField("_loadCallbackInitializator", BindingFlags.NonPublic | BindingFlags.Instance);
            if (initializatorField != null)
            {
                var initializator = initializatorField.GetValue(loadResult);
                if (initializator != null)
                {
                    BannerBrosModule.LogMessage($"[SaveLoader] Found LoadCallbackInitializator: {initializator.GetType().FullName}");

                    // Log all methods on the initializator
                    var initType = initializator.GetType();
                    var initMethods = initType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    BannerBrosModule.LogMessage($"[SaveLoader] LoadCallbackInitializator methods ({initMethods.Length}):");
                    foreach (var m in initMethods.Where(x => x.DeclaringType == initType || x.DeclaringType?.Name?.Contains("Load") == true))
                    {
                        var p = m.GetParameters();
                        BannerBrosModule.LogMessage($"[SaveLoader]   {m.Name}({string.Join(", ", p.Select(x => x.ParameterType.Name))})");
                    }

                    // Call InitializeObjects first - this is required!
                    var initObjectsMethod = initType.GetMethod("InitializeObjects", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (initObjectsMethod != null)
                    {
                        try
                        {
                            BannerBrosModule.LogMessage("[SaveLoader] Calling InitializeObjects()...");
                            initObjectsMethod.Invoke(initializator, null);
                            BannerBrosModule.LogMessage("[SaveLoader] InitializeObjects() completed!");
                        }
                        catch (TargetInvocationException tie)
                        {
                            var inner = tie.InnerException;
                            BannerBrosModule.LogMessage($"[SaveLoader] InitializeObjects() FAILED: {inner?.GetType().Name}: {inner?.Message}");
                            BannerBrosModule.LogMessage($"[SaveLoader] Inner stack: {inner?.StackTrace?.Split('\n').FirstOrDefault()}");
                            // Continue anyway - maybe we can still start the game
                        }
                    }

                    // Then call AfterInitializeObjects
                    var afterInitMethod = initType.GetMethod("AfterInitializeObjects", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (afterInitMethod != null)
                    {
                        try
                        {
                            BannerBrosModule.LogMessage("[SaveLoader] Calling AfterInitializeObjects()...");
                            afterInitMethod.Invoke(initializator, null);
                            BannerBrosModule.LogMessage("[SaveLoader] AfterInitializeObjects() completed!");
                        }
                        catch (TargetInvocationException tie)
                        {
                            var inner = tie.InnerException;
                            BannerBrosModule.LogMessage($"[SaveLoader] AfterInitializeObjects() FAILED: {inner?.GetType().Name}: {inner?.Message}");
                        }
                    }
                }
            }

            // Get the Root (loaded Game object)
            var rootProp = type.GetProperty("Root");
            if (rootProp != null)
            {
                var loadedGame = rootProp.GetValue(loadResult);
                if (loadedGame != null)
                {
                    BannerBrosModule.LogMessage($"[SaveLoader] Got loaded Game object: {loadedGame.GetType().FullName}");

                    // Log methods on the Game object
                    var gameType = loadedGame.GetType();
                    var gameMethods = gameType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                        .Where(m => m.Name.Contains("Start") || m.Name.Contains("Initialize") || m.Name.Contains("Load") || m.Name.Contains("Activate"))
                        .Take(10)
                        .ToArray();
                    BannerBrosModule.LogMessage($"[SaveLoader] Game relevant methods:");
                    foreach (var m in gameMethods)
                    {
                        BannerBrosModule.LogMessage($"[SaveLoader]   {m.Name}({string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name))})");
                    }

                    // Try to start the game using MBGameManager
                    TryStartGameViaMBGameManager(loadedGame);
                }
            }
        }
        catch (Exception ex)
        {
            BannerBrosModule.LogMessage($"[SaveLoader] TryStartLoadedGame error: {ex.Message}");
            BannerBrosModule.LogMessage($"[SaveLoader] Stack: {ex.StackTrace}");
        }
    }

    /// <summary>
    /// Tries to start the game using MBGameManager or similar.
    /// </summary>
    private static void TryStartGameViaMBGameManager(object loadedGame)
    {
        try
        {
            BannerBrosModule.LogMessage("[SaveLoader] Looking for ways to start the loaded game...");

            // Try SandBoxGameManager
            var sandboxAssembly = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "SandBox");

            if (sandboxAssembly != null)
            {
                // Look for SandBoxGameManager
                var gameManagerType = sandboxAssembly.GetType("SandBox.SandBoxGameManager");
                if (gameManagerType != null)
                {
                    BannerBrosModule.LogMessage($"[SaveLoader] Found SandBoxGameManager: {gameManagerType.FullName}");

                    // Look for StartGame or similar methods
                    var startMethods = gameManagerType.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Instance)
                        .Where(m => m.Name.Contains("Start") || m.Name.Contains("Load") || m.Name.Contains("Begin"))
                        .ToArray();

                    foreach (var m in startMethods)
                    {
                        BannerBrosModule.LogMessage($"[SaveLoader]   {m.Name}({string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name))})");
                    }

                    // Create a SandBoxGameManager instance for loading a saved game
                    BannerBrosModule.LogMessage("[SaveLoader] Looking for SandBoxGameManager constructor...");
                    var ctors = gameManagerType.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    foreach (var ctor in ctors)
                    {
                        var ctorParams = ctor.GetParameters();
                        BannerBrosModule.LogMessage($"[SaveLoader]   Ctor({string.Join(", ", ctorParams.Select(p => $"{p.ParameterType.Name} {p.Name}"))})");
                    }

                    // Try to find a ctor that takes SaveGameFileInfo or Game
                    object? sandBoxGameManager = null;

                    // Try parameterless ctor
                    var parameterlessCtor = ctors.FirstOrDefault(c => c.GetParameters().Length == 0);
                    if (parameterlessCtor != null)
                    {
                        BannerBrosModule.LogMessage("[SaveLoader] Creating SandBoxGameManager with parameterless ctor...");
                        sandBoxGameManager = parameterlessCtor.Invoke(null);
                        BannerBrosModule.LogMessage("[SaveLoader] SandBoxGameManager created!");

                        // Set LoadingSavedGame = true
                        var loadingSavedGameProp = gameManagerType.GetProperty("LoadingSavedGame");
                        if (loadingSavedGameProp != null && loadingSavedGameProp.CanWrite)
                        {
                            loadingSavedGameProp.SetValue(sandBoxGameManager, true);
                            BannerBrosModule.LogMessage("[SaveLoader] Set LoadingSavedGame = true");
                        }
                    }

                    // Try ctor that takes SaveGameFileInfo
                    if (sandBoxGameManager == null)
                    {
                        var saveGameCtor = ctors.FirstOrDefault(c =>
                        {
                            var p = c.GetParameters();
                            return p.Length == 1 && p[0].ParameterType.Name == "SaveGameFileInfo";
                        });

                        if (saveGameCtor != null)
                        {
                            BannerBrosModule.LogMessage("[SaveLoader] Found ctor that takes SaveGameFileInfo - need to get our save...");
                        }
                    }

                    // Now try to start the game with MBGameManager.StartNewGame
                    if (sandBoxGameManager != null)
                    {
                        TryStartWithMBGameManager(sandBoxGameManager, loadedGame);
                    }
                }
            }

            // Try MBGameManager directly
            var mbGameManagerType = typeof(MBSubModuleBase).Assembly.GetType("TaleWorlds.MountAndBlade.MBGameManager");
            if (mbGameManagerType == null)
            {
                mbGameManagerType = AppDomain.CurrentDomain.GetAssemblies()
                    .SelectMany(a => { try { return a.GetTypes(); } catch { return Array.Empty<Type>(); } })
                    .FirstOrDefault(t => t.Name == "MBGameManager");
            }

            if (mbGameManagerType != null)
            {
                BannerBrosModule.LogMessage($"[SaveLoader] Found MBGameManager: {mbGameManagerType.FullName}");

                var methods = mbGameManagerType.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Instance)
                    .Where(m => m.Name.Contains("Start") || m.Name.Contains("Load") || m.Name.Contains("Begin"))
                    .Take(10)
                    .ToArray();

                foreach (var m in methods)
                {
                    BannerBrosModule.LogMessage($"[SaveLoader]   {m.Name}({string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name))})");
                }
            }
        }
        catch (Exception ex)
        {
            BannerBrosModule.LogMessage($"[SaveLoader] TryStartGameViaMBGameManager error: {ex.Message}");
            BannerBrosModule.LogMessage($"[SaveLoader] Stack: {ex.StackTrace}");
        }
    }

    /// <summary>
    /// Tries to start the game using MBGameManager.StartNewGame
    /// </summary>
    private static void TryStartWithMBGameManager(object gameManager, object loadedGame)
    {
        try
        {
            var mbGameManagerType = typeof(MBSubModuleBase).Assembly.GetType("TaleWorlds.MountAndBlade.MBGameManager");
            if (mbGameManagerType == null)
            {
                mbGameManagerType = AppDomain.CurrentDomain.GetAssemblies()
                    .SelectMany(a => { try { return a.GetTypes(); } catch { return Array.Empty<Type>(); } })
                    .FirstOrDefault(t => t.Name == "MBGameManager");
            }

            if (mbGameManagerType == null)
            {
                BannerBrosModule.LogMessage("[SaveLoader] MBGameManager not found!");
                return;
            }

            // Try StartNewGame(MBGameManager)
            var startNewGameMethod = mbGameManagerType.GetMethod("StartNewGame",
                BindingFlags.Public | BindingFlags.Static,
                null, new[] { mbGameManagerType }, null);

            if (startNewGameMethod != null)
            {
                BannerBrosModule.LogMessage("[SaveLoader] Calling MBGameManager.StartNewGame(SandBoxGameManager)...");
                startNewGameMethod.Invoke(null, new[] { gameManager });
                BannerBrosModule.LogMessage("[SaveLoader] StartNewGame called! Game should be starting...");
                return;
            }
            else
            {
                BannerBrosModule.LogMessage("[SaveLoader] StartNewGame(MBGameManager) not found");

                // List all static methods
                var staticMethods = mbGameManagerType.GetMethods(BindingFlags.Public | BindingFlags.Static);
                BannerBrosModule.LogMessage($"[SaveLoader] MBGameManager static methods:");
                foreach (var m in staticMethods.Where(x => x.DeclaringType == mbGameManagerType))
                {
                    BannerBrosModule.LogMessage($"[SaveLoader]   {m.Name}({string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name))})");
                }
            }
        }
        catch (Exception ex)
        {
            BannerBrosModule.LogMessage($"[SaveLoader] TryStartWithMBGameManager error: {ex.Message}");
            BannerBrosModule.LogMessage($"[SaveLoader] Stack: {ex.StackTrace}");
        }
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
