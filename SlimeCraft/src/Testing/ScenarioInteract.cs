using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace SlimeCraft.Testing
{
    /// <summary>
    /// Scenario steps 11-19: they drive the REAL interaction code (Blocks' InteractionController, Entities' items and
    /// mobs, Hud's drop key, SR's own vacpack) through the IInputGate test hook – SetTestInput(attack, use, sneak) /
    /// ClearTestInput / SimulateKeyDown – while the camera is aimed with ISRBridge.SetLook at targets the step placed
    /// itself. Results are verified through the inventory, SC.Blocks, SC.Entities and the SR actors around the target.
    /// The runner clears the test input and the vac driver after every step.
    /// </summary>
    internal sealed partial class Scenario
    {
        private const string Pickaxe = "minecraft:diamond_pickaxe";
        private const string Sword = "minecraft:diamond_sword";

        // results of CollectDrops
        private int dropGain;
        private bool dropSeen;
        private string dropNote = "";

        // ------------------------------------------------------------------ shared
        private bool InteractPrereqs(StepResult r, bool needBlocks = false, bool needEntities = false, bool clearEntities = false)
        {
            if (SC.Input == null) { r.Fail("SC.Input available (Core module)"); return false; }
            if (SC.Inventory == null || SC.Inventory.Inv == null) { r.Fail("SC.Inventory available (Hud module)"); return false; }
            if (SC.SR == null || !SC.SR.InGame) { r.Fail("in game (SC.SR.InGame)"); return false; }
            if (needBlocks && SC.Blocks == null) { r.Fail("SC.Blocks available (Blocks module)"); return false; }
            if (needEntities && SC.Entities == null) { r.Fail("SC.Entities available (Entities module)"); return false; }
            try { if (SC.Hud != null && SC.Hud.ScreenOpen && SC.Commands != null) SC.Commands.Execute("/screen none"); } catch { }
            G.EnsureUnpaused();
            TI.Clear();
            if (clearEntities && SC.Entities != null)
            {
                // stray day-time animals / old drops must not end up between the crosshair and the target
                try { SC.Entities.ClearAll(); } catch (Exception e) { r.Note("ClearAll threw " + e.Message); }
            }
            return true;
        }

        /// <summary>Sets the game mode and checks that SlimeCraft accepts gameplay input (no screen, not paused, no SR UI).</summary>
        private IEnumerator Ready(StepResult r, string gameMode)
        {
            if (gameMode != null) G.Exec(r, "/gamemode " + gameMode);
            if (!TI.Set(false, false)) r.Check("IInputGate test hook (SetTestInput) works", false, "see the [Test] warning in the log");
            yield return G.WaitFrames(3);
            r.Check("gameplay input allowed (IInputGate.GameplayInputAllowed)", SC.Input.GameplayInputAllowed);
        }

        /// <summary>Aims at a point and waits until SR's camera applied it.</summary>
        private static IEnumerator Aim(Vector3 target)
        {
            G.LookAt(target);
            yield return G.WaitFrames(3);
        }

        /// <summary>
        /// Waits for dropped items of the given ids near a point and makes sure the player picks them up: when a drop is
        /// still lying there after 0.7 s the player is moved onto it (Minecraft picks items up 1 block around the player).
        /// Fills dropGain / dropSeen / dropNote.
        /// </summary>
        private IEnumerator CollectDrops(StepResult r, string[] ids, int baseline, Vector3 near, float timeout)
        {
            dropGain = 0;
            dropSeen = false;
            float t0 = G.Now;
            bool moved = false;
            Vector3 seenAt = Vector3.zero;
            while (G.Now - t0 < timeout)
            {
                int c = G.CountOf(ids);
                var ents = G.ItemEntities(ids, near, 6f);
                if (ents.Count > 0) { dropSeen = true; seenAt = ents[0].transform.position; }
                if (c > baseline && ents.Count == 0) break; // everything picked up
                if (!moved && ents.Count > 0 && G.Now - t0 > 0.7f && c <= baseline)
                {
                    moved = true;
                    Vector3 p = ents[0].transform.position;
                    if (!G.FindStandSpot(p, out Vector3 feet, 1.2f)) feet = p + Vector3.up * 0.1f;
                    r.Note("walking onto the drop at " + p.ToString("F1"));
                    yield return G.MoveAndSettle(feet, 0.1f);
                    continue;
                }
                yield return G.Wait(0.1f);
            }
            dropGain = Math.Max(0, G.CountOf(ids) - baseline);
            dropNote = (dropSeen ? "item entity seen at " + seenAt.ToString("F1") : "no item entity seen") + ", inventory +" + dropGain +
                       " after " + G.F1(G.Now - t0) + " s" + (moved ? " (player moved onto it)" : "");
        }

        /// <summary>A free cell (no block, no SR geometry, clear line of sight, not the player) dist m ahead at eye height + dy.</summary>
        private bool FindFreeCellAhead(float dist, float dy, out Vector3Int cell, out string why)
        {
            cell = default(Vector3Int);
            why = "no direction tried";
            Vector3 eye = G.Eye;
            Vector3 fl = G.FlatLook;
            Bounds pb = SC.SR.PlayerBounds;
            foreach (float off in new[] { 0f, 30f, -30f, 60f, -60f, 90f, -90f, 135f, -135f, 180f })
            {
                Vector3 dir = Quaternion.Euler(0f, off, 0f) * fl;
                Vector3Int c = G.ToBlock(eye + dir * dist + Vector3.up * dy);
                Vector3 center = G.BlockCenter(c);
                if (SC.Blocks.GetBlock(c) != null) { why = "cell occupied by a block"; continue; }
                if (Physics.CheckBox(center, Vector3.one * 0.45f, Quaternion.identity, G.Mask, QueryTriggerInteraction.Ignore)) { why = "SR geometry inside the cell"; continue; }
                Vector3 d = center - eye;
                float len = d.magnitude;
                if (len < 0.5f || G.FreeDistance(eye, d / len, len) < len - 0.6f) { why = "line of sight blocked"; continue; }
                if (pb.size.sqrMagnitude > 0f && pb.Intersects(new Bounds(center, Vector3.one))) { why = "inside the player"; continue; }
                cell = c;
                return true;
            }
            return false;
        }

        /// <summary>
        /// Walkable ground about dist m ahead (trying several directions): roughly at the player's height, visible from the
        /// eye and with room for a mob/slime. Falls back to straight ahead.
        /// </summary>
        private static bool FindOpenGroundAhead(float dist, out Vector3 ground, out Vector3 dir)
        {
            Vector3 eye = G.Eye, feet = G.Feet, fl = G.FlatLook;
            foreach (float off in new[] { 0f, 30f, -30f, 60f, -60f, 90f, -90f, 135f, -135f, 180f })
            {
                dir = Quaternion.Euler(0f, off, 0f) * fl;
                if (!G.FindGround(feet + dir * dist + Vector3.up * 2f, 5f, out ground, true)) continue;
                if (Mathf.Abs(ground.y - feet.y) > 1.5f) continue;
                Vector3 d = ground + Vector3.up * 0.4f - eye;
                if (G.FreeDistance(eye, d.normalized, d.magnitude) < d.magnitude - 0.3f) continue;
                if (!G.CapsuleFree(ground + Vector3.up * 0.05f, 0.45f, 1.2f)) continue;
                return true;
            }
            dir = fl;
            ground = feet + fl * dist;
            return false;
        }

        private static bool IsTerrain(Collider c)
        {
            if (c == null || c.isTrigger) return false;
            if (SC.Blocks != null && SC.Blocks.IsBlockCollider(c)) return false;
            if (c.attachedRigidbody != null && !c.attachedRigidbody.isKinematic) return false;
            try { if (c.GetComponentInParent<Identifiable>() != null) return false; } catch { }
            return true;
        }

        // ================================================================== 11
        private IEnumerator EmptyHand(StepResult r)
        {
            if (!InteractPrereqs(r)) yield break;
            yield return Ready(r, null);
            var inv = SC.Inventory.Inv;
            int slot = -1;
            for (int i = 0; i < 9 && i < inv.Size; i++) if (inv.Get(i).IsEmpty) { slot = i; break; }
            if (slot < 0)
            {
                // free a hotbar slot: move its stack into the main inventory (never the vacpack)
                int from = inv.Get(7).Id == Content.Vacpack ? 6 : 7;
                int to = -1;
                for (int i = 9; i < inv.Size; i++) if (inv.Get(i).IsEmpty) { to = i; break; }
                if (to < 0) { r.Fail("a free main-inventory slot to empty a hotbar slot"); yield break; }
                var st = inv.Get(from);
                inv.Set(to, st);
                inv.Set(from, ItemStack.Empty);
                slot = from;
                r.Note("moved " + st + " from hotbar slot " + from + " to inventory slot " + to);
            }
            SC.Inventory.SelectedSlot = slot;
            yield return G.Wait(0.9f); // Minecraft equip animation
            var sel = SC.Inventory.Selected;
            r.Check("empty hotbar slot selected", sel == null || sel.IsEmpty, "slot " + slot + ": " + (sel != null ? sel.ToString() : "null"));
            r.Check("vacpack not selected", !SC.Inventory.VacpackSelected);
            r.Check("SR vacpack hidden with an empty hand", !SC.SR.VacpackActive);
            r.Check("SR weapon input suppressed", SC.Input.SuppressSRWeapon);
            G.SetLook(-10f, G.YawOf(G.FlatLook)); // a bit up: nothing in reach to punch
            yield return G.Wait(0.4f);
            yield return Shot(r, "11a_empty_hand");
            // punch the air through the real attack path (InteractionController.LeftPress → swing)
            TI.Set(true, false);
            yield return G.WaitFrames(2);
            yield return G.Wait(0.06f);
            yield return Shot(r, "11b_empty_hand_punch");
            TI.Set(false, false);
            yield return G.Wait(0.35f);
            TI.Clear();
        }

        // ================================================================== 12
        private IEnumerator MineBlock(StepResult r)
        {
            if (!InteractPrereqs(r, needBlocks: true, clearEntities: true)) yield break;
            yield return Ready(r, "survival"); // creative would break the block instantly (no crack)
            const string stone = "minecraft:stone", cobble = "minecraft:cobblestone";
            if (!r.Check("diamond pickaxe selected", G.SelectItem(Pickaxe), G.HotbarText())) yield break;
            yield return G.Wait(0.8f); // equip + attack strength recharge

            if (!FindFreeCellAhead(2.5f, -0.5f, out Vector3Int cell, out string why)) { r.Fail("free cell for a stone block 2.5 m ahead (" + why + ")"); yield break; }
            bool placed = false;
            try { placed = SC.Blocks.SetBlock(cell, stone, BlockFacing.North, true); }
            catch (Exception e) { r.Note("SetBlock threw " + e.Message); }
            if (!r.Check("stone placed 2.5 m ahead at eye height -0.5 (SC.Blocks.SetBlock)", placed && SC.Blocks.GetBlock(cell) == stone, cell.ToString())) yield break;
            yield return G.Wait(0.25f); // chunk mesh
            Vector3 center = G.BlockCenter(cell);
            yield return Aim(center);
            bool onTarget = SC.Blocks.Raycast(new Ray(G.Eye, G.Look), 4.5f, out BlockHit bh) && bh.Pos == cell;
            r.Check("crosshair on the stone block (SC.Blocks.Raycast from the camera)", onTarget,
                onTarget ? "distance " + G.F2(bh.Distance) : "look " + G.Look.ToString("F2") + " from " + G.Eye.ToString("F1"));

            int cobble0 = G.CountOf(cobble);
            float t0 = G.Now, maxProgress = -1f;
            bool broken = false, crackShot = false;
            TI.Set(true, false); // hold left mouse
            while (G.Now - t0 < 4f)
            {
                yield return null;
                if (SC.Blocks.GetBlock(cell) == null) { broken = true; break; }
                float p = -1f;
                try { p = SC.FirstPerson != null ? SC.FirstPerson.MiningProgress : -1f; } catch { }
                if (p > maxProgress) maxProgress = p;
                bool half = SC.FirstPerson != null ? p >= 0.35f : G.Now - t0 > 0.15f;
                if (!crackShot && half)
                {
                    crackShot = true;
                    yield return Shot(r, "12a_mining_crack");
                    r.Note("crack screenshot at mining progress " + G.F2(p) + ", " + G.F2(G.Now - t0) + " s after the press");
                }
            }
            float tBreak = G.Now - t0;
            TI.Set(false, false);
            r.Check("holding attack broke the stone (Minecraft destroy progress)", broken,
                G.F2(tBreak) + " s, max IFirstPerson.MiningProgress " + G.F2(maxProgress) + (broken ? "" : ", block still " + SC.Blocks.GetBlock(cell)));
            if (!crackShot) r.Note("the block broke before a ~50 % crack screenshot could be taken");
            if (broken) r.Note("break time " + G.F2(tBreak) + " s (Minecraft: diamond pickaxe on stone = 6 ticks = 0.30 s, plus screenshot frames)");
            if (!broken) { TI.Clear(); yield break; }
            yield return G.WaitFrames(2);
            yield return Shot(r, "12b_block_broken");

            yield return CollectDrops(r, new[] { cobble }, cobble0, center, 3f);
            r.Check("stone dropped a cobblestone item entity", dropSeen || dropGain > 0, dropNote);
            r.Check("the cobblestone was picked up (inventory count increased within 3 s)", G.CountOf(cobble) > cobble0, cobble0 + " -> " + G.CountOf(cobble));
            TI.Clear();
        }

        // ================================================================== 13
        private IEnumerator PlaceBlocks(StepResult r)
        {
            if (!InteractPrereqs(r, needBlocks: true, clearEntities: true)) yield break;
            yield return Ready(r, "survival");
            const string planks = "minecraft:oak_planks";
            if (!r.Check("oak planks selected", G.SelectItem(planks, 8), G.HotbarText())) yield break;
            yield return G.Wait(0.6f);

            if (!FindTerrainTarget(3f, out RaycastHit hit, out Vector3Int expected))
            {
                r.Fail("SR terrain found ~3 m ahead to place on (tried 12 directions)");
                yield break;
            }
            r.Note("target: " + G.DescribeSurface(hit.collider) + " at " + hit.point.ToString("F2") + ", normal " + hit.normal.ToString("F2"));
            yield return Aim(hit.point);
            // recompute from the camera's real ray (what the InteractionController sees)
            var ray = new Ray(G.Eye, G.Look);
            if (SC.SR.RaycastWorld(ray, 4.5f, out RaycastHit h2) && IsTerrain(h2.collider))
                expected = Vector3Int.FloorToInt(h2.point + h2.normal * 0.5f);
            else r.Note("camera ray does not hit SR terrain within 4.5 m any more");

            var events = new List<string>();
            var placedCells = new List<Vector3Int>();
            Action<Vector3Int, string, string> onChange = (p, o, n) =>
            {
                if (n == null) return;
                placedCells.Add(p);
                if (events.Count < 8) events.Add(n + "@" + p);
            };
            SC.Blocks.BlockChanged += onChange;
            try
            {
                int planks0 = G.CountOf(planks);
                int count0 = SC.Blocks.Count;
                yield return TI.Click(false, true); // one right click
                yield return G.Wait(0.25f);
                bool ok = SC.Blocks.GetBlock(expected) == planks;
                r.Check("right click placed oak planks on the SR terrain at the aimed cell", ok,
                    "expected " + expected + "; placed: " + (events.Count > 0 ? string.Join(", ", events.ToArray()) : "nothing") + "; Count " + count0 + " -> " + SC.Blocks.Count);
                r.Check("exactly one block placed per click", placedCells.Count == 1, placedCells.Count + " placed");
                r.Check("one oak planks consumed (survival)", G.CountOf(planks) == planks0 - 1, planks0 + " -> " + G.CountOf(planks));

                Vector3Int first = ok ? expected : (placedCells.Count > 0 ? placedCells[0] : expected);
                if (SC.Blocks.GetBlock(first) == planks)
                {
                    // aim at the top face and place a second block on it
                    yield return G.Wait(0.3f);
                    yield return Aim(G.BlockCenter(first) + Vector3.up * 0.5f);
                    bool onBlock = SC.Blocks.Raycast(new Ray(G.Eye, G.Look), 4.5f, out BlockHit bh) && bh.Pos == first;
                    r.Check("crosshair on the placed block (SC.Blocks.Raycast)", onBlock, onBlock ? "face normal " + bh.Normal : "hit " + bh.Pos);
                    // normally the top face; if the terrain there is higher than the eye, the face the camera sees instead
                    var expected2 = first + (onBlock ? bh.Normal : Vector3Int.up);
                    if (onBlock && bh.Normal != Vector3Int.up) r.Note("top face not visible from the eye (" + G.Eye.ToString("F1") + "), placing against face " + bh.Normal);
                    placedCells.Clear();
                    events.Clear();
                    yield return TI.Click(false, true);
                    yield return G.Wait(0.25f);
                    r.Check("second block placed against the aimed face" + (expected2 == first + Vector3Int.up ? " (on top)" : ""),
                        SC.Blocks.GetBlock(expected2) == planks,
                        "expected " + expected2 + "; placed: " + (events.Count > 0 ? string.Join(", ", events.ToArray()) : "nothing"));
                    yield return Aim(G.BlockCenter(first) + Vector3.up * 0.5f);
                }
                yield return G.Wait(0.3f);
                yield return Shot(r, "13_placed_blocks");
            }
            finally
            {
                SC.Blocks.BlockChanged -= onChange;
            }
            TI.Clear();
        }

        /// <summary>Walkable SR terrain (not a block / actor / moving body) about dist m ahead whose placement cell is free.</summary>
        private bool FindTerrainTarget(float dist, out RaycastHit hit, out Vector3Int cell)
        {
            hit = default(RaycastHit);
            cell = default(Vector3Int);
            Vector3 eye = G.Eye;
            Vector3 feet = G.Feet;
            Vector3 fl = G.FlatLook;
            Bounds pb = SC.SR.PlayerBounds;
            foreach (float off in new[] { 0f, 25f, -25f, 50f, -50f, 80f, -80f, 110f, -110f, 145f, -145f, 180f })
            {
                Vector3 dir = Quaternion.Euler(0f, off, 0f) * fl;
                if (!G.FindGround(feet + dir * dist + Vector3.up * 2f, 5f, out Vector3 g, true)) continue;
                Vector3 d = g - eye;
                if (d.sqrMagnitude < 1f) continue;
                var ray = new Ray(eye, d.normalized);
                if (!SC.SR.RaycastWorld(ray, 4.4f, out RaycastHit h)) continue;
                if (!IsTerrain(h.collider) || h.normal.y < 0.6f || h.distance < 2f) continue;
                if (SC.Blocks.Raycast(ray, h.distance, out BlockHit _)) continue; // one of our blocks is in front
                var c = Vector3Int.FloorToInt(h.point + h.normal * 0.5f);
                if (SC.Blocks.GetBlock(c) != null || SC.Blocks.GetBlock(c + Vector3Int.up) != null) continue;
                var cb = new Bounds(G.BlockCenter(c), Vector3.one);
                if (pb.size.sqrMagnitude > 0f && (pb.Intersects(cb) || pb.Intersects(new Bounds(cb.center + Vector3.up, Vector3.one)))) continue;
                hit = h;
                cell = c;
                return true;
            }
            return false;
        }

        // ================================================================== 14
        private struct SurfaceCand
        {
            public RaycastHit Hit;
            public int Score;
            public string Desc;
        }

        private static readonly string[] RockTokens = { "rock", "cliff", "boulder", "stone", "crag", "mtn", "mountain", "mesa", "ledge", "pillar", "rubble", "cobble", "spire" };
        private static readonly string[] GroundTokens = { "ground", "terrain", "dirt", "grass", "sand", "moss", "soil", "ore", "crystal" };
        private static readonly string[] NotHarvestTokens =
        {
            "house", "roof", "plot", "corral", "coop", "silo", "incinerator", "garden", "pond", "gadget", "teleport", "warp",
            "drone", "market", "door", "gate", "fence", "sign", "water", "chest", "statue", "slime", "plort", "gordo",
            "treasure", "pod", "vac", "kill", "trigger", "barrier", "lamp", "dock", "deck", "bridge", "shop"
        };

        private static bool HasAny(string s, string[] tokens)
        {
            foreach (var t in tokens) if (s.IndexOf(t, StringComparison.Ordinal) >= 0) return true;
            return false;
        }

        /// <summary>SR world surfaces within reach in many directions; rock-like names first (Blocks classifies by names).</summary>
        private List<SurfaceCand> FindSurfaces(float reach, int max)
        {
            var list = new List<SurfaceCand>();
            var seen = new HashSet<int>();
            Vector3 eye = G.Eye;
            float yaw0 = G.YawOf(G.FlatLook);
            foreach (float pitch in new[] { 0f, -15f, 15f, 30f, 50f, 70f })
                for (int yi = 0; yi < 24; yi++)
                {
                    Vector3 dir = Quaternion.Euler(pitch, yaw0 + yi * 15f, 0f) * Vector3.forward;
                    if (!SC.SR.RaycastWorld(new Ray(eye, dir), reach, out RaycastHit h)) continue;
                    if (!IsTerrain(h.collider) || h.distance < 1.2f) continue;
                    if (!seen.Add(h.collider.GetInstanceID())) continue;
                    string desc = G.DescribeSurface(h.collider);
                    string low = desc.ToLowerInvariant();
                    if (HasAny(low, NotHarvestTokens)) continue;
                    int score = HasAny(low, RockTokens) ? 3 : HasAny(low, GroundTokens) ? 1 : 0;
                    list.Add(new SurfaceCand { Hit = h, Score = score, Desc = desc });
                }
            list.Sort((a, b) => a.Score != b.Score ? b.Score.CompareTo(a.Score) : a.Hit.distance.CompareTo(b.Hit.distance));
            if (list.Count > max) list.RemoveRange(max, list.Count - max);
            return list;
        }

        private IEnumerator HarvestTerrain(StepResult r)
        {
            if (!InteractPrereqs(r, needBlocks: true, clearEntities: true)) yield break;
            yield return Ready(r, "survival"); // creative harvests instantly and drops nothing
            if (!r.Check("diamond pickaxe selected", G.SelectItem(Pickaxe), G.HotbarText())) yield break;
            yield return G.Wait(0.7f);

            var cands = FindSurfaces(4.2f, 4);
            if (cands.Count == 0 || cands[0].Score < 3)
            {
                // no rock in reach: look farther for one and walk next to it
                var far = FindSurfaces(40f, 8);
                SurfaceCand rock = default(SurfaceCand);
                bool found = false;
                foreach (var c in far) if (c.Score >= 3) { rock = c; found = true; break; }
                if (found)
                {
                    Vector3 away = G.Horizontal(rock.Hit.normal);
                    if (away.sqrMagnitude < 0.01f) away = G.Horizontal(G.Eye - rock.Hit.point);
                    away = away.sqrMagnitude < 0.01f ? -G.FlatLook : away.normalized;
                    Vector3 standNear = rock.Hit.point + away * 2.2f;
                    if (G.FindStandSpot(standNear, out Vector3 feet, 1.5f))
                    {
                        r.Note("walking to a rock-like surface " + G.F1(rock.Hit.distance) + " m away: " + rock.Desc);
                        yield return G.MoveAndSettle(feet, 0.3f);
                        var near = FindSurfaces(4.2f, 4);
                        if (near.Count > 0) cands = near;
                    }
                }
            }
            if (cands.Count == 0) { r.Fail("an SR world surface within reach (SC.SR.RaycastWorld in 144 directions)"); yield break; }

            bool success = false;
            var tried = new StringBuilder();
            for (int i = 0; i < cands.Count && i < 3 && !success; i++)
            {
                var c = cands[i];
                yield return Aim(c.Hit.point);
                var ray = new Ray(G.Eye, G.Look);
                bool same = SC.SR.RaycastWorld(ray, 4.5f, out RaycastHit now) && now.collider == c.Hit.collider;
                Vector3 point = same ? now.point : c.Hit.point;
                var before = G.InvCounts();
                var known = new HashSet<int>();
                foreach (var e in G.ItemEntities((string[])null, point, 6f)) known.Add(e.GetInstanceID());

                TI.Set(true, false);
                float t0 = G.Now;
                bool shot = false;
                while (G.Now - t0 < 3f)
                {
                    yield return null;
                    if (!shot && i == 0 && G.Now - t0 > 1.2f) { shot = true; yield return Shot(r, "14_harvest_sr_terrain"); }
                }
                TI.Set(false, false);
                yield return G.Wait(1f); // drops pop out and land / get picked up

                var dropped = new List<string>();
                foreach (var e in G.ItemEntities((string[])null, point, 6f))
                    if (!known.Contains(e.GetInstanceID())) dropped.Add(G.ItemIdOfEntity(e));
                string gains = G.Gains(before, G.InvCounts());
                string result = (dropped.Count > 0 ? "dropped " + string.Join(", ", dropped.ToArray()) : "no item entity") +
                                (gains.Length > 0 ? "; picked up " + gains : "");
                r.Note("harvest #" + (i + 1) + " (score " + c.Score + (same ? "" : ", camera ray hit another collider") + ") " + c.Desc + " -> " + result);
                tried.Append(i > 0 ? " | " : "").Append(c.Desc).Append(" -> ").Append(result);
                success = dropped.Count > 0 || gains.Length > 0;
            }
            r.Check("harvesting SR terrain with the diamond pickaxe dropped Minecraft items (SR geometry untouched)", success, tried.ToString());
            TI.Clear();
        }

        // ================================================================== 15
        private IEnumerator EatFood(StepResult r)
        {
            if (!InteractPrereqs(r)) yield break;
            yield return Ready(r, "survival");
            const string beef = "minecraft:cooked_beef";
            if (!r.Check("cooked beef selected", G.SelectItem(beef, 3), G.HotbarText())) yield break;
            yield return G.Wait(0.6f);
            G.SetLook(-25f, G.YawOf(G.FlatLook)); // up at the sky: eating must not hit a block/entity use path
            yield return G.WaitFrames(3);

            G.Heal();
            yield return G.Wait(0.1f);
            SC.SR.DamagePlayer(30, null);
            yield return G.Wait(0.4f);
            int hp0 = SC.SR.Health, max = SC.SR.MaxHealth;
            if (!r.Check("player hurt below max health (SC.SR.DamagePlayer 30)", hp0 < max, hp0 + "/" + max)) yield break;
            int n0 = G.CountOf(beef);

            TI.Set(false, true); // hold right mouse
            float t0 = G.Now;
            yield return G.Wait(0.8f);
            yield return Shot(r, "15_eating");
            // Minecraft eats in 32 ticks (1.6 s); release at 2 s total
            while (G.Now - t0 < 2f) yield return null;
            TI.Set(false, false);
            yield return G.Wait(0.3f);
            int hp1 = SC.SR.Health, n1 = G.CountOf(beef);
            var def = Content.Item(beef);
            r.Check("eating healed the player (SR health)", hp1 > hp0, hp0 + " -> " + hp1 + "/" + max + " (cooked beef SRHeal " + (def != null ? def.SRHeal : 0) + ")");
            r.Check("one cooked beef consumed", n1 == n0 - 1, n0 + " -> " + n1);
            TI.Clear();
            G.Heal();
        }

        // ================================================================== 16
        private IEnumerator SwordVsPig(StepResult r)
        {
            if (!InteractPrereqs(r, needEntities: true, clearEntities: true)) yield break;
            yield return Ready(r, "survival");
            if (!r.Check("diamond sword selected", G.SelectItem(Sword), G.HotbarText())) yield break;

            if (!FindOpenGroundAhead(2.5f, out Vector3 p, out Vector3 _)) r.Note("no open ground found around the player, spawning straight ahead");
            GameObject pig = null;
            try { pig = SC.Entities.SpawnMob(MobIds.Pig, p); }
            catch (Exception e) { r.Note("SpawnMob threw " + e.Message); }
            if (!r.Check("pig spawned 2.5 m ahead (SC.Entities.SpawnMob)", pig != null, p.ToString("F1"))) yield break;
            yield return G.Wait(0.9f); // pig lands; sword attack strength recharges (20 / 1.6 ticks)

            int porkIds0 = G.CountOf(new[] { "minecraft:porkchop", "minecraft:cooked_porkchop" });
            Vector3 lastPos = pig.transform.position;
            int hits = 0;
            bool dead = false;
            for (int attempt = 0; attempt < 6 && !dead; attempt++)
            {
                if (pig == null) { dead = true; break; }
                lastPos = pig.transform.position;
                if (G.HorizontalDistance(G.Feet, lastPos) > 2.6f)
                {
                    // the pig panicked away: walk up to it (survival entity reach = 3 blocks)
                    Vector3 back = G.Horizontal(G.Feet - lastPos);
                    back = back.sqrMagnitude < 0.01f ? -G.FlatLook : back.normalized;
                    if (G.FindStandSpot(lastPos + back * 1.8f, out Vector3 feet, 1f)) yield return G.MoveAndSettle(feet, 0.05f);
                    if (pig == null) { dead = true; break; }
                    lastPos = pig.transform.position;
                }
                yield return Aim(lastPos + Vector3.up * 0.5f);
                string onWhat = CrosshairEntity(pig);
                if (onWhat != null) r.Note("hit " + (attempt + 1) + ": crosshair not on the pig per IEntities.RaycastEntity (" + onWhat + ")");
                TI.Set(true, false);
                yield return G.WaitFrames(2);
                yield return G.Wait(0.04f);
                if (attempt == 0) yield return Shot(r, "16a_sword_hit");
                TI.Set(false, false);
                hits++;
                // Minecraft: 20 tick hurt invulnerability, sword cooldown 12.5 ticks
                float tw = G.Now;
                while (G.Now - tw < 1.1f)
                {
                    yield return null;
                    if (pig == null || !IsLiveMob(pig)) { dead = true; break; }
                }
            }
            if (pig != null) lastPos = pig.transform.position;
            r.Check("pig killed with the diamond sword (real attack path)", dead, hits + " hit(s)");
            if (!dead) { TI.Clear(); yield break; }
            yield return G.Wait(0.4f);
            yield return Shot(r, "16b_pig_killed");
            yield return CollectDrops(r, new[] { "minecraft:porkchop", "minecraft:cooked_porkchop" }, porkIds0, lastPos, 4f);
            r.Check("pig dropped porkchop", dropSeen || dropGain > 0, dropNote);
            TI.Clear();
        }

        /// <summary>null when the camera ray hits the target entity first, else what it hits instead.</summary>
        private static string CrosshairEntity(GameObject target)
        {
            try
            {
                if (!SC.Entities.RaycastEntity(new Ray(G.Eye, G.Look), 3.5f, out EntityHit eh) || eh.Target == null) return "nothing";
                if (target != null && (eh.Target == target || eh.Target.transform.IsChildOf(target.transform))) return null;
                return eh.Target.name;
            }
            catch (Exception e) { return "RaycastEntity threw " + e.Message; }
        }

        private static bool IsLiveMob(GameObject go)
        {
            try { foreach (var m in SC.Entities.LiveMobs) if (m == go) return true; }
            catch { }
            return false;
        }

        // ================================================================== 17
        private IEnumerator SlimeEatsMcFood(StepResult r)
        {
            if (!InteractPrereqs(r, needEntities: true, clearEntities: true)) yield break;
            yield return Ready(r, null);
            const string carrot = "minecraft:carrot";
            if (!FindOpenGroundAhead(2.2f, out Vector3 p, out Vector3 _)) r.Note("no open ground found around the player, spawning straight ahead");
            GameObject slime = null;
            try { slime = SC.SR.SpawnSRActor("PINK_SLIME", p + Vector3.up * 0.5f, Quaternion.identity); }
            catch (Exception e) { r.Note("SpawnSRActor threw " + e.Message); }
            if (!r.Check("pink slime spawned 2 m ahead (SC.SR.SpawnSRActor)", slime != null, p.ToString("F1"))) yield break;
            try
            {
                var emo = slime.GetComponent<SlimeEmotions>();
                if (emo != null) emo.Adjust(SlimeEmotions.Emotion.HUNGER, 1f); // a hungry slime eats food that touches it
            }
            catch (Exception e) { r.Note("could not make the slime hungry: " + e.Message); }
            yield return G.Wait(1.2f);
            if (slime == null) { r.Fail("the slime still exists"); yield break; }
            Vector3 sp = slime.transform.position;
            yield return Aim(sp + Vector3.up * 0.3f);

            var actors0 = G.SRActorsNear(sp, 6f);
            int veg0 = G.Count(actors0, "CARROT_VEGGIE");
            int plort0 = G.CountWhere(actors0, k => k.EndsWith("_PLORT", StringComparison.Ordinal));
            int carrotInv0 = G.CountOf(carrot);
            GameObject item = null;
            try { item = SC.Entities.SpawnItem(new ItemStack(carrot, 3), sp + Vector3.up * 1.0f, Vector3.zero, 10f); }
            catch (Exception e) { r.Note("SpawnItem threw " + e.Message); }
            if (!r.Check("3 Minecraft carrots dropped onto the slime (SC.Entities.SpawnItem)", item != null)) yield break;

            float t0 = G.Now, tConv = -1f, tPlort = -1f;
            int vegMax = veg0, plortMax = plort0;
            bool convShot = false;
            while (G.Now - t0 < 12f)
            {
                yield return G.Wait(0.25f);
                Vector3 c = slime != null ? slime.transform.position : sp;
                var a = G.SRActorsNear(c, 6f);
                vegMax = Math.Max(vegMax, G.Count(a, "CARROT_VEGGIE"));
                plortMax = Math.Max(plortMax, G.CountWhere(a, k => k.EndsWith("_PLORT", StringComparison.Ordinal)));
                bool itemGone = item == null || !item.activeInHierarchy;
                if (tConv < 0f && (itemGone || vegMax > veg0) && G.Now - t0 <= 10f) tConv = G.Now - t0;
                if (tConv >= 0f && !convShot) { convShot = true; yield return Shot(r, "17a_carrots_to_sr_food"); }
                if (plortMax > plort0) { tPlort = G.Now - t0; break; }
            }
            bool pickedUp = G.CountOf(carrot) > carrotInv0;
            r.Check("MC carrots turned into SR food next to the slime within 10 s (Entities food bridge)", tConv >= 0f && !pickedUp,
                (tConv >= 0f ? "after " + G.F1(tConv) + " s" : "not within 10 s") + "; CARROT_VEGGIE near the slime " + veg0 + " -> max " + vegMax +
                (pickedUp ? "; the player picked the carrots up instead" : ""));
            if (tPlort >= 0f) r.Check("the slime ate it and produced a plort", true, "after " + G.F1(tPlort) + " s; plorts near " + plort0 + " -> " + plortMax);
            else r.Note("no new plort near the slime within 12 s (SR digests ~2 s after eating; depends on the slime catching the food)");
            yield return G.Wait(0.3f);
            if (slime != null) yield return Aim(slime.transform.position + Vector3.up * 0.3f);
            yield return Shot(r, "17b_slime_after_eating");
        }

        // ================================================================== 18
        private IEnumerator VacpackMcItems(StepResult r)
        {
            if (!InteractPrereqs(r, needEntities: true, clearEntities: true)) yield break;
            yield return Ready(r, "survival");
            int slot = G.EnsureInHotbar(Content.Vacpack, 0);
            if (!r.Check("vacpack in the hotbar", slot >= 0, "slot " + slot)) yield break;
            SC.Inventory.SelectedSlot = slot;
            yield return G.Wait(1.2f); // SR vacpack equip
            r.Check("vacpack selected", SC.Inventory.VacpackSelected);
            r.Check("SR vacpack shown and active", SC.SR.VacpackActive);

            // a spot ~4 m ahead with ground and a clear view
            bool found = FindOpenGroundAhead(4f, out Vector3 ground, out Vector3 dir);
            if (!r.Check("open ground ~4 m ahead for the items", found, ground.ToString("F1"))) yield break;
            var drops = new[] { new ItemStack("minecraft:diamond", 2), new ItemStack("minecraft:stick", 3), new ItemStack("minecraft:cobblestone", 4) };
            var ids = new string[drops.Length];
            var before = G.InvCounts();
            Vector3 right = Vector3.Cross(Vector3.up, dir).normalized;
            int spawned = 0;
            for (int k = 0; k < drops.Length; k++)
            {
                ids[k] = drops[k].Id;
                try { if (SC.Entities.SpawnItem(drops[k], ground + right * ((k - 1) * 0.6f) + Vector3.up * 0.3f, Vector3.zero, 0.3f) != null) spawned++; }
                catch (Exception e) { r.Note("SpawnItem threw " + e.Message); }
            }
            if (!r.Check("Minecraft items dropped 4 m ahead", spawned == drops.Length, spawned + "/" + drops.Length)) yield break;
            yield return G.Wait(0.8f); // land
            yield return Aim(ground + Vector3.up * 0.15f);

            // 1) the SlimeCraft input gate; 2) an InControl binding on SR's vac action; 3) WeaponVacuum.vacMode reflection
            string method = null;
            TI.Set(false, true);
            yield return G.Wait(0.4f);
            if (SC.SR.VacActive) method = "IInputGate.SetTestInput (reaches SR's WeaponVacuum)";
            else
            {
                r.Note("IInputGate.SetTestInput(use) does not reach SR's WeaponVacuum (it reads SRInput.Actions.vac)");
                if (VacDriver.PressViaBinding(out string err))
                {
                    yield return G.Wait(0.4f);
                    if (SC.SR.VacActive) method = "InControl test binding on SRInput.Actions.vac (SR's own vac code runs)";
                    else r.Note("InControl test binding pressed but SC.SR.VacActive stayed false");
                }
                else r.Note("InControl test binding failed: " + err);
            }
            bool forced = false;
            if (method == null)
            {
                VacDriver.RemoveBinding();
                forced = VacDriver.ForceVacMode(true);
                if (forced)
                {
                    yield return null;
                    VacDriver.ForceVacMode(true);
                    method = "WeaponVacuum.vacMode forced to VAC by reflection (SR vac FX/consume not running)";
                }
            }
            if (method == null)
            {
                TI.Set(false, false);
                VacDriver.ReleaseAll();
                TI.Clear();
                r.Skip("could not make SR's vacpack vacuum from the test (SetTestInput, InControl binding and vacMode reflection all failed)");
                yield break;
            }
            r.Check("SR vacuum running (SC.SR.VacActive)", SC.SR.VacActive, method);

            float t0 = G.Now, nextAim = G.Now + 0.5f;
            bool shot = false, all = false;
            while (G.Now - t0 < 6f)
            {
                if (forced) VacDriver.ForceVacMode(true); // after SR's Update, so the next FixedUpdate sees VAC
                yield return null;
                if (!shot && G.Now - t0 > 0.45f)
                {
                    shot = true;
                    yield return Shot(r, "18_vacpack_pulls_mc_items");
                    if (forced) VacDriver.ForceVacMode(true);
                }
                all = true;
                for (int k = 0; k < drops.Length; k++) if (G.CountOf(drops[k].Id) < G.Count(before, drops[k].Id) + drops[k].Count) all = false;
                if (all) break;
                if (G.Now >= nextAim)
                {
                    nextAim = G.Now + 0.5f;
                    var left = G.ItemEntities(ids, ground, 10f);
                    if (left.Count > 0)
                    {
                        Vector3 avg = Vector3.zero;
                        foreach (var e in left) avg += e.transform.position;
                        G.LookAt(avg / left.Count + Vector3.up * 0.1f);
                    }
                }
            }
            float took = G.Now - t0;
            TI.Set(false, false);
            VacDriver.ReleaseBinding();
            yield return G.WaitFrames(2);
            VacDriver.ReleaseAll();
            TI.Clear();
            string gains = G.Gains(before, G.InvCounts());
            int leftOver = G.ItemEntities(ids, ground, 12f).Count;
            r.Check("Minecraft items vacuumed into the Minecraft inventory", all,
                (gains.Length > 0 ? gains : "nothing gained") + " in " + G.F1(took) + " s; " + leftOver + " item entit" + (leftOver == 1 ? "y" : "ies") + " left; via " + method);
        }

        // ================================================================== 19
        private IEnumerator DropKey(StepResult r)
        {
            if (!InteractPrereqs(r, clearEntities: true)) yield break;
            yield return Ready(r, "survival");
            const string grass = "minecraft:grass_block";
            if (!r.Check("grass block selected", G.SelectItem(grass, 4), G.HotbarText())) yield break;
            yield return G.Wait(0.6f);
            KeyCode key = SC.Input.DropKey;
            if (key == KeyCode.None) { r.Skip("no drop key configured ([Core] DropKey = None)"); yield break; }
            G.SetLook(12f, G.YawOf(G.FlatLook));
            yield return G.WaitFrames(3);
            var known = new HashSet<int>();
            foreach (var e in G.ItemEntities(grass, G.Eye, 15f)) known.Add(e.GetInstanceID());
            int n0 = G.CountOf(grass);
            r.Check("drop key pressed (IInputGate.SimulateKeyDown(" + key + "))", TI.KeyDown(key));
            yield return G.WaitFrames(3);
            yield return G.Wait(0.12f);
            int n1 = G.CountOf(grass);
            r.Check("exactly one grass block left the inventory (no double drop by Hud + Blocks)", n1 == n0 - 1, n0 + " -> " + n1);
            int thrown = 0;
            Vector3 at = Vector3.zero;
            foreach (var e in G.ItemEntities(grass, G.Eye, 15f))
                if (!known.Contains(e.GetInstanceID())) { thrown++; at = e.transform.position; }
            r.Check("a grass block item entity was thrown", thrown > 0, thrown > 0 ? thrown + " new, at " + at.ToString("F1") : "none found");
            yield return G.Wait(0.15f);
            yield return Shot(r, "19_drop_item");
        }
    }
}
