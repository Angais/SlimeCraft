# SlimeCraft — Minecraft inside Slime Rancher

Goal: like the famous "Minecraft inside Red Dead Redemption 2" video — the **real Slime Rancher 1** world, slimes,
physics and vacpack, with **Minecraft layered on top**: Minecraft HUD (hotbar, hearts, hunger, XP, crosshair, font),
Steve's first-person arm holding Minecraft items, placeable/breakable Minecraft blocks, TNT that launches slimes with
Slime Rancher's own explosion physics, Minecraft mobs (creepers, zombies, pigs…), Minecraft sounds, chat + commands,
inventory/creative menu, crafting and the F3 debug screen. Everything Minecraft is loaded **at runtime from the user's own
Minecraft install** (client jar + asset index) — nothing from either game is redistributed.

## Tech stack
* Slime Rancher 1 (Steam), Unity **2019.4.29f1**, Mono, .NET 4.x profile. Default game dir:
  `C:\Program Files (x86)\Steam\steamapps\common\Slime Rancher` (the scripts also search every Steam library).
* **BepInEx 5.4.23.5** (x64) plugin + **HarmonyX** (`0Harmony.dll`). Reference DLLs: `lib/BepInEx/` if present,
  otherwise the game's `BepInEx/core` (neither is part of the repository).
* Plugin project: `SlimeCraft/SlimeCraft.csproj` (net472, references SR `Managed/*.dll`). Build:
  `dotnet build SlimeCraft/SlimeCraft.csproj -c Release` → `SlimeCraft/bin/Release/SlimeCraft.dll`.
  Partial build of selected modules: `-p:SCModules=Core%3BBlocks` (`%3B` = escaped `;`, a bare `;` or `,` splits the -p switch;
  `scripts/build.ps1 -Modules Core,Blocks` escapes it for you). Contracts + Plugin.cs are always compiled.
* Slime Rancher's game code is used only through its public/private API (direct calls, Harmony patches,
  `AccessTools`/`Traverse`); no Slime Rancher or Minecraft code is copied into this repository. Minecraft behaviour
  is reproduced from observable facts (numbers, layouts, timings), written in our own code.
* Minecraft install: `%APPDATA%\.minecraft` — jar `versions/26.1.2/26.1.2.jar`, asset index `assets/indexes/30.json`,
  objects `assets/objects/xx/hash`.

## Hard constraints (read before coding)
1. **Runtime = Unity 2019.4 Mono (.NET 4.x).** `System.ValueTuple`, `Span<T>` exist. **`System.IO.Compression.ZipArchive`
   does NOT exist** (no System.IO.Compression.dll) — read zips with our own central-directory reader + `DeflateStream`
   (in System.dll). No `netstandard.dll` facade. Avoid APIs newer than .NET 4.7.1. No `Index/Range` (`^1`, `..`),
   no default interface methods, no `record`/`init`.
2. **No new Unity assets.** We cannot ship shaders/AssetBundles. Use shaders that exist in the build. Always loaded
   (globalgamemanagers.assets): `SR/Paintlight/Basic`, `SR/Paintlight/Cutout` (alpha test: `_PrimaryTex`, `_Cutoff`,
   `_Depth`, `_Color`), `SR/Actor`, `SR/Slime/*`, `Standard`, `Unlit/Transparent`, `Unlit/Transparent Cutout`,
   `Sprites/Default`, `UI/Default`, `UI/Default Font`, `Legacy Shaders/Diffuse`; also `Unlit/Color` (in sharedassets2,
   loaded with the world scene). Always look shaders up with `Shader.Find` + fallbacks + a log line, never assume.
3. **Textures**: Minecraft pixel art → `FilterMode.Point`, `TextureWrapMode.Clamp`, no mipmaps (or mip levels generated
   with care for atlases). `Texture2D.LoadImage` (ImageConversionModule) decodes PNG.
4. **Audio**: Minecraft OGGs are loaded with `UnityWebRequestMultimedia.GetAudioClip("file:///...", AudioType.OGGVORBIS)`.
5. **Modules talk only through `src/Contracts`** (`SC.*` services + `Content` registry). Never reference another
   module's classes. Every service may be null → null-check (`SC.Audio?.Play(...)`).
6. **Lifecycle**: `IModule.Init()` runs in `Plugin.Awake` at game start (main menu not loaded yet). Create world
   GameObjects only after `SC.SR.WorldLoaded`; destroy/clear them on `SC.SR.WorldUnloading`. Persistent singletons go
   under `SC.Root` (DontDestroyOnLoad).
7. **Harmony**: annotate patch classes with `[HarmonyPatch(...)]` — Plugin.cs auto-applies every such class. Wrap patch
   bodies in try/catch, never break SR. Target methods must exist in SR's `Assembly-CSharp.dll` (check the signature
   against the game's assembly, e.g. with an assembly browser).
8. **Performance**: no per-frame allocations in hot paths, pool objects, cache components, avoid `FindObjectsOfType` per frame.
9. **Never touch the user's real saves.** Per-save mod data goes to `SC.DataDir/saves/<SaveGameId>.json`.
10. Scale: 1 SR unit (meter) = 1 Minecraft block. Block (x,y,z) = world cube [x,x+1)×[y,y+1)×[z,z+1).

## Controls (defaults, configurable in BepInEx config)
| Action | Key | Notes |
|---|---|---|
| Hotbar slot | 1–9 | SR's own slot1–5 actions are suppressed (Core). |
| Scroll | wheel | Vacpack selected → SR vac-slot cycling (unchanged SR). Otherwise → Minecraft hotbar. |
| Attack / break / shoot | LMB | Vacpack selected → SR shoot. Else → MC attack/mine. |
| Use / place / vac | RMB | Vacpack selected → SR vacuum. Else → MC use/place/eat/ignite. |
| Inventory | I | Survival inventory w/ 2x2 crafting; Creative: creative item grid. (SR uses E for interact.) |
| Chat / commands | Y | Chat line; lines starting with `/` run commands. (SR uses T for gadget mode.) |
| Drop item | Z | (SR uses Q for pulse.) |
| Debug screen | F3 | Minecraft F3 overlay. |
| HUD style | F8 | Toggle Minecraft HUD ↔ original SR HUD. |
SR keys kept: WASD, Space, Shift, E interact, F flashlight, R radar, M map, F1 pedia, T gadget, Q pulse, Esc pause.

## Module map (each module owns only its own folder)
| Module (folder) | Order | Owns services | Responsibilities |
|---|---|---|---|
| `Core` | 0 | Assets, Audio, SR, Input, Persistence, ItemVisuals, Commands | Minecraft jar/zip reader, texture cache, block atlas, lang, sounds.json + OGG audio, SR bridge (player, camera, health, energy, newbucks, time, zone, save id, vacpack show/hide, SR HUD hide, explosions, spawn SR actors), input gating (Harmony on SR input/weapon), per-save persistence, item icons/meshes/materials, command registry + built-in commands (/give /time /tp /gamemode /summon /kill /clear /help /seed-ish fun), Minecraft splash text on SR title screen. |
| `Hud` | 100 | Inventory, Hud | Minecraft HUD (hotbar + selection, hearts ← SR health, hunger ← SR energy, XP bar ← newbucks progress & level, crosshair, held item name, action bar, titles), Minecraft bitmap font (ascii.png), chat + command line UI, F3 screen, inventory screen (survival 2x2 crafting + armor-less), creative inventory, crafting table 3x3 screen (recipes loaded from jar `data/minecraft/recipe/*.json` + item tags, filtered to our items), item tooltips, drag/drop stacks, starter kit, game mode, hiding SR HUD when MC HUD active, vac-slot strip when vacpack selected (SR ammo icons). |
| `FirstPerson` | 400 | FirstPerson | Steve's right arm (steve.png wide model, sleeve overlay) + held item (block cube / extruded sprite / tools angled like MC) rendered in first person on top of the world, swing/equip/eat/bow animations, view bobbing compatible with SR camera, hides when vacpack selected (SR's vacpack shows). |
| `Blocks` | 200 | Blocks | Voxel block world: chunked meshes from BlockAtlas with face culling, colliders that SR player & slimes collide with, place/break (hold LMB, MC break-time formula, destroy_stage crack overlay, tools & drops), block outline, block particles, step sounds on our blocks, special blocks (slime block bounce, honey slow, ice slippery, gravity sand/gravel via SC.Entities.SpawnFallingBlock, light-emitting blocks with point lights, TNT ignition by flint&steel), **player interaction controller** (LMB/RMB dispatch for MC items: mine, attack via SC.Entities, place, eat food → SR heal/energy, spawn eggs, flint&steel, bow), harvesting SR terrain (hitting SR rocks/ground/trees with tools yields cobblestone/dirt/sand/logs etc. without altering SR geometry), persistence of placed blocks. |
| `Entities` | 300 | Entities, Explosions | Dropped item entities (magnet pickup, merge, despawn, vacpack-pullable), primed TNT (fuse flash/swell, chain), Minecraft explosion (ray-cast block destruction, damage, SR physics via SC.SR.SRExplode, particles, sound), falling blocks, arrows, **Minecraft mobs** (creeper, zombie, skeleton, pig, cow, sheep, chicken, slime, enderman, iron golem): cuboid models matching Minecraft's mob shapes, textured from the player's jar, walk/limb/head animation, hurt flash, death, AI, natural spawning (hostiles at SR night, passives by day), drops, SR interplay (dropped MC food that touches a slime turns into its SR equivalent so slimes eat it and make plorts; SR vacpack pulls MC mobs/items). |
| `Testing` | 900 | — | Opt-in automated test harness (config `Testing.AutoTest`): redirects SR saves to a sandbox folder, starts a new game from the main menu, runs a scripted scenario (give items, place blocks, TNT, spawn mobs, open screens) and captures screenshots + a report to `BepInEx/SlimeCraft_test/`, then quits. Also scripts: `scripts/build.ps1`, `scripts/install.ps1` (copies dll to `BepInEx/plugins/SlimeCraft/`), `scripts/uninstall.ps1`. |

## Data conventions
* Item/block ids are namespaced (`minecraft:stone`, `slimecraft:vacpack`). `Content.Item(id)`, `Content.Block(id)`.
* Atlas texture spec DSL: `"block/stone"`, `"block/oak_leaves@77AB2F"`, `"block/grass_block_side|block/grass_block_side_overlay@91BD59"`.
* Sound groups → events `block.<group>.<break|place|step|hit|fall>`; Audio falls back to `block.stone.*`.
* Persistence keys: `inventory`, `blocks`, `entities`, `mobs`, `gamemode`… (string payloads, JSON or base64).
