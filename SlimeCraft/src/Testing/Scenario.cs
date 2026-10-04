using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace SlimeCraft.Testing
{
    /// <summary>
    /// The scripted "Minecraft inside Slime Rancher" scenario (steps 01-10). Steps share state (where the house and
    /// TNT tower were built, spawned slimes) through fields; every step also works on its own when an earlier step
    /// failed or was not selected.
    /// </summary>
    internal sealed partial class Scenario
    {
        private readonly TestRunner runner;

        // shared state
        private Vector3Int fwd = new Vector3Int(0, 0, 1), right = new Vector3Int(1, 0, 0);
        private Vector3Int houseOrigin;
        private int baseY;
        private bool built;
        private readonly List<Vector3Int> houseBlocks = new List<Vector3Int>();
        private readonly List<Vector3Int> towerTnt = new List<Vector3Int>();
        private Vector3 towerFeet;
        private readonly List<GameObject> slimes = new List<GameObject>();
        private Vector3 spawnFeet;

        private const int HouseW = 7, HouseD = 5;
        private static readonly string[] Mobs =
        {
            MobIds.Creeper, MobIds.Zombie, MobIds.Pig, MobIds.Cow, MobIds.Sheep, MobIds.Chicken, MobIds.IronGolem, MobIds.Slime
        };

        public Scenario(TestRunner runner) { this.runner = runner; }

        private IEnumerator Shot(StepResult r, string name) => runner.Shot(r, name);

        public List<StepDef> All()
        {
            return new List<StepDef>
            {
                new StepDef("01", "spawn_view", 45f, SpawnView),
                new StepDef("02", "diamond_pickaxe", 45f, DiamondPickaxe),
                new StepDef("03", "build_house", 60f, BuildHouse),
                new StepDef("04", "spawn_slimes_and_mobs", 45f, SpawnSlimesAndMobs),
                new StepDef("05", "tnt_explosion", 60f, TntExplosion),
                new StepDef("06", "screens", 60f, Screens),
                new StepDef("07", "debug_screen", 30f, DebugScreen),
                new StepDef("08", "night_spawning", 120f, NightSpawning),
                new StepDef("09", "vacpack", 30f, Vacpack),
                new StepDef("10", "scenic_teleports", 180f, ScenicTeleports),
                // interaction steps: drive the REAL Blocks/Entities/Hud code through IInputGate.SetTestInput
                new StepDef("11", "empty_hand", 20f, EmptyHand),
                new StepDef("12", "mine_block", 30f, MineBlock),
                new StepDef("13", "place_blocks", 30f, PlaceBlocks),
                new StepDef("14", "harvest_sr_terrain", 50f, HarvestTerrain),
                new StepDef("15", "eat_food", 25f, EatFood),
                new StepDef("16", "sword_vs_pig", 30f, SwordVsPig),
                new StepDef("17", "slime_eats_mc_food", 35f, SlimeEatsMcFood),
                new StepDef("18", "vacpack_mc_items", 30f, VacpackMcItems),
                new StepDef("19", "drop_key", 15f, DropKey),
                new StepDef("20", "persistence_roundtrip", 240f, PersistenceRoundTrip),
            };
        }

        /// <summary>Testing.Scenario: full | smoke | interact | persist | boot | "01,05" | "tnt_explosion,screens".</summary>
        public List<StepDef> Select(string scenario)
        {
            var all = All();
            string s = (scenario ?? "full").Trim().ToLowerInvariant();
            if (s == "" || s == "full" || s == "all") return all;
            if (s == "boot" || s == "none") return new List<StepDef>();
            var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (s == "smoke") foreach (var id in new[] { "01", "02", "03", "06", "09" }) wanted.Add(id);
            else if (s == "interact") foreach (var id in new[] { "01", "11", "12", "13", "14", "15", "16", "17", "18", "19" }) wanted.Add(id);
            else if (s == "persist") foreach (var id in new[] { "01", "03", "13", "20" }) wanted.Add(id);
            else foreach (var part in s.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)) wanted.Add(part.Trim());
            return all.FindAll(d => wanted.Contains(d.Id) || wanted.Contains(d.Name) || wanted.Contains(d.Id.TrimStart('0')));
        }

        // ================================================================== 01
        private IEnumerator SpawnView(StepResult r)
        {
            r.Check("in game", G.InGame);
            if (SC.Hud != null)
            {
                SC.Hud.McHudEnabled = true;
                r.Check("Minecraft HUD enabled", SC.Hud.McHudEnabled);
            }
            else r.Fail("SC.Hud available (Hud module)");
            r.Check("Minecraft assets ready", SC.Assets != null && SC.Assets.Ready, SC.Assets != null ? SC.Assets.McVersion : "SC.Assets missing");
            r.Check("Minecraft audio ready", SC.Audio != null && SC.Audio.Ready);
            r.Check("player inventory available", SC.Inventory != null && SC.Inventory.Inv != null);
            r.Note("hotbar: " + G.HotbarText());
            if (SC.Inventory != null) r.Note("game mode: " + (SC.Inventory.Creative ? "creative" : "survival") + ", selected slot " + SC.Inventory.SelectedSlot);
            spawnFeet = G.Feet;
            r.Note("spawn feet " + spawnFeet.ToString("F1") + ", look " + G.Look.ToString("F2") + ", zone '" + (SC.SR != null ? SC.SR.ZoneName : "?") + "'");
            // level, slightly downward view like a fresh Minecraft spawn
            Vector3 fl = G.FlatLook;
            G.SetLook(8f, Mathf.Atan2(fl.x, fl.z) * Mathf.Rad2Deg);
            yield return G.Wait(2f);
            yield return Shot(r, "01_spawn_view");
        }

        // ================================================================== 02
        private IEnumerator DiamondPickaxe(StepResult r)
        {
            const string id = "minecraft:diamond_pickaxe";
            r.Check("diamond_pickaxe registered in Content", Content.Item(id) != null);
            if (SC.Inventory == null) { r.Fail("SC.Inventory available (Hud module)"); yield break; }
            int before = SC.Inventory.Inv.CountOf(id);
            G.Exec(r, "/give " + id + " 1");
            int after = SC.Inventory.Inv.CountOf(id);
            r.Check("/give added the pickaxe", after > before, before + " -> " + after);
            int slot = G.EnsureInHotbar(id, 1);
            r.Check("pickaxe is in the hotbar", slot >= 0, "slot " + slot);
            if (slot < 0) yield break;
            SC.Inventory.SelectedSlot = slot;
            yield return G.Wait(0.8f); // Minecraft equip animation
            r.Check("pickaxe selected", SC.Inventory.Selected != null && SC.Inventory.Selected.Id == id,
                SC.Inventory.Selected != null ? SC.Inventory.Selected.ToString() : "null");
            r.Check("vacpack not selected", !SC.Inventory.VacpackSelected);
            if (SC.SR != null) r.Check("SR vacpack hidden while a Minecraft item is held", !SC.SR.VacpackActive);
            if (SC.Input != null) r.Check("SR weapon input suppressed", SC.Input.SuppressSRWeapon);
            yield return Shot(r, "02_diamond_pickaxe");
            if (SC.FirstPerson != null)
            {
                SC.FirstPerson.Swing();
                yield return G.Wait(0.12f); // mid-swing (Minecraft swing = 6 ticks = 0.3 s)
                yield return Shot(r, "02b_pickaxe_swing");
            }
            else r.Fail("SC.FirstPerson available (FirstPerson module)");
        }

        // ================================================================== 03
        private IEnumerator BuildHouse(StepResult r)
        {
            if (SC.Blocks == null) { r.Fail("SC.Blocks available (Blocks module)"); yield break; }
            Vector3 feet = G.Feet;
            Vector3 fl = G.FlatLook;
            fwd = Mathf.Abs(fl.x) >= Mathf.Abs(fl.z) ? new Vector3Int(fl.x >= 0 ? 1 : -1, 0, 0) : new Vector3Int(0, 0, fl.z >= 0 ? 1 : -1);
            right = new Vector3Int(fwd.z, 0, -fwd.x); // Vector3.up x forward
            Vector3Int pb = G.ToBlock(feet);
            houseOrigin = pb + fwd * 6 - right * (HouseW / 2); // front-left corner, front (door) faces the player
            houseOrigin.y = 0;

            // terrain height per footprint column (SR ground only)
            var ground = new int[HouseW, HouseD];
            var heights = new List<int>();
            for (int i = 0; i < HouseW; i++)
                for (int j = 0; j < HouseD; j++)
                {
                    Vector3Int c = houseOrigin + right * i + fwd * j;
                    var top = new Vector3(c.x + 0.5f, feet.y + 10f, c.z + 0.5f);
                    ground[i, j] = G.FindGround(top, 30f, out Vector3 g, true) ? Mathf.FloorToInt(g.y + 0.05f) : pb.y - 1;
                    heights.Add(ground[i, j]);
                }
            heights.Sort();
            baseY = Mathf.Clamp(heights[heights.Count / 2], pb.y - 3, pb.y + 2);
            r.Note("house origin " + houseOrigin + " facing " + fwd + ", floor y " + baseY + " (terrain " + heights[0] + ".." + heights[heights.Count - 1] + ")");

            var plan = new List<KeyValuePair<Vector3Int, string>>();
            houseBlocks.Clear();
            towerTnt.Clear();
            // foundation: stone bricks from the terrain up to the floor
            for (int i = 0; i < HouseW; i++)
                for (int j = 0; j < HouseD; j++)
                    for (int y = Mathf.Max(ground[i, j], baseY - 4); y <= baseY; y++)
                        plan.Add(Pair(At(i, j, y), "minecraft:stone_bricks"));
            // walls (3 high) with log corners, glass windows, a door gap and glowstone above the door
            for (int k = 1; k <= 3; k++)
                for (int i = 0; i < HouseW; i++)
                    for (int j = 0; j < HouseD; j++)
                    {
                        bool edgeI = i == 0 || i == HouseW - 1, edgeJ = j == 0 || j == HouseD - 1;
                        if (!edgeI && !edgeJ) continue;
                        if (j == 0 && i == HouseW / 2 && k <= 2) continue; // door
                        string id = "minecraft:oak_planks";
                        if (edgeI && edgeJ) id = "minecraft:oak_log";
                        else if (k == 2 && ((j == 0 && (i == 1 || i == HouseW - 2)) || (j == HouseD - 1 && i >= 2 && i <= HouseW - 3) || (edgeI && j == HouseD / 2)))
                            id = "minecraft:glass";
                        else if (k == 3 && j == 0 && i == HouseW / 2) id = "minecraft:glowstone";
                        plan.Add(Pair(At(i, j, baseY + k), id));
                    }
            // roof: stone brick rim, planks, glowstone skylight in the middle
            for (int i = 0; i < HouseW; i++)
                for (int j = 0; j < HouseD; j++)
                {
                    bool rim = i == 0 || i == HouseW - 1 || j == 0 || j == HouseD - 1;
                    string id = rim ? "minecraft:stone_bricks" : (i == HouseW / 2 && j == HouseD / 2 ? "minecraft:glowstone" : "minecraft:oak_planks");
                    plan.Add(Pair(At(i, j, baseY + 4), id));
                }
            int houseCount = plan.Count;

            // TNT tower two blocks to the right of the house
            Vector3Int tcol = houseOrigin + right * (HouseW + 2) + fwd * (HouseD / 2);
            int tg = G.FindGround(new Vector3(tcol.x + 0.5f, feet.y + 10f, tcol.z + 0.5f), 30f, out Vector3 tgp, true) ? Mathf.FloorToInt(tgp.y + 0.05f) : baseY;
            tg = Mathf.Clamp(tg, baseY - 4, baseY + 2);
            plan.Add(Pair(new Vector3Int(tcol.x, tg, tcol.z), "minecraft:stone_bricks"));
            for (int k = 1; k <= 5; k++) plan.Add(Pair(new Vector3Int(tcol.x, tg + k, tcol.z), "minecraft:tnt"));
            towerFeet = new Vector3(tcol.x + 0.5f, tg + 1f, tcol.z + 0.5f);

            // place (a few blocks per frame like a fast builder; some with the Minecraft place sound)
            int placed = 0, refused = 0, n = 0;
            var refusedList = new List<string>();
            foreach (var kv in plan)
            {
                bool ok = false;
                try
                {
                    var def = Content.Block(kv.Value);
                    // logs stand upright; every other block of the house is not orientable
                    var facing = def != null && def.Rotation == BlockRotation.Axis ? BlockFacing.Up : BlockFacing.North;
                    ok = SC.Blocks.SetBlock(kv.Key, kv.Value, facing, n % 12 == 0);
                }
                catch (Exception e) { refusedList.Add(kv.Value + "@" + kv.Key + ": " + e.Message); }
                if (ok)
                {
                    placed++;
                    if (n < houseCount) houseBlocks.Add(kv.Key);
                    else if (kv.Value == "minecraft:tnt") towerTnt.Add(kv.Key);
                }
                else { refused++; if (refusedList.Count < 8) refusedList.Add(kv.Value + "@" + kv.Key); }
                n++;
                if (n % 16 == 0) yield return null;
            }
            built = placed > 0;
            r.Check("blocks placed with SC.Blocks.SetBlock", placed >= plan.Count * 0.9f, placed + "/" + plan.Count +
                (refusedList.Count > 0 ? "; refused: " + string.Join(", ", refusedList.ToArray()) : ""));
            int mismatch = 0;
            foreach (var kv in plan)
            {
                string got = SC.Blocks.GetBlock(kv.Key);
                if (houseBlocks.Contains(kv.Key) || towerTnt.Contains(kv.Key))
                    if (got != Content.Norm(kv.Value)) mismatch++;
            }
            r.Check("SC.Blocks.GetBlock returns what was placed", mismatch == 0, mismatch + " mismatches");
            r.Check("TNT tower has 5 TNT", towerTnt.Count == 5, towerTnt.Count.ToString());
            r.Note("SC.Blocks.Count = " + SC.Blocks.Count);

            // frame house + tower
            Vector3 mid = new Vector3(houseOrigin.x + 0.5f, baseY + 2.5f, houseOrigin.z + 0.5f) + (Vector3)right * (HouseW * 0.5f + 1.5f) + (Vector3)fwd * (HouseD * 0.5f);
            G.LookAt(mid);
            yield return G.Wait(1.2f); // chunk meshing + light
            yield return Shot(r, "03_house_and_tnt_tower");
        }

        private Vector3Int At(int i, int j, int y)
        {
            var p = houseOrigin + right * i + fwd * j;
            p.y = y;
            return p;
        }

        private static KeyValuePair<Vector3Int, string> Pair(Vector3Int p, string id) => new KeyValuePair<Vector3Int, string>(p, id);

        /// <summary>
        /// Moves the player (feet) near target through SC.SR.TeleportPlayer, for camera framing. The destination is a
        /// walkable spot with a free player capsule (G.FindStandSpot: the old version dropped the player from 6 m above
        /// onto whatever was there – ranch roofs, fences, slopes – and the character controller slid/was pushed ~3 m).
        /// Passes when the player ends within <paramref name="tolerance"/> m (horizontal) of the requested target.
        /// </summary>
        private IEnumerator MovePlayer(StepResult r, Vector3 target, string why, float tolerance = 3.5f)
        {
            Vector3 dest = target;
            bool spot = G.FindStandSpot(target, out Vector3 s, 2.5f);
            if (spot) dest = s;
            else if (G.FindGround(target + Vector3.up * 2.5f, 12f, out Vector3 g, true)) dest = g + Vector3.up * 0.05f;
            yield return G.MoveAndSettle(dest, 0.35f);
            float toDest = G.HorizontalDistance(G.Feet, dest);
            if (toDest > 1f)
            {
                // the controller was pushed away: hold it once more (regions/colliders may have streamed in meanwhile)
                yield return G.MoveAndSettle(dest, 0.35f);
                toDest = G.HorizontalDistance(G.Feet, dest);
            }
            float toTarget = G.HorizontalDistance(G.Feet, target);
            r.Check("player moved (" + why + ")", toTarget < tolerance,
                "horizontal distance to target " + G.F2(toTarget) + " m (to chosen spot " + G.F2(toDest) + " m, spot " +
                (spot ? "free capsule" : "ground only") + " at " + dest.ToString("F1") + ")");
        }

        // ================================================================== 04
        private IEnumerator SpawnSlimesAndMobs(StepResult r)
        {
            // Creative keeps the hostile mobs from mauling the player during the TNT shots (Minecraft behaviour).
            G.Exec(r, "/gamemode creative");
            Vector3 feet = G.Feet;
            Vector3 fl = G.FlatLook;
            if (!built)
            {
                // no house: improvise a TNT spot 9 blocks ahead
                towerFeet = feet + fl * 9f;
                if (G.FindGround(towerFeet + Vector3.up * 6f, 20f, out Vector3 tg, true)) towerFeet = tg;
                r.Note("no house/tower from step 03 – using " + towerFeet.ToString("F1"));
            }
            // step back so slimes, mobs, house and tower fit in the view and the player is outside the blast radius
            yield return MovePlayer(r, feet - (Vector3)fwd * 5f, "back 5 blocks");

            slimes.Clear();
            if (SC.SR == null) r.Fail("SC.SR available for SpawnSRActor");
            else
            {
                for (int n = 0; n < 6; n++)
                {
                    float a = n * 60f * Mathf.Deg2Rad;
                    Vector3 p = towerFeet + new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * 2.6f;
                    if (G.FindGround(p + Vector3.up * 8f, 20f, out Vector3 g)) p = g;
                    GameObject go = null;
                    try { go = SC.SR.SpawnSRActor("PINK_SLIME", p + Vector3.up * 0.6f, Quaternion.Euler(0f, n * 60f, 0f)); }
                    catch (Exception e) { r.Note("SpawnSRActor threw: " + e.Message); }
                    if (go != null) slimes.Add(go);
                }
                r.Check("6 pink slimes spawned (SC.SR.SpawnSRActor)", slimes.Count == 6, slimes.Count + "/6");
                if (slimes.Count > 0)
                {
                    string aid = SC.SR.GetSRActorId(slimes[0]);
                    r.Check("spawned actor is an SR PINK_SLIME", aid == "PINK_SLIME", aid ?? "null");
                }
            }

            if (SC.Entities == null) r.Fail("SC.Entities available (Entities module)");
            else
            {
                var known = new HashSet<string>(SC.Entities.MobIds);
                int before = SC.Entities.LiveMobCount, ok = 0;
                var failed = new List<string>();
                Vector3 rowStart = new Vector3(houseOrigin.x + 0.5f, 0f, houseOrigin.z + 0.5f) - (Vector3)fwd * 3f - (Vector3)right * 1.5f;
                if (!built) rowStart = towerFeet - (Vector3)fwd * 3f - (Vector3)right * 6f;
                for (int k = 0; k < Mobs.Length; k++)
                {
                    Vector3 p = rowStart + (Vector3)right * (k * 1.6f);
                    p.y = feet.y + 6f;
                    if (G.FindGround(p, 20f, out Vector3 g)) p = g; else p.y = feet.y;
                    GameObject mob = null;
                    try { mob = SC.Entities.SpawnMob(Mobs[k], p); }
                    catch (Exception e) { failed.Add(Mobs[k] + " threw " + e.Message); }
                    if (mob != null) ok++;
                    else if (!failed.Exists(f => f.StartsWith(Mobs[k], StringComparison.Ordinal))) failed.Add(Mobs[k] + (known.Contains(Mobs[k]) ? "" : " (not in MobIds)"));
                    yield return null;
                }
                r.Check("8 Minecraft mobs spawned (SC.Entities.SpawnMob)", ok == Mobs.Length, ok + "/" + Mobs.Length + (failed.Count > 0 ? "; failed: " + string.Join(", ", failed.ToArray()) : ""));
                r.Check("LiveMobCount increased", SC.Entities.LiveMobCount >= before + ok, before + " -> " + SC.Entities.LiveMobCount);
            }
            G.LookAt(Vector3.Lerp(towerFeet, new Vector3(houseOrigin.x + 0.5f, towerFeet.y, houseOrigin.z + 0.5f), 0.45f) + Vector3.up * 0.8f);
            yield return G.Wait(1.5f);
            yield return Shot(r, "04_slimes_and_mobs");
        }

        // ================================================================== 05
        private IEnumerator TntExplosion(StepResult r)
        {
            if (SC.Entities == null) { r.Fail("SC.Entities available (Entities module)"); yield break; }
            Vector3 tntPos;
            if (towerTnt.Count > 0 && SC.Blocks != null)
            {
                var b = towerTnt[0];
                SC.Blocks.SetBlock(b, null); // the bottom TNT block becomes primed TNT, the rest must chain-react
                tntPos = new Vector3(b.x + 0.5f, b.y, b.z + 0.5f);
            }
            else
            {
                if (towerFeet == Vector3.zero) towerFeet = G.Feet + G.FlatLook * 10f;
                tntPos = towerFeet;
                r.Note("no TNT tower – priming TNT at " + tntPos.ToString("F1"));
            }
            if (Vector3.Distance(G.Feet, tntPos) < 10f)
            {
                Vector3 away = G.Feet - tntPos;
                away.y = 0f;
                away = away.sqrMagnitude < 0.01f ? -(Vector3)fwd : away.normalized;
                yield return MovePlayer(r, tntPos + away * 12f, "outside the blast radius");
            }
            G.Heal();
            G.LookAt(tntPos + Vector3.up * 2f);

            var lastPos = new Vector3[slimes.Count];
            for (int i = 0; i < slimes.Count; i++) if (slimes[i] != null) lastPos[i] = slimes[i].transform.position;

            GameObject tnt = null;
            try { tnt = SC.Entities.SpawnPrimedTnt(tntPos, 80); }
            catch (Exception e) { r.Note("SpawnPrimedTnt threw: " + e.Message); }
            if (!r.Check("primed TNT spawned (SC.Entities.SpawnPrimedTnt, 80 ticks)", tnt != null)) yield break;
            float t0 = Time.time;

            yield return G.WaitGame(1f);
            r.Check("TNT still fusing after 1 s", tnt != null && tnt.activeInHierarchy);
            yield return Shot(r, "05a_tnt_fuse_1s");

            bool exploded = false;
            float tExp = 0f;
            while (Time.time - t0 < 8f)
            {
                if (tnt == null || !tnt.activeInHierarchy) { exploded = true; tExp = Time.time - t0; break; }
                for (int i = 0; i < slimes.Count; i++) if (slimes[i] != null) lastPos[i] = slimes[i].transform.position;
                yield return null;
            }
            yield return Shot(r, "05b_explosion");
            r.Check("TNT exploded after its 4 s fuse", exploded && tExp > 3.4f && tExp < 4.8f,
                exploded ? tExp.ToString("0.00", CultureInfo.InvariantCulture) + " s" : "did not explode within 8 s");

            // track how far/fast the SR slimes fly during the next second
            float maxSpeed = 0f;
            float tEnd = Time.time + 1f;
            while (Time.time < tEnd)
            {
                foreach (var s in slimes)
                {
                    if (s == null) continue;
                    var rb = s.GetComponent<Rigidbody>();
                    if (rb != null && rb.velocity.magnitude > maxSpeed) maxSpeed = rb.velocity.magnitude;
                }
                yield return null;
            }
            yield return Shot(r, "05c_after_1s");
            int launched = 0, alive = 0;
            float maxDisp = 0f;
            for (int i = 0; i < slimes.Count; i++)
            {
                if (slimes[i] == null) continue;
                alive++;
                float d = Vector3.Distance(slimes[i].transform.position, lastPos[i]);
                if (d > maxDisp) maxDisp = d;
                if (d > 2f) launched++;
            }
            if (slimes.Count > 0)
                r.Check("SR slimes launched by the Minecraft explosion", launched > 0 || maxSpeed > 6f,
                    launched + "/" + alive + " moved > 2 m, max speed " + maxSpeed.ToString("0.0", CultureInfo.InvariantCulture) +
                    " m/s, max displacement " + maxDisp.ToString("0.0", CultureInfo.InvariantCulture) + " m");
            else r.Note("no SR slimes to launch (step 04 not run or failed)");

            yield return G.WaitGame(2.5f); // chained TNT: Minecraft gives exploded-into TNT a 10-30 tick fuse
            if (towerTnt.Count > 1 && SC.Blocks != null)
            {
                int left = 0;
                for (int i = 1; i < towerTnt.Count; i++) if (SC.Blocks.GetBlock(towerTnt[i]) == "minecraft:tnt") left++;
                r.Check("TNT tower chain-reacted", left < towerTnt.Count - 1, left + " of " + (towerTnt.Count - 1) + " TNT blocks left");
                int houseLeft = 0;
                foreach (var b in houseBlocks) if (SC.Blocks.GetBlock(b) != null) houseLeft++;
                r.Note("house blocks remaining " + houseLeft + "/" + houseBlocks.Count);
            }
            yield return Shot(r, "05d_after_chain");
            G.Heal();
        }

        // ================================================================== 06
        private IEnumerator Screens(StepResult r)
        {
            if (SC.Hud == null) { r.Fail("SC.Hud available (Hud module)"); yield break; }
            G.Exec(r, "/gamemode survival");
            yield return OpenScreen(r, "inventory", "06a_inventory");
            yield return CloseScreens(r);

            G.Exec(r, "/gamemode creative");
            if (SC.Inventory != null) r.Check("creative mode active", SC.Inventory.Creative);
            yield return OpenScreen(r, "creative", "06b_creative_inventory");
            yield return CloseScreens(r);
            G.Exec(r, "/gamemode survival");
            if (SC.Inventory != null) r.Check("survival mode active", !SC.Inventory.Creative);

            yield return OpenScreen(r, "crafting", "06c_crafting_table");
            yield return CloseScreens(r);

            // chat with a few lines (player chat + command feedback)
            SC.Hud.AddChat("<Player> Hello from Minecraft inside Slime Rancher!");
            SC.Hud.AddChat("<Player> those pink slimes really fly with TNT");
            string fb = null;
            try { fb = SC.Commands != null ? SC.Commands.Execute("/give minecraft:golden_apple 1") : null; } catch { }
            if (!string.IsNullOrEmpty(fb)) SC.Hud.AddChat(fb);
            SC.Hud.AddChat("[SlimeCraft] automated test: chat + commands");
            yield return OpenScreen(r, "chat", "06d_chat");
            yield return CloseScreens(r);
            r.Check("all Minecraft screens closed", !SC.Hud.ScreenOpen);
            if (SC.Input != null) r.Check("input gate released (no screen open)", !SC.Input.AnyScreenOpen);
        }

        private IEnumerator OpenScreen(StepResult r, string screen, string shot)
        {
            bool known = G.Exec(r, "/screen " + screen);
            yield return G.Wait(0.5f);
            r.Check("'" + screen + "' screen open", known && SC.Hud.ScreenOpen);
            if (SC.Input != null) r.Check("'" + screen + "' screen holds the input gate", SC.Input.AnyScreenOpen);
            yield return Shot(r, shot);
        }

        private IEnumerator CloseScreens(StepResult r)
        {
            G.Exec(r, "/screen none");
            yield return G.Wait(0.35f);
        }
    }
}
