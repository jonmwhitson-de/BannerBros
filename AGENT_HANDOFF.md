# BannerBros - Agent Handoff Document

## Project Overview

**BannerBros** is a co-op multiplayer mod for Mount & Blade II: Bannerlord that allows up to 4 players to play the campaign together.

**Repository:** https://github.com/jonmwhitson-de/BannerBros.git

**Current State:** Code is complete and committed locally, but NOT yet pushed to GitHub. The user needs to push manually (no GitHub auth in this environment).

---

## Agreed Specification

### Core Design
- **Player count:** 4 max
- **Hosting model:** Player-hosted (one player is host/server)
- **Persistence:** World saves when host saves; picks up where left off
- **Game mode:** Campaign only (not sandbox)

### Player Independence
- Each player controls their own warband
- Separate economies (gold, inventory)
- Independent quest progress
- Can join different/opposing factions
- PvP allowed
- Companions are first-come-first-serve

### Time System
- Real-time server-driven progression (no player pause)
- Configurable speed multiplier (0.5x to 4x) controlled by host
- World continues while players are in battles/menus

### Battles
- Simultaneous parallel battle instances allowed
- Players can join ongoing battles by riding to them on the map
- Can choose which side to join (even enemy side)
- Press J near a battle to get join prompt

### Player Protection
- Protected from AI attacks during: NPC dialogue, trade/shop menus, in towns/villages/castles
- NOT protected during: character/inventory menu
- Configurable in settings

### Trading
- Players can trade items/gold directly with each other

### Disconnection
- Disconnected players' parties despawn from map

---

## What Has Been Implemented

### Project Structure
```
BannerLordMod/
├── BannerBros.sln
├── .gitignore
├── .github/workflows/build.yml      # GitHub Actions CI
├── Modules/BannerBros/
│   ├── SubModule.xml                # Bannerlord mod manifest
│   ├── bin/Win64_Shipping_Client/   # Build output directory
│   ├── ModuleData/
│   └── GUI/
└── src/
    ├── BannerBros.Core/             # Core mod logic
    │   ├── BannerBrosModule.cs      # Main entry, Harmony init
    │   ├── BannerBrosConfig.cs      # JSON settings persistence
    │   ├── PlayerManager.cs         # Connected player tracking
    │   ├── WorldStateManager.cs     # Battle instances, time sync
    │   ├── SessionManager.cs        # ★ NEW: Join flow, character spawning
    │   ├── Behaviors/
    │   │   ├── BannerBrosCampaignBehavior.cs  # ★ UPDATED: Full network sync
    │   │   ├── TimeControlBehavior.cs
    │   │   ├── BattleJoinBehavior.cs          # ★ UPDATED: Network events
    │   │   └── PlayerProtectionBehavior.cs
    │   └── Patches/
    │       ├── TimeControlPatches.cs    # Block pause/speed UI
    │       ├── PartyAIPatches.cs        # Prevent AI attacking protected
    │       └── BattlePatches.cs         # Battle start/join/end hooks
    ├── BannerBros.Network/          # Networking layer
    │   ├── NetworkModule.cs
    │   ├── NetworkManager.cs        # ★ UPDATED: SendToServer, message routing
    │   ├── MessageHandler.cs        # ★ UPDATED: All session packet handlers
    │   └── Packets.cs               # ★ UPDATED: Join/character creation packets
    └── BannerBros.Client/           # UI and input
        ├── ClientModule.cs          # ★ UPDATED: Session event handling
        ├── CoopMenuManager.cs       # (stub)
        ├── PlayerHUDManager.cs
        ├── Patches/
        │   └── MainMenuPatches.cs   # Inject Host/Join buttons
        └── UI/
            ├── MainMenuExtension.cs # Host/Join dialogs
            ├── CharacterCreationUI.cs  # ★ NEW: Joining player character creation
            └── PlayerMapMarkers.cs  # Show players on campaign map
```

### Key Technical Decisions
1. **Networking:** LiteNetLib (lightweight, reliable UDP)
2. **Patching:** Harmony 2.3.3 for runtime method patching
3. **Target:** .NET 6.0, Bannerlord 1.2.x
4. **Build system:** Dual-mode - uses local game DLLs if available, falls back to `Bannerlord.ReferenceAssemblies` NuGet for CI

### Dependencies (NuGet)
- `Lib.Harmony` 2.3.3
- `LiteNetLib` 1.2.0
- `Bannerlord.ReferenceAssemblies` 1.2.13.92972 (CI only)

### GitHub Actions
- `.github/workflows/build.yml` builds on push/PR
- Uploads artifacts (DLLs + SubModule.xml)
- Creates release zip on master branch pushes

---

## Current Git State

```
Branch: main
Last commit: 7152a4c "Initial commit: BannerBros co-op mod foundation"
Files: 28 files, +3286 lines
Remote: origin -> https://github.com/jonmwhitson-de/BannerBros.git (NOT PUSHED)
```

**Action needed:** User must push to GitHub manually. A tarball was created at `/home/user/BannerBros-source.tar.gz` for transfer.

---

## What Is NOT Yet Implemented

### High Priority (MVP blockers)
1. ~~**Save/Load synchronization**~~ ✅ DONE - SessionManager handles join flow
2. ~~**Player character creation flow**~~ ✅ DONE - CharacterCreationUI for joining players
3. ~~**Actual network message sending**~~ ✅ DONE - BannerBrosCampaignBehavior sends position/state updates
4. **Testing** - No tests, never been compiled against real game. This is the critical next step!

### Medium Priority
1. **Trading UI** - Player-to-player trade window (packets exist)
2. **Chat system** - Packets exist but no UI
3. **Proper map markers** - PlayerMapMarkers.cs needs real Bannerlord scene integration
4. **Battle joining integration** - TransitionToBattle() needs Bannerlord MapEvent integration

### Lower Priority
1. **Server browser / LAN discovery**
2. **Mod compatibility layer**
3. **Spectator mode on death**
4. **Reconnection with existing character**

---

## Configuration

Default config saved to:
`Documents/Mount and Blade II Bannerlord/Configs/BannerBros.json`

```json
{
  "MaxPlayers": 4,
  "DefaultPort": 7777,
  "TimeSpeedMultiplier": 1.0,
  "ProtectDuringDialogue": true,
  "ProtectDuringTrade": true,
  "ProtectDuringCharacterMenu": false,
  "LastServerAddress": "",
  "PlayerName": "Player"
}
```

---

## Keybinds (Planned)

| Key | Action |
|-----|--------|
| J | Join nearby battle |
| P | Show player list |

---

## Build Instructions

### Local (with game installed)
```powershell
$env:BANNERLORD_PATH = "C:\Program Files (x86)\Steam\steamapps\common\Mount & Blade II Bannerlord"
dotnet build BannerBros.sln
```

### CI (GitHub Actions)
Automatic on push - uses NuGet reference assemblies.

---

## User Info

- **GitHub:** jonmwhitson-de
- **Experience:** No prior Bannerlord modding or C# experience (learning as they go)
- **Relying on AI for expertise**

---

## Notes for Next Agent

1. The code compiles against reference assemblies but has never been tested in-game
2. Many Harmony patches use method names that may need verification against actual Bannerlord decompiled code
3. The `Modules/` directory contains reference mods (BannerlordOnline, etc.) that are gitignored - these were used to understand mod structure
4. User is motivated and responsive - they created the GitHub repo quickly when asked
5. Focus on getting a minimal working prototype that can:
   - Host a session
   - Have another player join
   - See each other on the map
   - Not crash

### Recent Changes (2026-01-24)
The following systems were implemented in this session:

1. **SessionManager** (`src/BannerBros.Core/SessionManager.cs`)
   - Full join flow: client connects → sends JoinRequest → host validates → sends JoinResponse
   - Character creation: client creates character → host spawns hero/party → confirms to client
   - State management: tracks SessionState (Disconnected, Joining, CharacterCreation, InSession)

2. **Network Packets** (`src/BannerBros.Network/Packets.cs`)
   - Added: JoinRequestPacket, JoinResponsePacket, CharacterCreationPacket, CharacterCreationResponsePacket, FullStateSyncPacket
   - Added: ConnectedPlayerInfo, DiplomacyState

3. **Character Creation UI** (`src/BannerBros.Client/UI/CharacterCreationUI.cs`)
   - Simple inquiry-based UI for name, gender, culture selection
   - Submits CharacterCreationPacket to host via SessionManager

4. **Network Sync** (`src/BannerBros.Core/Behaviors/BannerBrosCampaignBehavior.cs`)
   - Sends PlayerStatePacket at 10Hz with delta compression (only when position/state changes)
   - Host sends WorldSyncPacket at 1Hz with time and battle info
   - Host sends FullStateSyncPacket hourly with comprehensive state

5. **Battle Events** (`src/BannerBros.Core/Behaviors/BattleJoinBehavior.cs`)
   - Sends BattleEventPacket when starting/joining/ending battles
   - Other players can see battles on map and join them

---

*Document created: 2026-01-24*
*Last updated: 2026-01-24*
