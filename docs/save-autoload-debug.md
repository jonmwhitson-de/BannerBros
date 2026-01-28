# Save Auto-Load Debug Log

## Issue
Client receives save file from host but cannot auto-load it into the game. The game's internal save list doesn't recognize the newly written file.

---

## Attempts

### Attempt 1: Basic SaveGameLoader with MBSaveLoad.GetSaveFiles
**Date:** 2026-01-28
**Commit:** Initial implementation
**Result:** ❌ Failed
**Details:**
- Used `MBSaveLoad.GetSaveFiles()` to get list of saves
- Searched for our save by name
- Save not found in list (list was cached from game start)

---

### Attempt 2: Add ForceRefreshSaveList
**Date:** 2026-01-28
**Commit:** d7b6bc3
**Result:** ❌ Failed
**Details:**
- Added `ForceRefreshSaveList()` method
- Tried calling `MBSaveLoad.RefreshSaveFiles()`, `Initialize()`, `InitializeSaveSystem()`
- Refresh methods either don't exist or don't update the internal cache
- Log showed: `Called Initialize(defaults)` but save still not in list

---

### Attempt 3: Create SaveGameFileInfo from path/FileInfo
**Date:** 2026-01-28
**Commit:** d7b6bc3
**Result:** ❌ Failed
**Details:**
- Tried to find constructor that takes `string` or `FileInfo` parameter
- SaveGameFileInfo only has parameterless constructor: `Ctor: ()`
- Cannot create SaveGameFileInfo directly from file path

---

### Attempt 4: Populate empty SaveGameFileInfo via reflection
**Date:** 2026-01-28
**Commit:** db3d3d2
**Result:** ❌ Failed
**Details:**
- Created empty SaveGameFileInfo with parameterless constructor
- Tried to set Name, FilePath, Path properties via reflection
- **SaveGameFileInfo has NO public properties** (log showed empty properties list)
- Tried setting backing fields - none found with path/file in name
- Log showed: `SaveGameFileInfo state after population:` followed by nothing

---

### Attempt 5: Fix save file location (Native → Game Saves)
**Date:** 2026-01-28
**Commit:** ac9496b
**Result:** ❌ Partial success - file detected but not found by name
**Details:**
- Changed save location from `Game Saves\Native\` to `Game Saves\`
- **File IS now detected** - game shows 7 saves (was 6)
- BUT all save names return `(unknown)` - Name property doesn't work
- `TryLoadViaSandBoxHelper` never called because `TryPopulateSaveGameFileInfo` returned false
- Need to match by path instead of name

**Log evidence:**
```
[SaveLoader] Scanned 7 saves total  ← Was 6, now 7 - our file is detected!
[SaveLoader] All save names: [(unknown), (unknown), (unknown), (unknown), (unknown), (unknown), (unknown)]
[SaveLoader] Directory ...\Game Saves has 7 .sav files:
[SaveLoader]   CoOp_saveauto2 (5916491 bytes, 01/28/2026 13:59:56)  ← Our file!
```

---

### Attempt 6: Match by path instead of name, always try load methods
**Date:** 2026-01-28
**Commit:** df348bf
**Result:** ❌ Partial - TryLoadSave called but game crashed
**Details:**
- TryLoadSave was called with empty SaveGameFileInfo
- Module mismatch dialog appeared (game is trying to load!)
- User clicked "Yes" to load with different modules
- Game crashed ("The application faced a problem...")
- Root cause: Empty SaveGameFileInfo has no file reference

**Log evidence:**
```
[SaveLoader] Trying TryLoadSave(SaveGameFileInfo, Action`1, Action)
[SaveLoader] TryLoadSave(3 params) called!
[AutoLoad] Save loading initiated!
```

**Also noticed:**
- Double messages appearing (OnSaveFileReady triggered twice on rejoin)
- Progress went to 200%+ (chunks received twice)

---

### Attempt 7: Get save from game's list FIRST, then load
**Date:** 2026-01-28
**Commit:** bca67e7
**Result:** ❌ Failed - still crashed, save not found in list
**Details:**
- Changed order to try game's list first
- BUT save still not found - all 7 saves have Name = "(unknown)"
- Fell back to empty SaveGameFileInfo, crashed again
- SaveGameFileInfo has NO public properties at all

**Log evidence:**
```
[SaveLoader] SaveGameFileInfo properties available:
[SaveLoader] Scanned 7 saves total  ← Nothing logged between these
[SaveLoader] All save names: [(unknown) x7]
[SaveLoader] Save not in game's list, trying direct path load...
```

---

### Attempt 8: Log ALL members including private fields
**Date:** 2026-01-28
**Commit:** bac0757
**Result:** ❌ Found save, TryLoadSave called, but nothing happened
**Details:**
- Successfully found SaveGameFileInfo structure:
  - 0 properties
  - 3 fields: `Name`, `MetaData`, `IsCorrupted`
- Found our save via deep inspection: `Name = CoOp_saveauto2`
- Called TryLoadSave with proper SaveGameFileInfo
- But no module dialog appeared, no load happened
- TryLoadSave might have precondition checks that silently fail

**Log evidence:**
```
[SaveLoader] SaveGameFileInfo ALL members:
[SaveLoader]   Properties (0):
[SaveLoader]   Fields (3):
[SaveLoader]     Name = CoOp_saveauto2
[SaveLoader]     MetaData = TaleWorlds.SaveSystem.MetaData
[SaveLoader]     IsCorrupted = False
[SaveLoader] Found via field Name = CoOp_saveauto2
[SaveLoader] Found save in game's list: SaveGameFileInfo
[SaveLoader] Trying TryLoadSave(SaveGameFileInfo, Action`1, Action)
[SaveLoader] TryLoadSave(3 params) called!
[SaveLoader] Save loading initiated!
... (nothing happens, just state syncs)
```

---

### Attempt 9: Try LoadGameAction first, check return values
**Date:** 2026-01-28
**Commit:** bac0757
**Result:** ❌ Failed - TryLoadSave called but does nothing
**Details:**
- Log showed only `TryLoadSave` method found (not LoadGameAction)
- TryLoadSave was called successfully (no errors)
- Return value was empty (void method)
- No module dialog appeared, nothing happened
- Passing `null` for callback parameters may prevent load

**Log evidence:**
```
[SaveLoader] Found 1 load methods
[SaveLoader]   Available: TryLoadSave(SaveGameFileInfo, Action`1, Action)
[SaveLoader] Trying TryLoadSave(SaveGameFileInfo, Action`1, Action)...
[SaveLoader] TryLoadSave(3 params) called! Result:
[SaveLoader] Save loading initiated!
... (nothing happens)
```

---

### Attempt 10: Pass actual callbacks + check GetIsDisabledWithReason
**Date:** 2026-01-28
**Commit:** b736be7
**Result:** ⚠️ Partial - LoadGameAction called, callback fired, but no game load
**Details:**
- Confirmed all 7 SandBoxSaveHelper methods available (including LoadGameAction!)
- `GetIsDisabledWithReason` returned `False` - save is NOT disabled
- `LoadGameAction` was called successfully
- **Callback WAS fired!** - received `TaleWorlds.SaveSystem.Load.LoadResult`
- But we only logged the type name, not the contents

**Log evidence:**
```
[SaveLoader] SandBoxSaveHelper ALL methods (7):
[SaveLoader]   add_OnStateChange(Action`1)
[SaveLoader]   remove_OnStateChange(Action`1)
[SaveLoader]   TryLoadSave(SaveGameFileInfo, Action`1, Action)
[SaveLoader]   CheckMetaDataCompatibilityErrors(MetaData)
[SaveLoader]   GetIsDisabledWithReason(SaveGameFileInfo, TextObject&)
[SaveLoader]   GetModuleNameFromModuleId(String)
[SaveLoader]   LoadGameAction(SaveGameFileInfo, Action`1, Action)
[SaveLoader] GetIsDisabledWithReason returned: False
[SaveLoader] Found 2 load methods matching our targets
[SaveLoader] Trying LoadGameAction(SaveGameFileInfo, Action`1, Action)...
[SaveLoader] Creating callback for Action<LoadResult>
[SaveLoader] Created callback delegate successfully
[SaveLoader] Calling with actual callbacks...
[SaveLoader] Load callback received: TaleWorlds.SaveSystem.Load.LoadResult
[SaveLoader] LoadGameAction(3 params) called! Result:
[AutoLoad] Save loading initiated!
... (nothing happens, state syncs continue)
```

---

### Attempt 11: Inspect LoadResult contents
**Date:** 2026-01-28
**Commit:** 9fef9df
**Result:** ✅ Major discovery - Load SUCCEEDED but game not started!
**Details:**
- `Successful = True` - The save data loaded successfully!
- `Errors = (null)` - No errors
- `Root = TaleWorlds.Core.Game` - The loaded game object exists in memory
- `_loadCallbackInitializator` - Key object for finalizing the load

**Log evidence:**
```
[SaveLoader] Properties (4):
[SaveLoader]   Root = TaleWorlds.Core.Game
[SaveLoader]   Successful = True
[SaveLoader]   Errors = (null)
[SaveLoader]   MetaData = TaleWorlds.SaveSystem.MetaData
[SaveLoader] Fields (5):
[SaveLoader]   _loadCallbackInitializator = TaleWorlds.SaveSystem.Load.LoadCallbackInitializator
[SaveLoader] *** SUCCESS STATUS: True ***
```

**Conclusion:** LoadGameAction only loads data into memory - need separate step to START the game!

---

### Attempt 12: Start the loaded game
**Date:** 2026-01-28
**Commit:** (pending)
**Result:** 🔄 Pending
**Details:**
- After successful load, need to activate/start the game
- Inspect `LoadCallbackInitializator` for finalization methods
- Look for `InitializeAndCreateGame`, `CreateGame`, etc.
- Also check `SandBoxGameManager` and `MBGameManager` for StartGame methods

**Changes:**
- Added `TryStartLoadedGame()` to find how to start the game
- Added `TryStartGameViaMBGameManager()` to explore game manager APIs
- Log all methods on LoadCallbackInitializator

---

## Key Discoveries

### SaveGameFileInfo Structure
- Type: `TaleWorlds.SaveSystem.SaveGameFileInfo`
- Constructors: Only parameterless `()`
- Public Properties: **NONE** (or all return null)
- Static Methods: **NONE**
- The `Name` property returns `(unknown)` for all saves in the list

### SandBoxSaveHelper Methods Available
```
add_OnStateChange(Action`1)
remove_OnStateChange(Action`1)
TryLoadSave(SaveGameFileInfo, Action`1, Action)
CheckMetaDataCompatibilityErrors(MetaData)
GetIsDisabledWithReason(SaveGameFileInfo, TextObject&)
GetModuleNameFromModuleId(String)
LoadGameAction(SaveGameFileInfo, Action`1, Action)
```

### MBSaveLoad Location
- Not in `TaleWorlds.MountAndBlade` assembly
- Found in: `TaleWorlds.Core.MBSaveLoad`
- `GetSaveFiles` takes `Func<SaveGameFileInfo, bool>` filter parameter

### Save Directory Structure
```
Documents/Mount and Blade II Bannerlord/
├── Game Saves/           ← Game's internal list scans HERE
│   ├── save001.sav
│   ├── save002.sav
│   └── saveauto2.sav
└── Game Saves/Native/    ← We were writing HERE (wrong!)
    ├── CoOp_saveauto1.sav
    └── CoOp_saveauto2.sav
```

---

## Next Steps to Try

1. **Test with correct save location** - Save to `Game Saves\` and see if it appears in list
2. **Try calling TryLoadSave with empty SaveGameFileInfo** - Maybe it reads file internally
3. **Investigate how SaveGameFileInfo gets populated** - May need to read save file header
4. **Look for ISaveDriver or similar** - Lower-level save system access
5. **Check if there's a "scan directory" method** - Force rescan of save folder
6. **Try MBSaveLoad.LoadSaveGameData directly with path** - Bypass SaveGameFileInfo entirely

---

## Relevant Files

| File | Purpose |
|------|---------|
| `src/BannerBros.Core/SaveGameLoader.cs` | Handles programmatic save loading |
| `src/BannerBros.Core/SaveFileTransferManager.cs` | Handles save file transfer and writing |
| `src/BannerBros.Core/BannerBrosModule.cs` | Triggers auto-load in `OnSaveFileReadyToLoad` |

---

## Log Snippets

### Save list showing wrong directory (Attempt 4)
```
[SaveLoader] Scanned 6 saves total
[SaveLoader] All save names: [(unknown), (unknown), (unknown), (unknown), (unknown), (unknown)]
[SaveLoader] Save 'CoOp_saveauto2' not in game's list
[SaveLoader] Directory ...\Game Saves\Native has 2 .sav files:
[SaveLoader]   CoOp_saveauto1 (5686779 bytes)
[SaveLoader]   CoOp_saveauto2 (5929704 bytes)
[SaveLoader] Directory ...\Game Saves has 6 .sav files:
[SaveLoader]   save001, save002, save003, saveauto1, saveauto2, saveauto3
```

### SaveGameFileInfo has no properties (Attempt 4)
```
[SaveLoader] Found SaveGameFileInfo: TaleWorlds.SaveSystem.SaveGameFileInfo
[SaveLoader] SaveGameFileInfo properties:
[SaveLoader] SaveGameFileInfo has 1 constructors:
[SaveLoader]   Ctor: ()
[SaveLoader] Created empty SaveGameFileInfo, will try to populate
[SaveLoader] SaveGameFileInfo state after population:
[SaveLoader] SaveGameFileInfo has 0 static methods:
```
