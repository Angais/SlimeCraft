# Blocks module (Order 200)

Owns `SC.Blocks` (`IBlockWorld`) and the player's Minecraft "hands" (everything LMB/RMB/drop does when the
vacpack is **not** the selected hotbar item). Namespace `SlimeCraft.BlocksMod`.

## Files
| File | What |
|---|---|
| `BlocksModule.cs` | `IModule` MonoBehaviour on `SC.Root`: config, service assignment, SR world events, persistence, per-frame driver. |
| `BlockWorld.cs` | `IBlockWorld`: chunked storage (`Dictionary<long, Chunk>`, packed chunk key), palette of `BlockDef`s, Get/Set/Break, voxel DDA raycast, dirty queue + rebuild (budget per frame, immediate for the player's edits), chunk GameObjects, collider groups, atlas/material tracking. |
| `Chunk.cs` | 16³ `ushort` palette ids + `byte` facing, render/collider objects (opaque, translucent, emissive renderers), translucent sort data. |
| `ChunkMesher.cs` | Face-culled meshing (opaque+cutout / translucent / emissive / 4 collider groups), MC texture orientation, optional baked face shading, back-to-front translucent sorting. |
| `Util.cs` | Direction tables + per-face corner tables + rotation resolver, MC sound volume/pitch rules, MC mining math, RNG, rate-limited logging. |
| `Interaction.cs` | Interaction controller shell: per-frame pipeline (gate → held item → aim → clicks → 20 Hz steps → drop key → visuals), `HandsClock` step source, cue dispatch (each `Hand` is told `Click`/`Down`/`Up` in the order of the `ClickOrder`/`StepOrder` tables), the use hand with its repeat scheduler, targeting, drop key. |
| `Interaction.Attack.cs` | Left button: a planner (`PlanAttack`: cue + crosshair → one `AttackMove`, no side effects) and a carrier (`Carry`), melee strike roll, miss lockout, and the mining state machine (`FeedMiner` with Engage/Work/Disengage events; phase Idle/Digging/Cooldown derived from the job data) over voxel cells and SR terrain. |
| `Interaction.Use.cs` | Right button: crafting table, place, flint & steel, spawn eggs, hold-to-use (eat, drink, bow). |
| `TerrainHarvest.cs` | Classifies Slime Rancher colliders into Minecraft "surfaces" (stone/dirt/sand/logs/leaves/ore...) and rolls their drops. |
| `SpecialBlocks.cs` | Slime-block bounce, honey slowdown, footsteps/fall sounds on our blocks, gravity blocks. |
| `BlockParticles.cs` | Block debris, mining chips and food crumbs: camera-facing textured quads with Minecraft-like gravity and drag, pooled (512 per material) and drawn with one dynamic mesh per material. |
| `Overlays.cs` | Crack overlay (destroy_stage_0..9 cube / SR-terrain decal) and the black selection outline. |
| `BlockLights.cs` | Pooled warm point lights for light-emitting blocks (nearest N, nearest few forced per-pixel, offset towards open sides). |
| `BlockLoot.cs` | Drops (incl. MC loot specials: glowstone dust, melon slices, gravel flint, leaf apples/sticks). |
| `BlockPersistence.cs` | `"blocks"` persistence section: `SCB1:` + base64(deflate(palette + per-chunk entries)). |

## Behaviour
* **Meshing**: faces are skipped against full opaque neighbours; cutout/translucent faces are skipped against the
  same block (glass-glass, leaves-leaves, ice-ice). Texture orientation matches Minecraft's look (side textures
  upright, top image-up towards north, bottom towards south, never mirrored in Unity's left-handed space).
  `Horizontal` blocks: `Faces[2]` (front) points at the player when placed, top/bottom textures rotate with it.
  `Axis` blocks (logs, hay): end textures on the clicked axis, bark grain along it.
  Directional shading (1 / 0.5 / 0.8 / 0.6) is baked into vertex colours **only** if the atlas material's shader
  uses vertex colours (`SR/Paintlight/*` does not: its vertex input is POSITION/NORMAL/TEXCOORD only).
  Each chunk has three renderers: `opaque` (`BlockAtlas.Cutout`, SR Paintlight look), `translucent`
  (`BlockAtlas.Translucent`, faces sorted back-to-front) and `emissive` (`BlockAtlas.Emissive`, full-bright unlit):
  faces of light-emitting blocks (`BlockDef.Light > 0` and not translucent: glowstone, sea lantern, shroomlight,
  redstone lamp, jack o'lantern) go to `emissive`, so they glow at SR night like MC light-level-15 blocks. If the atlas
  has no emissive material yet, the cutout one is used (logged in the `atlas materials:` line). Swapping a material
  re-assigns renderers without re-meshing.
* **Colliders**: per chunk, faces adjacent to air only, non-convex `MeshCollider`s on `SC.SR.BlockLayer`, split into
  4 groups with PhysicMaterials: normal, slime (bounciness 1, Maximum), ice (friction 0.02, Minimum),
  honey (friction 1, Maximum) — SR rigidbodies (slimes, food) bounce/slide/stick accordingly.
* **Mining**: break progress grows by `speed / hardness / (canHarvest ? 30 : 100)` per 20 Hz tick
  (`speed` = tier speed 2/4/6/8/9/12 with the right tool, swords 1.5 on leaves/pumpkins/melons, /5 in the air),
  hit sound every 4 ticks at `(vol+1)/8`, pitch ×0.5, crack stage `(int)(progress*10)`, 5-tick delay after a
  break, instamine without delay, creative: instant every 6 ticks, swords cannot break in creative,
  hardness -1 is unbreakable. Swapping the held item restarts the progress. Durability: tools 1 / swords 2 per block,
  swords 1 / tools 2 per hit. While the button is held the arm swings on every 4th tick and a small chip flies off the
  mined face each tick; releasing the button (or looking away) abandons the dig and drains the attack charge.
* **Attack**: attack strength `(ticker+0.5)/(20/attackSpeed)`, damage ×`(0.2+s²·0.8)`, crit ×1.5 while falling at
  full strength; `entity.player.attack.strong/weak/crit`; 10-tick miss time after swinging at air (survival).
  Reach: blocks 4.5 (creative 5), entities 3 (creative 5) like MC (configurable).
* **Use**: block interactions first (crafting table → `/screen crafting`, unless sneaking with an item), then
  item on block (place / flint & steel / spawn egg), then item use (eat 32 ticks, honey bottle 40, bow draw).
  Repeats every 4 ticks while held (one click = exactly one action). Placing against our block uses `hit block + face normal`; on
  SR terrain the cell is `floor(hit.point + hit.normal*0.5)`. Placement refused inside our blocks, inside the player
  (exact vertical-capsule test on SR's `CharacterController`, radius = height/4, 0.02 tolerance so pillaring and
  building next to yourself work; AABB `PlayerBounds` fallback) or bodies (non-kinematic rigidbodies bigger than an
  item, kinematic ones only if SR actor / Minecraft mob). Place/break sounds `(vol+1)/2`, pitch ×0.8.
  Eating (food 32 ticks, honey bottle 40): quiet for the first 21.875 % of the time, then a bite sound every 4 ticks
  (food: `entity.generic.eat`, honey: `item.honey_bottle.drink`; FirstPerson draws the matching crumb puffs), and on
  completion a final bite, 16 crumbs (food), a burp and the `SRHeal`/`SREnergy` reward. Releasing early cancels.
  Bow: draw power `(s² + 2s) / 3` capped at 1 (s = seconds drawn, full after 1 s; below 0.1 nothing is shot), arrow
  velocity `power*3` blocks/tick (×20 m/s) with a tiny random spread, base damage 2 passed to `SC.Entities.ShootArrow`
  (Entities computes `ceil(speed*2)`, the full-draw crit and plays `entity.arrow.shoot`), consumes an arrow and 1 durability (survival).
  Flint & steel: primes our TNT (`SC.Entities.SpawnPrimedTnt`), ignites creepers (`SC.Entities.Ignite`), else ignites
  SR `Ignitable`s (slimes hop, food burns; actor roots incl. children, other objects only themselves). Placing a block /
  using a spawn egg calls `SC.FirstPerson.ItemUsed()`. Survival eating needs SR energy or health below max (MC
  "needs food"); creative always eats (nothing consumed).
* **Drop key**: 1 item (sneak/Ctrl held: whole stack) thrown 0.3 b/t forward +0.1 up, 2 s pickup delay, arm swing; the
  stack is only removed once `SC.Entities.SpawnItem` returned an entity. Skipped in `auto` mode when the Hud module
  declares it handles the drop key (`[Hud] HandleDropKey`, default true), so items are never thrown twice.
* **Hands loop**: the hands never poll the mouse; the controller tells each hand a cue. Clicks act within their frame
  (attack hand, then use hand; not delivered at all while a hold-to-use runs). Holds run on fixed 20 Hz steps (at most
  5 per frame, a larger backlog is dropped); each step advances the step clock, then tells the use hand and then the
  attack hand `Down` or `Up`. Attack charge, miss lockout and use repeat are marks and deadlines on that clock. Each
  button is read (gameplay gate included) right before its hand is told, so if a use closes the gameplay gate
  mid-frame (crafting table screen), the rest of the frame reads both buttons as released. The attack hand plans one
  move per cue (`Ignore`/`Strike`/`Engage`/`Whiff`/`Dig`/`Halt`/`LetGo`) and then carries it out; clicks that are not
  ignored swing the arm afterwards. Mining phases: **Cooldown** (5 recovery steps after a block gives way, each held step
  spends one), **Digging** (survival, job matches target cell/spot and tool), **Idle** (the held step engages the
  target; creative breaks it instead and starts a new cooldown).
* **Input**: everything goes through `SC.Input` only (`AttackHeld/AttackPressed/UseHeld/UsePressed/SneakHeld/
  GameKeyDown`), never `UnityEngine.Input`, so `IInputGate.SetTestInput/SimulateKeyDown` drive the real code paths.
  Holding alone is enough (a missed press edge still mines/places: a held attack on a new target starts digging it,
  and a held use key starts a new use once the 4-tick use delay has run out); entity attacks need a press like MC.
* **SR terrain harvesting**: mining SR rocks/ground/trees with the same progress/crack mechanic (decal crack at the
  hit point) drops Minecraft items; SR geometry is never changed. Classification uses material, texture, mesh and
  object names (+ zone name for variants): rock/cliff → stone (drops cobblestone; reef → terracotta, quarry →
  deepslate, ash → netherrack, moss → mossy cobblestone, desert → calcite, ruins → stone bricks), dirt/ground →
  dirt (moss → podzol, ash → coarse dirt), grass → grass block (drops dirt), sand (reef → red sand, ash → gravel),
  bark/trunk → logs (oak/spruce/birch/cherry by zone), leaves → leaves + apple chance, quarry ore → ore table
  (coal/iron/gold/lapis/redstone/diamond/emerald honoring pickaxe tier), crystals → amethyst, quartz, glow → glowstone
  dust, mushrooms → shroomlight. Never harvested: anything with `Identifiable`, `LandPlot`, `LandPlotLocation`,
  `Gadget`, `GadgetSite`, `TeleportSource/Destination`, `ScorePlort`, `UIActivator`, `PuzzleSlot`, `TreasurePod`,
  `GordoEat`, `GordoIdentifiable`, `DroneStation`, `SiloStorage`, `AccessDoor`, `SlimeGateActivator`, `Vacuumable`,
  `ResourceCycle`, `PlortCollector`, `KookadobaPatchNode`, `LiquidSource` in its parents, dynamic rigidbodies,
  Minecraft entities, and names with building/liquid tokens (house, roof, plot, corral, silo, water, slime sea...).
* **Special blocks**: landing on a slime block with v.y < -2 m/s bounces back 95 % (not while sneaking);
  honey pushes back 20 %/tick of the horizontal velocity (feedback loop through the SR controller, ≈ MC 0.4 speed); footsteps every 1/0.6 blocks (`block.X.step`, vol 0.15); landing
  after > 3 blocks plays `entity.player.small/big_fall` + `block.X.fall`; sand/gravel/red sand fall (2 ticks after
  a change) when the cell below is air and no SR geometry supports them (5 short downward rays).
* **Particles**: 24 (configurable, clamped to 0..128) debris pieces per broken block, spread over a small lattice
  inside the block and flung outward with an upward kick (max 6 break effects per frame so explosions stay cheap);
  1 smaller chip per mining tick; a final 16-crumb burst in front of the face when food is eaten (the periodic crumbs
  are FirstPerson's). Each piece shows a random quarter-size window of the block's side texture (grass crumbles as
  dirt, front-facing blocks show their back), tinted 0.6 grey (item crumbs untinted), is 0.1-0.2 m wide, lives
  0.2-2 s, falls at 16 m/s² with 0.98/tick air drag, lands on our blocks (per-axis grid test) or on a floor height
  sampled once from the SR ground below its spawn point, and slows with 0.7/tick ground friction.
* **Lights**: the block is full-bright (emissive renderer); a warm (1, 0.85, 0.62) point light makes it light its
  surroundings: range `PointLightRange × Light/15` (12 m for level 15), intensity `PointLightIntensity` (1.2), no
  shadows, nearest `MaxLights` (24) within 6 ranges of the camera, the nearest `PixelLights` (8) forced per-pixel
  (`LightRenderMode.ForcePixel`, so SR's quality pixel-light count cannot drop them). The light sits 0.6 m from the
  block centre towards its open sides (horizontal + up; below for hanging blocks) so the ground/walls around the block
  are lit, not only surfaces facing its centre. Re-evaluated every 0.5 s and immediately on emitter changes.
* **Unload → reload**: `WorldUnloading` resets the controller (mining/eating/outline/crack), special-block queues,
  particles, harvest caches and destroys every chunk object/mesh/collider (`colliderIds` cleared, pooled lights off).
  Persistent helpers (block root, light pool, crack/outline objects, particle batches) are `DontDestroyOnLoad` and are
  recreated if Unity destroyed them; chunks re-mesh from the loaded `"blocks"` section (`[Blocks] world built: ...`).

## Action log (`[Blocks] LogActions`, default on; rate-limited, "+N more" counts dropped lines)
Numbers are culture-invariant. Lines start with `[Blocks] `:
`mining <id> at (x, y, z) with <tool|hand>: <p>/tick -> N ticks (T s), correct tool for drops: yes|no[, airborne]`,
`block broken: <id> at (x, y, z) with <tool> in T s (N ticks)|instantly (instamine)|(creative, instant), drops: ...`,
`block placed: <id> at (x, y, z) facing <F> against <id> (x, y, z)|on SR terrain '<collider>' (x, y, z)`,
`placement refused: ... : cell already holds <id> | inside the player | occupied by '<name>'`,
`SR surface classified as '<kind>' (acts like <id>, drops <id>)|(not harvestable): collider '<n>' names '...' materials '...' parents '...'`
(once per collider), `terrain harvested: '<kind>' (acts like <id>) from collider ... -> drops: <id> xN`,
`food eaten: <id> after 32 ticks -> SR health a->b/max (+h), energy c->d/max (+e)`, `can't eat ...`,
`flint and steel primed TNT at ...`, `spawn egg <id> at ... -> spawned '<name>'`, `bow fired after N ticks: power P, V m/s`,
`attacked '<name>' with <tool>: damage D (strength S[, critical])`, `dropped Nx <id>`, `crafting table used at ...`,
plus `atlas materials: cutout=... translucent=... emissive=...`, `block lights: N active ...` (first time per world),
`world unloading: clearing ...` and `world built: N blocks in M chunks (... colliders, ... with glowing faces) ...`.

## Config (`[Blocks]`)
`MeshBudgetPerFrame` (4), `MaxLights` (24), `PointLightIntensity` (1.2), `PointLightRange` (12), `PixelLights` (8),
`LogActions` (true), `FaceShading` (auto/on/off), `BreakParticles` (24),
`HarvestSRTerrain` (true), `DebugHarvest` (false: logs the names used to classify each mined SR collider),
`SlimeBounce` (0.95), `HoneySlowdown` (0.2), `OutlineWidthPx` (2; ≤1 = GL lines),
`StepSounds` (true), `ReachSurvival` (4.5), `ReachCreative` (5), `EntityReachSurvival` (3), `EntityReachCreative` (5),
`LeafAppleChance` (0.05), `DropKeyHandling` (auto: this module throws items unless `[Hud] HandleDropKey` is true; on; off).
(The old `LightIntensity` key is no longer read; BepInEx leaves it in existing config files.)

## Known limitations
* Ice is slippery for SR rigidbodies only (PhysicMaterial); the SR player controller is not made to slide.
* Flint & steel cannot place fire. Sneak uses the shared `[Core] SneakKey` (Left Ctrl) through `SC.Input.SneakHeld`.
* Point lights only light materials whose shader has a forward-add pass. Faces exactly coplanar with an emitter on
  both of its open sides (e.g. a wall around a glowstone set into it, open front and back) stay unlit.
* Emissive faces get no MC directional face shading (the unlit atlas shader ignores vertex colours).
* SR terrain classification is name-based; unknown surfaces yield nothing (enable `DebugHarvest` to tune).
* No fall damage (Slime Rancher has none); only the Minecraft fall sounds.
