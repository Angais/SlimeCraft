using System;
using System.Collections.Generic;
using UnityEngine;

namespace SlimeCraft.Core
{
    /// <summary><see cref="IItemVisuals"/>: cached GUI icons, held/dropped meshes and materials for every item.</summary>
    internal sealed class ItemVisualsService : IItemVisuals
    {
        private readonly McAssets assets;
        private readonly Dictionary<string, Texture2D> icons = new Dictionary<string, Texture2D>(StringComparer.Ordinal);
        private readonly Dictionary<string, Mesh> meshes = new Dictionary<string, Mesh>(StringComparer.Ordinal);
        private readonly Dictionary<Texture, Material> litByTexture = new Dictionary<Texture, Material>();
        public readonly VacpackIcon Vacpack = new VacpackIcon();

        public ItemVisualsService(McAssets assets) { this.assets = assets; }

        /// <summary>World loaded: re-render the vacpack icon from the live SR model shortly after (same Texture2D object).</summary>
        public void OnWorldLoaded() => Vacpack.OnWorldLoaded();

        /// <summary>Per frame from CoreModule (cheap unless a vacpack icon render is pending).</summary>
        public void Tick()
        {
            if (Vacpack.Tick()) meshes.Remove(Content.Vacpack); // rebuild its extruded mesh from the new pixels
        }

        public bool IsBlockItem(string itemId)
        {
            var d = Content.Item(itemId);
            return d != null && d.IsBlock && Content.Block(d.BlockId ?? d.Id) != null;
        }

        private static string Key(string itemId) => itemId == null ? "" : Content.Norm(itemId);

        public Texture2D GetIcon(string itemId)
        {
            string id = Key(itemId);
            if (icons.TryGetValue(id, out var t) && t != null) return t;
            try
            {
                var def = Content.Item(id);
                if (def == null) t = assets.MissingTexture;
                else if (def.Special == "vacpack" || id == Content.Vacpack) t = Vacpack.Texture;
                else if (IsBlockItem(id))
                {
                    var atlas = assets.AtlasImpl; // also builds the spec cache
                    t = atlas != null ? IsoIconRenderer.Render(Content.Block(def.BlockId ?? def.Id), atlas.Specs, CoreConfig.IconSize.Value) : assets.MissingTexture;
                }
                else t = assets.GetTexture(def.Texture);
            }
            catch (Exception e)
            {
                CoreLog.Rate("icon " + id, e, 60f);
                t = assets.MissingTexture;
            }
            if (assets.Ready || id == Content.Vacpack) icons[id] = t;
            return t;
        }

        public Mesh GetItemMesh(string itemId)
        {
            string id = Key(itemId);
            if (meshes.TryGetValue(id, out var m) && m != null) return m;
            try
            {
                var def = Content.Item(id);
                if (def != null && IsBlockItem(id)) m = ItemMeshes.BlockCube(Content.Block(def.BlockId ?? def.Id), assets.BlockAtlas);
                else m = ItemMeshes.Extruded(GetIcon(id), id);
            }
            catch (Exception e)
            {
                CoreLog.Rate("mesh " + id, e, 60f);
                m = ItemMeshes.Extruded(assets.MissingTexture, "missing");
            }
            if (assets.Ready) meshes[id] = m;
            return m;
        }

        public Material GetItemMaterial(string itemId)
        {
            string id = Key(itemId);
            try
            {
                var def = Content.Item(id);
                if (def != null && IsBlockItem(id))
                {
                    var atlas = assets.BlockAtlas;
                    var b = Content.Block(def.BlockId ?? def.Id);
                    return b.Layer == RenderLayer.Translucent ? atlas.Translucent : atlas.Cutout;
                }
                return SharedItemMaterial(GetIcon(id));
            }
            catch (Exception e)
            {
                CoreLog.Rate("material " + id, e, 60f);
                return SharedItemMaterial(assets.MissingTexture);
            }
        }

        /// <summary>One shared lit cutout material per item texture (items of the same texture batch together).</summary>
        private Material SharedItemMaterial(Texture2D texture)
        {
            if (texture != null && litByTexture.TryGetValue(texture, out var cached) && cached != null) return cached;
            var m = MaterialFactory.CreateLit(texture, 0.5f, "item_" + (texture != null ? texture.name : "white"));
            if (texture != null) litByTexture[texture] = m;
            return m;
        }

        /// <summary>A NEW material every call (callers may tint it, e.g. mob hurt flash).</summary>
        public Material CreateLitMaterial(Texture2D texture, bool cutout = true) =>
            MaterialFactory.CreateLit(texture, cutout ? 0.5f : 0f, texture != null ? texture.name : "white");

        public Material CreateUnlitMaterial(Texture2D texture, bool transparent) =>
            MaterialFactory.CreateUnlit(texture, transparent, (texture != null ? texture.name : "white") + (transparent ? "_unlitT" : "_unlit"));

        public Material CreateColorMaterial(Color color) => MaterialFactory.CreateColor(color, "color");
    }
}
