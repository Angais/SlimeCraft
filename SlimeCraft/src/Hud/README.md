# Hud module (Order 100)

Owns **`SC.Inventory`** (`PlayerInventory : IPlayerInventory`) and **`SC.Hud`** (`HudModule : IHud`).
A pixel-perfect, Minecraft-style GUI drawn with UGUI and built entirely in code. Every sprite, font page and
container texture is read at runtime from the player's own Minecraft jar through `SC.Assets`; nothing from
Minecraft or Slime Rancher is bundled.

## Rendering architecture
* `HudModule` creates a `Canvas` (ScreenSpaceOverlay, `sortingOrder 32000`, no raycaster) with a `CanvasScaler`
  in ConstantPixelSize mode whose `scaleFactor` is the **integer GUI scale**. "Auto" picks the largest scale that
  still leaves at least 320x240 GUI pixels; the `GuiScale` config value caps it.
* `Gfx/Gui` is our immediate-mode drawing surface: `DrawSprite` (whole sprite, honouring the stretch / tile /
  nine-slice scaling declared in its `.png.mcmeta` `gui.scaling`), `DrawSheetPart` (an unscaled block of a sprite or
  of a 256x256 container sheet), `DrawRegion`, `DrawTexture`, `DrawSRSprite`, `Fill`, `DrawGradient`, `Text`,
  `TextOnAxis` (centred on a vertical line), `DrawItem`, `DrawItemOverlay` (wear gauge + count), `DrawTooltip`,
  `LayerBreak` (start a new batch drawn above everything so far) and a 2D transform stack (translate / scale /
  rotate) for titles and splash text. Image calls take the destination rectangle first, then the source rectangle,
  then the logical size of the image. Nine-slice splits each axis into border / middle / border pieces, and an
  axis drawn at its natural length stays one piece. Coordinates are GUI pixels with the origin at the top-left, the
  same space Minecraft's GUI uses, so layouts can be expressed with the same numbers players know.
* Draw requests are batched into pooled `GuiBatch : MaskableGraphic` children (one per run of the same texture,
  in request order, so later draws are always on top). A content hash avoids canvas rebuilds when nothing changed.
* `Gfx/GuiAtlas` packs every GUI sprite, container texture, font page and a small white block into one runtime
  2048² point-filtered atlas, so almost the whole HUD (sprites + text + fills) is a single batch. Item icons
  (`SC.ItemVisuals.GetIcon`) and Slime Rancher sprites are drawn from their own textures.
* `Gfx/McFont` builds its glyph table from the jar's font definitions (`font/default.json`, which pulls in the
  space provider and the `nonlatin_european`, `accented` and `ascii` bitmap pages). The first definition of a
  character wins; a glyph's advance is its visible pixel width scaled to the provider height (rounded half up)
  plus one pixel, and glyph tops sit at `7 - ascent`. Text gets a one-pixel drop shadow at a quarter of the text
  brightness, honours the `§` colour and style codes (bold, italic, underline, strikethrough, scrambled, reset),
  and wraps at spaces while carrying the active formatting onto the next line.
* `Gfx/Argb` holds the packed `0xAARRGGBB` colour helpers and a small generator that reproduces the
  `java.util.Random` sequence, so the heart wobble and hunger shake follow the pattern Minecraft players expect.

## Features
* **HUD** (`Overlay/HudLayer`): crosshair, hotbar + selection, item icons with a short squash-and-stretch "pop"
  on pickup, counts and durability bars (green → yellow → red), selected item name (2 s, fading during the last
  half second), hearts (SR health/max → 20 half hearts; blink on damage/heal via health polling +
  `OnPlayerDamaged`; at ≤ 4 half hearts (20 %) each heart wobbles by 0-1 px with a per-tick seeded random
  pattern; a small `HeartState` struct samples health at 20 Hz and keeps the blink window and the trailing
  value shown by the blinking hearts), food row (SR energy/max → 10 shanks, shakes below 50 % energy as a
  stand-in for exhausted saturation), XP bar + green outlined level number, action bar, title/subtitle (fade in 10 ticks, hold, fade out
  20 ticks; drawn at 4x and 2x scale), and a top-right `Day N  HH:MM` clock (only while Slime Rancher's own
  day/time text — `HudUI.timeText`, which Core keeps visible — is hidden, so there are never two clocks).
  * Layout (GUI px, `w`/`h` = `ceil(screen / scale)`): hotbar `(w/2-91, h-22)` 182x22, selection
    `(w/2-92+20*slot, h-23)` 24x23, icons at `(w/2-88+20*i, h-19)`; hearts row `y = h-39` starting at `w/2-91`
    (8 px steps), food row `y = h-39` right-aligned to `w/2+91`; XP bar `((w-182)/2, h-29)` 182x5 with a fill of
    `floor(progress*183)` px (max 182); level number at `y = h-35`, outlined by four black copies, colour
    `0xFF80FF20`; item name at `y = h - max(yShift+10, 59)` (= `h-59`, `+14` in creative); action bar at
    `h - max(yShift+23, 72)`.
  * **XP mapping:** the level number is the player's **newbucks** and the bar is `(newbucks % 1000) / 1000`
    (progress to the next thousand).
  * Creative mode hides hearts/food/XP like Minecraft and moves the item name down 14 px.
  * **Visibility:** the whole Minecraft canvas is off on the main menu (except the title extras), while
    `AutoSaveDirector.IsLoadingGame()` (SR loading screen, also hides the title extras), and draws nothing while
    SR's pause menu is open, `SC.SR.IsPaused` (time scale 0) or `SC.SR.SRUIOpen` (map, Slimepedia, shops/plots,
    death screen, any SR `BaseUI`) — unless a Minecraft screen is open. It re-appears the next frame after.
* **Vac ammo strip** (`Overlay/VacAmmo`): with the vacpack selected, Slime Rancher's ammo slots are drawn with
  hotbar pieces above the hearts (`y = h-49-24`, creative `h-25-24`): SR icon (`LookupDirector.GetIcon`), count
  (yellow when the slot is full), fill bar in the SR ammo colour, and the hotbar selection frame on SR's selected
  slot; the item name / action bar move up above the strip (2 px gap). Changing SR's selected slot (or its
  contents) shows the ammo name like an item name; selecting a Minecraft item always shows that item's name, and
  re-selecting the vacpack shows "Vacpack" first (ammo tracking is reset while a Minecraft item is held).
* **Vacpack icon:** a built-in 16x16 Minecraft-style pixel-art vacpack (orange tank + gauge, glass barrel with
  cyan rings, dark nozzle; drawn diagonally like Minecraft tools) packed into the GUI atlas
  (`slimecraft:vacpack_icon`), used everywhere the Hud draws the vacpack item (`VacpackPixelIcon`; the
  `SC.ItemVisuals` icon resolves to an SR light-bulb tutorial sprite).
* **Mode switching:** F8 (`SC.Input.HudToggleKey`) toggles `McHudEnabled` (persisted) →
  `SC.SR.SetSRHudVisible(!McHudEnabled)` (re-applied on WorldLoaded, +1 s, +3 s). Hotbar: keys 1-9, wheel only
  when the vacpack is not selected (Ctrl+wheel always; wheel up selects the previous slot). On every selection
  change / WorldLoaded: `SC.Input.SuppressSRWeapon = !VacpackSelected` and `SC.SR.SetVacpackActive(VacpackSelected)`.
* **Inventory model** (`PlayerInventory`): 36 slots, selection, creative flag, `Give` (merges, pop animation),
  `ConsumeSelected`, `DamageSelected` (breaks with `entity.item.break`), persistence keys `inventory`
  (`{"slots":"<Inventory.Serialize>","selected":n}`, crafting grid / carried stacks of an open screen are folded
  in) and `gamemode` (`creative`/`survival`). Fresh save → starter kit (vacpack, diamond pickaxe & sword, grass,
  planks, glass, TNT, flint & steel, steak; stone bricks, oak log, glowstone, slime, sand, white wool, bow,
  arrows, 6 spawn eggs, + crafting table). The vacpack always exists exactly once (re-added / de-duplicated,
  can't be dropped, trashed or deleted).
* **Screens** (`Screens/`, all derived from `HudScreen`), each `SC.Input.PushScreen("slimecraft.hud")` / delayed
  `PopScreen` (2 frames, so the closing Escape doesn't open SR's pause menu):
  * Survival inventory (`SurvivalInventoryScreen`: `inventory.png`, 2x2 crafting, armour/offhand only as
    decoration, 2D Steve looking at the mouse), crafting table 3x3 (`WorkbenchScreen`, `crafting_table.png`),
    creative inventory (`CreativePaletteScreen`: 12 tabs with the real tab sprites, 9x5 palette + scroll
    bar/wheel, search tab with a search box, survival-inventory tab with the bin cell), chat (`ChatInputScreen`).
  * **Item page model.** A `Panel` is the centred window (size, position, captions) with a list of `Cell`
    records: position, backing storage (`PlayerStore` over the player inventory or a page-owned `LooseStore`),
    storage entry, role (`Plain`, `Output`, `Prop`, `Palette`, `Bin`) and an entry filter. Cells have no
    behaviour; `StackMoves` does the stack arithmetic. `PanelScreen` turns pointer/key events into a `Gesture`
    (primary, secondary, middle, shift variants, double-click gather, drop / Ctrl+drop, number key), and a
    `ClickResolver` maps (gesture, cell role/contents, held stack) to a `Move` with per-role rule tables
    (`ClickRules.cs`): take all / half, put all / one, exchange, fill the hand from an output, shift-click
    transfer, gather, throw (held stack, one item, from a cell), hotbar exchange, creative clone, and the
    creative palette / bin moves. Drag-spreading lives in `SpreadGesture` (even shares, one each, or full stacks
    in creative; yellow counts where a cell would overflow). Shift-click destinations are `TransferRoute` data
    per page; a shift-click repeats while the same item keeps moving (bulk crafting).
  * Controls: left/right pick up, place, exchange, merge, split, place one, shift-click transfer, double-click
    gather (shift + double-click transfers every matching cell), drag to distribute, number-key hotbar exchange,
    drop key throw (Ctrl = whole stack), clicking outside drops the held stack (`SC.Entities.SpawnItem`),
    tooltips with the nine-slice tooltip sprites, the held stack follows the cursor, and grid/held items are
    returned on close. Items never vanish: a failed throw puts them back into the cell, the inventory or the hand.
  * Creative click rules: a click takes 1 item and adds 1 per further click, shift/middle click takes a full
    stack, right click removes one, clicking a different palette item deletes the held stack (the vacpack is
    returned instead), shift-clicking a hotbar cell clears it, shift-clicking the bin clears the inventory
    (except the vacpack).
  * Text input (`TextField`): caret / selection, horizontal scrolling, word-wise movement and erasing, clipboard,
    an edit callback and a grey completion hint. Each frame the characters that fit are measured once into a
    pen-offset table (`McFont.PrefixWidths`), and the text runs, selection band, caret mark and hint are placed
    from it. Scrolling: a caret inside the window keeps it, a caret left of it becomes the left end, and a caret on
    the left end or past the right end moves the window so the caret sits at its right end.
  * Chat (`Y`, or `/` to start a command): bottom input line, full history while open (`Overlay/ChatHistory`:
    ring buffers of 100 messages, wrapped rows and sent lines, with a view offset for scrolling), lines fade after
    10 s when closed, Enter sends (`/…` → `SC.Commands.Execute`, feedback printed), Up/Down recall sent lines,
    PageUp/PageDown/wheel scroll, command suggestion popup with Tab completion/cycling (`SC.Commands.Complete`:
    command names and every registered argument completer; /screen, /hud, /debug and /guiscale completers are
    registered here; a small local table adds full `minecraft:` ids).
* **Crafting** (`Crafting/Recipes`): all `crafting_shaped`/`crafting_shapeless` recipes from
  `data/minecraft/recipe/` with item tags (`data/minecraft/tags/item/`, nested tags resolved), filtered to
  Content; shaped matching with mirroring on the trimmed grid, shapeless bipartite matching, tool repair recipe;
  taking the result consumes one item per filled cell, shift-click crafts as many as fit (leftovers are dropped).
  Loaded incrementally (3-8 ms per frame).
* **F3** (`Overlay/DebugOverlay`): an F3-style overlay laid out like Minecraft's debug screen. Each non-empty line
  sits on its own background box `fill(x-1, y-1, x+width+1, y+8, 0x90505050)` at `y = 2 + 9*i` (left column
  `x = 2`, right column `x = w-2-width`, empty lines leave a gap without a box), with `0xE0E0E0` text and no
  shadow; `width` is the sum of glyph advances (each already including the 1 px spacing), all in GUI px (the
  canvas applies the GUI scale once). Text and widths are rebuilt at most 20 times per second. Lines: SlimeCraft and Minecraft asset versions,
  fps, XYZ/Block/Chunk/Facing (Minecraft yaw/pitch convention), zone as biome, day/time, health/energy/newbucks,
  mobs, blocks placed, game mode, recipes; right column Unity version, memory, CPU, display/GPU, GUI scale,
  targeted block (`SC.Blocks.Raycast`, falling back to SR geometry) and targeted entity. Toggle: F3,
  `/debug [on|off|toggle]`, `/screen f3 [on|off|toggle]`, `IHud.DebugScreenVisible`.
* **Title screen extras** (`Overlay/TitleExtras`): while `MainMenuUI` is active (SR deactivates it for its
  sub-menus) and SR is not loading, a yellow (`0xFFFF00`, shadowed) pulsing splash text (`texts/splashes.txt` or
  SlimeCraft splashes, plus SlimeCraft's own holiday lines; kept when coming back from a sub-menu), tilted by -20° and scaled by
  `(1.8 - |sin(ms%1000/1000·2π)·0.1|)·100/(textWidth+32)`, text at `(-w/2, -8)`. It is anchored the way Minecraft
  places its splash relative to its logo (a 256x44 logo at `(w/2-128, 30)` with the splash at `(w/2+123, 69)`,
  i.e. 251/256 across and 39/44 down), applied to the SR logo: the active `Image`/`RawImage` whose sprite is
  `logoTitle` (else sprite `*logo*`, object `TitleImage`, object `*logo*`), measured from its `RectTransform`
  world corners (root canvas camera, `preserveAspect` honoured) and trimmed to the sprite's opaque pixels (one GPU
  read-back per sprite, cached) → the right end of the logo's lower edge, slightly below the last "r" of
  "Rancher"; clamped to stay on screen. The chosen graphic and its screen rect are logged once
  (`[Hud] title logo ...`).
  Core also has a fixed-position splash (`[Core] ShowSplash`); while `TitleScreenExtras` is on the Hud switches
  that entry off **in memory only** (SaveOnConfigSet suspended; the file value is restored on quit/give-back) so
  exactly one splash shows. Bottom-left: `SlimeCraft 1.0 - Minecraft <version> inside Slime Rancher`.
* **Commands:** `/screen <inventory|creative|crafting|chat|f3|none>` (used by Blocks for crafting tables and by
  tests; `f3` takes `[on|off|toggle]`), `/debug [on|off|toggle]` (F3; only registered if no other module owns
  `/debug`), `/guiscale <0-8>`, `/hud <mc|sr|toggle>` (also `on`/`off`; no argument = toggle), `/say <msg>`.

## Config (`[Hud]`)
| Key | Default | Meaning |
|---|---|---|
| GuiScale | 0 | 0 = auto, otherwise capped to the auto maximum |
| McHudEnabled | true | Minecraft HUD on, SR HUD hidden (F8 toggles) |
| ShowClock | true | top-right Day/time clock (only while SR's own clock is hidden) |
| TitleScreenExtras | true | logo-anchored splash + version line on SR's main menu (replaces Core's splash in memory) |
| HandleDropKey | true | Hud handles the drop key (disable if another module does) |
| VacAmmoStrip | true | SR ammo strip when the vacpack is selected |
| PlayerName | Beatrix | name for your chat messages |
| ChatBackgroundOpacity | 0.5 | chat line background |
| StarterKit / CraftingTableInStarterKit | true / true | starter kit on fresh saves |
| VacpackIconFromSR | false | draw the vacpack with the SR Slimepedia "Vacing" sprite instead of the ItemVisuals icon |
| VacpackPixelIcon | true | draw the vacpack with the built-in Minecraft-style pixel-art icon (ignored if VacpackIconFromSR) |

## Known limitations
* The crosshair uses normal alpha blending (Minecraft inverts the colours behind it; no such shader is
  available in the build).
* The player in the inventory is a flat 2D Steve (head follows the mouse) instead of a 3D render.
* No recipe book button, armour/offhand slots are decoration only, no hotbar save/load creative tab.
* The `unifont` fallback font is not loaded (very rare characters render as `?`).
* SR has no saturation: the food bar shakes below 50 % energy.
* Crafting has no remainders (e.g. buckets used in a cake are fully consumed).
