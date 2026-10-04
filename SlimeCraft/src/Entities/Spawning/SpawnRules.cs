using System;
using System.Collections.Generic;
using MonomiPark.SlimeRancher.Regions;
using UnityEngine;

namespace SlimeCraft.Entities
{
    /// <summary>Why a spawn / teleport spot was refused (counted by the natural spawner's debug summary).</summary>
    internal enum SpawnReject
    {
        None, NoGround, Steep, Height, OnSRActor, Water, KillVolume, Occupied, InsideGeometry, InsideBlocks, UnderPlayerBlocks,
        NearLight, RanchArea, RegionUnloaded, Visible, SpawnFailed, Count
    }

    [Flags]
    internal enum StandChecks
    {
        None = 0,
        /// <summary>Refuse spots on The Ranch (hostiles while Entities.SpawnOnRanch is off).</summary>
        Ranch = 1,
        /// <summary>Refuse spots lit by a Minecraft light block (Minecraft: hostiles need block light 0).</summary>
        Light = 2,
        /// <summary>Refuse spots with player-placed Minecraft blocks overhead (inside the player's houses).</summary>
        UnderBlocks = 4,
        /// <summary>Use the narrow World mask for the free-space test (ignores slimes/mobs standing there).</summary>
        WorldOnly = 8,
    }

    /// <summary>
    /// "Can a mob of this size stand here?" tests shared by natural spawning and enderman teleports:
    /// never inside Minecraft blocks, never under a roof of player blocks (spawning ON blocks is fine, like Minecraft),
    /// never in water / the slime sea, never overlapping solid colliders, never inside Slime Rancher geometry
    /// (back-face ray test), optionally not on The Ranch and not near Minecraft light blocks.
    /// </summary>
    internal static class SpawnRules
    {
        private static readonly RaycastHit[] upHits = new RaycastHit[16];

        public static SpawnReject CheckStand(Vector3 feet, float width, float height, StandChecks checks)
        {
            if (OverlapsBlocks(feet, width, height)) return SpawnReject.InsideBlocks;
            // a proxied SR region has no colliders: what the ray found would be geometry below the real (unloaded) ground
            if (!SRZones.RegionLoadedAt(feet + Vector3.up * 0.5f)) return SpawnReject.RegionUnloaded;
            if ((checks & StandChecks.UnderBlocks) != 0 && PlayerBlocksAbove(feet, height)) return SpawnReject.UnderPlayerBlocks;
            if ((checks & StandChecks.Ranch) != 0 && SRZones.IsRanchAt(feet + Vector3.up * 0.5f)) return SpawnReject.RanchArea;
            var liquid = Liquid(feet, height);
            if (liquid != SpawnReject.None) return liquid;
            float r = Mathf.Max(0.1f, width * 0.5f);
            int mask = (checks & StandChecks.WorldOnly) != 0 ? ELayers.World : ELayers.Ground;
            if ((checks & StandChecks.WorldOnly) != 0) r *= 0.9f;
            if (Physics.CheckCapsule(feet + Vector3.up * (r + 0.1f), feet + Vector3.up * Mathf.Max(r + 0.11f, height - r), r, mask, QueryTriggerInteraction.Ignore))
                return SpawnReject.Occupied;
            if (InsideGeometry(feet)) return SpawnReject.InsideGeometry;
            if ((checks & StandChecks.Light) != 0 && NearLightBlock(feet)) return SpawnReject.NearLight;
            return SpawnReject.None;
        }

        // ------------------------------------------------------------------ Minecraft blocks

        /// <summary>True if any Minecraft block occupies a cell overlapped by the mob's box.</summary>
        public static bool OverlapsBlocks(Vector3 feet, float width, float height)
        {
            var bw = SC.Blocks;
            if (bw == null || bw.Count == 0) return false;
            float r = width * 0.5f;
            int x0 = Mathf.FloorToInt(feet.x - r + 0.01f), x1 = Mathf.FloorToInt(feet.x + r - 0.01f);
            int z0 = Mathf.FloorToInt(feet.z - r + 0.01f), z1 = Mathf.FloorToInt(feet.z + r - 0.01f);
            int y0 = Mathf.FloorToInt(feet.y + 0.01f), y1 = Mathf.FloorToInt(feet.y + Mathf.Max(0.1f, height) - 0.01f);
            for (int x = x0; x <= x1; x++)
                for (int y = y0; y <= y1; y++)
                    for (int z = z0; z <= z1; z++)
                        if (bw.GetBlock(new Vector3Int(x, y, z)) != null) return true;
            return false;
        }

        /// <summary>A player-placed block within 24 blocks straight above the mob's head: the spot is inside / under a structure.</summary>
        public static bool PlayerBlocksAbove(Vector3 feet, float height)
        {
            var bw = SC.Blocks;
            if (bw == null || bw.Count == 0) return false;
            int x = Mathf.FloorToInt(feet.x), z = Mathf.FloorToInt(feet.z);
            int y0 = Mathf.FloorToInt(feet.y + 0.01f);
            for (int y = y0; y <= y0 + 24 + Mathf.CeilToInt(height); y++)
                if (bw.GetBlock(new Vector3Int(x, y, z)) != null) return true;
            return false;
        }

        private static Vector3Int[] lightOffsets;
        private static int[] lightDist;

        /// <summary>
        /// Minecraft hostiles only spawn at block light 0: refuse spots within Manhattan distance &lt; Light of a light-emitting
        /// block (glowstone, sea lantern, jack o'lantern, shroomlight, lamp). Light does not go through walls here (no occlusion).
        /// </summary>
        public static bool NearLightBlock(Vector3 feet)
        {
            var bw = SC.Blocks;
            if (bw == null || bw.Count == 0) return false;
            if (lightOffsets == null) BuildLightOffsets(14);
            var c = Vector3Int.FloorToInt(feet + Vector3.up * 0.5f);
            for (int i = 0; i < lightOffsets.Length; i++)
            {
                var id = bw.GetBlock(c + lightOffsets[i]);
                if (id == null) continue;
                var def = Content.Block(id);
                if (def != null && def.Light - lightDist[i] > 0) return true;
            }
            return false;
        }

        private static void BuildLightOffsets(int radius)
        {
            var offs = new List<Vector3Int>();
            var dist = new List<int>();
            for (int d = 0; d <= radius; d++) // nearest first
                for (int x = -d; x <= d; x++)
                    for (int y = -d; y <= d; y++)
                    {
                        int rest = d - Mathf.Abs(x) - Mathf.Abs(y);
                        if (rest < 0) continue;
                        offs.Add(new Vector3Int(x, y, rest)); dist.Add(d);
                        if (rest != 0) { offs.Add(new Vector3Int(x, y, -rest)); dist.Add(d); }
                    }
            lightOffsets = offs.ToArray();
            lightDist = dist.ToArray();
        }

        // ------------------------------------------------------------------ Slime Rancher world

        /// <summary>Water (LiquidSource / Water layer) or the slime sea (KillOnTrigger) around the mob's legs or chest.</summary>
        public static SpawnReject Liquid(Vector3 feet, float height)
        {
            var r = LiquidAt(feet + Vector3.up * 0.4f);
            if (r != SpawnReject.None) return r;
            return LiquidAt(feet + Vector3.up * Mathf.Clamp(height * 0.8f, 0.5f, 1.6f));
        }

        private static SpawnReject LiquidAt(Vector3 p)
        {
            int n = EPhys.Overlap(p, 0.3f, ~0, QueryTriggerInteraction.Collide, out var cols);
            var result = SpawnReject.None;
            for (int i = 0; i < n; i++)
            {
                var c = cols[i];
                if (c == null) continue;
                if (c.gameObject.layer == ELayers.SRWater) result = SpawnReject.Water;
                if (!c.isTrigger) continue;
                if (c.GetComponent<KillOnTrigger>() != null) return SpawnReject.KillVolume;
                if (c.GetComponent<LiquidSource>() != null) result = SpawnReject.Water;
            }
            return result;
        }

        /// <summary>
        /// Inside closed Slime Rancher geometry (under the terrain surface, inside a rock mesh): looking straight up, the
        /// nearest surface is a BACK face (a floor seen from below). Detected by comparing an upward ray that hits only front
        /// faces with one that also hits back faces. Primitive / convex colliders are covered by the capsule overlap test.
        /// </summary>
        public static bool InsideGeometry(Vector3 feet)
        {
            var origin = feet + Vector3.up * 0.25f;
            bool front = NearestUp(origin, out float dFront);
            bool prev = Physics.queriesHitBackfaces;
            bool back; float dBack;
            try
            {
                Physics.queriesHitBackfaces = true;
                back = NearestUp(origin, out dBack);
            }
            finally { Physics.queriesHitBackfaces = prev; }
            return back && (!front || dBack < dFront - 0.02f);
        }

        private static bool NearestUp(Vector3 origin, out float dist)
        {
            dist = float.MaxValue;
            int n = Physics.RaycastNonAlloc(origin, Vector3.up, upHits, 48f, ELayers.World, QueryTriggerInteraction.Ignore);
            bool found = false;
            var playerT = SC.SR != null && SC.SR.Player != null ? SC.SR.Player.transform : null;
            for (int i = 0; i < n; i++)
            {
                var c = upHits[i].collider;
                if (c == null || upHits[i].distance >= dist) continue;
                if (playerT != null && c.transform.IsChildOf(playerT)) continue;
                if (c.GetComponentInParent<McEntity>() != null) continue;
                dist = upHits[i].distance; found = true;
            }
            return found;
        }
    }

    /// <summary>Slime Rancher zone queries (through SceneContext, PlayerZoneTracker, RegionRegistry and ZoneDirector).</summary>
    internal static class SRZones
    {
        private static List<Region> regions = new List<Region>();
        private static bool regionLookupBroken;

        /// <summary>The player is on The Ranch (SR's own zone tracker; falls back to the zone name).</summary>
        public static bool PlayerOnRanch()
        {
            try
            {
                var ctx = SRSingleton<SceneContext>.Instance;
                var tracker = ctx != null ? ctx.PlayerZoneTracker : null;
                if (tracker != null) return tracker.GetCurrentZone() == ZoneDirector.Zone.RANCH;
            }
            catch (Exception e) { ELog.WarnOnce("zonetracker", "PlayerZoneTracker query failed, using the zone name: " + e.Message); }
            string zone = SC.SR != null ? SC.SR.ZoneName ?? "" : "";
            return zone.IndexOf("The Ranch", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>The position lies inside a region (cell) of The Ranch zone.</summary>
        public static bool IsRanchAt(Vector3 pos)
        {
            if (regionLookupBroken) return false;
            try
            {
                var ctx = SRSingleton<SceneContext>.Instance;
                var rr = ctx != null ? ctx.RegionRegistry : null;
                if (rr == null) return false;
                rr.GetContaining(ref regions, pos);
                for (int i = 0; i < regions.Count; i++)
                {
                    var reg = regions[i];
                    if (reg == null) continue;
                    var z = reg.GetZoneId(); // the zone the region (cell) belongs to
                    if (z == ZoneDirector.Zone.NONE)
                    {
                        var zd = reg.GetComponentInParent<ZoneDirector>();
                        if (zd != null) z = zd.zone;
                    }
                    if (z == ZoneDirector.Zone.RANCH) return true;
                }
            }
            catch (Exception e)
            {
                regionLookupBroken = true;
                ELog.Warn("Ranch region lookup failed, disabled (ranch protection then only uses the player's zone): " + e.Message);
            }
            return false;
        }

        /// <summary>
        /// SR streams the world in regions: regions away from the player are "proxied" (their root,
        /// with all terrain colliders, is deactivated and only a visual proxy mesh remains). True when the position lies in at
        /// least one loaded region of the current region set, or in none at all (unknown = assume loaded).
        /// </summary>
        public static bool RegionLoadedAt(Vector3 pos)
        {
            if (regionLookupBroken) return true;
            try
            {
                var ctx = SRSingleton<SceneContext>.Instance;
                var rr = ctx != null ? ctx.RegionRegistry : null;
                if (rr == null) return true;
                rr.GetContaining(ref regions, pos);
                if (regions.Count == 0) return true;
                for (int i = 0; i < regions.Count; i++)
                {
                    var reg = regions[i];
                    if (reg == null || reg.root == null || reg.root.activeInHierarchy) return true;
                }
                return false;
            }
            catch (Exception e)
            {
                regionLookupBroken = true;
                ELog.Warn("Region lookup failed, disabled: " + e.Message);
                return true;
            }
        }
    }
}
