using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;

namespace SlimeCraft.Entities
{
    /// <summary>
    /// Minecraft-style explosions inside Slime Rancher (the <see cref="IExplosions"/> service).
    ///
    /// One blast, at a point with a Minecraft "power" (TNT 4, creeper 3), happens in this order:
    ///  1. pick the voxel blocks it destroys by marching rays outward, honouring blast resistance; Slime Rancher
    ///     terrain is never destroyed and shields whatever lies behind it;
    ///  2. damage and push our Minecraft entities, scaled by how much of each one the blast can see;
    ///  3. damage and push the player the same way;
    ///  4. remove the chosen blocks, turn hit TNT into primed TNT and drop some block items;
    ///  5. push Slime Rancher actors through the game's own explosion physics;
    ///  6. sound, particles and camera shake;
    ///  7. one rate-limited log line with statistics.
    /// Blocks are removed only after the damage steps so they still shield entities, and items or TNT created by the
    /// blast are not hit by it.
    ///
    /// It also forwards Slime Rancher's own explosions (Boom slimes...) to our entities, because those only push
    /// colliders that carry their own Rigidbody and ours keep their colliders on child objects.
    /// </summary>
    internal sealed class ExplosionSystem : IExplosions
    {
        // ------------------------------------------------------------------ tuning facts

        private const float RayStep = 0.3f;
        private const float RayDecay = 0.22500001f;      // intensity lost per step
        private const float UnknownBlockResistance = 6f;
        private const int SrActorLayerMaskAllButSixteen = ~(1 << 16);
        private const int MaxDebrisParticles = 400;

        private static ExplosionSystem active;

        /// <summary>True only while our own call into Slime Rancher's explosion runs (so the forwarding hook ignores it).</summary>
        internal static bool InOwnExplosion { get; private set; }
        private static int ownBlastFrame = -1;
        private static Vector3 ownBlastCenter;

        // ------------------------------------------------------------------ reusable buffers

        private static readonly Vector3[] RayDirections = BuildRayDirections();
        private readonly RaycastHit[] terrainHits = new RaycastHit[24];
        private readonly Collider[] nearbyColliders = new Collider[256];
        private readonly Dictionary<Vector3Int, string> blockLookups = new Dictionary<Vector3Int, string>();
        private readonly HashSet<Vector3Int> doomedCells = new HashSet<Vector3Int>();
        private readonly HashSet<Vector3Int> openCells = new HashSet<Vector3Int>();
        private readonly List<Vector3Int> removalOrder = new List<Vector3Int>();
        private readonly HashSet<GameObject> pushedActors = new HashSet<GameObject>();
        private readonly List<McEntity> victims = new List<McEntity>();
        private readonly List<DropPile> piles = new List<DropPile>();
        private readonly Vector3[] debrisDirs = new Vector3[MaxDebrisParticles];
        private readonly float[] debrisFractions = new float[MaxDebrisParticles];
        private static readonly List<McEntity> forwardedVictims = new List<McEntity>();

        private sealed class DropPile
        {
            public ItemStack Stack;
            public Vector3Int Cell;
        }

        /// <summary>Counters for the summary log line of the explosion in progress.</summary>
        private struct Tally
        {
            public int BlocksDestroyed, TntPrimed, StacksDropped, MobsHit, MobsKilled, ItemsDestroyed, ActorsPushed, PlayerDamage;
        }

        private Tally tally;
        private GameObject chainOwner;

        public ExplosionSystem() { active = this; }

        // ------------------------------------------------------------------ entry points

        public void Explode(Vector3 center, float power, GameObject source = null, bool breakBlocks = true)
            => Detonate(center, power, source, breakBlocks, null, null);

        /// <summary>Labelled explosion; <paramref name="attacker"/> is who gets the blame (for example the TNT igniter).</summary>
        internal static void Boom(Vector3 center, float power, GameObject source, bool breakBlocks, string label, GameObject attacker = null)
        {
            if (active != null) active.Detonate(center, power, source, breakBlocks, label, attacker);
            else SC.Explosions?.Explode(center, power, source, breakBlocks);
        }

        private void Detonate(Vector3 center, float power, GameObject source, bool breakBlocks, string label, GameObject attacker)
        {
            try
            {
                if (power <= 0.01f || SC.SR == null || !SC.SR.InGame) return;
                Particles.Ensure();

                bool touchBlocks = breakBlocks && Setting(EConfig.ExplosionsBreakBlocks, true);
                tally = default(Tally);
                chainOwner = attacker;

                bool marched = touchBlocks && SC.Blocks != null && SC.Blocks.Count > 0;
                int affected;
                if (marched) affected = MarchRays(center, power);
                else
                {
                    float r = 1.3f * power;
                    affected = Mathf.RoundToInt(4f / 3f * Mathf.PI * r * r * r);
                }

                HitEntities(center, power, source, attacker != null ? attacker : source);
                bool playerHurt = HitPlayer(center, power, source);
                if (marched && doomedCells.Count > 0) RemoveBlocks(power);
                PushSlimeRancherActors(center, power);
                PlayEffects(center, power, touchBlocks, affected, playerHurt);
                WriteLog(label, center, power, touchBlocks);
            }
            catch (Exception e) { ELog.Error("Explosion", e); }
        }

        // ------------------------------------------------------------------ 1. block selection

        /// <summary>Directions to every point on the surface of a 16x16x16 grid spanning [-1, 1]^3 (1352 of them), normalised.</summary>
        private static Vector3[] BuildRayDirections()
        {
            var list = new List<Vector3>(1352);
            for (int a = 0; a < 16; a++)
                for (int b = 0; b < 16; b++)
                    for (int c = 0; c < 16; c++)
                    {
                        bool onSurface = a == 0 || a == 15 || b == 0 || b == 15 || c == 0 || c == 15;
                        if (!onSurface) continue;
                        var v = new Vector3(a / 15f * 2f - 1f, b / 15f * 2f - 1f, c / 15f * 2f - 1f);
                        list.Add(v.normalized);
                    }
            return list.ToArray();
        }

        /// <summary>Marks the blocks the blast destroys; returns the number of affected positions (for the particle count).</summary>
        private int MarchRays(Vector3 center, float power)
        {
            blockLookups.Clear();
            doomedCells.Clear();
            openCells.Clear();

            for (int r = 0; r < RayDirections.Length; r++)
            {
                Vector3 dir = RayDirections[r];
                float strength = power * (0.7f + Rng.F() * 0.6f);
                float reach = strength / RayDecay * RayStep + RayStep;
                float terrainAt = DistanceToTerrain(center, dir, reach);

                Vector3 probe = center;
                float travelled = 0f;
                while (strength > 0f && travelled <= terrainAt)
                {
                    Vector3Int cell = Vector3Int.FloorToInt(probe);
                    string id = BlockAt(cell);
                    if (id == null)
                    {
                        openCells.Add(cell);
                    }
                    else
                    {
                        var def = Content.Block(id);
                        float resistance = def != null ? def.BlastResistance : UnknownBlockResistance;
                        strength -= (resistance + 0.3f) * 0.3f;
                        bool breakable = def == null || def.Hardness >= 0f;
                        if (strength > 0f && breakable) doomedCells.Add(cell);
                    }
                    probe += dir * RayStep;
                    travelled += RayStep;
                    strength -= RayDecay;
                }
            }
            return openCells.Count + doomedCells.Count;
        }

        /// <summary>Block id at a cell, looked up at most once per explosion (a failing lookup counts as empty).</summary>
        private string BlockAt(Vector3Int cell)
        {
            if (blockLookups.TryGetValue(cell, out var known)) return known;
            string id = null;
            try { id = SC.Blocks.GetBlock(cell); } catch { id = null; }
            if (string.IsNullOrEmpty(id)) id = null;
            blockLookups[cell] = id;
            return id;
        }

        /// <summary>
        /// Distance along a ray to the nearest Slime Rancher solid surface, ignoring our blocks, our entities and
        /// Slime Rancher actors (slimes and food do not shield anything). Infinity when nothing is hit.
        /// </summary>
        private float DistanceToTerrain(Vector3 origin, Vector3 dir, float maxDistance)
        {
            int n = Physics.RaycastNonAlloc(origin, dir, terrainHits, maxDistance, ELayers.World, QueryTriggerInteraction.Ignore);
            float nearest = float.PositiveInfinity;
            for (int i = 0; i < n; i++)
            {
                var hit = terrainHits[i];
                var col = hit.collider;
                if (col == null || hit.distance >= nearest) continue;
                if (SC.Blocks != null && SC.Blocks.IsBlockCollider(col)) continue;
                if (col.GetComponentInParent<McEntity>() != null) continue;
                if (SC.SR.GetSRActorId(col.gameObject) != null) continue;
                nearest = hit.distance;
            }
            return nearest;
        }

        // ------------------------------------------------------------------ 2. Minecraft entities

        private void HitEntities(Vector3 center, float power, GameObject source, GameObject blame)
        {
            float reach = 2f * power;
            victims.Clear();
            victims.AddRange(McEntity.All);   // hits can add or remove entities

            foreach (var e in victims)
            {
                if (e == null || e.Removed) continue;
                if (source != null && e.gameObject == source) continue;

                float ratio = Vector3.Distance(e.Feet, center) / reach;
                if (ratio > 1f) continue;

                var mob = e as McMob;
                Vector3 aim = e is LitTnt ? e.Feet : mob != null ? mob.EyePos : e.Center;
                Vector3 dir = aim - center;
                dir = dir.sqrMagnitude < 1e-6f ? Vector3.up : dir.normalized;

                float exposure = Exposure(center, e.BB);
                bool hurtable = !(e is LitTnt || e is FallingBlockBody || e is FlyingArrow);
                float damage = hurtable ? BlastDamage(ratio, exposure, reach) : 0f;
                float push = (1f - ratio) * exposure;

                bool wasAlive = mob != null && !mob.Dead;
                try { e.OnExplosion(dir * (push * Mc.BptToMs), damage, blame); }
                catch (Exception ex) { ELog.Error("OnExplosion", ex); }

                if (wasAlive)
                {
                    tally.MobsHit++;
                    if (mob.Dead) tally.MobsKilled++;
                }
                else if (e is DroppedItem && e.Removed)
                {
                    tally.ItemsDestroyed++;
                }
            }
            victims.Clear();
        }

        /// <summary>Minecraft half-heart damage; even a fully hidden target in range takes 1.</summary>
        private static float BlastDamage(float ratio, float exposure, float reach)
        {
            float impact = (1f - ratio) * exposure;
            return (impact * impact + impact) / 2f * 7f * reach + 1f;
        }

        /// <summary>
        /// Fraction of sample points of a box that have a clear line to the blast centre: up to 3 x 4 x 3 points
        /// spread evenly over the box, each nudged 0.05 blocks inward so ground-level samples do not start inside
        /// the floor.
        /// </summary>
        private static float Exposure(Vector3 center, Bounds box)
        {
            Vector3 size = box.size;
            Vector3 min = box.min;
            Vector3 middle = box.center;
            int nx = Mathf.Clamp(Mathf.FloorToInt(size.x * 2f + 1f), 1, 3);
            int ny = Mathf.Clamp(Mathf.FloorToInt(size.y * 2f + 1f), 1, 4);
            int nz = Mathf.Clamp(Mathf.FloorToInt(size.z * 2f + 1f), 1, 3);

            int clear = 0, total = 0;
            for (int ix = 0; ix < nx; ix++)
            {
                float x = min.x + size.x * SpreadFraction(ix, nx);
                for (int iy = 0; iy < ny; iy++)
                {
                    float y = min.y + size.y * SpreadFraction(iy, ny);
                    for (int iz = 0; iz < nz; iz++)
                    {
                        float z = min.z + size.z * SpreadFraction(iz, nz);
                        Vector3 sample = Vector3.MoveTowards(new Vector3(x, y, z), middle, 0.05f);
                        total++;
                        if (!EPhys.WorldBlocked(sample, center)) clear++;
                    }
                }
            }
            return total == 0 ? 0f : clear / (float)total;
        }

        private static float SpreadFraction(int index, int count) => count <= 1 ? 0.5f : index / (float)(count - 1);

        // ------------------------------------------------------------------ 3. player

        /// <summary>Damages and pushes the player; returns true when damage was actually dealt.</summary>
        private bool HitPlayer(Vector3 center, float power, GameObject source)
        {
            if (SC.SR.Player == null) return false;
            float reach = 2f * power;
            Vector3 feet = SC.SR.PlayerFeet;
            float ratio = Vector3.Distance(feet, center) / reach;
            if (ratio > 1f) return false;

            var body = new Bounds(feet + new Vector3(0f, 0.9f, 0f), new Vector3(0.6f, 1.8f, 0.6f));
            float exposure = Exposure(center, body);
            Vector3 dir = SC.SR.EyePosition - center;
            dir = dir.sqrMagnitude < 1e-6f ? Vector3.up : dir.normalized;

            bool damaged = false;
            bool creative = SC.Inventory != null && SC.Inventory.Creative;
            if (!creative && exposure > 0f)
            {
                int srDamage = Mathf.RoundToInt(BlastDamage(ratio, exposure, reach) * Setting(EConfig.PlayerDamageScale, 5f));
                if (srDamage > 0)
                {
                    try
                    {
                        SC.SR.DamagePlayer(srDamage, source);
                        tally.PlayerDamage += srDamage;
                        damaged = true;
                    }
                    catch (Exception e) { ELog.Error("DamagePlayer", e); }
                }
            }

            // Knockback applies in creative too.
            Vector3 shove = dir * ((1f - ratio) * exposure * Mc.BptToMs * Setting(EConfig.PlayerKnockbackScale, 0.5f));
            if (shove.sqrMagnitude > 0.01f)
            {
                try { SC.SR.AddPlayerVelocity(shove); }
                catch (Exception e) { ELog.Error("AddPlayerVelocity", e); }
            }
            return damaged;
        }

        // ------------------------------------------------------------------ 4. blocks, chain TNT, drops

        private void RemoveBlocks(float power)
        {
            removalOrder.Clear();
            removalOrder.AddRange(doomedCells);
            for (int i = removalOrder.Count - 1; i > 0; i--)
            {
                int j = Rng.I(i + 1);
                var tmp = removalOrder[i]; removalOrder[i] = removalOrder[j]; removalOrder[j] = tmp;
            }

            piles.Clear();
            float keepChance = 1f / power;
            foreach (var cell in removalOrder)
            {
                try { RemoveCell(cell, keepChance); }
                catch (Exception e) { ELog.Error("Explosion block", e); }
            }

            foreach (var pile in piles)
            {
                var c = pile.Cell;
                var at = new Vector3(c.x + 0.5f + 0.25f * Rng.Diff(),
                                     c.y + 0.5f + 0.25f * Rng.Diff() - 0.125f,
                                     c.z + 0.5f + 0.25f * Rng.Diff());
                var pop = new Vector3(Rng.F() * 0.2f - 0.1f, 0.2f, Rng.F() * 0.2f - 0.1f) * Mc.BptToMs;
                if (EntitiesModule.SpawnItemStatic(pile.Stack, at, pop, 0.5f) != null) tally.StacksDropped++;
            }
            piles.Clear();
        }

        /// <summary>Clears one marked cell: TNT turns into primed TNT, anything else may leave an item behind.</summary>
        private void RemoveCell(Vector3Int cell, float keepChance)
        {
            string id;
            try { id = SC.Blocks.GetBlock(cell); } catch { return; }
            if (string.IsNullOrEmpty(id)) return;   // something earlier already changed this cell

            var def = Content.Block(id);
            if (def != null && def.IsTnt)
            {
                // Chain reaction with a short random fuse; TNT never drops itself.
                if (!SC.Blocks.SetBlock(cell, null)) return;
                LitTnt.Spawn(new Vector3(cell.x + 0.5f, cell.y + 0.5f, cell.z + 0.5f), 10 + Rng.I(20), Vector3.zero, false, chainOwner);
                tally.TntPrimed++;
                return;
            }

            if (!SC.Blocks.SetBlock(cell, null)) return;
            tally.BlocksDestroyed++;

            string dropId = def == null ? id : (!string.IsNullOrEmpty(def.Drop) ? def.Drop : def.Id);
            if (string.IsNullOrEmpty(dropId)) return;
            int rolls = def == null ? 1 : Mathf.Max(1, def.DropCount);
            int kept = 0;
            for (int k = 0; k < rolls; k++)
                if (Rng.F() <= keepChance) kept++;
            if (kept > 0) AddToPile(dropId, kept, cell);
        }

        /// <summary>Adds to a pile of the same item when the whole amount fits (at most 16 per pile), else starts a new pile.</summary>
        private void AddToPile(string itemId, int count, Vector3Int cell)
        {
            foreach (var pile in piles)
            {
                if (pile.Stack.Id != itemId) continue;
                int cap = Mathf.Min(16, pile.Stack.MaxStack);
                if (pile.Stack.Count + count > cap) continue;
                pile.Stack.Count += count;
                return;
            }
            piles.Add(new DropPile { Stack = new ItemStack(itemId, count), Cell = cell });
        }

        // ------------------------------------------------------------------ 5. Slime Rancher actors

        private void PushSlimeRancherActors(Vector3 center, float power)
        {
            float radius = 2f * power;
            int counted = 0;
            try
            {
                // Count the bodies Slime Rancher's blast will push (log only).
                pushedActors.Clear();
                int n = Physics.OverlapSphereNonAlloc(center, radius, nearbyColliders, SrActorLayerMaskAllButSixteen, QueryTriggerInteraction.Ignore);
                for (int i = 0; i < n; i++)
                {
                    var col = nearbyColliders[i];
                    if (col == null || col.isTrigger) continue;
                    var go = col.gameObject;
                    if (go.GetComponent<Rigidbody>() == null || go.layer == ELayers.SRPlayer) continue;
                    if (SC.SR.GetSRActorId(go) == null) continue;
                    pushedActors.Add(go);
                }
                counted = pushedActors.Count;
                pushedActors.Clear();
            }
            catch (Exception e) { ELog.Error("CountSRActors", e); counted = 0; }
            tally.ActorsPushed = counted;

            // No player damage and no ignition: the player was already handled above.
            InOwnExplosion = true;
            ownBlastCenter = center;
            ownBlastFrame = Time.frameCount;
            try { SC.SR.SRExplode(center, radius, power * Setting(EConfig.SRExplosionPowerPerMcPower, 200f), 0f, 0f, false); }
            catch (Exception e) { ELog.Error("SRExplode", e); }
            finally { InOwnExplosion = false; }
        }

        // ------------------------------------------------------------------ 6. effects

        private void PlayEffects(Vector3 center, float power, bool touchBlocks, int affected, bool playerHurt)
        {
            SC.Audio?.Play("entity.generic.explode", center, 4f, (1f + Rng.Diff() * 0.2f) * 0.7f);

            if (power < 2f || !touchBlocks) Particles.Explosion(center, 0f);
            else Particles.ExplosionEmitter(center);

            ScatterDebris(center, power, Mathf.Min(affected, MaxDebrisParticles));

            // Slime Rancher already shakes the camera when the player takes damage.
            if (!playerHurt && EConfig.ScreenShake != null && EConfig.ScreenShake.Value && SC.SR.Player != null)
            {
                try
                {
                    float range = 6f * power;
                    float d = Vector3.Distance(SC.SR.EyePosition, center);
                    if (d <= range)
                    {
                        float closeness = 1f - d / range;
                        float intensity = Mathf.Lerp(0.3f, 2f, closeness) * Mathf.Clamp(power / 4f, 0.5f, 2f);
                        var shaker = SC.SR.Player.GetComponent<ScreenShaker>();
                        if (shaker != null) shaker.ShakeDamage(intensity);
                    }
                }
                catch (Exception e) { ELog.Error("ScreenShake", e); }
            }
        }

        /// <summary>
        /// Debris cloud in two passes. First up to <paramref name="budget"/> points are drawn inside the ball of
        /// radius <paramref name="power"/> (direction from a random point of the [-1, 1] cube, radial fraction
        /// u^(1/3) so the points fill the volume evenly) and points inside blocks are dropped. Then each survivor
        /// flies outward at 0.3 + 0.5 * u1 * u2 / (fraction + 0.1) blocks per tick, as a puff placed halfway out or as
        /// smoke placed on the point itself (even odds).
        /// </summary>
        private void ScatterDebris(Vector3 center, float power, int budget)
        {
            int kept = 0;
            for (int i = 0; i < budget; i++)
            {
                Vector3 dir = new Vector3(Rng.Range(-1f, 1f), Rng.Range(-1f, 1f), Rng.Range(-1f, 1f)).normalized;
                float fraction = Mathf.Pow(Rng.F(), 1f / 3f);
                if (IsSolidCell(center + dir * (fraction * power))) continue;
                debrisDirs[kept] = dir;
                debrisFractions[kept] = fraction;
                kept++;
            }

            for (int i = 0; i < kept; i++)
            {
                Vector3 dir = debrisDirs[i];
                float fraction = debrisFractions[i];
                Vector3 velocity = dir * (0.3f + 0.5f * Rng.F() * Rng.F() / (fraction + 0.1f));
                bool puff = Rng.F() < 0.5f;
                Vector3 at = center + dir * (fraction * power * (puff ? 0.5f : 1f));
                if (puff) Particles.Poof(at, velocity);
                else Particles.Smoke(at, velocity);
            }
        }

        private static bool IsSolidCell(Vector3 point)
        {
            if (SC.Blocks == null) return false;
            try { return !string.IsNullOrEmpty(SC.Blocks.GetBlock(Vector3Int.FloorToInt(point))); }
            catch { return false; }
        }

        // ------------------------------------------------------------------ 7. log

        private void WriteLog(string label, Vector3 center, float power, bool touchBlocks)
        {
            string name = string.IsNullOrEmpty(label) ? "Explosion" : label;
            string msg = name + " exploded at " + ELog.V(center) + ", power " + ELog.F(power)
                         + ": blocks destroyed " + tally.BlocksDestroyed
                         + (tally.TntPrimed > 0 ? " (+" + tally.TntPrimed + " TNT primed)" : "")
                         + (touchBlocks ? "" : " (block damage off)")
                         + ", item stacks dropped " + tally.StacksDropped
                         + ", SR actors pushed " + tally.ActorsPushed
                         + ", MC mobs hit " + tally.MobsHit
                         + (tally.MobsKilled > 0 ? " (" + tally.MobsKilled + " killed)" : "")
                         + ", items destroyed " + tally.ItemsDestroyed
                         + ", player damage " + tally.PlayerDamage + " SR hp";
            ELog.Event("explosion", msg, 1f, 8);
        }

        // ------------------------------------------------------------------ Slime Rancher explosions -> our entities

        /// <summary>
        /// Called after every Slime Rancher explosion (radius in metres, power in Rigidbody force units where a Boom
        /// slime is 600, player damage in SR health points). Pushes our entities with the game's soft falloff and
        /// converts the player damage range back into Minecraft damage for mobs. Our own blasts are skipped.
        /// </summary>
        internal static void OnSRExplosion(GameObject source, float radius, float power, float minPlayerDamage, float maxPlayerDamage)
        {
            try
            {
                if (InOwnExplosion || source == null) return;
                Vector3 center = source.transform.position;
                if (Time.frameCount == ownBlastFrame && (center - ownBlastCenter).sqrMagnitude < 1f) return;
                if (radius <= 0f) return;

                float damageScale = Mathf.Max(0.01f, Setting(EConfig.PlayerDamageScale, 5f));
                int pushed = 0;
                forwardedVictims.Clear();
                forwardedVictims.AddRange(McEntity.All);
                foreach (var e in forwardedVictims)
                {
                    if (e == null || e.Removed || e.gameObject == source) continue;
                    Vector3 offset = e.Center - center;
                    float d = offset.magnitude;
                    if (d > radius) continue;

                    Vector3 dir = d < 0.01f ? Vector3.up : offset / d;
                    float falloff = Mathf.Max(0f, 1f - Mathf.Max(2f, d) / radius);
                    float speed = power * falloff * falloff / 600f * 18f;    // a Boom slime gives at most 18 m/s
                    Vector3 shove = (dir + new Vector3(0f, 0.35f, 0f)).normalized * speed;
                    float damage = e is McMob ? Mathf.Lerp(minPlayerDamage, maxPlayerDamage, 1f - d / radius) / damageScale : 0f;

                    try { e.OnExplosion(shove, damage, source); pushed++; }
                    catch (Exception ex) { ELog.Error("OnSRExplosion entity", ex); }
                }
                forwardedVictims.Clear();

                if (pushed > 0)
                {
                    string actorId = SC.SR?.GetSRActorId(source) ?? "no id";
                    ELog.Event("srexplosion", "SR explosion of '" + source.name + "' (" + actorId + ") at " + ELog.V(center)
                                              + " pushed " + pushed + " Minecraft entities", 0.5f);
                }
            }
            catch (Exception e) { ELog.Error("OnSRExplosion", e); }
        }

        // ------------------------------------------------------------------ helpers

        private static bool Setting(ConfigEntry<bool> entry, bool fallback) => entry != null ? entry.Value : fallback;
        private static float Setting(ConfigEntry<float> entry, float fallback) => entry != null ? entry.Value : fallback;
    }
}
