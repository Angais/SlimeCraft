using System;
using System.Collections.Generic;
using UnityEngine;

namespace SlimeCraft.FP
{
    /// <summary>
    /// Mesh + material lookup for the held item. Block items use our own Minecraft-convention cube (BlockItemMesh) with the
    /// block atlas materials (Emissive for light-emitting blocks); other items come from SC.ItemVisuals (shared with the mod);
    /// the bow uses meshes built here from item/bow and item/bow_pulling_0..2 so the draw stages swap like Minecraft's
    /// range_dispatch on minecraft:use_duration. Everything is cached; failed lookups are retried once per second.
    /// </summary>
    internal sealed class HeldItemVisuals
    {
        private sealed class Entry
        {
            public Mesh Mesh;
            public Material Material;
            public Quaternion Correction = Quaternion.identity;
            public float RetryAt;
            public bool Ok;
        }

        private static readonly string[] BowTextures = { "item/bow", "item/bow_pulling_0", "item/bow_pulling_1", "item/bow_pulling_2" };

        private readonly FpConfig cfg;
        private readonly Dictionary<string, Entry> items = new Dictionary<string, Entry>();
        private readonly Entry[] bow = new Entry[4];
        private readonly Dictionary<Texture2D, Material> ownMaterials = new Dictionary<Texture2D, Material>();
        private readonly List<Mesh> ownMeshes = new List<Mesh>();
        /// <summary>Held block cubes by block id (atlas rects never move, so they survive world reloads).</summary>
        private readonly Dictionary<string, Mesh> blockMeshes = new Dictionary<string, Mesh>();

        public HeldItemVisuals(FpConfig cfg) { this.cfg = cfg; }

        /// <summary>bowStage: -1 = normal model; 0..2 = bow_pulling_N (only used for bow items).</summary>
        public bool TryGet(string itemId, ItemDef def, int bowStage, out Mesh mesh, out Material material, out Quaternion correction)
        {
            mesh = null; material = null; correction = Quaternion.identity;
            if (string.IsNullOrEmpty(itemId)) return false;
            Entry e;
            if (def != null && def.Special == "bow")
            {
                int i = bowStage < 0 ? 0 : Mathf.Clamp(bowStage + 1, 1, 3);
                e = bow[i];
                if (e == null) { e = new Entry(); bow[i] = e; }
                if (!e.Ok && Time.realtimeSinceStartup >= e.RetryAt) LoadOwnSprite(e, BowTextures[i]);
            }
            else
            {
                if (!items.TryGetValue(itemId, out e)) { e = new Entry(); items[itemId] = e; }
                if (!e.Ok && Time.realtimeSinceStartup >= e.RetryAt) Load(e, itemId, def);
            }
            if (!e.Ok) return false;
            mesh = e.Mesh; material = e.Material; correction = e.Correction;
            return mesh != null && material != null;
        }

        private void Load(Entry e, string itemId, ItemDef def)
        {
            e.RetryAt = Time.realtimeSinceStartup + 1f;
            try
            {
                var iv = SC.ItemVisuals;
                bool isBlock = def != null ? def.IsBlock : (iv != null && iv.IsBlockItem(itemId));
                // Block items: our own cube in Minecraft's block-model face convention (see BlockItemMesh).
                if (isBlock && LoadBlockCube(e, itemId, def)) return;
                if (iv != null)
                {
                    var m = iv.GetItemMesh(itemId);
                    var mat = iv.GetItemMaterial(itemId);
                    if (m != null && mat != null)
                    {
                        e.Mesh = m; e.Material = mat;
                        e.Correction = (!isBlock && cfg.AutoOrientItemMeshes.Value) ? DetectSpriteOrientation(m, itemId) : Quaternion.identity;
                        e.Ok = true;
                        return;
                    }
                }
                // Fallback for flat items: extrude the item texture ourselves.
                if (!isBlock && def != null && !string.IsNullOrEmpty(def.Texture)) LoadOwnSprite(e, def.Texture);
            }
            catch (Exception ex)
            {
                FpLog.Error("Item visuals " + itemId, ex, 30f);
            }
        }

        /// <summary>
        /// Minecraft-convention cube with atlas UVs + the block's material: light-emitting blocks (BlockDef.Light &gt; 0)
        /// use the atlas' full-bright Emissive material (they glow in the hand like the placed block does at night),
        /// translucent blocks the alpha-blended one, everything else the lit cutout one.
        /// </summary>
        private bool LoadBlockCube(Entry e, string itemId, ItemDef def)
        {
            var assets = SC.Assets;
            if (assets == null || !assets.Ready) return false;
            BlockDef block = Content.Block(def != null && !string.IsNullOrEmpty(def.BlockId) ? def.BlockId : itemId);
            if (block == null) return false;
            IBlockAtlas atlas = assets.BlockAtlas;
            if (atlas == null) return false;

            Mesh mesh;
            if (!blockMeshes.TryGetValue(block.Id, out mesh) || mesh == null)
            {
                mesh = BlockItemMesh.Build(block, atlas);
                if (mesh == null) return false;
                blockMeshes[block.Id] = mesh;
            }

            string kind;
            Material mat = BlockMaterial(block, atlas, out kind);
            if (mat == null)
            {
                var iv = SC.ItemVisuals;
                if (iv != null) { mat = iv.GetItemMaterial(itemId); kind = "IItemVisuals"; }
            }
            if (mat == null) return false;

            e.Mesh = mesh; e.Material = mat; e.Correction = Quaternion.identity; e.Ok = true;
            FpLog.Info("Held block " + itemId + ": Minecraft-convention cube, " + kind + " material '" + mat.name + "' (" + (mat.shader != null ? mat.shader.name : "?") + ")");
            return true;
        }

        private static Material BlockMaterial(BlockDef block, IBlockAtlas atlas, out string kind)
        {
            kind = "?";
            try
            {
                if (block.Light > 0)
                {
                    Material em = null;
                    try { em = atlas.Emissive; }
                    catch (Exception ex) { FpLog.Error("BlockAtlas.Emissive", ex, 300f); }
                    if (em != null) { kind = "emissive"; return em; }
                }
                if (block.Layer == RenderLayer.Translucent && atlas.Translucent != null) { kind = "translucent"; return atlas.Translucent; }
                kind = "cutout";
                return atlas.Cutout;
            }
            catch (Exception ex)
            {
                FpLog.Error("Block material " + block.Id, ex, 60f);
                return null;
            }
        }

        private void LoadOwnSprite(Entry e, string texturePath)
        {
            e.RetryAt = Time.realtimeSinceStartup + 1f;
            var assets = SC.Assets;
            if (assets == null || !assets.Ready) return;
            try
            {
                var tex = assets.GetTexture(texturePath);
                if (tex == null) return;
                var mat = LitMaterial(tex);
                if (mat == null) return;
                var mesh = SpriteMeshBuilder.Build(tex, "SlimeCraft_Held_" + texturePath);
                if (mesh == null) return;
                ownMeshes.Add(mesh);
                e.Mesh = mesh; e.Material = mat; e.Correction = Quaternion.identity; e.Ok = true;
            }
            catch (Exception ex)
            {
                FpLog.Error("Sprite mesh " + texturePath, ex, 30f);
            }
        }

        /// <summary>Lit cutout material for one of our own textures (via IItemVisuals, fallback shader otherwise).</summary>
        public Material LitMaterial(Texture2D tex)
        {
            if (tex == null) return null;
            if (ownMaterials.TryGetValue(tex, out var m) && m != null) return m;
            m = CreateLit(tex);
            if (m != null) ownMaterials[tex] = m;
            return m;
        }

        public static Material CreateLit(Texture2D tex)
        {
            Material m = null;
            var iv = SC.ItemVisuals;
            if (iv != null)
            {
                try { m = iv.CreateLitMaterial(tex, true); } catch (Exception e) { FpLog.Error("CreateLitMaterial", e, 60f); }
            }
            if (m != null) return m;
            // Fallback (no Core item visuals): shaders that are always present in SR builds.
            string[] names = { "Legacy Shaders/Transparent/Cutout/Diffuse", "Unlit/Transparent Cutout", "Standard" };
            foreach (var n in names)
            {
                var sh = Shader.Find(n);
                if (sh == null) continue;
                m = new Material(sh) { name = "SlimeCraft_FP_" + tex.name, mainTexture = tex };
                if (m.HasProperty("_Cutoff")) m.SetFloat("_Cutoff", 0.5f);
                FpLog.WarnLimited("fallbackShader", "IItemVisuals unavailable, using fallback shader " + n + " for first-person materials", 300f);
                return m;
            }
            return null;
        }

        /// <summary>
        /// Finds how the texture lies on a flat item mesh and returns the rotation that makes its upright front face look
        /// towards -Z (Minecraft's "south" face after the Z mirror), so the item is shown like Minecraft's regardless of
        /// how the mesh was authored. Analyses the largest triangle whose normal is ±Z (UV gradients ∂u/∂x and ∂v/∂y).
        /// </summary>
        private static Quaternion DetectSpriteOrientation(Mesh m, string itemId)
        {
            try
            {
                if (m == null || !m.isReadable) return Quaternion.identity;
                Vector3[] v = m.vertices;
                Vector2[] uv = m.uv;
                int[] tris = m.triangles;
                if (uv == null || uv.Length != v.Length || tris == null) return Quaternion.identity;
                float best = 0f; int bi = -1; float side = 0f;
                for (int t = 0; t + 2 < tris.Length; t += 3)
                {
                    Vector3 cr = Vector3.Cross(v[tris[t + 1]] - v[tris[t]], v[tris[t + 2]] - v[tris[t]]);
                    float area = cr.magnitude;
                    if (area < 1e-7f) continue;
                    float nz = cr.z / area;
                    if (Mathf.Abs(nz) < 0.9f) continue;
                    if (area > best) { best = area; bi = t; side = Mathf.Sign(nz); }
                }
                if (bi < 0) return Quaternion.identity;
                Vector3 p0 = v[tris[bi]], p1 = v[tris[bi + 1]], p2 = v[tris[bi + 2]];
                Vector2 t0 = uv[tris[bi]], t1 = uv[tris[bi + 1]], t2 = uv[tris[bi + 2]];
                float d1x = p1.x - p0.x, d1y = p1.y - p0.y, d2x = p2.x - p0.x, d2y = p2.y - p0.y;
                float det = d1x * d2y - d1y * d2x;
                if (Mathf.Abs(det) < 1e-9f) return Quaternion.identity;
                float du1 = t1.x - t0.x, du2 = t2.x - t0.x, dv1 = t1.y - t0.y, dv2 = t2.y - t0.y;
                float gux = (du1 * d2y - du2 * d1y) / det; // ∂u/∂x
                float gvy = (d1x * dv2 - d2x * dv1) / det; // ∂v/∂y
                Quaternion c0 = Quaternion.identity;
                if (side > 0f) { c0 = Quaternion.AngleAxis(180f, Vector3.up); gux = -gux; }
                Quaternion c1;
                if (gux >= 0f && gvy >= 0f) c1 = Quaternion.identity;
                else if (gux < 0f && gvy >= 0f) c1 = Quaternion.AngleAxis(180f, Vector3.up);
                else if (gux < 0f && gvy < 0f) c1 = Quaternion.AngleAxis(180f, Vector3.forward);
                else c1 = Quaternion.AngleAxis(180f, Vector3.right);
                Quaternion c = c1 * c0;
                if (Quaternion.Angle(c, Quaternion.identity) > 1f)
                    FpLog.Info("Item mesh of " + itemId + " re-oriented by " + c.eulerAngles + " to match Minecraft's sprite facing");
                return c;
            }
            catch (Exception e)
            {
                FpLog.Error("DetectSpriteOrientation " + itemId, e, 60f);
                return Quaternion.identity;
            }
        }

        /// <summary>Drops cached lookups (meshes/materials owned by IItemVisuals are not destroyed).</summary>
        public void ClearLookups()
        {
            items.Clear();
        }
    }
}
