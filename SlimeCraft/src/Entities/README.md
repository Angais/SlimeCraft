# Entities module (Order 300)

Owns `SC.Entities` (`IEntities`) and `SC.Explosions` (`IExplosions`). Everything Minecraft that moves: dropped items,
primed TNT, falling blocks, arrows, explosions and ten Minecraft mobs, living inside the real Slime Rancher world.

All textures and sounds (mob skins, particles, fire, sound events) are read at runtime from the player's own Minecraft
install through `SC.Assets` / `SC.Audio`. Nothing from either game is bundled with the mod.

## Files
| Path | What |
|---|---|
| `EntitiesModule.cs` | `IModule` + `IEntities`: lifecycle (WorldLoaded / WorldUnloading), spawn APIs, `RaycastEntity`, `AttackEntity`, persistence (`mobs`), `/explode`, `/mobs` and `/mobspawning` commands (registered only if Core has none with that name). |
| `Core/EConfig.cs` | BepInEx config entries (section `Entities`). |
| `Core/EUtil.cs` | `Rng` (Minecraft-style random helpers), `ELog` (rate limited errors + rate limited gameplay `Event` lines, culture-invariant `V()`), `Mc` (tick/block unit conversions, `WalkSpeed`), `ELayers` (layer decision + masks), `EPhys` (allocation-free queries). |
| `Core/McEntity.cs` | Base of every entity: registry, child colliders, Minecraft-style rigidbody, ground probe, SR `DeathHandler.Interface`. |
| `Objects/DroppedItem.cs` | `DroppedItem`: dropped items (+ pickup fly animation). |
| `Objects/LitTnt.cs`, `FallingBlockBody.cs`, `FlyingArrow.cs` | Primed TNT (`LitTnt`), sand/gravel in motion (`FallingBlockBody`), arrows (`FlyingArrow`). |
| `Explosions/ExplosionSystem.cs` | `IExplosions`: ray-marched block destruction, entity/player damage and knockback, SR actor push, effects; plus forwarding of SR explosions to our entities. |
| `Explosions/ExplosionPatches.cs` | Harmony postfix on Slime Rancher's `PhysicsUtil.Explode`. |
| `Model/RigSpec.cs` | Rig data: `RigSpec` (skin size, origin pose, flat bone array with parent indices), `BoneSpec`, `BonePose` (Offset / Euler / Scale), `RigBox` (min corner, size, pad, skin origin, flip, vertical skin stretch). |
| `Model/RigSheet.cs` | Parser of the line-based rig sheet text format (`skin`, `origin-scale`, `bone NAME [on P] [at X Y Z] [turn degrees] [scale S]`, `box U V from X Y Z size W H D [pad P] [flip] [stretch-v K]`). |
| `Model/MobRigs.cs` | Rig sheets for every mob and the arrow. |
| `Model/RigBaker.cs` | Bakes rigs into Unity meshes (unfolded-box UVs from a face table, flipped boxes, zero-thickness cards), Euler/pose math, flat baking (arrow), cache per key. |
| `Model/RigInstance.cs` | Spawned rig: one GameObject per `Bone` (runtime `Offset` / `Euler` / `Scale` / `Shown`, `RestPose`), `Rest()` / `Push()`, follower rigs that copy a leader's pose by bone name, extra render layers, hurt/flash overlay. |
| `Mobs/MobDef.cs` | Per-mob stats (health, hitbox, speed, sounds, follow range...). |
| `Mobs/McMob.cs`, `McMobAI.cs` | Mob base: physics, health/hurt/death, fire, water, sounds, look control, `Stride` walk cycle, `PoseInput` snapshot per frame, shared behaviours (stroll, look around, panic, targeting). |
| `Mobs/Posing.cs` | Declarative pose pieces: `PoseInput`, `Stride`, `PoseTerms` (gait, look), `QuadLegs.Trot`, `BipedPose` (every biped bone built from gait + arm style + melee chop + breathing sway; `Stiffen` for the enderman). |
| `Mobs/Behaviours.cs` | Reusable behaviour components: `KiteAndShoot` (generic archer tuned by `KiteTuning`, composed of `SightMemory`, `OrbitChoice` and the `BowCycle` Idle/Drawing/Cooldown machine), `FuseTimer`, `Hopper`, `SquashSpring`, `WingFlutter`, `EggClock`. |
| `Mobs/HumanoidMobs.cs` | Zombie, skeleton (a `KiteAndShoot` archer), enderman (calm / hostile moods, blinking with a portal trail). |
| `Mobs/SpecialMobs.cs` | Creeper (`FuseTimer`), Minecraft slime (`Hopper` + `SquashSpring` kicked by the `OnLanded` / `OnLeftGround` contact hooks), iron golem. |
| `Mobs/AnimalMobs.cs` | Pig, cow, sheep, chicken (`WingFlutter`, `EggClock`). |
| `Spawning/MobSpawnDirector.cs` | `MobSpawnDirector`: natural spawning / despawning, rejection statistics, `[spawn-debug]` summary, `/mobspawning status`. |
| `Spawning/SpawnRules.cs` | `SpawnRules.CheckStand` (shared by spawning and enderman teleports) + `SRZones` (ranch / region queries on SR's `PlayerZoneTracker` and `RegionRegistry`). |
| `Visuals/EMat.cs`, `Particles.cs`, `FireFx.cs` | Materials (SR look via `SC.ItemVisuals`, fallbacks), particle engine, burning flames. |

## Features
* **Dropped items** – 0.25 hitbox, gravity 0.04 b/t², drag 0.98, ground friction `slipperiness*0.98`
  (reads our block below), bob `sin(age/10+bobOffs)*0.1+0.1`, spin `age/20+bobOffs` rad, blocks at 0.25 scale / items
  0.5, 1-5 rendered copies depending on the stack size (seeded jitter, flat items stacked in Z), merge with
  identical neighbours (bbox inflated 0.5), 6000 tick despawn, pickup when the player's box inflated (1,0.5,1) touches
  (1.5 horizontal), `entity.item.pickup` vol 0.2 pitch `((r1-r2)*0.7+1)*2`, 3-tick fly-to-player animation, leftovers stay.
  Inventory full: retried 4×/s (no per-frame allocation). Items float in SR water like Minecraft items do under water
  (no gravity, +5e-4 b/t up, 0.99 horizontal drag) and vanish in the slime sea (KillOnTrigger).
  Items in an SR region that is unloaded (proxied, no terrain colliders) are held kinematic instead of falling out of the world.
  **SR vacpack**: inside `SC.SR.VacRay` cone (30°, 10 m, line of sight) items are sucked to the nozzle and collected at 1.2 m.
  **SR food bridge**: items whose `ItemDef.SRFoodEquivalent` is set and that come within 1.2 m of an SR actor whose id ends
  in `_SLIME`/`_LARGO` **and whose diet contains that food** (`SlimeEat.DoesEat(Identifiable.Id)`) turn into
  `min(count,5)` SR actors via `SC.SR.SpawnSRActor` (the rest stays as an item). All equivalents in Content exist in
  `Identifiable.Id` (POGO_FRUIT, GOLD_PLORT, CARROT_VEGGIE, OCAOCA_VEGGIE, CUBERRY_FRUIT, ROOSTER, HEN); an unknown id
  disables the bridge for that item with one warning. The diet check means veggie-only slimes never get live hens next
  to them; GOLD_PLORT is not part of SR's plort food group, so no slime eats it and golden apples stay Minecraft items
  (no free gold plorts). Content should map golden_apple to a real food to re-enable it.
* **Primed TNT** – pop velocity, gravity/drag/ground bounce damping, smoke every tick, white flash (75 % white when
  `(int)fuse/5 % 2 == 0`), swell `1+g*0.3` in the last 10 ticks, `entity.tnt.primed`, explodes with power 4 at y+1/16.
* **Explosions** – rays are marched outward from the centre in 1352 directions (the surface points of a 16³ grid), each
  starting with `power*(0.7..1.3)`, stepping 0.3 blocks and losing 0.225 per step plus `(resistance+0.3)*0.3` per block
  passed. Blocks come from `SC.Blocks`; rays stop at SR terrain (one raycast per ray) and SR terrain is never destroyed.
  Hit TNT becomes primed TNT with a 10..29 tick fuse; other blocks are removed without per-block sounds and drop with
  probability `1/power` (merged stacks ≤16, small random pop). Entity damage `((i²+i)/2*7*2r+1)` with
  `i = (1-dist/2r)*exposure`, exposure = share of up to 36 sample points of the hitbox with a clear line to the centre;
  knockback `dir*(1-dist/2r)*exposure` b/t; dropped items in the blast are destroyed. Player damage × `PlayerDamageScale`,
  knockback via `SC.SR.AddPlayerVelocity`. SR actors: `SC.SR.SRExplode(center, 2*power,
  power*SRExplosionPowerPerMcPower, 0, 0, false)`. Effects: an explosion emitter (8 ticks × 6 large explosion puffs,
  ±4 blocks) or a single puff for small blasts, up to 400 poof/smoke debris particles inside a sphere of radius `power`,
  `entity.generic.explode` vol 4 pitch `(1+(r1-r2)*0.2)*0.7`, SR `ScreenShaker.ShakeDamage` when the player was not
  damaged (SR already shakes on damage). Blocks are removed only after the damage steps, so they still shield entities
  and the drops / chain TNT of a blast are not hit by it.
* **SR explosions → Minecraft entities** – Boom slime (and any `PhysicsUtil.Explode`) pushes/damages our mobs,
  items and TNT through a Harmony postfix (ours are skipped).
* **Falling blocks** – fall with Minecraft gravity, re-place themselves with `SC.Blocks.SetBlock` at the cell they rest in
  (or drop the block's item when occupied / after 30 s / when they come to rest on a mob, item or SR actor).
* **Arrows** – raycast projectile (gravity 0.05, inertia 0.99 per tick), damage `ceil(|v|*base)` + crit
  `random(dmg/2+2)` (crit when shot by the player at ≥2.9 b/t i.e. full draw) with crit particles, knockback 0.4,
  hits mobs / SR actors (`HitSRActor`) / the player (skeleton arrows), sticks into terrain or blocks for 1200 ticks with
  the 7-tick shake, falls again if its block disappears, player arrows can be picked up (creative: just removed).
  Mesh = the baked arrow model (two crossed cards + end cap) with `entity/projectiles/arrow.png`.
* **Mobs** (creeper, zombie, skeleton, pig, cow, sheep, chicken, slime, enderman, iron golem):
  * geometry matches Minecraft's mob shapes and texture layouts so the player's own textures map correctly
    (pig/cow/chicken *temperate* variants with 64×64 layouts, sheep wool + undercoat tinted by dye colour, slime inner
    cutout + translucent outer shell, enderman eyes unlit layer, golem crack layers by health);
  * animations with Minecraft's look and feel: four-legged diagonal walk, two-legged walk with melee chop and idle arm
    sway, zombie arms forward, skeleton bow pose, enderman stiff gait + open jaw + shaking, creeper trot + swell scale +
    white flash, chicken wing flutter, sheep grazing head, iron golem arm/leg swing + walk rocking, slime squash and
    stretch; walk animation state (`min(dist*4,1)`, 0.4), head look control (75° yaw), red hurt overlay 10 ticks,
    death fall-over (20 ticks) then 20 poof particles;
  * physics: rigidbody with MC gravity 0.08 b/t², drag 0.98, ground friction 0.546, air 0.91, jump 0.42 b/t; ground speed
    `44*(movement_speed*modifier)²` m/s (the steady state of Minecraft's movement formula); jumps over 1-block obstacles,
    refuses >4 block drops (except chasing), side-steps when stuck, floats in SR water (LiquidSource), dies in the
    slime sea (KillOnTrigger), fall damage `ceil(fall-3)` (chicken/golem immune), LOD freeze beyond 80 m and while the SR
    region under the mob is unloaded (SR proxies far regions and deactivates their colliders);
  * AI at 10 Hz: stroll/look-around behaviours, zombie chase + melee (and targets iron golems), skeleton bow attack
    (20 tick draw, 40 tick reload, strafing within 15 blocks, 1.6 b/t arrows, aim spread for Normal difficulty), creeper
    fuse (starts within 3 blocks, aborts beyond 7, 30 ticks, power 3, griefing config), slime hops (10-30 tick delay, /3
    while hunting), contact damage, split into 2-4, enderman stare detection, teleports (daylight, water, non-melee
    damage 90 %, always dodges arrows, toward far targets) with portal particles, iron golem hunts hostiles except
    creepers (7.5+rand(15) dmg + 0.4 b/t fling), retaliates against the player; passives panic for 5 s when hurt
    (cow 2.0, pig/sheep 1.25, chicken 1.4), chickens lay eggs every 5-10 min, zombies/skeletons burn in SR daylight under
    open sky (8 s fire, 1 dmg/s, fire overlay + flames);
  * health/damage: MC max health, 20 tick invulnerability (excess-damage rule), knockback 0.4, MC loot (cooked meat when on
    fire), player damage × `PlayerDamageScale`, creative players are ignored by hostiles;
  * sounds from the player's `sounds.json`: ambient (Minecraft's random ambient timer), hurt, death, step (every 1/0.6
    blocks; block sound when the mob has none), slime jump/squish (small variants), creeper primed (pitch 0.5), skeleton
    shoot, enderman stare/scream/teleport, golem attack/damage, chicken egg.
* **Particles** – one runtime atlas built from the player's sprites (`explosion_0..15`, `generic_0..7`, `flame`,
  `critical_hit`), one dynamic mesh, back-to-front sorted, simulated at 20 TPS with interpolation; every effect is a row
  in one behaviour table (lifetime, size, colour, velocity, gravity, drag, sprite sequence, size over life, motion).
* **Burning** – the two fire textures are composed into one 32-step strip so one shared material animates every burning
  mob; flame sheets (count from the hitbox height, 1.4 × width wide) are turned towards the camera around Y only.
* **Natural spawning** (`MobSpawnDirector` + `SpawnRules`) – one cycle every 1.5 s (first one 3 s after the world loads):
  * SR night (`SC.SR.IsNight`, 18:00-06:00): one hostile (zombie 40 % / skeleton 30 % / creeper 25 % / enderman 4 % /
    slime 1 %), skipped while the player is on The Ranch (SR `PlayerZoneTracker` zone RANCH) unless `SpawnOnRanch`;
    SR day: a group of 1-3 animals when the zone name matches `PassiveZones`.
  * caps: hostiles (zombie, skeleton, creeper, enderman, slime) and animals (pig, cow, sheep, chicken) are counted
    **separately** within 96 blocks; iron golems count for neither.
  * up to 10 attempts: random column 24-44 blocks (horizontal) from the player, `RaycastNonAlloc` down from player.y+32
    (80 m, `ELayers.World` = `SC.SR.WorldMask` minus player/water/ignore-raycast/weapon, **triggers ignored** so SR's big
    trigger volumes can never fill the hit buffer), hits sorted by distance; steep (normal.y < 0.7) or too-high
    (> +24) surfaces are skipped to try the surface below, surfaces < -24 stop, SR actors (gordos...) refuse the column;
    the first 3 candidates go through `SpawnRules.CheckStand`:
    never inside Minecraft blocks (every cell of the mob box), SR region loaded, **no player block within 24 blocks
    above** (= inside / under the player's structures; spawning ON TOP of blocks is allowed like Minecraft), not in
    The Ranch's regions for hostiles (`RegionRegistry.GetContaining` + `Region.GetZoneId`), no water / LiquidSource /
    KillOnTrigger at legs or chest (trigger overlaps), free capsule (World + mobs + SR actors), **not inside SR geometry**
    (upward ray with and without `Physics.queriesHitBackfaces`: a back face first = we are under a floor), and for
    hostiles no Minecraft light block (glowstone, sea lantern, jack o'lantern, shroomlight, lamp) with
    `Light - manhattanDistance > 0` (Minecraft: hostiles need block light 0).
  * the first 7 attempts also require a spot the camera cannot see.
  * despawn: natural hostiles beyond 64 blocks at once, beyond 32 blocks with Minecraft's 1/800 per tick once 30 s old,
    any hostile beyond 128 blocks, anything 64 blocks below the player.
  * every rejection is counted (`SpawnReject`); every 30 s an Info line `[spawn-debug] last 30 s: ...` summarises cycles,
    attempts, spawns, skips (ranch / cap / zone) and rejection reasons (config `SpawnDebugSummary`).
* **Persistence** – `SC.Persistence.Register("mobs")`: passive animals and iron golems (id, position, yaw, health, variant,
  chicken egg timer); hostiles are not saved. Restored 1.5 s after the world loads; a save that happens before the
  restore ran (SR autosave right after loading, quitting at once) writes the pending data back instead of an empty list.

## Commands
* `/mobspawning on|off` – natural spawning (config `NaturalSpawning`, saved immediately).
* `/mobspawning ranch on|off` – hostiles on The Ranch (config `SpawnOnRanch`, saved immediately).
* `/mobspawning status` – settings, day/night, zone, hostile/animal counts vs caps, totals and top rejection reasons,
  outcome of the last cycle.
* `/mobspawning now` – runs one spawn cycle immediately (same rules) and returns its outcome.
* `/explode [power]`, `/mobs` (only if Core does not define them).

## Log lines (BepInEx `LogOutput.log`, prefix `[Entities]`, rate limited per key)
| Event | Example |
|---|---|
| natural spawn | `spawned minecraft:zombie at (12.3, 8.0, -40.1) (Dry Reef, night, 31 m from the player, attempt 2)` |
| spawn summary (30 s) | `[spawn-debug] last 30 s: night, zone 'Dry Reef', hostiles 3/10, animals 0/8 \| cycles 20, attempts 120, spawned 3 \| skipped: ranch 0, cap 0, zone 0 \| rejected: Visible 40, NoGround 21, ...` |
| despawn | `despawned minecraft:creeper at (...) (far away)` |
| API / egg / command spawn | `SpawnMob minecraft:cow at (...) (egg / command / API)` |
| item pickup | `item picked up: 3x minecraft:dirt (vacpack)` |
| food bridge | `MC food converted into SR food: 2x minecraft:carrot -> 2x CARROT_VEGGIE next to PINK_SLIME at (...)` / `SR food bridge: PHOSPHOR_SLIME does not eat HEN (...)` |
| explosion | `TNT exploded at (...), power 4: blocks destroyed 12 (+2 TNT primed), item stacks dropped 3, SR actors pushed 6, MC mobs hit 2 (1 killed), items destroyed 0, player damage 0 SR hp` (labels `TNT`, `Creeper`, `/explode`, `Explosion` for other callers; ` (block damage off)` is appended after the block count when blocks are not affected) |
| SR explosion | `SR explosion of 'slimeBoom(Clone)' (BOOM_SLIME) at (...) pushed 3 Minecraft entities` |
| creeper | `creeper ignited with flint and steel at (...)`, `creeper exploded at (...) (target in range)` / `(ignited with flint and steel)` |
| mob killed | `mob killed: minecraft:zombie at (...) by PlayerAttack (player kill), drops: 1x minecraft:rotten_flesh` |
| commands | `/mobspawning ranch on -> Hostile mobs on The Ranch ALLOWED ...` |
* **RaycastEntity** – SphereCast radius 0.1, nearest first: our living mobs (`IsMcMob`), SR actors
  (`SC.SR.GetSRActorId != null`, target = attached rigidbody), solid world in front blocks the hit; items/TNT/arrows are
  transparent to it.

## Key design decisions
* **Colliders on child objects** – SR's `PhysicsUtil.Explode` / `KillOnTrigger` / meteor magnetism only act on colliders
  whose own GameObject has a Rigidbody, so they never touch our entities; we handle SR explosions via the Harmony postfix
  and the slime sea / water via periodic trigger overlaps. This avoids double knockback from our own `SRExplode`.
* **Layers** – decided at WorldLoaded from `Physics.GetIgnoreLayerCollision` (logged): mobs/TNT/falling blocks on the
  first of Actor(15)/Default(0) that collides with Default, `SC.SR.BlockLayer`, the player (8) and itself; items and
  arrows on ActorIgnorePlayer(11) if it collides with terrain/blocks but not the player, otherwise the mob layer plus
  per-collider `Physics.IgnoreCollision` with the player.
* **Coordinate conversion** – rig sheets are authored in pixels with +Y down and the face towards -Z. Showing them in
  Unity's left-handed frame reduces to a point reflection: bone pivots are negated (px/16), bone rotations are applied
  as declared (X, then Y, then Z), box vertices are negated and the triangle order flipped, and the rig origin sits at
  y = 1.501 so the feet touch the ground. Every frame each rig is put back into its rest pose (`Rest()`), the mob's
  pose function writes `Bone.Offset` / `Bone.Euler` in rig space from a `PoseInput` snapshot, and `Push()` moves the
  transforms; follower rigs (sheep wool, slime shell) then copy the pose through a bone map built once.
* **Behaviour components** – mob AI is built from small owned components that only decide (`KiteAndShoot` returns a
  plan: circle or approach, which way, loose now; `FuseTimer` reports lit / spent; `Hopper` says when to leap); the mob
  turns those answers into movement, sounds and attacks. The archer's bow is an explicit phase machine (`BowCycle`:
  Idle → Drawing → Cooldown, one cue per decision step), and body reactions such as the slime's squash and stretch are
  driven by the base class's ground contact hooks (`OnLeftGround`, `OnLanded`) instead of per-frame edge checks.
* **Overlays** – the hurt tint (red 30 %) and the TNT/creeper white flash (up to 75 %) are drawn as an extra pass of the
  same mesh using `Sprites/Default` and a white alpha mask of the texture, coloured per renderer via
  MaterialPropertyBlock; this does not depend on how SR's paint-light shader treats `_Color`.
* `SpawnPrimedTnt(position)` takes the **cube center**; `SpawnItem(position)` the item's bottom; `SpawnMob(feet)`.
* `ShootArrow(..., damage)`: `damage` is Minecraft's **base damage** (2.0 for a normal arrow; ≤0 → 2). For player shots it
  plays `entity.arrow.shoot` (pitch `1/(r*0.4+1.2)+power*0.5`).
* `AttackEntity(target, damage, dir)`: MC mobs get knockback `0.4 * clamp(|dir|,1,3)` (pass a longer vector for sprint
  knockback); SR actors get `HitSRActor(target, damage, dir.normalized*8 + up*3)`.
* `Ignite(target)` (flint & steel, called by Blocks): a creeper's fuse is lit for good (it stops and explodes regardless
  of its target); returns false for everything else. `LiveMobs` returns a snapshot of living mob GameObjects.
* Our `SRExplode` calls pass 0 player damage, so Core leaves the player out of the SR blast (no 0-damage hurt flash, no
  second SR push): the player only gets the Minecraft explosion damage + knockback computed by `ExplosionSystem`.

## Config (`BepInEx/config/com.angais.slimecraft.cfg`, section `Entities`)
`NaturalSpawning` (true), `HostileCap` (10), `PassiveCap` (8), `SpawnOnRanch` (false – hostiles only), `PassiveZones`
("Ranch,Moss"), `PlayerDamageScale` (5), `PlayerKnockbackScale` (0.5), `SRExplosionPowerPerMcPower` (200 → TNT 800,
creeper 600 = Boom slime), `ExplosionsBreakBlocks` (true), `MobGriefing` (true), `ExplosionsDestroyItems` (true),
`SRFoodBridge` (true), `VacpackPullsMobs` (true), `ScreenShake` (true), `MaxParticles` (1536), `MobActiveDistance` (80),
`SpawnDebugSummary` (true).

Explosion sources: primed TNT passes itself as the damage source (SR's hurt direction) and its igniter as the attacker
mobs react to; chain-primed TNT inherits that igniter. Creepers pass themselves.

## Known limitations
* No pathfinding: mobs steer straight at targets, jump 1-block steps, side-step when stuck; they can get stuck in
  complex SR geometry.
* Mob sun burning uses an upward raycast (no light levels); SR "night" = `SC.SR.IsNight`.
* Light blocks block hostile spawning by Manhattan distance only (no occlusion by walls).
* The "inside SR geometry" test assumes SR terrain meshes are one-sided surfaces facing outward; check the
  `InsideGeometry` count in `[spawn-debug]` if spawns look too rare somewhere.
* The skeleton's bow placement in the hand was tuned by eye.
* The enderman does not pick up or carry blocks; sheep cannot be sheared (grazing changes nothing in the world).
* Explosion block damage uses Minecraft resistance only for our blocks; SR terrain simply stops rays.
* Spawning and logging behaviour is best checked in game through the log lines above.
