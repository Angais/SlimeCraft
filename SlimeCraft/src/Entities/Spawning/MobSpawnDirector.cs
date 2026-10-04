using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace SlimeCraft.Entities
{
    /// <summary>
    /// Minecraft-like natural spawning adapted to Slime Rancher. Every 1.5 s one spawn cycle: up to 10 attempts, each picks a
    /// random column 24-44 blocks (horizontally) from the player, raycasts down from 32 blocks above the player onto solid
    /// SR terrain / our blocks (World mask, triggers ignored) and tests the first surfaces it finds with
    /// <see cref="SpawnRules.CheckStand"/> (not steep, within ±24 blocks of the player's height, not on SR actors, not in
    /// water / slime sea, free space, not inside SR geometry, never inside or under player blocks; hostiles: not on The Ranch
    /// unless allowed and not near Minecraft light blocks). The first 7 attempts also require a spot the camera cannot see.
    /// SR night: hostiles (zombie 40 %, skeleton 30 %, creeper 25 %, enderman 4 %, slime 1 %). SR day: groups of 1-3
    /// animals in the PassiveZones. Caps: hostiles and animals are counted separately within 96 blocks (iron golems count
    /// for neither). Despawn: natural hostiles beyond 64 blocks at once, beyond 32 blocks randomly (Minecraft's 1/800 per
    /// tick), any hostile beyond 128 blocks. Rejection reasons are counted and summarised in the log every 30 s.
    /// </summary>
    internal sealed class MobSpawnDirector
    {
        public const float Interval = 1.5f;
        private const float SummaryInterval = 30f;
        private const int Attempts = 10;
        private const int HiddenAttempts = 7;
        private const float CapRadius = 96f;

        private float nextTick, nextSummary;
        private readonly RaycastHit[] hits = new RaycastHit[32];

        // statistics: current summary window + totals since the game started
        private readonly int[] rej = new int[(int)SpawnReject.Count];
        private readonly int[] rejTotal = new int[(int)SpawnReject.Count];
        private readonly int[] attemptRej = new int[(int)SpawnReject.Count];
        private int cycles, attempts, spawned, skipRanch, skipCap, skipZone;
        private int totalCycles, totalAttempts, totalSpawned;
        private string lastOutcome = "no spawn cycle yet";
        private float lastOutcomeTime = -1f;

        public string LastOutcome => lastOutcome;

        public void Tick()
        {
            if (Time.time < nextTick) return;
            nextTick = Time.time + Interval;
            try
            {
                if (SC.SR == null || !SC.SR.InGame || SC.SR.IsPaused || SC.SR.Player == null) return;
                Despawn();
                if (EConfig.NaturalSpawning.Value) RunCycle();
                MaybeSummary();
            }
            catch (Exception e) { ELog.Error("MobSpawnDirector", e); }
        }

        /// <summary>One spawn cycle (also used by "/mobspawning now"). Returns a one-line outcome.</summary>
        public string RunCycle()
        {
            if (SC.SR == null || !SC.SR.InGame || SC.SR.Player == null) return Outcome("not in game");
            cycles++; totalCycles++;
            bool night = SC.SR.IsNight;
            string zone = SC.SR.ZoneName ?? "";
            if (night)
            {
                if (!EConfig.SpawnOnRanch.Value && SRZones.PlayerOnRanch())
                {
                    skipRanch++;
                    return Outcome("night, but the player is on The Ranch and Entities.SpawnOnRanch is off (no hostiles)");
                }
                int n = CountNear(true);
                if (n >= EConfig.HostileCap.Value) { skipCap++; return Outcome("hostile cap reached (" + n + "/" + EConfig.HostileCap.Value + ")"); }
                return TrySpawn(PickHostile(), 1, true, zone);
            }
            if (!ZoneAllowsPassives(zone)) { skipZone++; return Outcome("day, zone '" + zone + "' is not in Entities.PassiveZones (no animals)"); }
            int p = CountNear(false);
            if (p >= EConfig.PassiveCap.Value) { skipCap++; return Outcome("animal cap reached (" + p + "/" + EConfig.PassiveCap.Value + ")"); }
            // animals come in small groups of 1-3
            return TrySpawn(PickPassive(), 1 + Rng.I(3), false, zone);
        }

        private string Outcome(string s) { lastOutcome = s; lastOutcomeTime = Time.time; return s; }

        private static bool ZoneAllowsPassives(string zone)
        {
            var parts = (EConfig.PassiveZones.Value ?? "").Split(',');
            foreach (var p in parts)
            {
                var t = p.Trim();
                if (t.Length > 0 && zone.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }

        private static string PickHostile()
        {
            float r = Rng.F() * 100f;
            if (r < 40f) return MobIds.Zombie;
            if (r < 70f) return MobIds.Skeleton;
            if (r < 95f) return MobIds.Creeper;
            if (r < 99f) return MobIds.Enderman;
            return MobIds.Slime;
        }

        private static string PickPassive()
        {
            int r = Rng.I(4);
            return r == 0 ? MobIds.Pig : r == 1 ? MobIds.Cow : r == 2 ? MobIds.Sheep : MobIds.Chicken;
        }

        /// <summary>Mobs of the category within 96 blocks: hostiles (zombie, skeleton, creeper, enderman, slime) or animals (pig, cow, sheep, chicken).</summary>
        public static int CountNear(bool hostile)
        {
            var p = SC.SR.PlayerFeet;
            int n = 0;
            foreach (var m in McMob.Mobs)
            {
                if (m == null || m.Dead || m.Removed) continue;
                bool counts = hostile ? m.IsHostile : m is AnimalMob;
                if (!counts) continue;
                if ((m.transform.position - p).sqrMagnitude < CapRadius * CapRadius) n++;
            }
            return n;
        }

        /// <summary>
        /// Despawn rules (Minecraft's, scaled to SR): natural hostiles beyond 64 blocks at once, beyond 32 blocks with
        /// Minecraft's 1/800 chance per tick once they are 30 s old; any hostile beyond 128 blocks; anything lost far below the player.
        /// </summary>
        private static void Despawn()
        {
            var p = SC.SR.PlayerFeet;
            float randomChance = 1f - Mathf.Pow(1f - 1f / 800f, Interval * Mc.TPS);
            for (int i = McMob.Mobs.Count - 1; i >= 0; i--)
            {
                var m = McMob.Mobs[i];
                if (m == null || m.Removed || m.Dead) continue;
                var d = m.transform.position - p;
                float h2 = Mc.Horizontal(d).sqrMagnitude;
                string why = null;
                if (m.IsHostile && m.NaturalSpawn && h2 > 64f * 64f) why = "far away";
                else if (m.IsHostile && h2 > 128f * 128f) why = "very far away";
                else if (d.y < -64f) why = "fell far below the player";
                else if (m.IsHostile && m.NaturalSpawn && h2 > 32f * 32f && Time.time - m.SpawnTime > 30f && Rng.Chance(randomChance)) why = "random despawn beyond 32 blocks";
                if (why == null) continue;
                ELog.Event("despawn", "despawned " + m.Def.Id + " at " + ELog.V(m.transform.position) + " (" + why + ")", 2f);
                m.Discard();
            }
        }

        private string TrySpawn(string mobId, int count, bool hostile, string zone)
        {
            var def = MobDef.Get(mobId);
            if (def == null) return Outcome("unknown mob " + mobId);
            var cam = SC.SR.MainCamera;
            Array.Clear(attemptRej, 0, attemptRej.Length);
            var checks = StandChecks.UnderBlocks;
            if (hostile) checks |= StandChecks.Light;
            if (hostile && !EConfig.SpawnOnRanch.Value) checks |= StandChecks.Ranch;
            for (int attempt = 0; attempt < Attempts; attempt++)
            {
                attempts++; totalAttempts++;
                var player = SC.SR.PlayerFeet;
                var c = Rng.OnUnitCircle() * Rng.Range(24f, 44f);
                if (!GroundAt(new Vector3(player.x + c.x, player.y, player.z + c.y), def, checks, out var feet, out var why)) { Note(why); continue; }
                if (cam != null && attempt < HiddenAttempts && IsVisible(cam, feet + Vector3.up * def.Height * 0.5f)) { Note(SpawnReject.Visible); continue; }

                int n = 0;
                for (int k = 0; k < count; k++)
                {
                    var p = feet;
                    if (k > 0)
                    {
                        var off = Rng.OnUnitCircle() * Rng.Range(1f, 3f);
                        if (!GroundAt(new Vector3(feet.x + off.x, feet.y + 1f, feet.z + off.y), def, checks, out p, out _, feet.y + 3f, 6f)) continue;
                    }
                    var mob = EntitiesModule.SpawnMobStatic(mobId, p, Rng.F() * 360f, -1);
                    if (mob == null) continue;
                    mob.NaturalSpawn = true;
                    n++;
                }
                if (n == 0) { Note(SpawnReject.SpawnFailed); continue; }
                spawned += n; totalSpawned += n;
                float dist = Mc.Horizontal(feet - player).magnitude;
                string msg = "spawned " + (n > 1 ? n + "x " : "") + mobId + " at " + ELog.V(feet) + " (" + (zone.Length > 0 ? zone : "unknown zone")
                             + ", " + (SC.SR.IsNight ? "night" : "day") + ", " + Mathf.RoundToInt(dist) + " m from the player, attempt " + (attempt + 1) + ")";
                ELog.Event("spawn", msg, 1f, 4);
                return Outcome(msg);
            }
            return Outcome("no valid spot for " + mobId + " in " + Attempts + " attempts (" + Reasons(attemptRej, 4) + ")");
        }

        private void Note(SpawnReject why)
        {
            int i = (int)why;
            if (i <= 0 || i >= rej.Length) return;
            rej[i]++; rejTotal[i]++; attemptRej[i]++;
        }

        /// <summary>
        /// Raycast down the column onto solid ground and test the first few surfaces found (nearest first): steep surfaces and
        /// surfaces too far above the player are skipped (the next surface below is tried), SR actors refuse the column.
        /// </summary>
        private bool GroundAt(Vector3 column, MobDef def, StandChecks checks, out Vector3 feet, out SpawnReject why, float originY = float.NaN, float maxDist = 80f)
        {
            feet = Vector3.zero;
            why = SpawnReject.NoGround;
            var player = SC.SR.PlayerFeet;
            var origin = new Vector3(column.x, float.IsNaN(originY) ? player.y + 32f : originY, column.z);
            int n = Physics.RaycastNonAlloc(origin, Vector3.down, hits, maxDist, ELayers.World, QueryTriggerInteraction.Ignore);
            if (n == 0) return false;
            Array.Sort(hits, 0, n, HitComparer.I);
            var playerT = SC.SR.Player != null ? SC.SR.Player.transform : null;
            int tested = 0;
            for (int i = 0; i < n && tested < 3; i++)
            {
                var h = hits[i];
                var col = h.collider;
                if (col == null) continue;
                if (playerT != null && col.transform.IsChildOf(playerT)) continue;
                if (col.GetComponentInParent<McEntity>() != null) continue;
                if (SC.SR.GetSRActorId(col.gameObject) != null) { why = SpawnReject.OnSRActor; return false; } // gordos, static actors
                float dy = h.point.y - player.y;
                if (dy > 24f) { why = SpawnReject.Height; continue; }
                if (dy < -24f) { why = SpawnReject.Height; return false; }
                if (h.normal.y < 0.7f) { why = SpawnReject.Steep; continue; }
                tested++;
                var f = h.point + Vector3.up * 0.05f;
                why = SpawnRules.CheckStand(f, def.Width, def.Height, checks);
                if (why == SpawnReject.None) { feet = f; return true; }
                // spots further down the same column are under water / the structure / in the same lit or ranch area too
                if (why == SpawnReject.Water || why == SpawnReject.KillVolume || why == SpawnReject.UnderPlayerBlocks
                    || why == SpawnReject.NearLight || why == SpawnReject.RanchArea || why == SpawnReject.RegionUnloaded) return false;
            }
            return false;
        }

        private static bool IsVisible(Camera cam, Vector3 p)
        {
            var vp = cam.WorldToViewportPoint(p);
            if (vp.z <= 0f || vp.x < -0.05f || vp.x > 1.05f || vp.y < -0.05f || vp.y > 1.05f) return false;
            return !EPhys.WorldBlocked(cam.transform.position, p);
        }

        private sealed class HitComparer : IComparer<RaycastHit>
        {
            public static readonly HitComparer I = new HitComparer();
            public int Compare(RaycastHit a, RaycastHit b) => a.distance.CompareTo(b.distance);
        }

        // ------------------------------------------------------------------ debug summary / status

        private static string Reasons(int[] counts, int max)
        {
            var order = new List<int>();
            for (int i = 1; i < counts.Length; i++) if (counts[i] > 0) order.Add(i);
            if (order.Count == 0) return "no rejections";
            order.Sort((a, b) => counts[b].CompareTo(counts[a]));
            var sb = new StringBuilder();
            for (int k = 0; k < order.Count && k < max; k++)
            {
                if (sb.Length > 0) sb.Append(", ");
                sb.Append(((SpawnReject)order[k]).ToString()).Append(' ').Append(counts[order[k]]);
            }
            if (order.Count > max) sb.Append(", ...");
            return sb.ToString();
        }

        private void MaybeSummary()
        {
            if (Time.time < nextSummary) return;
            nextSummary = Time.time + SummaryInterval;
            if (cycles == 0 || EConfig.SpawnDebugSummary == null || !EConfig.SpawnDebugSummary.Value) return;
            string state = (SC.SR.IsNight ? "night" : "day") + ", zone '" + (SC.SR.ZoneName ?? "") + "'" + (SRZones.PlayerOnRanch() ? " (ranch)" : "");
            ELog.Info("[spawn-debug] last " + Mathf.RoundToInt(SummaryInterval) + " s: " + state + ", hostiles " + CountNear(true) + "/" + EConfig.HostileCap.Value
                      + ", animals " + CountNear(false) + "/" + EConfig.PassiveCap.Value + " | cycles " + cycles + ", attempts " + attempts + ", spawned " + spawned
                      + " | skipped: ranch " + skipRanch + ", cap " + skipCap + ", zone " + skipZone + " | rejected: " + Reasons(rej, 16));
            Array.Clear(rej, 0, rej.Length);
            cycles = attempts = spawned = skipRanch = skipCap = skipZone = 0;
        }

        /// <summary>Text for "/mobspawning status".</summary>
        public string Status()
        {
            var sb = new StringBuilder();
            sb.Append("Natural spawning ").Append(EConfig.NaturalSpawning.Value ? "ON" : "OFF");
            sb.Append(", hostiles on The Ranch ").Append(EConfig.SpawnOnRanch.Value ? "ALLOWED" : "blocked");
            if (SC.SR != null && SC.SR.InGame)
            {
                sb.Append(" | ").Append(SC.SR.IsNight ? "night (hostiles)" : "day (animals)").Append(", zone '").Append(SC.SR.ZoneName).Append("'");
                if (SRZones.PlayerOnRanch()) sb.Append(" (ranch)");
                sb.Append(", hostiles ").Append(CountNear(true)).Append('/').Append(EConfig.HostileCap.Value);
                sb.Append(", animals ").Append(CountNear(false)).Append('/').Append(EConfig.PassiveCap.Value);
            }
            sb.Append(" | total: ").Append(totalCycles).Append(" cycles, ").Append(totalAttempts).Append(" attempts, ").Append(totalSpawned).Append(" spawned");
            sb.Append(" | top rejections: ").Append(Reasons(rejTotal, 5));
            if (lastOutcomeTime >= 0f) sb.Append(" | last (").Append(Mathf.RoundToInt(Time.time - lastOutcomeTime)).Append(" s ago): ").Append(lastOutcome);
            return sb.ToString();
        }

        public void ResetForWorld()
        {
            nextTick = Time.time + 3f; // let SR settle the player/terrain first
            nextSummary = Time.time + SummaryInterval;
            Array.Clear(rej, 0, rej.Length);
            cycles = attempts = spawned = skipRanch = skipCap = skipZone = 0;
            lastOutcome = "no spawn cycle yet"; lastOutcomeTime = -1f;
        }
    }
}
