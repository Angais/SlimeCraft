using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace SlimeCraft.Entities
{
    /// <summary>
    /// Entities module (Order 300): owns SC.Entities (dropped items, primed TNT, falling blocks, arrows, Minecraft mobs)
    /// and SC.Explosions. Lives on SC.Root; world objects are created after SC.SR.WorldLoaded and cleared on WorldUnloading.
    /// </summary>
    public sealed class EntitiesModule : MonoBehaviour, IModule, IEntities
    {
        public string ModuleName => "Entities";
        public int Order => 300;

        internal static EntitiesModule Instance;
        private ExplosionSystem explosions;
        private readonly MobSpawnDirector spawner = new MobSpawnDirector();
        private bool srHooked, persistenceHooked, worldReady;
        private string pendingMobs;
        private readonly RaycastHit[] rayBuf = new RaycastHit[32];

        // ================================================================== lifecycle

        public void Init()
        {
            Instance = this;
            EConfig.Bind();
            explosions = new ExplosionSystem();
            SC.Entities = this;
            SC.Explosions = explosions;
            HookServices();
            SC.AllModulesInitialized += HookServices;
            ELog.Info("Entities ready: " + MobDef.ById.Count + " mob types");
        }

        private void HookServices()
        {
            try
            {
                if (!srHooked && SC.SR != null)
                {
                    srHooked = true;
                    SC.SR.WorldLoaded += OnWorldLoaded;
                    SC.SR.WorldUnloading += OnWorldUnloading;
                }
                if (!persistenceHooked && SC.Persistence != null)
                {
                    persistenceHooked = true;
                    SC.Persistence.Register("mobs", SaveMobs, LoadMobs);
                }
                if (SC.Commands != null && !commandsRegistered) RegisterCommands();
            }
            catch (Exception e) { ELog.Error("Entities.HookServices", e); }
        }

        private void OnWorldLoaded()
        {
            try
            {
                ELayers.Decide();
                Particles.Ensure();
                spawner.ResetForWorld();
                worldReady = true;
                ELog.Info("World loaded: natural spawning " + (EConfig.NaturalSpawning.Value ? "on" : "off") + ", hostiles on The Ranch "
                          + (EConfig.SpawnOnRanch.Value ? "allowed" : "blocked") + ", caps " + EConfig.HostileCap.Value + " hostiles / " + EConfig.PassiveCap.Value + " animals");
                SC.Audio?.Preload(PreloadSounds());
                if (pendingMobs != null) StartCoroutine(SpawnPendingLater());
            }
            catch (Exception e) { ELog.Error("Entities.OnWorldLoaded", e); }
        }

        private void OnWorldUnloading()
        {
            worldReady = false;
            pendingMobs = null;
            ClearAll();
        }

        private void Update()
        {
            try
            {
                if (!worldReady || SC.SR == null || !SC.SR.InGame) return;
                spawner.Tick();
                FireFx.Animate();
            }
            catch (Exception e) { ELog.Error("Entities.Update", e); }
        }

        private static string[] PreloadSounds()
        {
            var l = new List<string> { "entity.generic.explode", "entity.tnt.primed", "entity.item.pickup", "entity.arrow.shoot", "entity.arrow.hit",
                "entity.skeleton.shoot", "entity.creeper.primed", "entity.player.hurt", "entity.slime.jump", "entity.slime.squish",
                "entity.slime.jump_small", "entity.slime.squish_small", "entity.enderman.teleport", "entity.enderman.stare", "entity.enderman.scream",
                "entity.iron_golem.attack", "entity.chicken.egg" };
            foreach (var d in MobDef.ById.Values)
            {
                if (d.AmbientSound != null) l.Add(d.AmbientSound);
                if (d.HurtSound != null) l.Add(d.HurtSound);
                if (d.DeathSound != null) l.Add(d.DeathSound);
                if (d.StepSound != null) l.Add(d.StepSound);
            }
            return l.ToArray();
        }

        // ================================================================== IEntities

        public GameObject SpawnItem(ItemStack stack, Vector3 position, Vector3 velocity, float pickupDelay = 0.5f)
        {
            var e = SpawnItemStatic(stack, position, velocity, pickupDelay);
            return e != null ? e.gameObject : null;
        }

        internal static DroppedItem SpawnItemStatic(ItemStack stack, Vector3 position, Vector3 velocity, float pickupDelay)
        {
            try
            {
                if (stack == null || stack.IsEmpty || Content.Item(stack.Id) == null) return null;
                if (SC.SR == null || !SC.SR.InGame) return null;
                if (!ELayers.Decided) ELayers.Decide();
                int max = Mathf.Max(1, stack.MaxStack);
                DroppedItem last = null;
                int left = stack.Count;
                while (left > 0)
                {
                    int n = Mathf.Min(left, max);
                    last = DroppedItem.Spawn(stack.WithCount(n), position, velocity, pickupDelay);
                    left -= n;
                }
                return last;
            }
            catch (Exception e) { ELog.Error("SpawnItem", e); return null; }
        }

        public GameObject SpawnPrimedTnt(Vector3 position, int fuseTicks = 80, Vector3 velocity = default(Vector3))
        {
            try
            {
                if (SC.SR == null || !SC.SR.InGame) return null;
                if (!ELayers.Decided) ELayers.Decide();
                var player = SC.SR.Player;
                return LitTnt.Spawn(position, fuseTicks, velocity, true, player).gameObject;
            }
            catch (Exception e) { ELog.Error("SpawnPrimedTnt", e); return null; }
        }

        public GameObject SpawnFallingBlock(string blockId, Vector3Int from)
        {
            try
            {
                if (SC.SR == null || !SC.SR.InGame || Content.Block(blockId) == null) return null;
                if (!ELayers.Decided) ELayers.Decide();
                return FallingBlockBody.Spawn(blockId, from).gameObject;
            }
            catch (Exception e) { ELog.Error("SpawnFallingBlock", e); return null; }
        }

        public GameObject SpawnMob(string mobId, Vector3 feetPosition)
        {
            float yaw = Rng.F() * 360f;
            var m = SpawnMobStatic(mobId, feetPosition, yaw, -1);
            if (m != null) ELog.Event("spawnmob", "SpawnMob " + m.Def.Id + " at " + ELog.V(feetPosition) + " (egg / command / API)", 1f, 10);
            return m != null ? m.gameObject : null;
        }

        internal static McMob SpawnMobStatic(string mobId, Vector3 feet, float yaw, int variant)
        {
            try
            {
                var def = MobDef.Get(mobId);
                if (def == null) { ELog.Warn("Unknown mob id '" + mobId + "'"); return null; }
                if (SC.SR == null || !SC.SR.InGame) return null;
                if (!ELayers.Decided) ELayers.Decide();
                Particles.Ensure();
                return McMob.Create(def, feet, yaw, variant);
            }
            catch (Exception e) { ELog.Error("SpawnMob " + mobId, e); return null; }
        }

        public GameObject ShootArrow(Vector3 from, Vector3 velocity, GameObject shooter, float damage)
        {
            var a = ShootArrowStatic(from, velocity, shooter, damage);
            if (a != null && a.ShotByPlayer)
            {
                // bow release sound: a slightly random pitch that rises with the draw power
                float power = Mathf.Clamp01(velocity.magnitude / (3f * Mc.BptToMs));
                SC.Audio?.Play("entity.arrow.shoot", from, 1f, 1f / (Rng.F() * 0.4f + 1.2f) + power * 0.5f);
            }
            return a != null ? a.gameObject : null;
        }

        internal static FlyingArrow ShootArrowStatic(Vector3 from, Vector3 velocity, GameObject shooter, float damage)
        {
            try
            {
                if (SC.SR == null || !SC.SR.InGame) return null;
                if (!ELayers.Decided) ELayers.Decide();
                Particles.Ensure();
                return FlyingArrow.Spawn(from, velocity, shooter, damage);
            }
            catch (Exception e) { ELog.Error("ShootArrow", e); return null; }
        }

        /// <summary>Raycast Minecraft mobs and SR actors with a fat (0.1) ray; solid world in front blocks the hit.</summary>
        public bool RaycastEntity(Ray ray, float maxDistance, out EntityHit hit)
        {
            hit = default(EntityHit);
            try
            {
                if (SC.SR == null || !SC.SR.InGame) return false;
                int mask = ~((1 << ELayers.SRPlayer) | (1 << ELayers.SRIgnoreRaycast) | (1 << ELayers.SRWater) | (1 << ELayers.SRWeapon));
                int n = Physics.SphereCastNonAlloc(ray.origin, 0.1f, ray.direction, rayBuf, maxDistance, mask, QueryTriggerInteraction.Ignore);
                if (n == 0) return false;
                Array.Sort(rayBuf, 0, n, HitDistance.I);
                var playerT = SC.SR.Player != null ? SC.SR.Player.transform : null;
                for (int i = 0; i < n; i++)
                {
                    var h = rayBuf[i];
                    var c = h.collider;
                    if (c == null) continue;
                    if (playerT != null && c.transform.IsChildOf(playerT)) continue;
                    bool startOverlap = h.distance <= 0f && h.point == Vector3.zero;
                    var ent = c.GetComponentInParent<McEntity>();
                    if (ent != null)
                    {
                        if (ent is McMob mob && !mob.Dead)
                        {
                            hit = new EntityHit { Target = mob.gameObject, Point = startOverlap ? ray.origin : h.point, Distance = h.distance, IsMcMob = true };
                            return true;
                        }
                        continue; // items / TNT / arrows are not attack targets
                    }
                    if (startOverlap) continue;
                    string id = SC.SR.GetSRActorId(c.gameObject);
                    if (id != null)
                    {
                        var target = c.attachedRigidbody != null ? c.attachedRigidbody.gameObject : c.gameObject;
                        hit = new EntityHit { Target = target, Point = h.point, Distance = h.distance, IsMcMob = false };
                        return true;
                    }
                    return false; // terrain / blocks in front
                }
            }
            catch (Exception e) { ELog.Error("RaycastEntity", e); }
            return false;
        }

        private sealed class HitDistance : IComparer<RaycastHit>
        {
            public static readonly HitDistance I = new HitDistance();
            public int Compare(RaycastHit a, RaycastHit b) => a.distance.CompareTo(b.distance);
        }

        /// <summary>
        /// Player melee: Minecraft mobs get damage + 0.4 knockback (scaled by |knockbackDirection| when &gt; 1, e.g. sprint
        /// hits) + hurt sound/red flash; SR actors are forwarded to SC.SR.HitSRActor with a velocity-like push.
        /// </summary>
        public void AttackEntity(GameObject target, float damage, Vector3 knockbackDirection)
        {
            try
            {
                if (target == null) return;
                var mob = target.GetComponentInParent<McMob>();
                var player = SC.SR?.Player;
                if (mob != null)
                {
                    var dir = Mc.Horizontal(knockbackDirection);
                    if (dir.sqrMagnitude < 1e-6f && player != null) dir = Mc.Horizontal(mob.transform.position - player.transform.position);
                    float strength = 0.4f * Mathf.Clamp(knockbackDirection.magnitude, 1f, 3f);
                    mob.Hurt(damage, DamageKind.PlayerAttack, player, dir.normalized, strength);
                    return;
                }
                var kd = knockbackDirection.sqrMagnitude > 1e-6f ? knockbackDirection.normalized : (player != null ? (target.transform.position - player.transform.position).normalized : Vector3.forward);
                SC.SR?.HitSRActor(target, damage, kd * 8f + Vector3.up * 3f);
            }
            catch (Exception e) { ELog.Error("AttackEntity", e); }
        }

        /// <summary>Flint and steel on a creeper lights its fuse, which then runs to the end whatever the creeper's target does.</summary>
        public bool Ignite(GameObject target)
        {
            try
            {
                if (target == null) return false;
                var ent = target.GetComponentInParent<McEntity>();
                if (ent == null) return false;
                if (ent is CreeperMob creeper && !creeper.Dead && !creeper.Removed)
                {
                    creeper.Ignite();
                    ELog.Event("ignite", "creeper ignited with flint and steel at " + ELog.V(creeper.transform.position), 0.5f);
                    return true;
                }
                return false;
            }
            catch (Exception e) { ELog.Error("Ignite", e); return false; }
        }

        public IEnumerable<string> MobIds => SlimeCraft.MobIds.All;

        public IEnumerable<GameObject> LiveMobs
        {
            get
            {
                var list = new List<GameObject>();
                foreach (var m in McMob.Mobs) if (m != null && !m.Dead && !m.Removed) list.Add(m.gameObject);
                return list;
            }
        }

        public int LiveMobCount
        {
            get
            {
                int n = 0;
                foreach (var m in McMob.Mobs) if (m != null && !m.Dead && !m.Removed) n++;
                return n;
            }
        }

        public void ClearAll()
        {
            try
            {
                var list = new List<McEntity>(McEntity.All);
                foreach (var e in list)
                {
                    if (e == null) continue;
                    try { e.Discard(); } catch (Exception ex) { ELog.Error("ClearAll discard", ex); }
                }
                McEntity.All.Clear();
                Particles.I?.Clear();
            }
            catch (Exception e) { ELog.Error("ClearAll", e); }
        }

        // ================================================================== persistence ("mobs")

        private string SaveMobs()
        {
            // saved mobs not restored yet (SR saves right after loading, or the player quits within 1.5 s): keep them
            if (pendingMobs != null) return pendingMobs;
            try
            {
                var arr = JsonNode.NewArray();
                foreach (var m in McMob.Mobs)
                {
                    if (m == null || m.Removed || m.Dead || m.Def == null || !m.Def.Persistent) continue;
                    var o = JsonNode.NewObject();
                    m.Save(o);
                    arr.Add(o);
                }
                var root = JsonNode.NewObject();
                root["version"] = JsonNode.Of(1);
                root["mobs"] = arr;
                return root.ToString();
            }
            catch (Exception e) { ELog.Error("SaveMobs", e); return null; }
        }

        private void LoadMobs(string data)
        {
            pendingMobs = string.IsNullOrEmpty(data) ? null : data;
            if (pendingMobs != null && worldReady && isActiveAndEnabled) StartCoroutine(SpawnPendingLater());
        }

        private IEnumerator SpawnPendingLater()
        {
            // let SR finish placing the player and terrain colliders
            yield return new WaitForSeconds(1.5f);
            var data = pendingMobs;
            pendingMobs = null;
            if (data == null || !worldReady) yield break;
            try
            {
                var root = JsonNode.Parse(data);
                if (root == null) yield break;
                int n = 0;
                foreach (var o in root["mobs"].Items)
                {
                    var id = o["id"].AsString();
                    var pos = new Vector3(o["x"].AsFloat(), o["y"].AsFloat(), o["z"].AsFloat());
                    var m = SpawnMobStatic(id, pos + Vector3.up * 0.05f, o["yaw"].AsFloat(), o["v"].AsInt(-1));
                    if (m == null) continue;
                    m.Load(o);
                    n++;
                }
                ELog.Info("Restored " + n + " saved Minecraft mobs");
            }
            catch (Exception e) { ELog.Error("LoadMobs", e); }
        }

        // ================================================================== commands (only if Core did not define them)

        private bool commandsRegistered;

        private void RegisterCommands()
        {
            commandsRegistered = true;
            try
            {
                var names = new HashSet<string>(SC.Commands.Names ?? new string[0]);
                if (!names.Contains("explode"))
                    SC.Commands.Register("explode", "/explode [power]  - Minecraft explosion where you look", args =>
                    {
                        if (SC.SR == null || !SC.SR.InGame) return "Not in game";
                        float power = 4f;
                        if (args.Length > 0) float.TryParse(args[0], NumberStyles.Float, CultureInfo.InvariantCulture, out power);
                        var ray = new Ray(SC.SR.EyePosition, SC.SR.LookDirection);
                        Vector3 p = SC.SR.RaycastWorld(ray, 64f, out var h) ? h.point : ray.GetPoint(8f);
                        ExplosionSystem.Boom(p, Mathf.Clamp(power, 0.5f, 16f), null, true, "/explode");
                        return "Boom (power " + power.ToString(CultureInfo.InvariantCulture) + ")";
                    });
                if (!names.Contains("mobspawning"))
                {
                    SC.Commands.Register("mobspawning", "/mobspawning <on|off|ranch on|ranch off|status|now>  - Minecraft mob natural spawning", MobSpawningCommand);
                    SC.Commands.RegisterCompleter("mobspawning", (i, a) =>
                        i == 0 ? new[] { "on", "off", "ranch", "status", "now" }
                        : i == 1 && a.Length > 0 && a[0].Equals("ranch", StringComparison.OrdinalIgnoreCase) ? new[] { "on", "off" } : new string[0]);
                }
                if (!names.Contains("mobs"))
                    SC.Commands.Register("mobs", "/mobs  - count Minecraft entities", args =>
                    {
                        var counts = new Dictionary<string, int>();
                        foreach (var m in McMob.Mobs) if (m != null && !m.Dead && !m.Removed && m.Def != null) { counts.TryGetValue(m.Def.Id, out int c); counts[m.Def.Id] = c + 1; }
                        var parts = new List<string>();
                        foreach (var kv in counts) parts.Add(kv.Key.Replace("minecraft:", "") + " x" + kv.Value);
                        return "Mobs: " + LiveMobCount + (parts.Count > 0 ? " (" + string.Join(", ", parts.ToArray()) + ")" : "") + ", items: " + DroppedItem.Items.Count;
                    });
            }
            catch (Exception e) { ELog.Error("RegisterCommands", e); }
        }

        /// <summary>/mobspawning on|off|ranch on|ranch off|status|now (settings are BepInEx config entries, saved to the .cfg).</summary>
        private string MobSpawningCommand(string[] args)
        {
            try
            {
                string a0 = args.Length > 0 ? args[0].Trim().ToLowerInvariant() : "status";
                string a1 = args.Length > 1 ? args[1].Trim().ToLowerInvariant() : "";
                string result;
                switch (a0)
                {
                    case "on":
                    case "off":
                        EConfig.NaturalSpawning.Value = a0 == "on";
                        SaveConfig();
                        result = "Natural mob spawning " + (a0 == "on" ? "enabled" : "disabled") + " (saved to the config)";
                        break;
                    case "ranch":
                        if (a1 != "on" && a1 != "off") return "Usage: /mobspawning ranch <on|off>  (hostiles on The Ranch are currently " + (EConfig.SpawnOnRanch.Value ? "allowed" : "blocked") + ")";
                        EConfig.SpawnOnRanch.Value = a1 == "on";
                        SaveConfig();
                        result = "Hostile mobs on The Ranch " + (a1 == "on" ? "ALLOWED - your slimes are no longer protected" : "blocked (the ranch is protected)") + " (saved to the config)";
                        break;
                    case "status":
                        result = spawner.Status();
                        break;
                    case "now":
                        if (SC.SR == null || !SC.SR.InGame) return "Not in game";
                        if (!EConfig.NaturalSpawning.Value) return "Natural spawning is off (/mobspawning on)";
                        result = "Spawn cycle: " + spawner.RunCycle();
                        break;
                    default:
                        return "Usage: /mobspawning <on|off|ranch on|ranch off|status|now>";
                }
                ELog.Info("/mobspawning " + string.Join(" ", args) + " -> " + result);
                return result;
            }
            catch (Exception e) { ELog.Error("/mobspawning", e); return "Error: " + e.Message; }
        }

        private static void SaveConfig()
        {
            try { SC.Config?.Save(); } catch (Exception e) { ELog.Error("Config save", e); }
        }
    }
}
