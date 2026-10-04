using System;
using System.Collections.Generic;
using UnityEngine;

namespace SlimeCraft.Entities
{
    /// <summary>
    /// A dropped item that behaves and looks like one in Minecraft: 0.25 block hitbox, gravity 0.04 b/t², drag 0.98,
    /// ground friction 0.6*0.98, a slow spin and a gentle bob, 1-5 visible copies depending on the stack size, merging
    /// with identical neighbours, 6000 tick (5 min) despawn, pickup into SC.Inventory with Minecraft's pickup sound and
    /// a fly-to-player animation. Slime Rancher bridges: vacpack suction and "food touching a slime" conversion.
    /// </summary>
    internal sealed class DroppedItem : McEntity
    {
        public static readonly List<DroppedItem> Items = new List<DroppedItem>();

        public ItemStack Stack;
        public float PickupDelay;       // seconds
        public float AgeTicks;
        private float bobOffs;
        private Transform vis;
        private readonly List<Transform> copies = new List<Transform>();
        private int renderedCount = -1;
        private bool isBlock;
        private BoxCollider col;
        private bool grounded;
        private float nextMerge, nextFoodCheck;
        private bool foodBridgeDisabled;
        private bool vacPulled;
        private bool inWater; private float waterTopY;
        private float nextPickupTry, nextRegionCheck;
        private Mesh mesh; private Material material;

        private static readonly Collider[] foodCols = new Collider[64];
        private static readonly Dictionary<string, Identifiable.Id> srIdCache = new Dictionary<string, Identifiable.Id>();
        private static readonly HashSet<string> badSrIds = new HashSet<string>();

        public override float BBWidth => 0.25f;
        public override float BBHeight => 0.25f;

        public const float MaxAgeTicks = 6000f;

        public static DroppedItem Spawn(ItemStack stack, Vector3 pos, Vector3 velocity, float pickupDelay)
        {
            if (stack == null || stack.IsEmpty) return null;
            var go = new GameObject("MC Item " + stack.Id);
            go.layer = ELayers.Item;
            go.transform.position = pos;
            var it = go.AddComponent<DroppedItem>();
            it.Stack = stack.Copy();
            it.PickupDelay = pickupDelay;
            it.Init(velocity);
            return it;
        }

        protected override void OnEnable() { base.OnEnable(); if (!Items.Contains(this)) Items.Add(this); }
        protected override void OnDestroy() { base.OnDestroy(); Items.Remove(this); }

        private void Init(Vector3 velocity)
        {
            bobOffs = Rng.F() * Mathf.PI * 2f;
            SetupBody(0.1f);
            Body.velocity = velocity;
            col = AddChildCollider<BoxCollider>("col", ELayers.Item);
            col.size = new Vector3(0.25f, 0.25f, 0.25f);
            col.center = new Vector3(0f, 0.125f, 0f);
            col.sharedMaterial = Frictionless;
            if (ELayers.ItemNeedsPlayerIgnore) IgnorePlayer(col);

            vis = new GameObject("vis").transform;
            vis.SetParent(transform, false);
            BuildVisual();
        }

        private void BuildVisual()
        {
            try
            {
                isBlock = SC.ItemVisuals != null && SC.ItemVisuals.IsBlockItem(Stack.Id);
                mesh = SC.ItemVisuals?.GetItemMesh(Stack.Id);
                material = SC.ItemVisuals?.GetItemMaterial(Stack.Id);
            }
            catch (Exception e) { ELog.Error("Item visuals " + Stack.Id, e); }
            if (mesh == null) { mesh = EMat.UnitCube(); isBlock = true; }
            if (material == null) material = EMat.Lit("missing", EMat.Tex("missing"));
            RefreshCopies();
        }

        /// <summary>
        /// Shows 1 to 5 copies of the item depending on the stack size (1 / up to 16 / 32 / 48 / more), so a big stack
        /// looks like a pile. Block copies are scattered up to 0.15 around the first one; flat items are fanned out
        /// front to back, 1.5 item thicknesses apart, with half that scatter. The layout is stable per item kind.
        /// </summary>
        private void RefreshCopies()
        {
            int amount = CopiesFor(Stack.Count);
            if (amount == renderedCount) return;
            renderedCount = amount;
            foreach (var c in copies) if (c != null) Destroy(c.gameObject);
            copies.Clear();
            var rnd = new System.Random(Stack.Id.GetHashCode() + Stack.Damage);
            float scale = isBlock ? 0.25f : 0.5f;
            float thickness = isBlock ? 0.25f : 0.5f / 16f;
            bool flat = thickness <= 0.0625f;
            float scatter = flat ? 0.075f : 0.15f;
            float spacing = thickness * 1.5f;
            float firstZ = -spacing * (amount - 1) * 0.5f;
            for (int i = 0; i < amount; i++)
            {
                var go = new GameObject("copy" + i);
                go.layer = gameObject.layer;
                go.transform.SetParent(vis, false);
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                var mr = go.AddComponent<MeshRenderer>();
                mr.sharedMaterial = material;
                mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
                go.transform.localScale = Vector3.one * scale;
                Vector3 off = Vector3.zero;
                if (i > 0)
                {
                    off.x = Jitter(rnd) * scatter;
                    off.y = Jitter(rnd) * scatter;
                    if (!flat) off.z = Jitter(rnd) * scatter;
                }
                if (flat) off.z = firstZ + spacing * i;
                go.transform.localPosition = off;
                copies.Add(go.transform);
            }
        }

        private static int CopiesFor(int count)
        {
            if (count <= 1) return 1;
            if (count <= 16) return 2;
            if (count <= 32) return 3;
            if (count <= 48) return 4;
            return 5;
        }

        private static float Jitter(System.Random rnd) => (float)rnd.NextDouble() * 2f - 1f;

        public void SetCount(int count)
        {
            Stack.Count = count;
            if (count <= 0) { Discard(); return; }
            RefreshCopies();
        }

        private void Update()
        {
            if (Removed) return;
            try
            {
                float dt = Time.deltaTime;
                if (dt <= 0f) return;
                AgeTicks += dt * Mc.TPS;
                if (PickupDelay > 0f) PickupDelay -= dt;
                if (AgeTicks >= MaxAgeTicks) { Discard(); return; }

                // floating look: bob between 0 and 0.2 blocks above the resting height and spin one radian per
                // second; the model rests a sixteenth of a block plus half its own size above the entity position
                float bobHeight = 0.1f * (1f + Mathf.Sin(AgeTicks * 0.1f + bobOffs));
                float restHeight = 0.0625f + (isBlock ? 0.125f : 0.25f);
                vis.localPosition = Vector3.up * (restHeight + bobHeight);
                float spinRad = AgeTicks / Mc.TPS + bobOffs;
                vis.localRotation = Quaternion.AngleAxis(spinRad * Mathf.Rad2Deg, Vector3.up);

                if (SC.SR == null || !SC.SR.InGame) return;
                TryPickup();
                if (Removed) return;
                if (Time.time >= nextMerge) { nextMerge = Time.time + (Body.velocity.sqrMagnitude > 0.01f ? 0.1f : 2f); TryMerge(); }
                if (Removed) return;
                if (Time.time >= nextRegionCheck)
                {
                    // SR unloaded the region under us (terrain colliders gone): hold still instead of falling out of the world
                    nextRegionCheck = Time.time + 0.75f + Rng.F() * 0.5f;
                    bool loaded = SRZones.RegionLoadedAt(transform.position + Vector3.up * 0.2f);
                    if (Body != null && Body.isKinematic == loaded) { Body.isKinematic = !loaded; if (loaded) Body.velocity = Vector3.zero; }
                }
                if (Time.time >= nextFoodCheck)
                {
                    nextFoodCheck = Time.time + 0.25f;
                    CheckLiquid();
                    if (Removed) return;
                    FoodBridge();
                }
                if (Removed) return;
                if (transform.position.y < SC.SR.PlayerFeet.y - 96f) Discard();
            }
            catch (Exception e) { ELog.Error("DroppedItem.Update", e); }
        }

        private void FixedUpdate()
        {
            if (Removed || Body == null || Body.isKinematic) return;
            try
            {
                float dt = Time.fixedDeltaTime;
                var v = Body.velocity;
                vacPulled = false;
                if (SC.SR != null && SC.SR.InGame && SC.SR.VacActive) vacPulled = VacPull(ref v);
                if (!vacPulled)
                {
                    grounded = ProbeGround(0.125f, 0.06f, out var gh);
                    if (inWater && Center.y < waterTopY)
                    {
                        // under water: no gravity, light horizontal drag and a slow upward drift (capped at 0.06 b/t)
                        float hd = Mc.PerTick(0.99f, dt);
                        v.x *= hd; v.z *= hd;
                        if (v.y < 0.06f * Mc.BptToMs) v.y += 5e-4f * Mc.Bpt2ToMs2 * dt;
                    }
                    else v.y -= 0.04f * Mc.Bpt2ToMs2 * dt;
                    float drag = Mc.PerTick(0.98f, dt);
                    float friction = drag;
                    if (grounded)
                    {
                        float slip = 0.6f;
                        try
                        {
                            var below = Vector3Int.FloorToInt(transform.position + Vector3.down * 0.1f);
                            var id = SC.Blocks?.GetBlock(below);
                            if (id != null) { var def = Content.Block(id); if (def != null) slip = def.Slipperiness; }
                        }
                        catch { }
                        friction = Mc.PerTick(slip * 0.98f, dt);
                    }
                    v.x *= friction; v.z *= friction; v.y *= drag;
                }
                Body.velocity = v;
            }
            catch (Exception e) { ELog.Error("DroppedItem.FixedUpdate", e); }
        }

        /// <summary>SR vacpack: items inside the vac cone (30°, 10 m) are sucked toward the nozzle and collected at 1.2 m.</summary>
        private bool VacPull(ref Vector3 v)
        {
            var ray = SC.SR.VacRay;
            var to = Center - ray.origin;
            float d = to.magnitude;
            if (d > 10f || d < 0.01f) return false;
            if (Vector3.Angle(ray.direction, to) > 30f) return false;
            if (EPhys.WorldBlocked(ray.origin, Center)) return false;
            float speed = 6f + (10f - d) * 1.2f;
            v = Vector3.Lerp(v, -to / d * speed, 0.35f);
            if (d < 1.2f && PickupDelay <= 0f) GiveToPlayer(true);
            return true;
        }

        /// <summary>Player inside Minecraft's pickup box (player AABB inflated 1,0.5,1).</summary>
        private void TryPickup()
        {
            if (PickupDelay > 0f || SC.Inventory == null || Time.time < nextPickupTry) return;
            var feet = SC.SR.PlayerFeet;
            var p = transform.position;
            float dx = p.x - feet.x, dz = p.z - feet.z;
            if (dx * dx + dz * dz > 1.5f * 1.5f) return;
            if (p.y + 0.25f < feet.y - 0.5f || p.y > feet.y + 1.8f + 0.5f) return;
            GiveToPlayer(false);
        }

        private void GiveToPlayer(bool viaVac)
        {
            if (SC.Inventory == null || Removed) return;
            int before = Stack.Count;
            int left;
            try { left = SC.Inventory.Give(Stack.Copy()); }
            catch (Exception e) { ELog.Error("Inventory.Give", e); return; }
            if (left >= before) { nextPickupTry = Time.time + 0.25f; return; } // inventory full: retry a few times per second
            ELog.Event("pickup:" + Stack.Id, "item picked up: " + (before - Mathf.Max(0, left)) + "x " + Stack.Id + (viaVac ? " (vacpack)" : "")
                + (left > 0 ? ", " + left + " left on the ground (inventory full)" : ""), 0.5f);
            SC.Audio?.Play("entity.item.pickup", transform.position, 0.2f, (Rng.Diff() * 0.7f + 1f) * 2f);
            PickupAnimation.Start(this, mesh, material, isBlock);
            if (left <= 0) Discard();
            else SetCount(left);
        }

        /// <summary>Merges with identical items whose boxes, grown by 0.5 horizontally, overlap ours.</summary>
        private void TryMerge()
        {
            if (!Mergeable) return;
            var p = transform.position;
            for (int i = 0; i < Items.Count; i++)
            {
                var o = Items[i];
                if (o == null || o == this || o.Removed || !o.Mergeable) continue;
                var q = o.transform.position;
                if (Mathf.Abs(q.x - p.x) > 0.75f || Mathf.Abs(q.z - p.z) > 0.75f || Mathf.Abs(q.y - p.y) > 0.25f) continue;
                if (!Stack.CanMergeWith(o.Stack)) continue;
                int max = Stack.MaxStack;
                if (Stack.Count + o.Stack.Count > max) continue;
                // the bigger stack absorbs the smaller one
                DroppedItem to = o.Stack.Count < Stack.Count ? this : o, from = to == this ? o : this;
                to.Stack.Count += from.Stack.Count;
                to.PickupDelay = Mathf.Max(to.PickupDelay, from.PickupDelay);
                to.AgeTicks = Mathf.Min(to.AgeTicks, from.AgeTicks);
                to.RefreshCopies();
                from.Stack.Count = 0;
                from.Discard();
                if (from == this) return;
            }
        }

        private bool Mergeable => !Removed && AgeTicks < MaxAgeTicks && Stack.Count < Stack.MaxStack;

        /// <summary>Minecraft food touching a Slime Rancher slime becomes the SR food so the slime eats it (and makes plorts).</summary>
        private void FoodBridge()
        {
            if (foodBridgeDisabled || EConfig.SRFoodBridge == null || !EConfig.SRFoodBridge.Value) return;
            var def = Stack.Def;
            if (def == null || string.IsNullOrEmpty(def.SRFoodEquivalent) || SC.SR == null) { foodBridgeDisabled = true; return; }
            if (!TryParseSRId(def.SRFoodEquivalent, out var foodId))
            {
                foodBridgeDisabled = true;
                ELog.WarnOnce("srfoodid:" + def.SRFoodEquivalent, "SR food bridge: '" + def.SRFoodEquivalent + "' (for " + Stack.Id + ") is not an Identifiable.Id; disabled for this item.");
                return;
            }
            int mask = ~((1 << ELayers.SRWater) | (1 << ELayers.SRPlayer) | (1 << ELayers.SRWeapon) | (1 << ELayers.SRIgnoreRaycast));
            int n = Physics.OverlapSphereNonAlloc(Center, 1.2f, foodCols, mask, QueryTriggerInteraction.Ignore);
            string refusedBy = null;
            for (int i = 0; i < n; i++)
            {
                var c = foodCols[i];
                if (c == null || c.GetComponentInParent<McEntity>() != null) continue;
                string id = SC.SR.GetSRActorId(c.gameObject);
                if (id == null || !(id.EndsWith("_SLIME") || id.EndsWith("_LARGO"))) continue;
                // only when this slime actually eats the SR food (SR's SlimeEat.DoesEat checks its diet), so a veggie-only
                // slime does not get live hens next to it and nobody gets free plorts
                var eat = c.GetComponentInParent<SlimeEat>();
                if (eat == null || !eat.DoesEat(foodId)) { refusedBy = id; continue; }
                ConvertToSRFood(def.SRFoodEquivalent, id);
                return;
            }
            if (refusedBy != null)
                ELog.Event("srfood-refused:" + Stack.Id + refusedBy, "SR food bridge: " + refusedBy + " does not eat " + def.SRFoodEquivalent + " (" + Stack.Id + " stays a Minecraft item)", 30f, 1);
        }

        private static bool TryParseSRId(string s, out Identifiable.Id id)
        {
            if (srIdCache.TryGetValue(s, out id)) return true;
            if (badSrIds.Contains(s)) return false;
            try
            {
                if (Enum.IsDefined(typeof(Identifiable.Id), s)) { id = (Identifiable.Id)Enum.Parse(typeof(Identifiable.Id), s); srIdCache[s] = id; return true; }
            }
            catch { }
            badSrIds.Add(s);
            return false;
        }

        private void ConvertToSRFood(string srId, string slimeId)
        {
            int n = Mathf.Min(Stack.Count, 5);
            int spawned = 0;
            for (int k = 0; k < n; k++)
            {
                var pos = Center + Vector3.up * 0.3f + new Vector3(Rng.Diff() * 0.3f, k * 0.25f, Rng.Diff() * 0.3f);
                GameObject go = null;
                try { go = SC.SR.SpawnSRActor(srId, pos, Quaternion.Euler(0f, Rng.F() * 360f, 0f)); }
                catch (Exception e) { ELog.Error("SpawnSRActor " + srId, e); }
                if (go == null) break;
                spawned++;
            }
            if (spawned == 0)
            {
                foodBridgeDisabled = true;
                ELog.WarnOnce("srfood:" + srId, "SR food bridge: SpawnSRActor(\"" + srId + "\") failed for " + Stack.Id + "; disabling for this item.");
                return;
            }
            for (int k = 0; k < 4; k++) Particles.Poof(Center, new Vector3(Rng.Gaussian() * 0.02f, 0.03f, Rng.Gaussian() * 0.02f));
            ELog.Event("srfood:" + Stack.Id, "MC food converted into SR food: " + spawned + "x " + Stack.Id + " -> " + spawned + "x " + srId + " next to " + slimeId
                + " at " + ELog.V(transform.position) + (Stack.Count - spawned > 0 ? " (" + (Stack.Count - spawned) + " left as item)" : ""), 0.5f);
            SetCount(Stack.Count - spawned);
        }

        /// <summary>Water (LiquidSource with a waterTop) makes the item float like Minecraft items; the slime sea (KillOnTrigger) removes it.</summary>
        private void CheckLiquid()
        {
            inWater = false;
            int n = EPhys.Overlap(Center, 0.1f, ~0, QueryTriggerInteraction.Collide, out var cols);
            for (int i = 0; i < n; i++)
            {
                var c = cols[i];
                if (c == null || !c.isTrigger) continue;
                if (c.GetComponent<KillOnTrigger>() != null) { Discard(); return; }
                var ls = c.GetComponent<LiquidSource>();
                if (ls != null && ls.waterTop != null) { inWater = true; waterTopY = ls.waterTop.position.y; }
            }
        }

        public override void OnExplosion(Vector3 knockbackMs, float damage, GameObject source)
        {
            if (Time.time - SpawnTime < 0.05f) return; // drops created by this very explosion survive
            if (EConfig.ExplosionsDestroyItems != null && EConfig.ExplosionsDestroyItems.Value && damage > 0f) { Discard(); return; }
            base.OnExplosion(knockbackMs, damage, source);
        }
    }

    /// <summary>Pickup animation: the item model flies into the player over 3 ticks.</summary>
    internal sealed class PickupAnimation : MonoBehaviour
    {
        private Vector3 from; private float t;
        private const float Duration = 3f / Mc.TPS;

        public static void Start(DroppedItem src, Mesh mesh, Material mat, bool isBlock)
        {
            try
            {
                if (mesh == null || mat == null) return;
                var go = new GameObject("MC Pickup");
                go.transform.position = src.transform.position + Vector3.up * ((isBlock ? 0.125f : 0.25f) + 0.0625f);
                go.transform.localScale = Vector3.one * (isBlock ? 0.25f : 0.5f);
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                go.AddComponent<MeshRenderer>().sharedMaterial = mat;
                var pa = go.AddComponent<PickupAnimation>();
                pa.from = go.transform.position;
            }
            catch (Exception e) { ELog.Error("PickupAnimation", e); }
        }

        private void Update()
        {
            t += Time.deltaTime;
            float a = Mathf.Clamp01(t / Duration);
            var target = SC.SR != null ? SC.SR.PlayerFeet + Vector3.up * 0.9f : from;
            transform.position = Vector3.Lerp(from, target, a * a);
            if (a >= 1f) Destroy(gameObject);
        }
    }
}
