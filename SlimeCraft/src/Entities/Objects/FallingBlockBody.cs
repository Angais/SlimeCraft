using System;
using UnityEngine;

namespace SlimeCraft.Entities
{
    /// <summary>
    /// A gravity block (sand, gravel) in motion: a 0.98 cube that falls (gravity 0.04 b/t², drag 0.98) and on landing re-places its
    /// block at the cell it occupies (via SC.Blocks.SetBlock) or drops as an item if that cell is taken.
    /// </summary>
    internal sealed class FallingBlockBody : McEntity
    {
        public string BlockId;
        private float age;
        private float restTime;
        private bool landedOnce;

        public override float BBWidth => 0.98f;
        public override float BBHeight => 0.98f;

        public static FallingBlockBody Spawn(string blockId, Vector3Int from)
        {
            var go = new GameObject("MC FallingBlock " + blockId);
            go.layer = ELayers.Mob;
            go.transform.position = new Vector3(from.x + 0.5f, from.y, from.z + 0.5f);
            var fb = go.AddComponent<FallingBlockBody>();
            fb.BlockId = Content.Norm(blockId);
            fb.Init();
            return fb;
        }

        private void Init()
        {
            SetupBody(1f);
            var col = AddChildCollider<BoxCollider>("col", ELayers.Mob);
            col.size = new Vector3(0.98f, 0.98f, 0.98f);
            col.center = new Vector3(0f, 0.49f, 0f);
            col.sharedMaterial = Frictionless;
            Mesh mesh = null; Material mat = null;
            try { mesh = SC.ItemVisuals?.GetItemMesh(BlockId); mat = SC.ItemVisuals?.GetItemMaterial(BlockId); }
            catch (Exception e) { ELog.Error("FallingBlock visuals", e); }
            if (mesh == null) mesh = EMat.UnitCube();
            var mgo = new GameObject("cube");
            mgo.layer = gameObject.layer;
            mgo.transform.SetParent(transform, false);
            mgo.transform.localPosition = new Vector3(0f, 0.5f, 0f);
            mgo.AddComponent<MeshFilter>().sharedMesh = mesh;
            mgo.AddComponent<MeshRenderer>().sharedMaterial = mat != null ? mat : EMat.Lit("fb_fallback", EMat.Tex("block/sand"));
        }

        private void FixedUpdate()
        {
            if (Removed || Body == null || Body.isKinematic) return;
            try
            {
                float dt = Time.fixedDeltaTime;
                age += dt;
                var v = Body.velocity;
                v.y -= 0.04f * Mc.Bpt2ToMs2 * dt;
                v *= Mc.PerTick(0.98f, dt);
                bool grounded = ProbeGround(0.49f, 0.04f, out var gh);
                if (grounded)
                {
                    float f = Mc.PerTick(0.7f, dt);
                    v.x *= f; v.z *= f;
                    if (v.y < 0f) v.y = 0f;
                }
                Body.velocity = v;

                if (grounded && age > 0.1f && v.sqrMagnitude < 0.25f) restTime += dt; else restTime = 0f;
                // resting on a mob / item / SR actor instead of the world: break into an item (Minecraft: lands on a non-full block)
                if (restTime > 0.05f && !landedOnce && gh.collider != null
                    && (gh.collider.GetComponentInParent<McEntity>() != null || (SC.SR != null && SC.SR.GetSRActorId(gh.collider.gameObject) != null)))
                { landedOnce = true; DropAsItem(); return; }
                if (restTime > 0.05f && !landedOnce) Land();
                else if (age > 30f || (SC.SR != null && SC.SR.InGame && transform.position.y < SC.SR.PlayerFeet.y - 96f)) DropAsItem();
            }
            catch (Exception e) { ELog.Error("FallingBlockBody.FixedUpdate", e); }
        }

        private void Land()
        {
            landedOnce = true;
            var p = transform.position;
            var cell = new Vector3Int(Mathf.FloorToInt(p.x), Mathf.RoundToInt(p.y), Mathf.FloorToInt(p.z));
            bool placed = false;
            try
            {
                var pb = SC.SR != null ? SC.SR.PlayerBounds : new Bounds();
                var cellBounds = new Bounds(new Vector3(cell.x + 0.5f, cell.y + 0.5f, cell.z + 0.5f), Vector3.one * 0.98f);
                bool playerInside = SC.SR != null && SC.SR.InGame && pb.Intersects(cellBounds);
                if (SC.Blocks != null && SC.Blocks.GetBlock(cell) == null && !playerInside)
                {
                    Discard(); // remove our collider first so the new block does not overlap us
                    placed = SC.Blocks.SetBlock(cell, BlockId, BlockFacing.North, false);
                    if (!placed) EntitiesModule.SpawnItemStatic(new ItemStack(BlockId, 1), p + Vector3.up * 0.5f, Vector3.zero, 0.5f);
                    return;
                }
            }
            catch (Exception e) { ELog.Error("FallingBlockBody.Land", e); }
            if (!placed) DropAsItem();
        }

        private void DropAsItem()
        {
            if (Removed) return;
            var p = transform.position + Vector3.up * 0.5f;
            Discard();
            var def = Content.Block(BlockId);
            string drop = def == null ? BlockId : (def.Drop == null ? def.Id : def.Drop);
            if (!string.IsNullOrEmpty(drop)) EntitiesModule.SpawnItemStatic(new ItemStack(drop, Mathf.Max(1, def != null ? def.DropCount : 1)), p, Vector3.zero, 0.5f);
        }

        public override void OnExplosion(Vector3 knockbackMs, float damage, GameObject source)
        {
            if (Body != null && !Body.isKinematic) Body.velocity += knockbackMs;
        }
    }
}
