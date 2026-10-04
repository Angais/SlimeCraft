using System;
using System.Collections.Generic;
using UnityEngine;

namespace SlimeCraft.BlocksMod
{
    /// <summary>
    /// Block behaviours that act on the SR player / world: slime-block bounce, honey slowdown, footstep and fall
    /// sounds on our blocks, and gravity blocks (sand/gravel/red sand) that turn into falling-block entities.
    /// </summary>
    internal sealed class SpecialBlocks
    {
        private readonly BlockWorld world;

        // landing / bounce state
        private bool wasGrounded = true;
        private float minAirVelY;
        private float fallTopY;
        private bool airborne;

        // footsteps: one step sound for every StrideLength blocks walked on the ground
        private const float StrideLength = 1f / 0.6f;
        private float walked, nextStepAt = StrideLength;
        private Vector3 lastFeet;
        private bool haveLastFeet;

        private float tickAcc;

        // gravity block queue (MC schedules a tick 2 ticks after a neighbour update)
        private struct Pending { public Vector3Int Pos; public float Due; }
        private readonly List<Pending> gravityQueue = new List<Pending>();
        private readonly HashSet<Vector3Int> queued = new HashSet<Vector3Int>();
        private static readonly RaycastHit[] hits = new RaycastHit[16];

        public SpecialBlocks(BlockWorld world)
        {
            this.world = world;
            world.BlockChanged += OnBlockChanged;
        }

        public void Reset()
        {
            gravityQueue.Clear();
            queued.Clear();
            wasGrounded = true; airborne = false; minAirVelY = 0f;
            haveLastFeet = false; walked = 0f; nextStepAt = StrideLength;
        }

        /// <summary>
        /// Minecraft sneak (shared key [Core] SneakKey). Read ONLY through SC.Input.SneakHeld so the automated test's
        /// IInputGate.SetTestInput(..., sneakHeld) drives it too (false while a screen/SR UI holds the input).
        /// </summary>
        public static bool Sneaking
        {
            get
            {
                var input = SC.Input;
                try { return input != null && input.SneakHeld; }
                catch (Exception e) { RateLog.Error("SneakHeld", e); return false; }
            }
        }

        private void OnBlockChanged(Vector3Int pos, string oldId, string newId)
        {
            var nd = newId != null ? Content.Block(newId) : null;
            if (nd != null && nd.Gravity) Enqueue(pos);
            if (newId == null) Enqueue(pos + Vector3Int.up);
        }

        private void Enqueue(Vector3Int p)
        {
            if (!queued.Add(p)) return;
            gravityQueue.Add(new Pending { Pos = p, Due = Time.time + 0.1f });
        }

        /// <summary>The block (ours) the player is standing on, checking the feet center then the player's footprint.</summary>
        public BlockDef BlockUnderFeet(Vector3 feet, out Vector3Int cell)
        {
            float y = feet.y - 0.2f;
            cell = Vector3Int.FloorToInt(new Vector3(feet.x, y, feet.z));
            var d = world.GetDef(cell);
            if (d != null) return d;
            const float r = 0.3f;
            for (int i = 0; i < 4; i++)
            {
                float ox = (i & 1) == 0 ? -r : r, oz = (i & 2) == 0 ? -r : r;
                var c = Vector3Int.FloorToInt(new Vector3(feet.x + ox, y, feet.z + oz));
                d = world.GetDef(c);
                if (d != null) { cell = c; return d; }
            }
            return null;
        }

        public void Update()
        {
            var sr = SC.SR;
            if (sr == null || !sr.InGame) return;
            if (!sr.IsPaused) PlayerEffects(sr);
            ProcessGravity();
        }

        private void PlayerEffects(ISRBridge sr)
        {
            if (world.Count == 0) { wasGrounded = sr.PlayerGrounded; haveLastFeet = false; return; }
            bool grounded = sr.PlayerGrounded;
            Vector3 v = sr.PlayerVelocity;
            Vector3 feet = sr.PlayerFeet;
            float dt = Time.deltaTime;

            if (!grounded)
            {
                if (!airborne) { airborne = true; fallTopY = feet.y; minAirVelY = 0f; }
                minAirVelY = Mathf.Min(minAirVelY, v.y);
                fallTopY = Mathf.Max(fallTopY, feet.y);
            }
            else if (!wasGrounded || airborne)
            {
                // just landed
                var below = BlockUnderFeet(feet, out var cell);
                float fallDist = fallTopY - feet.y;
                if (below != null)
                {
                    if (below.Bouncy && !Sneaking)
                    {
                        if (minAirVelY < -2f)
                            sr.AddPlayerVelocity(Vector3.up * (-minAirVelY * (BlocksConfig.SlimeBounce?.Value ?? 0.95f)));
                    }
                    else if (fallDist > 3f && (BlocksConfig.StepSounds?.Value ?? true))
                    {
                        // a long fall plays the small or big landing sound plus the block's fall sound (SR has no fall damage)
                        int dmg = Mathf.CeilToInt(fallDist - 3f);
                        SC.Audio?.Play(dmg > 4 ? "entity.player.big_fall" : "entity.player.small_fall", feet, 1f, 1f);
                        BlockSounds.Fall(below, BlockWorld.Center(cell));
                    }
                }
                airborne = false;
                minAirVelY = 0f;
            }
            wasGrounded = grounded;

            // MC ticks (20 Hz) for velocity effects
            tickAcc += dt;
            int ticks = 0;
            while (tickAcc >= 0.05f && ticks < 4) { tickAcc -= 0.05f; ticks++; }
            if (tickAcc > 0.2f) tickAcc = 0f;

            if (grounded && ticks > 0)
            {
                var under = BlockUnderFeet(feet, out _);
                if (under != null && under.Sticky)
                {
                    var h = new Vector3(v.x, 0f, v.z);
                    if (h.sqrMagnitude > 0.04f)
                    {
                        float k = BlocksConfig.HoneySlowdown?.Value ?? 0.2f;
                        sr.AddPlayerVelocity(-h * (k * ticks));
                    }
                }
            }

            // footsteps
            if (!haveLastFeet) { lastFeet = feet; haveLastFeet = true; }
            var delta = feet - lastFeet;
            lastFeet = feet;
            float horiz = new Vector2(delta.x, delta.z).magnitude;
            if (horiz > 2f) horiz = 0f; // teleport
            if (grounded && horiz > 0f)
            {
                walked += horiz;
                if (walked >= nextStepAt)
                {
                    nextStepAt = (Mathf.Floor(walked / StrideLength) + 1f) * StrideLength;
                    if (BlocksConfig.StepSounds?.Value ?? true)
                    {
                        var under = BlockUnderFeet(feet, out var c);
                        if (under != null) BlockSounds.Step(under, new Vector3(feet.x, c.y + 1f, feet.z));
                    }
                }
            }
        }

        // ------------------------------------------------------------------ gravity blocks
        private void ProcessGravity()
        {
            if (gravityQueue.Count == 0) return;
            float now = Time.time;
            int processed = 0;
            for (int i = 0; i < gravityQueue.Count && processed < 64; i++)
            {
                var p = gravityQueue[i];
                if (p.Due > now) continue;
                gravityQueue.RemoveAt(i); i--;
                queued.Remove(p.Pos);
                processed++;
                try { TryFall(p.Pos); }
                catch (Exception e) { RateLog.Error("gravity block", e); }
            }
        }

        private void TryFall(Vector3Int pos)
        {
            var def = world.GetDef(pos);
            if (def == null || !def.Gravity) return;
            var ent = SC.Entities;
            if (ent == null) return;
            if (world.IsSolid(pos + Vector3Int.down)) return;
            if (SupportedBySR(pos)) return;
            string id = def.Id;
            world.SetBlock(pos, null);
            world.RebuildAround(pos);
            ent.SpawnFallingBlock(id, pos);
        }

        /// <summary>Is Slime Rancher geometry directly under/in the bottom of this cell? (5 short downward rays.)</summary>
        private bool SupportedBySR(Vector3Int p)
        {
            var sr = SC.SR;
            if (sr == null) return true;
            int mask = sr.WorldMask;
            for (int i = 0; i < 5; i++)
            {
                float ox = 0.5f, oz = 0.5f;
                if (i > 0) { ox = (i & 1) != 0 ? 0.1f : 0.9f; oz = (i & 2) != 0 ? 0.1f : 0.9f; }
                // from 0.5 above the cell top down to 0.1 below its bottom
                var origin = new Vector3(p.x + ox, p.y + 1.5f, p.z + oz);
                int n = Physics.RaycastNonAlloc(new Ray(origin, Vector3.down), hits, 1.6f, mask, QueryTriggerInteraction.Ignore);
                for (int k = 0; k < n; k++)
                {
                    var c = hits[k].collider;
                    if (c == null || world.IsBlockCollider(c) || c.attachedRigidbody != null) continue;
                    return true;
                }
            }
            return false;
        }
    }
}
