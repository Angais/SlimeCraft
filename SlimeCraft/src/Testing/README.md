# Testing module (Order 900)

Opt-in, fully automated in-game test of SlimeCraft. It is **inert for players**: unless
`Testing.AutoTest = true`, the module disables itself in `Init()` and none of its Harmony patches are even applied
(every patch class has `Prepare() => TestConfig.Active`). It assigns no `SC.*` service.

Run it with `scripts/run-autotest.ps1` (backs up your saves, enables the flag, launches the game, waits for the
report, restores everything and verifies your save folder is byte-identical).

## What a run does

1. **Sandbox** (`SandboxPatches.cs`)
   * `FileStorageProvider.SavePath()` (private) is the single place where Slime
     Rancher resolves its folder for saves (`*.sav`), the profile (`slimerancher.prf`) **and** the settings
     (`slimerancher.cfg`). It is redirected to `BepInEx/config/SlimeCraft/test_saves` — the user's saves, profile,
     achievements and options are never read or written. The prefix never falls through to the original.
   * `SavedProfile.PushOptions(OptionsV12)` (private): the fresh sandbox profile would default to 800x600
     fullscreen at LOWEST quality; forced to `Testing.Width x Testing.Height` windowed, tutorials `NONE`, and
     `SRQualitySettings.CurrentLevel = Testing.Quality`.
   * `TutorialDirector.MaybeShowPopup(Id)` and `PopupDirector.QueueForPopup(PopupCreator)` are skipped
     (no tutorial / mail / pedia / blueprint popups).
   * `IntroUI.AnimateIntro()` (private): the 13 s "welcome" sequence is skipped; two frames later the intro is marked
     as finished and closed through `BaseUI.Close()`, so AutoSaveDirector's close callback and the intro's
     suppressors stay balanced.
   * `AchievementsDirector.AwardAchievement` (private): no Steam achievements
     from test games (SteamDirector is deliberately NOT patched: patching runs its cctor before GameContext exists).
   * Before creating anything the boot step asks SR's own `AutoSaveDirector.StorageProvider` for its folder (calls
     the private `SavePath` via Traverse) and **aborts** unless it is the sandbox and differs from the real folder
     (`TestConfig.RealSaveDir`, worked out on its own from Unity's `persistentDataPath`, never from the patched
     method).
   * Old sandbox `*.sav` files and SlimeCraft per-save files of previous test games (`*SlimeCraftTest*` in
     `BepInEx/config/SlimeCraft/saves`) are removed at the start of a run.
2. **Boot** (step `00_boot`): waits for `MainMenu` + `MainMenuUI`, screenshots the title screen, then calls
   `GameContext.Instance.AutoSaveDirector.LoadNewGame("SlimeCraftTest", Identifiable.Id.PINK_SLIME,
   PlayerState.GameMode.CLASSIC, onError)` (the public call that starting a new classic game from the menu ends in) and waits for the world
   (`SC.SR.InGame`, with an own SR-based fallback), closes the intro and checks the game mode.
3. **Scenario** (`Scenario.cs`, `ScenarioWorld.cs`, `ScenarioInteract.cs`, `ScenarioPersist.cs`) — every step runs in its own manually-driven coroutine with
   try/catch and a timeout; failures never stop the run:

| Step | What | Screenshots |
|---|---|---|
| 01 spawn_view | MC HUD on, services ready, hotbar | `01_spawn_view` |
| 02 diamond_pickaxe | `/give`, move to hotbar, select, vacpack hidden, swing | `02_diamond_pickaxe`, `02b_pickaxe_swing` |
| 03 build_house | 7x5 house 6 blocks ahead via `SC.Blocks.SetBlock`: stone-brick foundation following the terrain, oak planks + oak log corners, glass windows, door, glowstone, roof with skylight, 5-TNT tower | `03_house_and_tnt_tower` |
| 04 spawn_slimes_and_mobs | creative mode, step back 5 blocks to a free standing spot (`G.FindStandSpot`: walkable ground + free player capsule; passes within 3.5 m horizontally – it is only camera framing), 6× `SC.SR.SpawnSRActor("PINK_SLIME")` around the tower, creeper/zombie/pig/cow/sheep/chicken/iron golem/slime via `SC.Entities.SpawnMob` | `04_slimes_and_mobs` |
| 05 tnt_explosion | bottom tower TNT → `SC.Entities.SpawnPrimedTnt(80)`; fuse timing (3.4–4.8 s), slime launch speed/displacement, chain reaction, house damage | `05a_tnt_fuse_1s`, `05b_explosion`, `05c_after_1s`, `05d_after_chain` |
| 06 screens | `/screen inventory`, `/gamemode creative` + `/screen creative`, `/screen crafting`, chat lines + `/screen chat`, `/screen none`; checks `SC.Hud.ScreenOpen` and the input gate | `06a`…`06d` |
| 07 debug_screen | F3 via a `f3`/`debug`/`debugscreen` command, else via reflection on a Hud visibility flag (restored afterwards); skipped if neither exists | `07_debug_screen` |
| 08 night_spawning | `Entities.SpawnOnRanch = false` by design, so: creative (hostiles ignore the player), `ClearAll`, teleport off the ranch (Dry Reef, else Moss Blanket / Indigo Quarry), `/mobspawning on` if such a command exists, `/time set night`, waits ≤45 s for NEW mobs in `IEntities.LiveMobs`, aims at the nearest, `ClearAll`, `/time set day`, survival, back to the ranch. Skipped if `[Entities] NaturalSpawning = false` | `08a_night`, `08b_night_spawns` |
| 09 vacpack | select the vacpack slot, add SR ammo (`Ammo.MaybeAddToSlot`) so the vac strip has icons, `VacpackSelected`/`VacpackActive` | `09_vacpack` |
| 10 scenic_teleports | Dry Reef, Moss Blanket, Indigo Quarry via `SC.SR.TeleportPlayer`; best open view picked by raycasts | `10a_dry_reef`, `10b_moss_blanket`, `10c_indigo_quarry` |
| 11 empty_hand | empties a hotbar slot (moves its stack to the main inventory), selects it: empty selection, SR vacpack hidden, SR weapon suppressed; punches the air through the real attack path | `11a_empty_hand`, `11b_empty_hand_punch` |
| 12 mine_block | survival, diamond pickaxe; `SetBlock` stone 2.5 m ahead at eye height −0.5 (free cell, clear line of sight, tries 10 directions), aims (`SetLook`), checks `SC.Blocks.Raycast` from the camera, **holds attack** (`SetTestInput`) until `GetBlock` is air; crack screenshot when `IFirstPerson.MiningProgress ≥ 0.35`; cobblestone item entity seen and picked up (player walks onto it after 0.7 s) within 3 s | `12a_mining_crack`, `12b_block_broken` |
| 13 place_blocks | survival, oak planks; SR terrain ~3 m ahead (non-block, non-actor, static, normal.y ≥ 0.6, free cell), **one right click** → planks at `floor(hit + normal·0.5)`, exactly one `BlockChanged`, one item consumed; aims at the new block's top face → second click → block at cell + aimed face normal (top) | `13_placed_blocks` |
| 14 harvest_sr_terrain | survival, diamond pickaxe; scans 144 camera rays (`SC.SR.RaycastWorld`, reach 4.2) for SR surfaces, rock-like names first (walks to a rock up to 40 m away if none in reach), holds attack 3 s on up to 3 candidates; reports surface names → new item entities / inventory gains | `14_harvest_sr_terrain` |
| 15 eat_food | survival, cooked beef, looks up; `SC.SR.DamagePlayer(30)`, **holds use** 2 s (MC eat = 32 ticks): health increased, exactly one beef consumed | `15_eating` |
| 16 sword_vs_pig | survival, diamond sword; `SpawnMob(pig)` 2.5 m ahead on open ground, waits for the attack-strength recharge, **clicks attack** every 1.1 s (hurt invulnerability), re-approaches a panicking pig, until it leaves `IEntities.LiveMobs`; porkchop drop seen / picked up | `16a_sword_hit`, `16b_pig_killed` |
| 17 slime_eats_mc_food | `SpawnSRActor("PINK_SLIME")` 2.2 m ahead (made hungry via `SlimeEmotions.Adjust(HUNGER, 1)`), `SpawnItem(3 carrots)` 1 m above it (pickup delay 10 s): within 10 s the carrot entity disappears / `CARROT_VEGGIE` actors appear near the slime and the player did not pick them up; a new `*_PLORT` near the slime within 12 s is a bonus check (note otherwise) | `17a_carrots_to_sr_food`, `17b_slime_after_eating` |
| 18 vacpack_mc_items | vacpack, 2 diamonds + 3 sticks + 4 cobblestone dropped 4 m ahead, aim, hold use: first via `SetTestInput`; if `SC.SR.VacActive` stays false, via a temporary InControl binding on `SRInput.Actions.vac` (SR's real vac code); else by forcing `WeaponVacuum.vacMode` (reflection); skipped if all fail. All items must reach the Minecraft inventory within 6 s | `18_vacpack_pulls_mc_items` |
| 19 drop_key | grass block selected, `SimulateKeyDown(SC.Input.DropKey)`: exactly one item leaves the inventory (catches a double drop by Hud + Blocks) and a new grass item entity exists | `19_drop_item` |
| 20 persistence_roundtrip | guard: SR's storage provider must still be the sandbox and the game the test game; `ClearAll`, a diamond-block marker; `AutoSaveDirector.SaveAllNow()` (new `.sav` in the sandbox, SlimeCraft per-save JSON rewritten); **Save and Quit** via `PauseMenu.Quit()` (the step fails if the pause menu is missing); main menu; `AutoSaveDirector.BeginLoad(...)` with the game's newest save (what the Continue button loads); compares save id, `SC.Blocks.Count`, marker block, `Inventory.Serialize()`, game mode | `20a_main_menu_after_quit`, `20b_after_reload` |

   Steps 11–19 drive the real interaction code (Blocks' InteractionController, Entities, Hud's drop key) through the
   `IInputGate` test hook — `SetTestInput(attack, use, sneak)` / `ClearTestInput()` / `SimulateKeyDown(key)` — and aim
   with `ISRBridge.SetLook` at targets the step placed itself (pitch/yaw from the target vector). A click is held ≥ 2
   frames (rising edge) and < 0.2 s (Minecraft repeats a held right click every 4 ticks). The runner calls
   `ClearTestInput()` and releases the vac driver after **every** step (also after a timeout/exception), so a failed
   step can never leave a button "held".

   Teleport targets are found at runtime (`ScenicSpots.cs`): `DebugTeleportDestination` objects left in the world
   scene, else the zone's `DirectedSlimeSpawner`s nearest the zone centroid (`ZoneDirector.zones`), else region
   bounds. The player is held in place (through SR's player event handler and character controller) until the destination's colliders have streamed in, then snapped to the ground.
4. **Report**: `<BepInEx>/SlimeCraft_test/report.json` (written atomically: `.tmp` + move) and `report.txt`:
   per-step status (pass/fail/skip/timeout/error), checks, notes, screenshots (+ average luminance, black frames
   fail), duration, avg/min FPS and hitches, log errors per step; all Unity errors/exceptions/asserts
   (`Application.logMessageReceivedThreaded`) **and** BepInEx `LogError`/`LogFatal` from SlimeCraft modules
   (`ILogListener`), counted, with the first 200 unique messages + stack traces; 1 Hz FPS samples; sandbox
   counters; environment (GPU, resolution, Minecraft version, missing services, registered commands).
   `progress.txt` shows the running step (the script prints it).
5. **Quit** (`Testing.QuitWhenDone`): `Application.Quit()`; SR's `OnApplicationQuit` save goes to the sandbox.
   A watchdog (`Testing.MaxMinutes`) aborts a hung run, writes the report and quits; closing the window by hand
   still writes a partial report.

## Config (`BepInEx/config/com.angais.slimecraft.cfg`, section `[Testing]`)

| Key | Default | Meaning |
|---|---|---|
| `AutoTest` | `false` | Run the test on the next start. **One-shot**: reset to false as soon as a run starts, so a crash never leaves the game in test mode. |
| `Scenario` | `full` | `full`, `smoke` (01,02,03,06,09), `interact` (01,11–19), `persist` (01,03,13,20), `boot` (start a game only) or a list like `01,05` / `tnt_explosion,screens`. |
| `QuitWhenDone` | `true` | Quit the game after writing the report. |
| `Width` / `Height` | `1600` / `900` | Window size during a run (windowed). |
| `Quality` | `3` | SR quality preset for the sandbox profile (0–4, -1 = keep). |
| `MaxMinutes` | `9` | Watchdog. A full run (00–20) is expected to take ~3–5 min. |
| `KeepAutoTestEnabled` | `false` | Keep `AutoTest` on (test every launch). |

## Files

* `TestingModule.cs` – IModule (MonoBehaviour on `SC.Root`), one-shot flag, watchdog, per-frame FPS tick.
* `TestConfig.cs` – config entries, lazily bound (needed by patch `Prepare()` before `Init`); the per-session
  test-run decision (made once, so the one-shot reset cannot switch the harness off mid-run); the sandbox and output
  folders; and the real SR save folder used as a never-write reference (Unity's persistent data path; on macOS the
  game uses a nested `Monomi Park/Slime Rancher` folder next to Unity's bundle-id folder).
* `SandboxPatches.cs` – the Harmony patches + counters.
* `TestRunner.cs` – boot, step driver (nested enumerators, timeouts, exceptions, per-step input hygiene), screenshots,
  report/quit.
* `Scenario.cs`, `ScenarioWorld.cs` – steps 01–10 (`GoToZone` = zone teleport shared by steps 08 and 10).
* `ScenarioInteract.cs` – steps 11–19 (real interaction code via the input test hook).
* `ScenarioPersist.cs` – step 20 (save → Save and Quit → main menu → load → compare).
* `TestInput.cs` – `TI` (guarded `SetTestInput` / `ClearTestInput` / `SimulateKeyDown`, one-click helper) and
  `VacDriver` (InControl test binding on `SRInput.Actions.vac`, `WeaponVacuum.vacMode` reflection fallback).
* `GameHelpers.cs` – SR/SlimeCraft helpers (player hold/look via vp_FP*, ground raycasts, commands, inventory).
* `GameHelpers.Interact.cs` – standing spots (ground + free capsule), item-entity scan ("MC Item <id>" scene roots),
  SR actors near a point (`Identifiable`), inventory snapshots/diffs, surface descriptions.
* `ScenicSpots.cs` – runtime zone teleport targets.
* `LogCollector.cs`, `TestReport.cs` – error capture and report writers.

## Known limitations / needs in-game verification

* Step 07 shows F3 through `IHud.DebugScreenVisible`; look direction goes through `ISRBridge.SetLook`; step 08 aims at the
  nearest new mob from `IEntities.LiveMobs` and waits for `/time set` fast-forwarding to reach night/day.
* Steps 11–19 need Core's `IInputGate.SetTestInput/ClearTestInput/SimulateKeyDown` (rising edges are computed by Core).
  Item entities are found by the Entities naming convention `MC Item <id>` (root objects); every drop check also
  accepts an inventory gain, so a renamed entity only weakens the "seen" part.
* Step 12: diamond pickaxe on stone breaks in 6 ticks (0.3 s), so the "~50 %" crack screenshot is taken on the first
  frame with `IFirstPerson.MiningProgress ≥ 0.35`; the capture frame can still show a later stage or (rarely) the
  broken block — the note in the report gives the progress at capture time.
* Step 14 depends on Blocks' name-based classification of SR surfaces: the report lists the surface names
  (object / mesh / materials / parent) and what dropped for each candidate.
* Step 17: Entities logs nothing when the food bridge converts an item, so the check uses world state (the carrot
  entity disappears / `CARROT_VEGGIE` actors appear near the slime, inventory unchanged). The plort is a bonus.
* Step 18: SR's `WeaponVacuum` reads InControl (`SRInput.Actions.vac`), not the SlimeCraft input gate; the report
  says which of the three methods engaged the vac. The InControl binding has `BindingSourceType.None` (ignored by
  `SavedProfile.PullBindings`, so it can never reach the profile) and is hard-removed after the step.
* Step 20 leaves the test game loaded again at the end, so the final `Application.Quit()` saves into the sandbox
  once more. Core's atomic writes leave `<save>.json.bak`; the next run's sandbox cleanup removes `.json`, `.json.bak`
  and `.json.tmp` of earlier test games.
* Screenshots use `ScreenCapture.CaptureScreenshotAsTexture()` after `WaitForEndOfFrame` (includes uGUI overlay
  canvases and IMGUI); falls back to `ScreenCapture.CaptureScreenshot(path)`.
* Unity itself always writes `Player.log`/`Player-prev.log` and its analytics cache (`Unity/`) into
  `LocalLow/Monomi Park/Slime Rancher`; the script excludes those from the byte comparison. Unity's
  `Screenmanager*`/`UnityGraphicsQuality` PlayerPrefs (registry) are restored by the script after the run.
* Zone spots are heuristic (spawners near the zone centroid); the "covered sky" check skips caves but a view can
  still be unspectacular. Natural-spawn and slime-launch checks depend on Entities' spawn rates/physics.
* Runs from the main menu only; it refuses to run if a game is already loaded.
