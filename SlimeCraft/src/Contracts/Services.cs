using System;
using System.Collections.Generic;
using UnityEngine;

namespace SlimeCraft
{
    // =====================================================================================
    //  CORE services
    // =====================================================================================

    /// <summary>Read-only access to the user's local Minecraft install (client jar + asset index).</summary>
    public interface IMcAssets
    {
        /// <summary>True once the jar was opened and the asset index parsed.</summary>
        bool Ready { get; }
        /// <summary>Minecraft version whose jar is used (e.g. "26.1.2").</summary>
        string McVersion { get; }
        /// <summary>The .minecraft folder in use.</summary>
        string MinecraftDir { get; }

        /// <summary>Entry exists in the client jar. Path like "assets/minecraft/textures/block/stone.png".</summary>
        bool JarExists(string jarPath);
        /// <summary>Raw bytes of a jar entry or null.</summary>
        byte[] ReadJarBytes(string jarPath);
        /// <summary>UTF-8 text of a jar entry or null.</summary>
        string ReadJarText(string jarPath);
        /// <summary>Parsed JSON of a jar entry or null.</summary>
        JsonNode ReadJarJson(string jarPath);
        /// <summary>All jar entry names starting with the prefix (e.g. "data/minecraft/recipe/").</summary>
        IEnumerable<string> ListJar(string prefix);

        /// <summary>
        /// Resolves an asset-index object (sounds, sounds.json, music, non-English lang) to an absolute file path
        /// inside .minecraft/assets/objects. logicalName like "minecraft/sounds.json" or
        /// "minecraft/sounds/random/explode1.ogg". Null if missing.
        /// </summary>
        string ResolveIndexedAsset(string logicalName);

        /// <summary>
        /// Texture "assets/minecraft/textures/{path}.png" (path without extension, e.g. "block/stone",
        /// "item/apple", "gui/sprites/hud/hotbar", "entity/creeper/creeper"). Point filtered, clamped,
        /// no mipmaps, readable (GetPixels works). Cached. Animated textures (with .mcmeta) return the first
        /// square frame. Missing textures return a magenta/black checker (never null).
        /// </summary>
        Texture2D GetTexture(string path);
        /// <summary>Full-rect Sprite of <see cref="GetTexture"/> (pivot center, 1 pixel per unit). Cached.</summary>
        Sprite GetSprite(string path);
        /// <summary>True if "assets/minecraft/textures/{path}.png" exists.</summary>
        bool TextureExists(string path);

        /// <summary>Block texture atlas (built lazily on first access after Ready) holding every face texture of every BlockDef.</summary>
        IBlockAtlas BlockAtlas { get; }

        /// <summary>English display name from the jar's en_us.json ("item.minecraft.apple" → "Apple"); key returned if unknown.</summary>
        string Translate(string key);
    }

    /// <summary>
    /// One texture atlas for all block faces. Texture specs (see <see cref="BlockDef"/>) are strings:
    ///   "block/stone"                         plain texture
    ///   "block/oak_leaves@77AB2F"             texture multiplied by RGB hex tint
    ///   "block/grass_block_side|block/grass_block_side_overlay@91BD59"   base with tinted overlay composited on top
    /// </summary>
    public interface IBlockAtlas
    {
        Texture2D Texture { get; }
        /// <summary>
        /// UV rect (0..1) of a texture spec, already inset slightly (config [Core] AtlasUvInsetTexels, default 0.02 texel;
        /// every tile is padded in the atlas so faces never bleed). Adds the spec on demand; rects never move once handed out.
        /// </summary>
        Rect GetUV(string textureSpec);
        /// <summary>Lit material (Slime Rancher paint-light look) using the atlas, alpha-tested (cutoff 0.5). Used for opaque AND cutout blocks.</summary>
        Material Cutout { get; }
        /// <summary>Alpha-blended material for translucent blocks (ice, slime, honey, stained glass).</summary>
        Material Translucent { get; }
        /// <summary>
        /// Full-bright (unlit, alpha-tested) material using the atlas, for light-emitting blocks (BlockDef.Light &gt; 0:
        /// glowstone, sea lantern, jack o'lantern...) so they glow at night exactly like in Minecraft.
        /// </summary>
        Material Emissive { get; }
    }

    /// <summary>Minecraft sound events from the user's install (sounds.json + asset index, OGG loaded at runtime).</summary>
    public interface IMcAudio
    {
        bool Ready { get; }
        /// <summary>
        /// Plays a sound event ("block.stone.break", "entity.generic.explode", "entity.creeper.primed",
        /// "random.pop" style aliases are NOT supported, use modern names) at a world position with a random
        /// variant and the variant's volume/pitch from sounds.json multiplied by the given values. Missing
        /// "block.X.Y" events fall back to "block.stone.Y". Clips load asynchronously; the first play of a
        /// never-loaded event may start a few frames late. Never throws.
        /// </summary>
        void Play(string soundEvent, Vector3 position, float volume = 1f, float pitch = 1f);
        /// <summary>Non-positional (UI) sound.</summary>
        void Play2D(string soundEvent, float volume = 1f, float pitch = 1f);
        /// <summary>Sound that follows a transform (e.g. TNT fuse). Returns a handle (may be null) to stop it.</summary>
        AudioSource PlayAttached(string soundEvent, Transform follow, float volume = 1f, float pitch = 1f, bool loop = false);
        /// <summary>Start loading clips for these events in the background.</summary>
        void Preload(params string[] soundEvents);
    }

    /// <summary>Everything Slime Rancher. All members are safe to call when not in game (they return defaults).</summary>
    public interface ISRBridge
    {
        /// <summary>A save is loaded in the world scene and the player exists.</summary>
        bool InGame { get; }
        /// <summary>SR is paused (pause menu) or time is stopped.</summary>
        bool IsPaused { get; }
        /// <summary>Some SR UI is open (map, slimepedia, plot/market/shop UI, ...): Minecraft features must ignore gameplay input.</summary>
        bool SRUIOpen { get; }

        GameObject Player { get; }
        Camera MainCamera { get; }
        /// <summary>Camera position (eyes).</summary>
        Vector3 EyePosition { get; }
        Vector3 LookDirection { get; }
        /// <summary>Feet position of the player.</summary>
        Vector3 PlayerFeet { get; }
        Vector3 PlayerVelocity { get; }
        bool PlayerGrounded { get; }
        /// <summary>Approximate player collider (for "don't place a block inside the player").</summary>
        Bounds PlayerBounds { get; }
        /// <summary>Adds velocity to the SR character controller (knockback, slime-block bounce, explosions).</summary>
        void AddPlayerVelocity(Vector3 velocity);
        void TeleportPlayer(Vector3 feetPosition);

        int Health { get; }
        int MaxHealth { get; }
        int Energy { get; }
        int MaxEnergy { get; }
        void DamagePlayer(int amount, GameObject source);
        void HealPlayer(int amount);
        void AddEnergy(int amount);
        int Newbucks { get; }
        /// <summary>Adds (or with negative value spends) newbucks.</summary>
        void AddNewbucks(int amount);

        /// <summary>Time of day 0..1 (0 = midnight, 0.5 = noon).</summary>
        float DayFraction { get; }
        bool IsNight { get; }
        int DayNumber { get; }
        /// <summary>Human readable current zone ("The Ranch", "The Dry Reef", ...).</summary>
        string ZoneName { get; }

        /// <summary>Stable identifier of the currently loaded SR game (used to key per-save data), null if not in game.</summary>
        string SaveGameId { get; }

        /// <summary>Shows/hides SR's own vacpack (first-person model) and enables/disables its fire/vac input.</summary>
        void SetVacpackActive(bool active);
        bool VacpackActive { get; }
        /// <summary>True while the SR vacpack is vacuuming (right mouse held with vacpack selected).</summary>
        bool VacActive { get; }
        /// <summary>Origin and direction of the vacuum cone (valid when VacActive).</summary>
        Ray VacRay { get; }

        /// <summary>Shows/hides Slime Rancher's own HUD (health/energy meters, ammo slots, crosshair, currency).</summary>
        void SetSRHudVisible(bool visible);

        /// <summary>Layer mask of solid world geometry (SR terrain/props + our blocks) for raycasts.</summary>
        int WorldMask { get; }
        /// <summary>Layer that our block colliders and Minecraft entities must use so SR physics (player, slimes) collides with them.</summary>
        int BlockLayer { get; }
        /// <summary>Raycast solid world (SR + our blocks), ignoring triggers and the player.</summary>
        bool RaycastWorld(Ray ray, float maxDistance, out RaycastHit hit);

        /// <summary>Spawns a Slime Rancher actor by Identifiable.Id enum name (e.g. "PINK_SLIME", "CARROT_VEGGIE", "HEN"). Null on failure.</summary>
        GameObject SpawnSRActor(string identifiableId, Vector3 position, Quaternion rotation);
        /// <summary>Identifiable.Id enum name of an SR actor (walks up parents), or null.</summary>
        string GetSRActorId(GameObject go);
        /// <summary>
        /// Slime Rancher explosion physics (PhysicsUtil.Explode): launches slimes/actors, damages and pushes the player.
        /// With min/max player damage 0 and ignites=false the player is left out entirely (no SR push, no 0-damage
        /// hurt flash): the caller handles the player itself (Minecraft explosions do).
        /// </summary>
        void SRExplode(Vector3 position, float radius, float power, float minPlayerDamage, float maxPlayerDamage, bool ignites);
        /// <summary>
        /// Applies damage/knockback to an SR actor (slime etc.) hit by a Minecraft weapon. Returns true if it was an SR actor.
        /// <paramref name="damage"/> is Minecraft damage (scaled by [Core] SRDamageMultiplier); <paramref name="knockback"/>
        /// is a velocity change in m/s applied to the actor's Rigidbody (Entities passes ~8 m/s along the hit + 3 m/s up).
        /// </summary>
        bool HitSRActor(GameObject target, float damage, Vector3 knockback);

        /// <summary>
        /// Points the player's camera: pitch in degrees (positive = looking down, clamped by SR), yaw in degrees
        /// (0 = world +Z, clockwise seen from above). Uses SR's own vp_FPCamera rotation so it sticks.
        /// </summary>
        void SetLook(float pitch, float yaw);

        event Action WorldLoaded;
        event Action WorldUnloading;
        /// <summary>SR is about to write its save file (persist your data now).</summary>
        event Action Saving;
        /// <summary>
        /// The player actually lost health through Slime Rancher's damage path (any source: slimes, Tarr, explosions,
        /// Minecraft mobs/arrows via <see cref="DamagePlayer"/>). Args: health lost (SR points), damage source (may be
        /// null; its position gives the hit direction).
        /// </summary>
        event Action<int, GameObject> PlayerDamaged;
    }

    /// <summary>Routes mouse/keyboard between Slime Rancher and Minecraft features.</summary>
    public interface IInputGate
    {
        /// <summary>When true (a Minecraft item is selected) SR's vacpack ignores attack/vac/slot inputs.</summary>
        bool SuppressSRWeapon { get; set; }
        /// <summary>Open a Minecraft screen (inventory, chat, ...): frees cursor, stops SR look/move/fire. Ref-counted by id.</summary>
        void PushScreen(string id);
        void PopScreen(string id);
        bool AnyScreenOpen { get; }

        /// <summary>True when Minecraft gameplay input should be processed (in game, not paused, no SR UI, no MC screen).</summary>
        bool GameplayInputAllowed { get; }
        /// <summary>Left mouse (attack/break). Respects GameplayInputAllowed.</summary>
        bool AttackHeld { get; }
        bool AttackPressed { get; }
        /// <summary>Right mouse (use/place). Respects GameplayInputAllowed.</summary>
        bool UseHeld { get; }
        bool UsePressed { get; }
        /// <summary>Mouse wheel delta this frame (positive = up). Respects GameplayInputAllowed.</summary>
        float Scroll { get; }
        /// <summary>Key pressed this frame and gameplay input allowed.</summary>
        bool GameKeyDown(KeyCode key);

        // Configurable Minecraft keys (BepInEx config). Defaults avoid SR's bindings.
        KeyCode InventoryKey { get; }
        KeyCode ChatKey { get; }
        KeyCode CommandKey { get; }
        KeyCode DropKey { get; }
        KeyCode DebugKey { get; }
        KeyCode HudToggleKey { get; }
        /// <summary>Minecraft "sneak" (default Left Ctrl; SR uses Shift for sprint).</summary>
        KeyCode SneakKey { get; }
        /// <summary>Sneak key held and gameplay input allowed.</summary>
        bool SneakHeld { get; }

        /// <summary>
        /// TEST HOOK (used by the Testing module only): while set, AttackHeld/UseHeld/SneakHeld report these values
        /// (OR-ed with the real mouse/keys) and AttackPressed/UsePressed fire on their rising edges, still subject to
        /// GameplayInputAllowed. Lets the automated test drive the real interaction code paths (mining, placing,
        /// eating, attacking, bow) exactly like a player would.
        /// </summary>
        void SetTestInput(bool attackHeld, bool useHeld, bool sneakHeld = false);
        /// <summary>Removes the test input override.</summary>
        void ClearTestInput();
        /// <summary>TEST HOOK: simulates one press of a configurable key (e.g. DropKey) for GameKeyDown next frame.</summary>
        void SimulateKeyDown(KeyCode key);
    }

    /// <summary>Per-SR-save storage for module data (written next to SR's save, restored on load).</summary>
    public interface IPersistence
    {
        /// <summary>
        /// Registers a section. <paramref name="save"/> returns the section's data string (JSON or base64);
        /// <paramref name="load"/> receives it when a game is loaded (null for a new game / no data).
        /// </summary>
        void Register(string key, Func<string> save, Action<string> load);
        /// <summary>Write all sections for the current game now.</summary>
        void SaveNow();
    }

    /// <summary>Shared visuals for items (GUI icons, held/dropped meshes) and materials.</summary>
    public interface IItemVisuals
    {
        /// <summary>GUI icon (blocks: isometric 3-face render like Minecraft's inventory; items: item texture). Point filtered. Cached.</summary>
        Texture2D GetIcon(string itemId);
        /// <summary>
        /// Mesh for held/dropped rendering, centered on origin, 1 unit = 1 block:
        /// block items → unit cube with atlas UVs (render with <see cref="GetItemMaterial"/>; North face = -Z, East = +X,
        /// side textures upright and unmirrored seen from outside, same convention as the block world),
        /// other items → extruded pixel sprite 1x1 units and 1/16 thick (like Minecraft): the front face looks toward -Z,
        /// texture u runs along +X and v along +Y (unmirrored when viewed from -Z), back face mirrored, outward edge faces.
        /// </summary>
        Mesh GetItemMesh(string itemId);
        Material GetItemMaterial(string itemId);
        bool IsBlockItem(string itemId);
        /// <summary>Lit, alpha-tested material for entity textures (mobs, Steve arm, TNT).</summary>
        Material CreateLitMaterial(Texture2D texture, bool cutout = true);
        /// <summary>Unlit material (particles, overlays, outlines). transparent=true for alpha blending.</summary>
        Material CreateUnlitMaterial(Texture2D texture, bool transparent);
        /// <summary>Solid colored unlit material for lines / boxes (e.g. block outline).</summary>
        Material CreateColorMaterial(Color color);
    }

    /// <summary>Minecraft style /commands.</summary>
    public interface ICommands
    {
        /// <summary>Handler gets args (without the command name) and returns feedback text (may be null).</summary>
        void Register(string name, string usage, Func<string[], string> handler);
        /// <summary>Executes "/give minecraft:tnt 64" (leading slash optional). Returns feedback.</summary>
        string Execute(string commandLine);
        IEnumerable<string> Names { get; }
        string Usage(string name);
        /// <summary>
        /// Tab completion: candidate replacements for the LAST token of a partial command line ("/giv" → "give",
        /// "/give minecraft:ap" → "apple"...). Command names are returned without the slash. Never null.
        /// </summary>
        IEnumerable<string> Complete(string partialCommandLine);
        /// <summary>
        /// Registers argument completion for a command: completer(argIndex, argsSoFar) returns candidates for the
        /// argument at argIndex (argsSoFar includes the partial last argument). Results are prefix-filtered by Complete.
        /// </summary>
        void RegisterCompleter(string name, Func<int, string[], IEnumerable<string>> completer);
    }

    // =====================================================================================
    //  HUD services
    // =====================================================================================

    public interface IPlayerInventory
    {
        /// <summary>36 slots, 0-8 = hotbar.</summary>
        Inventory Inv { get; }
        int SelectedSlot { get; set; }
        ItemStack Selected { get; }
        /// <summary>The selected hotbar slot holds the Slime Rancher vacpack ("slimecraft:vacpack") – SR controls are active.</summary>
        bool VacpackSelected { get; }
        bool Creative { get; set; }
        /// <summary>Adds the stack (merging); returns the leftover count that did not fit (0 = all added).</summary>
        int Give(ItemStack stack);
        /// <summary>Removes n from the selected stack (no-op in creative).</summary>
        void ConsumeSelected(int n = 1);
        /// <summary>Adds durability damage to the selected tool (breaks it at max; no-op in creative).</summary>
        void DamageSelected(int amount = 1);
        event Action Changed;
    }

    public interface IHud
    {
        bool McHudEnabled { get; set; }
        void ShowActionBar(string text, float seconds = 2.5f);
        void ShowTitle(string title, string subtitle = null, float seconds = 3f);
        void AddChat(string message);
        /// <summary>
        /// Minecraft-style damage feedback (heart blink). The Hud calls this itself on <see cref="ISRBridge.PlayerDamaged"/>;
        /// other modules only need it for damage that bypasses SR's damage path.
        /// </summary>
        void OnPlayerDamaged();
        /// <summary>True while any Minecraft screen (inventory, chat, creative menu) is open.</summary>
        bool ScreenOpen { get; }
        /// <summary>The F3 debug overlay is shown (toggled by the debug key; settable for tests).</summary>
        bool DebugScreenVisible { get; set; }
    }

    // =====================================================================================
    //  FIRST PERSON
    // =====================================================================================

    public enum UseAnimation { None, Eat, Bow, Block }

    public interface IFirstPerson
    {
        /// <summary>
        /// Plays the arm/item swing (attack, place, use, drop). A swing while the attack button is down also resets the
        /// attack-strength dip (unless it is part of block mining, see <see cref="MiningProgress"/>).
        /// </summary>
        void Swing();
        /// <summary>
        /// The held item pops back up from below, as after using it in Minecraft. Call when a use consumed the item or
        /// succeeded in creative (block placed, spawn egg used).
        /// </summary>
        void ItemUsed();
        /// <summary>Starts a hold animation (eating, drawing bow) until StopUsing.</summary>
        void StartUsing(UseAnimation anim);
        void StopUsing();
        /// <summary>
        /// Hit/break progress feedback (0..1) for subtle arm shake while mining, -1 to clear. Must be &gt;= 0 for the whole
        /// time a block (or SR terrain) is being mined, so mining swings don't count as attacks.
        /// </summary>
        float MiningProgress { get; set; }
    }

    // =====================================================================================
    //  BLOCKS / WORLD INTERACTION
    // =====================================================================================

    public struct BlockHit
    {
        public Vector3Int Pos;      // block that was hit
        public Vector3Int Normal;   // face normal (place position = Pos + Normal)
        public Vector3 Point;
        public float Distance;
    }

    /// <summary>Block facing (for orientable blocks / log axis). Stored per block.</summary>
    public enum BlockFacing : byte { North = 0, South = 1, East = 2, West = 3, Up = 4, Down = 5 }

    /// <summary>
    /// The voxel block layer living inside the Slime Rancher world. Block (x,y,z) occupies world space
    /// [x,x+1)×[y,y+1)×[z,z+1) in SR units (1 SR meter = 1 block).
    /// </summary>
    public interface IBlockWorld
    {
        /// <summary>Block id at the position or null for air.</summary>
        string GetBlock(Vector3Int pos);
        BlockFacing GetFacing(Vector3Int pos);
        /// <summary>Places/replaces a block (null = remove). No drops. Optionally plays the place sound. Returns false if refused.</summary>
        bool SetBlock(Vector3Int pos, string blockId, BlockFacing facing = BlockFacing.North, bool playSound = false);
        /// <summary>Breaks with sound + particles; drops the block's item (via SC.Entities) when drop=true.</summary>
        bool BreakBlock(Vector3Int pos, bool drop);
        /// <summary>Raycast against our blocks only.</summary>
        bool Raycast(Ray ray, float maxDistance, out BlockHit hit);
        bool IsBlockCollider(Collider c);
        int Count { get; }
        /// <summary>(pos, oldId, newId) – ids may be null (air).</summary>
        event Action<Vector3Int, string, string> BlockChanged;
    }

    // =====================================================================================
    //  ENTITIES
    // =====================================================================================

    public struct EntityHit
    {
        public GameObject Target;
        public Vector3 Point;
        public float Distance;
        /// <summary>True for our Minecraft mobs, false for Slime Rancher actors (slimes, animals...).</summary>
        public bool IsMcMob;
    }

    public interface IEntities
    {
        /// <summary>Spawns a Minecraft dropped-item entity (spinning/bobbing, magnet pickup into SC.Inventory).</summary>
        GameObject SpawnItem(ItemStack stack, Vector3 position, Vector3 velocity, float pickupDelay = 0.5f);
        /// <summary>Spawns primed (lit) TNT. position = centre of the TNT cube (block pos + 0.5). fuseTicks: 80 = 4 s like Minecraft.</summary>
        GameObject SpawnPrimedTnt(Vector3 position, int fuseTicks = 80, Vector3 velocity = default(Vector3));
        /// <summary>Spawns a falling-block entity (sand/gravel) that re-places itself on landing.</summary>
        GameObject SpawnFallingBlock(string blockId, Vector3Int from);
        /// <summary>Spawns a Minecraft mob ("minecraft:creeper", ... see <see cref="MobIds"/>). Null if unknown.</summary>
        GameObject SpawnMob(string mobId, Vector3 feetPosition);
        /// <summary>Raycast Minecraft mobs AND Slime Rancher actors (slimes, chickens, food...).</summary>
        bool RaycastEntity(Ray ray, float maxDistance, out EntityHit hit);
        /// <summary>
        /// Melee hit from the player (sword/fist): damages+knocks back MC mobs, or pushes/damages SR actors.
        /// damage = final Minecraft damage (attack strength / crit already applied). knockbackDirection: horizontal
        /// direction; its length (clamped 1..3) multiplies Minecraft's 0.4 knockback strength.
        /// </summary>
        void AttackEntity(GameObject target, float damage, Vector3 knockbackDirection);
        /// <summary>
        /// Fires an arrow projectile. velocity in m/s (Minecraft blocks/tick × 20). damage = Minecraft BASE damage
        /// (2 for a normal arrow): the hit damage is ceil(speed[b/t] × damage), full-speed player shots are critical.
        /// Plays "entity.arrow.shoot" for player shots (callers must not play it again).
        /// </summary>
        GameObject ShootArrow(Vector3 from, Vector3 velocity, GameObject shooter, float damage);
        /// <summary>
        /// Flint and steel used on an entity: a Minecraft creeper is ignited (fuse starts, cannot be stopped).
        /// Returns true if the target was a Minecraft entity that reacted; false for anything else (SR actors are not handled here).
        /// </summary>
        bool Ignite(GameObject target);
        IEnumerable<string> MobIds { get; }
        int LiveMobCount { get; }
        /// <summary>Root GameObjects of all living Minecraft mobs (snapshot).</summary>
        IEnumerable<GameObject> LiveMobs { get; }
        /// <summary>Removes all Minecraft mobs/items/TNT entities (used by /kill and on world unload).</summary>
        void ClearAll();
    }

    public interface IExplosions
    {
        /// <summary>
        /// Minecraft explosion at center: ray-based block destruction honoring blast resistance (drops some items),
        /// damage + knockback to Minecraft mobs and the player, Slime Rancher physics push on all SR actors
        /// (via SC.SR.SRExplode), chain-ignites TNT, explosion particles + "entity.generic.explode" sound.
        /// power: TNT 4, creeper 3.
        /// </summary>
        void Explode(Vector3 center, float power, GameObject source = null, bool breakBlocks = true);
    }
}
