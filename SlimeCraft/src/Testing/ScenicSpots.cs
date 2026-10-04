using System.Collections.Generic;
using UnityEngine;

namespace SlimeCraft.Testing
{
    /// <summary>
    /// Finds places to teleport to inside a Slime Rancher zone at runtime (SR has no hard-coded zone coordinates):
    ///  1. DebugTeleportDestination objects left in the world scene (each carries a name meant for SR's
    ///     developer menu) whose name or parent ZoneDirector matches the zone;
    ///  2. the zone's DirectedSlimeSpawner objects (children of the zone's CellDirector/Region hierarchy under its
    ///     ZoneDirector) – they sit on walkable ground where slimes live, the ones nearest the zone's centroid first;
    ///  3. the centers of the zone's Region bounds.
    /// </summary>
    internal static class ScenicSpots
    {
        public struct Spot { public Vector3 Position; public string Source; }

        public static string Keyword(ZoneDirector.Zone zone)
        {
            switch (zone)
            {
                case ZoneDirector.Zone.REEF: return "reef";
                case ZoneDirector.Zone.MOSS: return "moss";
                case ZoneDirector.Zone.QUARRY: return "quarry";
                case ZoneDirector.Zone.RUINS: return "ruin";
                case ZoneDirector.Zone.DESERT: return "desert";
                case ZoneDirector.Zone.RANCH: return "ranch";
                default: return zone.ToString().ToLowerInvariant();
            }
        }

        /// <summary>ZoneDirector above a transform (walks parents manually so inactive objects work too).</summary>
        public static ZoneDirector ZoneOf(Transform t)
        {
            for (; t != null; t = t.parent)
            {
                var zd = t.GetComponent<ZoneDirector>();
                if (zd != null) return zd;
            }
            return null;
        }

        /// <summary>All DebugTeleportDestination names in the loaded world (for the report).</summary>
        public static List<string> DebugDestinationNames()
        {
            var names = new List<string>();
            foreach (var d in Resources.FindObjectsOfTypeAll<DebugTeleportDestination>())
            {
                if (d == null || !d.gameObject.scene.IsValid()) continue;
                var zd = ZoneOf(d.transform);
                names.Add((d.name ?? d.gameObject.name) + (zd != null ? " [" + zd.zone + "]" : "") + " @ " + d.transform.position.ToString("F0"));
            }
            return names;
        }

        public static List<Spot> Find(ZoneDirector.Zone zone, int max)
        {
            var result = new List<Spot>();
            string kw = Keyword(zone);

            // 1) curated debug teleport points
            foreach (var d in Resources.FindObjectsOfTypeAll<DebugTeleportDestination>())
            {
                if (d == null || !d.gameObject.scene.IsValid()) continue;
                var zd = ZoneOf(d.transform);
                string n = (d.name ?? d.gameObject.name ?? "").ToLowerInvariant();
                if ((zd != null && zd.zone == zone) || n.Contains(kw))
                    result.Add(new Spot { Position = d.transform.position, Source = "DebugTeleportDestination '" + d.name + "'" });
                if (result.Count >= max) return result;
            }

            // 2) slime spawners of the zone
            ZoneDirector zoneDir = null;
            if (ZoneDirector.zones != null) ZoneDirector.zones.TryGetValue(zone, out zoneDir);
            if (zoneDir == null)
            {
                foreach (var z in Resources.FindObjectsOfTypeAll<ZoneDirector>())
                    if (z != null && z.gameObject.scene.IsValid() && z.zone == zone) { zoneDir = z; break; }
            }
            if (zoneDir != null)
            {
                var spawners = zoneDir.GetComponentsInChildren<DirectedSlimeSpawner>(true);
                if (spawners.Length > 0)
                {
                    Vector3 c = Vector3.zero;
                    foreach (var s in spawners) c += s.transform.position;
                    c /= spawners.Length;
                    var sorted = new List<DirectedSlimeSpawner>(spawners);
                    sorted.Sort((a, b) => (a.transform.position - c).sqrMagnitude.CompareTo((b.transform.position - c).sqrMagnitude));
                    foreach (var s in sorted)
                    {
                        result.Add(new Spot { Position = s.transform.position, Source = "DirectedSlimeSpawner '" + s.gameObject.name + "'" });
                        if (result.Count >= max) return result;
                    }
                }

                // 3) region bounds centers
                foreach (var region in zoneDir.GetComponentsInChildren<MonomiPark.SlimeRancher.Regions.Region>(true))
                {
                    result.Add(new Spot { Position = region.bounds.center, Source = "Region '" + region.gameObject.name + "' center" });
                    if (result.Count >= max) return result;
                }
            }
            return result;
        }

        /// <summary>Yaw (degrees) whose horizontal view from the eye is the most open – for a scenic screenshot.</summary>
        public static float BestViewYaw(Vector3 eye, out float freeDistance)
        {
            float bestYaw = 0f;
            freeDistance = -1f;
            for (int i = 0; i < 12; i++)
            {
                float yaw = i * 30f;
                Vector3 dir = Quaternion.Euler(0f, yaw, 0f) * Vector3.forward;
                // mostly horizontal, plus a slightly downward ray so we prefer views over terrain (not just sky)
                float d = G.FreeDistance(eye, dir, 150f);
                float down = G.FreeDistance(eye, Quaternion.Euler(12f, yaw, 0f) * Vector3.forward, 150f);
                float score = d + 0.25f * Mathf.Min(down, 60f);
                if (score > freeDistance) { freeDistance = score; bestYaw = yaw; }
            }
            return bestYaw;
        }
    }
}
