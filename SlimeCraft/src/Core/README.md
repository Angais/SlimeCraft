# Core module (Order 0)

Owns and assigns `SC.Assets`, `SC.Audio`, `SC.SR`, `SC.Input`, `SC.Persistence`, `SC.ItemVisuals`, `SC.Commands`.
`CoreModule` is a MonoBehaviour on `SC.Root` that drives the per-frame work of all of them, and also draws the
Minecraft splash text on Slime Rancher's title screen.

## Files
| File | What |
|---|---|
| `CoreModule.cs` | `IModule` (Order 0): creates services, Update/OnGUI/OnApplicationQuit pump |
| `CoreConfig.cs` | every `Core.*` config entry |
| `Assets/McInstall.cs` | `.minecraft` discovery (version auto-pick, asset index id) |
| `Assets/ZipReader.cs` | own zip/jar reader (EOCD, zip64, central directory index, stored + deflate) |
| `Assets/Inflater.cs` | managed RFC 1951 inflater, fallback if `DeflateStream`/MonoPosixHelper is unusable. Adapted from Mark Adler's `puff.c` (zlib license, see the file header and `THIRD_PARTY_NOTICES.md`) |
| `Assets/McAssets.cs` | `IMcAssets`: jar access, asset index, texture cache (first anim frame), lang |
| `Assets/TextureSpec.cs` | atlas spec DSL (`a`, `a@RRGGBB`, `base\|overlay@RRGGBB`, any number of layers) |
| `Assets/BlockAtlas.cs` | `IBlockAtlas`: 16px tiles in 20px cells, 2px extruded border, stable UVs |
| `Assets/MaterialFactory.cs` | shader choice, non-foliage SR Paintlight template, neutral `_Depth`, emissive (unlit cutout) material |
| `Audio/SoundRegistry.cs` | sounds.json model (weights, `type:"event"` references, attenuation) |
| `Audio/McAudio.cs` | `IMcAudio`: async OGG loading, pooled AudioSources, SR SFX volume, pause |
| `SR/SRBridge.cs` | `ISRBridge` (player, camera, health/energy/newbucks, time, zone, layers, actors, lifecycle) |
| `SR/SRVacpack.cs` | vacpack show/hide + clean release (ClearVac, gadget mode off) |
| `SR/SRHudHider.cs` | hides SR's clock/mail/keys/currency/meters (+ icons)/crosshair/ammo with CanvasGroups, verified every second |
| `SR/SRPatches.cs` | all Harmony patches + `CoreRuntime` shared state |
| `Input/InputGate.cs` | `IInputGate` (screen stack via SR's own input-mode stack) |
| `Persistence/SavePersistence.cs` | `IPersistence` (per-save JSON, atomic writes) |
| `Items/ItemVisualsService.cs` | `IItemVisuals` |
| `Items/IsoIconRenderer.cs` | CPU isometric block icons (vanilla gui transform) |
| `Items/ItemMeshes.cs` | block cube mesh + Minecraft-style extruded item sprites |
| `Items/VacpackIcon.cs` | vacpack icon source chain (model render → cached PNG → SR shop sprite → pixel-art placeholder), one stable Texture2D |
| `Items/VacpackIconRenderer.cs` | renders the real SR vacpack model into a 32x32 Minecraft-style icon |
| `Commands/CommandRegistry.cs`, `Commands/BuiltinCommands.cs` | `ICommands` + built-ins |
| `Menu/McFont.cs`, `Menu/SplashText.cs` | ascii.png bitmap font + title-screen splash |
| `Util/CoreLog.cs`, `Util/Pixels.cs` | rate-limited logging, CPU image helpers |
| `ForceEnglishPatch.cs` | prefix on private `MessageDirector.SetCulture(CultureInfo, bool)` keeping SR's UI in English (`Core.ForceEnglish`, default true) |

## Key design decisions
* **Minecraft install**: `Core.MinecraftVersion=auto` uses 26.1.2 if its jar exists, else the newest vanilla release
  (version json `"type":"release"`, no `inheritsFrom`, newest `releaseTime`; forge/fabric/quilt/snapshots skipped).
  Missing install → one clear error, `Ready=false`, every texture is Minecraft's magenta/black missingno, audio off.
* **Jar**: indexed once (~35 ms for 29k entries), file handle kept open, lookups+reads under a lock (thread-safe),
  inflate outside the lock. Verified against the real 26.1.2 jar: managed inflater output is byte-identical to DeflateStream.
* **Textures**: `LoadImage` → RGBA32, Point, Clamp, no mips, readable. `.mcmeta` animations: first frame of `frames[0]`
  honoring `width`/`height`. Missing → cached missingno (only cached once `Ready`).
* **Atlas**: built lazily on first access from every `BlockDef` face (+ `destroy_stage_0..9`). Size ≥ 512² with ≥2× spare
  cells, so on-demand `GetUV` additions never move existing tiles (UVs stay valid); additions re-upload immediately.
  grass_block_side in 26.1 already contains baked green pixels exactly where the overlay is opaque, so
  `side|overlay@91BD59` reproduces the in-game biome-tinted look.
  UV inset defaults to **0.02 texel** (config `AtlasUvInsetTexels`), not the classic half texel: with 2px extruded
  padding and point filtering there is no bleeding, and a half-texel inset makes the edge pixels of every face
  visibly thinner than in Minecraft. Set 0.5 for the classic behaviour.
* **Materials**: lit → `SR/Paintlight/Cutout` → `Unlit/Transparent Cutout` → `Standard`(cutout) → `Legacy Shaders/Diffuse`.
  `SR/Paintlight/Cutout` only declares `_PrimaryTex`, `_Depth`, `_Cutoff` (seen in game and in the asset files). Every lit
  material we make (blocks, items, mobs, Steve's arm via `CreateLitMaterial`) gets a **neutral 4x4 mid-gray `_Depth`**
  (`PaintDepthGray`, 128) and all wind/sway/vertex-offset props (`_SwayStrength`, `_WindTurbulance`, `_YOffset`, …) forced
  to 0. The SR template (keywords, render queue, stale props) is chosen among loaded `SR/Paintlight/Cutout` (preferred) and
  `SR/Paintlight/Basic` materials, **never a foliage one** (name contains leaf/leaves/grass/petal/flower/plant/moss/…, or a
  declared non-zero wind prop): in the ranch the old pick was `objLeaves01` (`_SwayStrength` 0.232, its own leaf texture
  as `_Depth`, `_ENABLEDETAILTEX_ON`); expected now: `objRoots01/02` (sway 0, wind 0; verify in the log). Every candidate (name, shader, declared props)
  and the choice are logged on each WorldLoaded; materials made before a template existed are upgraded in place.
  **Emissive** (`IBlockAtlas.Emissive`): `Unlit/Transparent Cutout`, `_MainTex` = atlas, `_Cutoff` 0.5, queue AlphaTest
  (full-bright light-emitting blocks). Translucent → `Unlit/Transparent` → `Sprites/Default`. Colour: `Unlit/Color`
  (opaque) or `Sprites/Default` (alpha). Choice is logged.
  `CreateLitMaterial` returns a NEW material per call (safe to tint); `GetItemMaterial` returns shared ones.
* **Audio** (sounds and mixing behave like Minecraft's): `SoundRegistry` flattens each sounds.json event once into a
  table of playable files, so every reachable file is picked in proportion to its own weight and event references pass
  their volume/pitch on to the files they bring in (references are followed up to 8 levels deep, which also stops
  cycles); gain = clamp01(volume·entryVolume); pitch clamped 0.5–2;
  linear rolloff to 0 at `attenuation_distance(16)·max(1, volume)`; no doppler. Clips via
  `UnityWebRequestMultimedia.GetAudioClip(file:///…, OGGVORBIS)` kept Vorbis-compressed, max 6 concurrent loads,
  plays requested while loading start when ready (dropped if >0.75 s late). Missing `block.X.Y` → `block.stone.Y`.
  SR has no AudioMixer: SR's SFX volume lives on SECTR audio buses, so voices follow the "SFX" bus `EffectiveVolume`,
  mute with its non-UI children (sleeping) and pause with SR (timeScale 0 or paused non-UI SFX buses). 2D (UI) sounds
  never pause. `PlayAttached` voices follow the transform and stop when it is destroyed.
* **World lifecycle**: WorldLoaded fires once per load when the `worldGenerated` scene is active, SceneContext has a
  player and `AutoSaveDirector.IsLoadingGame()` is false (both new and loaded games), then persistence loads.
  WorldUnloading fires from a prefix on `SceneContext.OnSessionEnded` (quit to menu / game over / app quit, world
  still intact) with fallbacks `SceneManager.sceneUnloaded` and "SceneContext vanished". Order on unload:
  persistence save → WorldUnloading listeners → input/audio cleanup. Each listener is invoked separately.
* **Saving**: prefix on private `AutoSaveDirector.SaveGame` (every SR save path: autosave, sleep, quit) → `Saving`
  listeners → persistence save. Skipped while SR is loading.
* **SaveGameId** = `AutoSaveDirector.SavedGame.GetName()` (SR's game name, e.g. `20261002031400_MyRanch`): identical for
  every save file / autosave of that game, different between games.
* **Input gate**: an open Minecraft screen pushes `SRInput.InputMode.NONE` on SR's own input-mode stack (same API
  LockOnDeath/PauseMenu use) → no SR move/look/fire/map/pedia/pause; cursor freed via `TimeDirector.EnableCursor`
  (re-asserted each frame, SR re-locks on focus). Because SR's `PlayerState.CanBeDamaged` requires input mode DEFAULT,
  a postfix keeps the player damageable while only our screen holds NONE (as in Minecraft). Escape: the screen owner
  (Hud) pops its screen; Core blocks `PauseMenu.Update` while any screen is open and for 0.25 s after the last one
  closed so the same Escape press never opens SR's pause menu. Screens are a set of ids (pushing an id twice is idempotent).
* **Test input** (`SetTestInput/ClearTestInput/SimulateKeyDown`, Testing module only): OR-ed with the real mouse/keys and
  still gated by `GameplayInputAllowed`. Changes take effect on the NEXT frame with held+pressed together like a real
  button: a rising edge gives `AttackPressed`/`UsePressed` for exactly one frame (the first held frame), a release ends
  held on the next frame, `SimulateKeyDown(key)` makes `GameKeyDown(key)` true for exactly one frame. Cleared on world unload.
* **Vacpack**: `SetVacpackActive(false)` disables every renderer under `WeaponVacuum` (re-enforced each frame) and
  skips `WeaponVacuum.Update` (prefix) after releasing it once (`ClearVac` drops held objects, gadget mode/overlay off,
  targeting cleared). `SuppressSRWeapon` blocks Update without hiding. SR slot keys 1–5 are always swallowed
  (`UpdateSlotForInputs` prefix); the wheel branch is left to SR. SR's ammo-slot HUD hides with the vacpack.
* **SR HUD**: SR's HUD (HUD Root/HudUI/UIContainer) is flat — every element is a direct child of UIContainer (dumped from
  the game's level3 asset: crossHair, RadarPanel, CurrentDay, CurrentTime, TimeIcon, MailIcon, PartnerArea, CurrencyIcon,
  Currency, KeysIcon, Keys, HealthIcon, Health Meter, EnergyIcon, Energy Meter, Ammo Slots, RadIcon, Rad Meter, Targeting,
  SaveIndicator, Debug). `SetSRHudVisible(false)` puts a CanvasGroup (alpha 0, no raycasts) on: day/time/weather icon,
  mail, partner level, keys, currency + coin, Health/Energy/Rad meters (bars, frames, value labels) + heart/bolt/rad
  icons, crosshair, ammo slots (ammo also hides whenever the vacpack is not selected). Kept: the Popups canvas
  (mail/pedia/achievement/tutorial popups), RadarPanel, Targeting (what you point at), SaveIndicator, Debug,
  DeathObscurer, glitch exit HUD and all SR windows. Targets come from HudUI's fields, the meter/crosshair/ammo
  components and the icon names; the binding is verified every second (new HudUI / destroyed element → rebind) and the
  alpha re-applied, so nothing SR re-enables can leak. Bound element names are logged once.
* **Layers**: BlockLayer = first of Default(0) / unnamed layers that collides with Player(8) and Actor(15) (expected: 0,
  same as SR terrain, so slimes walk on blocks and SR raycasts see them). WorldMask = Default + BlockLayer + layers
  whose name looks like terrain/ground/mountain/static/platform/rock/building. Decision + matrix logged.
* **Player physics**: vp_FPController moves `(externalForce + motorThrottle + fallSpeed)·Δt·60` per step.
  `AddPlayerVelocity(v)`: horizontal → `AddForce(v/60)` (decays 5 %/step); vertical → added to the controller's own
  gravity-integrated `m_FallSpeed` (true ballistic arc, "add" semantics so a bounce passes −2·vy). SR clamps fall speed
  to 0.09/step (5.4 m/s), so the excess of a big upward launch goes into `m_MotorThrottle.y` exactly like SR's jump,
  sized so the apex reaches v²/2g; a non-zero throttle also disables vp's grounded "anti-bump" push-down that would
  otherwise swallow small impulses on the ground. (Private fields via `AccessTools.FieldRefAccess`, fallback AddForce.)
* **Zone / time**: `ZoneName` = SR's own localized Slimepedia title of `PlayerZoneTracker.GetCurrentZone()` (bundle
  `pedia`, key `t.<lower-case pedia id>` via `ZoneDirector.zonePediaIdLookup`), a readable form of the zone enum name
  (`MOCHI_RANCH` → "Mochi Ranch") as fallback, cached per
  zone (cache cleared on every world load, so a language change in SR's menu is picked up), polled every 0.5 s.
  `DayFraction` = `TimeDirector.CurrDayFraction()` (0 = midnight), `IsNight` = before 6:00 or after 18:00,
  `DayNumber` = `CurrDay()`.
* **SetLook(pitch, yaw)**: `vp_FPPlayerEventHandler.Rotation.Set` (fallback `vp_FPCamera.Angle`).
* **SRExplode**: with player damage or ignition it simply calls SR's `PhysicsUtil.Explode` (from a hidden, reused
  `SlimeCraft_ExplosionSource` object). With 0 player damage and no ignition the player is left out entirely: SlimeCraft's
  own actors-only push overlaps a sphere on every layer except ActorEchoes and applies SR's public
  `PhysicsUtil.SoftExplosionForce` once to each rigidbody that owns an overlapping solid collider (bodies reached only
  through child colliders, i.e. Minecraft mobs, are left to the Entities module). This avoids SR's hurt cue, red flash
  and controller push, which SR plays even for 0 damage; Minecraft explosions handle the player themselves.
* **PlayerDamaged** event: raised from a `PlayerState.Damage` patch with the health actually lost and the source.
* **Damage**: player damage goes through SR's `PlayerDamageable.Damage` (hurt cue, red overlay, screen shake, game-mode
  multiplier) + `DeathHandler.Kill` when lethal. `HitSRActor`: VelocityChange push, SlimeEmotions agitation/fear,
  and `Damageable` (SlimeHealth) damage × `SRDamageMultiplier`.
* **Icons**: vanilla `block/block.json` gui transform (rotation [30,225,0], scale 0.625) → top, **east on the left**,
  **north on the right**, Minecraft default face UVs, shading 1.0/0.8/0.6, alpha-test (translucent blocks keep alpha).
  Verified offline against the real jar (TNT text reads correctly on both faces, furnace/crafting-table fronts on the
  right like Minecraft). Items use their texture directly. Default size 64 px (`IconSize`). Icons, item textures,
  meshes and `GetItemMaterial` results are shared/cached — never modify them (use `CreateLitMaterial` for tintable copies).
* **Vacpack icon**: ONE Texture2D for the session, pixels replaced in place (the HUD draws it with full UVs every frame, the
  cached extruded mesh is dropped on change). ~1 s after each WorldLoaded the real SR vacpack model is rendered
  (`VacpackIconRenderer`): MeshRenderer/SkinnedMeshRenderer under `WeaponVacuum` ('vac' body, 'Vac Display' gauge,
  'mesh_glass'; Beatrix's 'arms'/'mesh_l_armextra', inactive mochi/timer parts, FX/particles skipped), temporary ortho
  camera on their layers (31), 3/4 view from the left/above/slightly behind, rolled so the nozzle (`vacOrigin.up`) points
  to the upper-left; SR's lights reaching the model are switched off for the render and one white key light from the
  viewer's upper-left + neutral flat ambient used (deterministic day/night), fog off; renderers hidden by
  `SetVacpackActive(false)` are enabled just for the render. Rendered at 256² over black AND white → exact alpha for any
  SR shader (refractive glass, emission); cropped to the model, box-filtered to 30² in a 32² icon, alpha ≥ 0.4 → opaque,
  1px outline = darkened neighbour colour. Saved to `SC.DataDir/vacpack_icon.png` (used from the first frame of the next
  session) and `vacpack_icon_render.png` (256² render for inspection). Fallbacks: cached PNG → SR sprite
  `iconShopTank01` (pixelated the same way) → hand-drawn 16² placeholder. Everything touched is restored in the same call.
* **Meshes**: block items = unit cube (North −Z, East +X, textures unmirrored from outside, top "up" = north, bottom
  "up" = south — same convention as the Blocks module). Flat items are extruded pixel sprites in the Minecraft style
  (front faces −Z, 1/16 thick, side quad per opaque pixel edge bordering transparency, 0.1px UV shrink). All side faces
  point outward so none of them is back-face culled.
* **Commands**: feedback that has a Minecraft counterpart is looked up at runtime in the player's own `en_us.json`
  (`McLang.Format(key, fallback, args)` in Contracts; the lang key is only an identifier). When the key is missing,
  SlimeCraft's own English wording is shown instead, so no Minecraft text is shipped with the mod.
  `~` relative and `^` local coordinates. Built-ins: `/help /give /gamemode /tp /teleport /time /summon /kill /clear
  /setblock /fill /heal /newbucks /seed`. `/time set` fast-forwards SR time (`TimeDirector.FastForwardTo`, SR time can
  only go forward; day=07:00, noon=12:00, night=19:00, midnight=00:00; also ticks, `Nd`/`Ns`/`Nt`, `/time add`,
  `/time query daytime|day|gametime`). `/summon slimerancher:pink_slime` (or plain `pink_slime`) spawns SR actors in
  front of the player; MC mobs via SC.Entities. `/kill` clears Minecraft entities, `/kill @s` kills Beatrix.
  `/clear` never removes the vacpack. `ICommands.Complete()` / `RegisterCompleter()` provide Tab completion (command
  names without the slash, item/block/mob/SR actor ids; other modules register completers for their own commands).
* **Splash**: a yellow line under where Minecraft's logo would be, matching the look and feel of Minecraft's title splash:
  anchored at screen centre + 123 and 69 GUI units from the top (`SplashOffsetX/Y`), tilted 20° so it rises to the right,
  pulsing twice a second between 1.7× and 1.8×, shrunk for long lines so every splash has a similar length, and scaled
  with a Minecraft-style automatic GUI scale (largest of 1–8 that still leaves 320×240 GUI units). `McFont` rasterises
  the line once per pick from the player's own `font/ascii.png` (per-glyph widths measured from the sheet, 1px shadow at
  quarter brightness). A new line is picked on every visit to the title screen and shown only while the main title panel
  (`MainMenuUI`) is active: five in six picks come from the jar's `texts/splashes.txt` (non-ASCII lines skipped), one in
  six from a short list of SlimeCraft-written lines, and SlimeCraft's own holiday lines replace both on Dec 24–26, Jan 1
  and Oct 31.

## Config keys (section `Core`)
`MinecraftDir` (`%APPDATA%/.minecraft`), `MinecraftVersion` (`auto`), `InventoryKey` (I), `ChatKey` (Y),
`CommandKey` (None = type `/` in chat), `DropKey` (Z), `DebugKey` (F3), `HudToggleKey` (F8), `SuppressSRSlotKeys` (true),
`IconSize` (64), `AtlasUvInsetTexels` (0.02), `SoundVolume` (1), `AudioSources` (24), `SRDamageMultiplier` (4),
`WeaponsDamageSRActors` (true), `ShowSplash` (true), `SplashOffsetX` (123), `SplashOffsetY` (69), `VerboseLog` (false),
`SneakKey` (LeftControl, shared Minecraft sneak exposed as `IInputGate.SneakKey/SneakHeld`), `PaintDepthGray` (128),
`VacpackIconRender` (true), `VacpackIconGlass` (true), `VacpackIconOutline` (true),
`ForceEnglish` (true, bound lazily by ForceEnglishPatch).

## Harmony targets
`WeaponVacuum.Update` (prefix), `WeaponVacuum.UpdateSlotForInputs` (private, prefix), `PauseMenu.Update` (prefix),
`AutoSaveDirector.SaveGame` (private, prefix), `SceneContext.OnSessionEnded` (prefix), `BaseUI.Awake` / `BaseUI.OnDestroy`
(postfix, open SR window tracking), `PlayerState.CanBeDamaged` (postfix), `PlayerState.Damage` (prefix+postfix:
`PlayerDamaged` event with the health actually lost), `MessageDirector.SetCulture(CultureInfo,bool)`
(private, prefix — ForceEnglishPatch).

## Known limitations / needs in-game verification
* Translucent blocks use an unlit shader (SR has no lit alpha-blended shader we can rely on) → they don't darken at night.
* Paintlight `_Depth`: mid-gray 128 is assumed neutral (SR's own `dpt*` textures average 160–175, `objRoots02_dpt` ~13);
  if blocks/mobs look too dark/bright compared to SR terrain, tune `PaintDepthGray`.
* Vacpack icon: framing/lighting may need tuning — check the hotbar slot and
  `BepInEx/config/SlimeCraft/vacpack_icon_render.png`; `VacpackIconGlass=false` drops the glass tube if it looks bad.
* `/time set` takes up to ~5 s (SR fast-forward speed) and SR makes the player invulnerable while fast-forwarding.
* Unity linear rolloff uses minDistance 0.01 (OpenAL reference distance 0 in Minecraft) — equivalent in practice.
