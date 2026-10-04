# FirstPerson module (Order 400)

Steve's first-person right arm and the held Minecraft item, drawn inside Slime Rancher so that they look and move
like Minecraft's first-person hand. Implements `IFirstPerson` (`SC.FirstPerson`).

No Minecraft or Slime Rancher assets are bundled: the skin and item textures and the item display transforms are read
at runtime from the player's own Minecraft install (through `SC.Assets`).

## Files
| File | Purpose |
|---|---|
| `FirstPersonModule.cs` | `IModule` + `IFirstPerson` MonoBehaviour: lifecycle, 20 Hz tick clock + partial ticks, visibility rules, rendering, eat effects, bow FOV zoom. |
| `ItemInHand.cs` | Hand simulation state, advanced at 20 ticks/s: swing, equip height and attack-cooldown dip, hand sway, view bob, hurt tilt, hold animations (eat/drink, bow, block), eat-effect timing, bow FOV factor. |
| `ItemInHandPoses.cs` | Frame-side pose math for the same class: shared base pose, bare-arm pose and held-item pose, interpolated with the partial tick. Constant parts of each chain are pre-composed once. |
| `McPose.cs` | Rigid pose in Minecraft-style camera space (local-frame translate/rotate) + exact conversion to Unity (Z mirror). |
| `HandRig.cs` | Overlay camera + transform hierarchy, automatic free-layer choice, culling-mask/light-mask maintenance, depth choice, camera diagnostics log. |
| `PlayerArmMesh.cs` | Arm mesh from the 64x64 skin: arm box at texel (40,16) and optional sleeve box at (40,32) grown by 0.25 px, wide (4 px) or slim (3 px), unwrapped in Minecraft's skin layout. |
| `ItemDisplayResolver.cs` | Reads `firstperson_righthand` display transforms from the jar (`items/<id>.json` → model → parent chain). |
| `HeldItemVisuals.cs` | Item mesh/material lookup: own block cubes (+ atlas Cutout/Translucent/Emissive), `SC.ItemVisuals` for flat items, sprite-orientation auto-detection, own bow draw-stage meshes, fallbacks. |
| `BlockItemMesh.cs` | Held block cube laid out so its visible faces and texture orientation match a held block in Minecraft. |
| `SpriteMeshBuilder.cs` | Extruded pixel sprite for `bow_pulling_0..2` and fallback items. |
| `EatParticles.cs` | Eating crumb particles (Minecraft-style look and physics) with a fixed pool of 40. |
| `MeshBuilder.cs`, `FpLog.cs`, `FpConfig.cs` | Helpers, rate-limited logging, config. |

## Behaviour
All poses are built in a right-handed camera space (x right, y up, -z forward) as a chain of local translations and
rotations, then mirrored on Z into Unity. Time runs at 20 ticks per second; every frame interpolates between the last two
ticks, and nothing advances while SR is paused.

* **Arm** (shown only when the selected hotbar slot is empty; with an item in hand only the item is drawn). The arm sits
  at the lower right of the view and punches toward the centre when swinging. The mesh is built relative to the shoulder
  pivot; the pose adds the pivot offset (-5, 2, 0) px and a fixed 0.1 rad roll. Wide arm box (-3,-2,-2) 4x12x4, slim
  (-2,-2,-2) 3x12x4; the sleeve shares the arm's frame. Both boxes are single sided and alpha tested: in first person the
  sleeve's inner faces are always behind the opaque arm, and a coincident inward copy would z-fight with SR's material,
  which may render without back-face culling.
* **Held item**: rests at (0.56, -0.52, -0.72) (pushed down by 0.6 × the lowered fraction), swings in an arc on attack,
  then gets the item's own `firstperson_righthand` display transform from the jar (translation, X→Y→Z rotation, scale).
  Defaults when the jar can't be read: `item/handheld` and `item/generated` [0,-90,25] / (1.13,3.2,1.13) px / 0.68,
  `block/block` [0,45,0] / 0 / 0.4. The item definition's `swap_animation_scale` and `hand_animation_on_swap` are honoured.
* **Block items**: our own cube (`BlockItemMesh`), oriented so the same faces are visible as in Minecraft (top, a large
  side on the left and a narrow side on the right; a furnace/pumpkin/crafting-table front faces away). Material:
  `BlockAtlas.Emissive` for light-emitting blocks (glowstone, sea lantern, jack o'lantern... glow at night like the placed
  block), `BlockAtlas.Translucent` for translucent blocks, `BlockAtlas.Cutout` otherwise.
* **Swing**: 6 ticks. A new request restarts the swing unless it is still in its first half, so holding the button while
  mining gives a continuous mining swing. Between ticks a drop in progress is drawn as a wrap past 1, so the sweep always
  completes.
* **Equip / cooldown**: on an item change the hand lowers by up to 0.4 per tick, the new item takes over once the hand is
  below 0.1, and it rises again following the cube of the attack-cooldown fraction (cooldown = 20 / `ItemDef.AttackSpeed`
  ticks: 5 for most items, 12.5 for swords). Attacking or swinging at air restarts the cooldown, so the item dips and
  recovers; swings during block mining do not (decided one tick later from `MiningProgress`, which Blocks keeps >= 0 while
  mining). Attack swings are told apart with `SC.Input.Attack*`. `IFirstPerson.ItemUsed()` (called by Blocks when a
  placement or spawn egg consumed the item) drops the item out of view so it pops back up; `StartUsing` does the same.
* **Vacpack transition**: the vacpack is treated as an item that draws nothing, so switching to it lowers the Minecraft
  item (about 3 ticks) and switching back raises the new item or the arm from the bottom, while Hud toggles SR's vacpack.
* **Eat/drink**: the item swings to the mouth within a few ticks and bobs there until the last fifth of the meal. Food
  takes 32 ticks, the honey bottle (drink) 40. Crumbs (5 per burst, food only) and the optional eat/drink sound fire
  every 4 ticks once about 22 % of the meal has passed. Crumbs are camera-facing squares cut from the item texture that
  fall under gravity, slide on the ground and vanish after 4..40 ticks; they collide with the world through
  `ISRBridge.RaycastWorld`.
* **Bow**: raised draw pose with a draw power that reaches 1 after 20 ticks, a slight tension tremble, the bow moving
  closer and stretching 20 % along its depth. Draw-stage models `bow_pulling_0/1/2` switch after 13 and 18 use ticks.
  Bow FOV zoom (up to 15 %, smoothed by half the gap per tick) is applied to SR's camera on top of SR's FOV option and
  restored afterwards.
* **Block** (`UseAnimation.Block`): the classic sword-block guarding pose (no shield).
* **View bobbing**: driven by the horizontal distance walked per tick while grounded, applied to the hand only (SR's own
  camera bob is untouched). **Hurt tilt**: a roll of up to 14° (× `HurtTilt`) toward the damage source, fading over 10
  ticks. **Hand sway**: the hand trails fast camera turns by a tenth of the gap to a smoothed copy of the view angles.
* **Mining shake** (SlimeCraft addition): a faint jitter while mining that grows with the mining progress.

## Rendering design (Slime Rancher side)
SR renders the world with its main FPS camera (depth 0, culling mask without layer 31 "Weapon" and layers 8 and 13) and
the vacpack on layer 31 through a separate weapon camera. We add our own camera as a child of `SC.SR.MainCamera`:
* `clearFlags = Depth` → the hand never clips into terrain/slimes and always draws on top; vertical FOV 70, matching
  Minecraft's fixed hand FOV (independent of the FOV option and of the bow zoom), aspect = SR's viewport, near 0.05;
  Forward path; HDR off; no image effects. The bow zoom only changes SR's world camera.
* SR's post effects (in game: SSAO + bloom on the FPS camera, depth 0) are resolved before SR's weapon camera (depth 2,
  layer 31 only) and our camera (depth 3) draw, so SSAO/bloom never touch the hand and the hand never feeds their depth
  buffer.
* Depth: just above the highest camera of the player rig drawing to the same target, below the next camera (UI camera), or
  `CameraDepthOverride`.
* Layer: an unnamed user layer with no renderers in the scene, not in `SC.SR.WorldMask`, not `SC.SR.BlockLayer`
  (preference 30, 23, 31..8), logged. It is removed from every other camera's culling mask each frame (SR restores cached
  masks when cameras are re-enabled) and added to lights that light layer 0 or 31 (rescanned every 10 s) so the hand is
  lit like the vacpack. Renderers: no shadows, light probes on (darker in caves), no reflection probes.
* Hierarchy `Camera → HandRoot → Arm` and `→ ItemA → ItemPull(scale) → ItemB → ItemDisplay(scale) → ItemMesh`, so the
  non-uniform bow stretch and the display scale are represented exactly (no matrix decomposition).
* The camera is rebuilt whenever SR's main camera changes or was destroyed (world reload / return to menu: our camera is a
  child of SR's and dies with the scene; `WorldUnloading` also destroys it), with a fresh layer choice and light fix.
* Once per build, after our layer was stripped from SR's cameras, the cameras are logged in render order
  (`handLayer=yes/no`, masks, clear flags, FOV, image effects) followed by `Hand camera check OK` (only the hand camera draws
  the hand layer and it renders after every image-effect camera) or `Hand camera check FAILED` (warning).
* Once per visual and world, when it is fully raised and idle, `At rest, <item|empty hand (arm)> mesh box covers screen
  x a..b %, y c..d %` is logged (8 corners of the mesh's local box projected by the hand camera, y from the top). Expected
  values measured in Minecraft (FOV 70, 16:9): flat/handheld items x 72..183 y 31..225, block items x 65..101 y 73..168,
  arm x 70..112 y 68..192.

## Visibility
Hidden when: not in game, SR paused, `SC.SR.SRUIOpen`, `SC.Inventory` missing, the vacpack is the visible item, or the main
camera is disabled. Visible while Minecraft screens (inventory/chat) are open and when the MC HUD is toggled off (F8).

## Config (`[FirstPerson]`)
| Key | Default | Meaning |
|---|---|---|
| Enabled | true | Master switch. |
| HandFov | 70 | Hand camera vertical FOV. |
| ViewBobbing | true | Minecraft view bobbing on the hand. |
| RespectSRCameraBobOption | false | Disable hand bob when SR's "disable camera bob" option is on (SR default: on). |
| HurtTilt | 1 | Damage tilt strength (0 = off). |
| HandSway | true | Hand lag on fast camera rotation. |
| MiningShake | 1 | Subtle shake while mining (SlimeCraft addition, 0 = off). |
| SkinTexture | entity/player/wide/steve | Arm texture; paths containing `/slim/` use slim arms. |
| ShowSleeve | true | Sleeve overlay layer. |
| EatParticles | true | Eating crumbs. |
| EatSounds | false | Eat/drink sounds from this module (enable only if the interaction module doesn't play them). |
| BowFovZoom | true | Minecraft bow FOV zoom on SR's camera. |
| HandLayer | -1 | Force a layer (8-31), -1 = automatic. |
| CameraDepthOverride | -1000 | Force hand camera depth, -1000 = automatic. |
| AutoOrientItemMeshes | true | Detect and fix the facing of item sprite meshes from IItemVisuals. |

## Checking the pose
The `At rest, ... mesh box covers screen ...` log line makes it easy to compare a held item with Minecraft at the same
FOV and aspect ratio. A held diamond pickaxe at 1600x900 was checked this way: its size and placement match Minecraft's,
and so does the swing frame (item at the bottom centre). Block, empty-arm, eating and bow poses still need the same
in-game comparison.

## Known limitations / to verify in game
* Lighting comes from SR's scene lights + probes rather than Minecraft's light level; post effects don't apply to the hand.
* Flat item meshes are auto-oriented by inspecting UV gradients once (needs a readable mesh); if Core's convention is
  unusual, disable `AutoOrientItemMeshes` and report.
* The hurt-tilt direction comes from `ISRBridge.PlayerDamaged` (source position relative to the camera); unknown sources
  tilt as if hit from the left. There is no death tilt/FOV effect (SR has its own death sequence).
* `right_rotation` of item transforms (unused by the items we have) is ignored. Off-hand, left-handed mode,
  maps/crossbow/trident/spear/brush poses are not implemented (not needed by our item set).
