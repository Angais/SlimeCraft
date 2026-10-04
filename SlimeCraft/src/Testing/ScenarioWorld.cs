using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace SlimeCraft.Testing
{
    /// <summary>Scenario steps 07-10: F3 screen, night + natural spawning (off the ranch), vacpack, scenic teleports.</summary>
    internal sealed partial class Scenario
    {
        // ================================================================== 07
        private IEnumerator DebugScreen(StepResult r)
        {
            // F3 cannot be simulated (UnityEngine.Input is read-only): toggle it through IHud.DebugScreenVisible
            var hud = SC.Hud;
            if (hud == null) { r.Skip("SC.Hud not available (Hud module missing)"); yield break; }
            bool old = false;
            try
            {
                old = hud.DebugScreenVisible;
                hud.DebugScreenVisible = true;
                r.Check("F3 debug screen shown (IHud.DebugScreenVisible)", hud.DebugScreenVisible);
            }
            catch (Exception e) { r.Fail("IHud.DebugScreenVisible threw " + e.Message); yield break; }
            yield return G.Wait(1f);
            yield return Shot(r, "07_debug_screen");
            try { hud.DebugScreenVisible = old; } catch (Exception e) { r.Note("restoring F3 threw " + e.Message); }
            yield return G.Wait(0.2f);
        }

        // ================================================================== 08
        /// <summary>
        /// Natural night spawning. Entities.SpawnOnRanch is false by design (no hostiles on the player's ranch), so the
        /// player is taken to an off-ranch zone first (Dry Reef, else Moss Blanket / Indigo Quarry), in creative mode so
        /// the hostiles ignore them, and the spawner gets up to 45 s. Afterwards: ClearAll, day, back to the ranch.
        /// </summary>
        private IEnumerator NightSpawning(StepResult r)
        {
            if (SC.Entities == null) { r.Fail("SC.Entities available (natural spawning)"); yield break; }
            if (SC.SR == null) { r.Fail("SC.SR available"); yield break; }
            Vector3 home = G.Feet;
            try
            {
                if (SC.Config != null && SC.Config.TryGetEntry<bool>("Entities", "NaturalSpawning", out var ns) && !ns.Value)
                {
                    r.Skip("[Entities] NaturalSpawning = false in the config");
                    yield break;
                }
                if (SC.Config != null && SC.Config.TryGetEntry<bool>("Entities", "SpawnOnRanch", out var onRanch))
                    r.Note("[Entities] SpawnOnRanch = " + onRanch.Value);
            }
            catch (Exception e) { r.Note("reading the Entities config failed: " + e.Message); }

            // creative: Minecraft hostiles ignore creative players (natural spawning does not depend on the game mode)
            G.Exec(r, "/gamemode creative");
            // the mobs of step 04 (ranch) would despawn/skew the count once we are far away: start from a clean slate
            try { SC.Entities.ClearAll(); } catch (Exception e) { r.Note("ClearAll threw: " + e.Message); }

            // 1) off the ranch
            bool reached = false;
            Vector3 dest = Vector3.zero;
            foreach (var zone in new[] { ZoneDirector.Zone.REEF, ZoneDirector.Zone.MOSS, ZoneDirector.Zone.QUARRY })
            {
                yield return GoToZone(r, zone, false, (ok, at) => { reached = ok; dest = at; });
                string zn = SC.SR.ZoneName ?? "";
                if (reached && zn.IndexOf("Ranch", StringComparison.OrdinalIgnoreCase) < 0) break;
                reached = false;
            }
            if (!r.Check("player is off the ranch for natural spawning", reached, "SR zone '" + SC.SR.ZoneName + "' at " + G.Feet.ToString("F0")))
            {
                yield return ReturnHome(r, home);
                yield break;
            }
            if (SC.Commands != null && new List<string>(SC.Commands.Names).Contains("mobspawning")) G.Exec(r, "/mobspawning on", false);

            var known = new HashSet<GameObject>();
            try { foreach (var m in SC.Entities.LiveMobs) if (m != null) known.Add(m); } catch { }
            int before = SC.Entities.LiveMobCount;

            // 2) night
            G.Exec(r, "/time set night");
            // /time fast-forwards SR time (a full day takes a few real seconds): wait for night to arrive
            yield return G.WaitUntil(() => SC.SR != null && SC.SR.IsNight, 12f);
            yield return G.Wait(0.5f);
            r.Check("Slime Rancher is at night", SC.SR.IsNight,
                "day fraction " + SC.SR.DayFraction.ToString("0.000", CultureInfo.InvariantCulture) + ", day " + SC.SR.DayNumber);
            float skyYaw = ScenicSpots.BestViewYaw(G.Eye, out float _);
            G.SetLook(-4f, skyYaw); // slightly up: the Minecraft night sky over SR
            yield return G.Wait(0.3f);
            yield return Shot(r, "08a_night");

            // 3) Minecraft spawns hostiles in the dark; the Entities spawner tries every 1.5 s, 24-44 blocks away
            float t0 = Time.realtimeSinceStartup;
            int spawned = 0;
            while (Time.realtimeSinceStartup - t0 < 45f)
            {
                spawned = CountNewMobs(known);
                if (spawned > 0) break;
                yield return G.Wait(0.5f);
                G.Heal();
                if (G.HorizontalDistance(G.Feet, dest) > 6f) G.HoldPlayerAt(dest); // stay where the spawner was evaluated
            }
            float waited = Time.realtimeSinceStartup - t0;
            if (spawned > 0) yield return G.Wait(3f); // let a few more appear / walk closer
            spawned = CountNewMobs(known);
            r.Check("natural night spawning produced mobs", spawned > 0, spawned + " new mob(s) (LiveMobCount " + before + " -> " +
                SC.Entities.LiveMobCount + ") after " + waited.ToString("0", CultureInfo.InvariantCulture) + " s in '" + SC.SR.ZoneName + "'");

            // aim at the nearest naturally spawned mob (IEntities.LiveMobs), else the open view
            GameObject nearest = null;
            float bestSq = 64f * 64f;
            try
            {
                foreach (var m in SC.Entities.LiveMobs)
                {
                    if (m == null || known.Contains(m)) continue;
                    float dSq = (m.transform.position - G.Eye).sqrMagnitude;
                    if (dSq < bestSq) { bestSq = dSq; nearest = m; }
                }
            }
            catch (Exception e) { r.Note("LiveMobs threw " + e.Message); }
            if (nearest != null)
            {
                r.Note("aiming at " + nearest.name + " " + Mathf.Sqrt(bestSq).ToString("0.0", CultureInfo.InvariantCulture) + " m away");
                G.LookAt(nearest.transform.position + Vector3.up * 0.9f);
            }
            else G.SetLook(8f, skyYaw);
            yield return G.Wait(0.3f);
            yield return Shot(r, "08b_night_spawns");

            // 4) clean up for the remaining steps
            try { SC.Entities.ClearAll(); } catch (Exception e) { r.Note("ClearAll threw: " + e.Message); }
            r.Check("SC.Entities.ClearAll removed the mobs", SC.Entities.LiveMobCount == 0, SC.Entities.LiveMobCount + " left");
            G.Exec(r, "/time set day");
            yield return G.WaitUntil(() => SC.SR != null && !SC.SR.IsNight, 12f);
            yield return G.Wait(0.5f);
            r.Check("back to day", !SC.SR.IsNight);
            G.Exec(r, "/gamemode survival");
            yield return ReturnHome(r, home);
            G.Heal();
        }

        private static int CountNewMobs(HashSet<GameObject> known)
        {
            int n = 0;
            try { foreach (var m in SC.Entities.LiveMobs) if (m != null && !known.Contains(m)) n++; }
            catch { }
            return n;
        }

        /// <summary>Back to where a step started (the ranch), holding the player until the ground is there.</summary>
        private IEnumerator ReturnHome(StepResult r, Vector3 home)
        {
            if (home == Vector3.zero) home = spawnFeet;
            if (home == Vector3.zero || G.HorizontalDistance(G.Feet, home) < 2f) yield break;
            G.Teleport(home);
            float t0 = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - t0 < 5f)
            {
                G.HoldPlayerAt(home);
                yield return null;
                if (G.FindGround(home + Vector3.up * 2f, 6f, out Vector3 _)) break;
            }
            yield return G.Wait(0.5f);
            r.Note("back at " + G.Feet.ToString("F0") + " ('" + (SC.SR != null ? SC.SR.ZoneName : "?") + "')");
        }

        // ================================================================== 09
        private IEnumerator Vacpack(StepResult r)
        {
            if (SC.Inventory == null) { r.Fail("SC.Inventory available (Hud module)"); yield break; }
            int slot = G.EnsureInHotbar(Content.Vacpack, 0);
            if (!r.Check("vacpack item in the hotbar", slot >= 0, "slot " + slot)) yield break;
            // a few SR items so the vac ammo strip shows icons (added through SR's Ammo.MaybeAddToSlot)
            try
            {
                var ps = SRSingleton<SceneContext>.Instance != null ? SRSingleton<SceneContext>.Instance.PlayerState : null;
                if (ps != null && ps.Ammo != null)
                {
                    int added = 0;
                    for (int i = 0; i < 3; i++) if (ps.Ammo.MaybeAddToSlot(Identifiable.Id.PINK_SLIME, null)) added++;
                    for (int i = 0; i < 6; i++) if (ps.Ammo.MaybeAddToSlot(Identifiable.Id.CARROT_VEGGIE, null)) added++;
                    for (int i = 0; i < 4; i++) if (ps.Ammo.MaybeAddToSlot(Identifiable.Id.PINK_PLORT, null)) added++;
                    r.Note("added " + added + " SR items to the vacpack ammo");
                }
            }
            catch (Exception e) { r.Note("could not add SR ammo: " + e.Message); }

            SC.Inventory.SelectedSlot = slot;
            yield return G.Wait(1.2f); // SR vacpack equip animation
            r.Check("vacpack selected (SC.Inventory.VacpackSelected)", SC.Inventory.VacpackSelected);
            if (SC.SR != null) r.Check("SR vacpack shown and active", SC.SR.VacpackActive);
            if (SC.Input != null) r.Check("SR weapon input not suppressed", !SC.Input.SuppressSRWeapon);
            Vector3 fl = G.FlatLook;
            G.SetLook(12f, Mathf.Atan2(fl.x, fl.z) * Mathf.Rad2Deg);
            yield return G.Wait(0.3f);
            yield return Shot(r, "09_vacpack");
        }

        // ================================================================== 10
        private IEnumerator ScenicTeleports(StepResult r)
        {
            var targets = new[]
            {
                new KeyValuePair<ZoneDirector.Zone, string>(ZoneDirector.Zone.REEF, "10a_dry_reef"),
                new KeyValuePair<ZoneDirector.Zone, string>(ZoneDirector.Zone.MOSS, "10b_moss_blanket"),
                new KeyValuePair<ZoneDirector.Zone, string>(ZoneDirector.Zone.QUARRY, "10c_indigo_quarry"),
            };
            int reached = 0;
            foreach (var t in targets)
            {
                bool ok = false;
                yield return TeleportTo(r, t.Key, t.Value, v => ok = v);
                if (ok) reached++;
            }
            r.Check("teleported to at least 2 scenic zones", reached >= 2, reached + "/" + targets.Length);
            // back home so a following manual session starts at the ranch
            if (spawnFeet != Vector3.zero) { G.Teleport(spawnFeet); yield return G.Wait(0.5f); }
        }

        private IEnumerator TeleportTo(StepResult r, ZoneDirector.Zone zone, string shot, Action<bool> done)
        {
            bool ok = false;
            yield return GoToZone(r, zone, true, (v, at) => ok = v);
            if (!ok) { done(false); yield break; }
            yield return G.Wait(2f); // regions, slimes and lights settle
            float yaw = ScenicSpots.BestViewYaw(G.Eye, out float score);
            G.SetLook(6f, yaw);
            yield return G.Wait(1f);
            r.Note(zone + ": SR zone now '" + (SC.SR != null ? SC.SR.ZoneName : "?") + "', view yaw " + yaw.ToString("0", CultureInfo.InvariantCulture) +
                   " (openness " + score.ToString("0", CultureInfo.InvariantCulture) + ")");
            yield return Shot(r, shot);
            done(true);
        }

        /// <summary>
        /// Teleports into a zone (spots from ScenicSpots: debug destinations, slime spawners, region centers), holds the
        /// player until the destination's colliders exist, prefers spots under open sky. done(ok, feet).
        /// </summary>
        private IEnumerator GoToZone(StepResult r, ZoneDirector.Zone zone, bool strict, Action<bool, Vector3> done)
        {
            List<ScenicSpots.Spot> spots;
            try { spots = ScenicSpots.Find(zone, 4); }
            catch (Exception e) { r.Note(zone + ": spot search failed: " + e.Message); spots = new List<ScenicSpots.Spot>(); }
            if (spots.Count == 0) { Report(r, strict, zone + ": a teleport spot was found", false, null); done(false, Vector3.zero); yield break; }
            r.Note(zone + ": " + spots.Count + " candidate(s), first: " + spots[0].Source + " @ " + spots[0].Position.ToString("F0"));

            bool ok = false;
            Vector3 dest = Vector3.zero;
            for (int c = 0; c < spots.Count && c < 3 && !ok; c++)
            {
                Vector3 target = spots[c].Position + Vector3.up * 0.5f;
                G.Teleport(target);
                // hold the player while SR wakes the destination regions (terrain colliders appear a few frames later)
                bool ground = false;
                Vector3 g = target;
                float t0 = Time.realtimeSinceStartup;
                while (Time.realtimeSinceStartup - t0 < 6f)
                {
                    G.HoldPlayerAt(target);
                    yield return null;
                    if (G.FindGround(target + Vector3.up * 3f, 15f, out g)) { ground = true; break; }
                }
                if (!ground) { r.Note(zone + ": no ground at candidate " + c + " (" + spots[c].Source + ")"); continue; }
                dest = g + Vector3.up * 0.05f;
                G.HoldPlayerAt(dest);
                yield return G.Wait(0.5f);
                float sky = G.FreeDistance(G.Eye, Vector3.up, 40f);
                if (sky < 20f && c < Math.Min(spots.Count, 3) - 1)
                {
                    r.Note(zone + ": candidate " + c + " is covered (sky " + sky.ToString("0", CultureInfo.InvariantCulture) + " m), trying the next one");
                    continue;
                }
                ok = true;
            }
            float dist = Vector3.Distance(G.Feet, dest);
            string zoneName = SC.SR != null ? SC.SR.ZoneName : "?";
            if (!Report(r, strict, zone + ": player teleported (SC.SR.TeleportPlayer)", ok && dist < 6f,
                "at " + dest.ToString("F0") + ", distance " + dist.ToString("0.0", CultureInfo.InvariantCulture) + ", SR zone '" + zoneName + "'"))
            {
                done(false, dest);
                yield break;
            }
            done(true, dest);
        }

        /// <summary>A check when strict, otherwise only a note (an attempt that is allowed to fail over to another one).</summary>
        private static bool Report(StepResult r, bool strict, string name, bool ok, string detail)
        {
            if (strict) return r.Check(name, ok, detail);
            r.Note((ok ? "ok: " : "not ok: ") + name + (detail != null ? " (" + detail + ")" : ""));
            return ok;
        }
    }
}
