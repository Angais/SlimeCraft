using System;
using System.Collections.Generic;
using UnityEngine;

namespace SlimeCraft.FP
{
    /// <summary>One display transform of a Minecraft item model (rotation in degrees, translation already in blocks).</summary>
    internal struct ItemDisplay
    {
        public Vector3 Rotation;
        public Vector3 Translation;
        public Vector3 Scale;

        public ItemDisplay(Vector3 rotation, Vector3 translationPixels, Vector3 scale)
        {
            Rotation = rotation; Translation = translationPixels / 16f; Scale = scale;
        }

        // Typical values of the standard item models, used only when the jar can't be read.
        public static readonly ItemDisplay Generated = new ItemDisplay(new Vector3(0f, -90f, 25f), new Vector3(1.13f, 3.2f, 1.13f), new Vector3(0.68f, 0.68f, 0.68f)); // item/generated
        public static readonly ItemDisplay Handheld = new ItemDisplay(new Vector3(0f, -90f, 25f), new Vector3(1.13f, 3.2f, 1.13f), new Vector3(0.68f, 0.68f, 0.68f));  // item/handheld
        public static readonly ItemDisplay Block = new ItemDisplay(new Vector3(0f, 45f, 0f), Vector3.zero, new Vector3(0.4f, 0.4f, 0.4f));                              // block/block
    }

    /// <summary>Per item client properties from the jar's item definition + resolved first-person transform.</summary>
    internal sealed class ItemModelInfo
    {
        public ItemDisplay FirstPersonRight;
        /// <summary>Item definition field "swap_animation_scale": speed of the lower/raise animation (default 1).</summary>
        public float SwapAnimationScale = 1f;
        /// <summary>Item definition field "hand_animation_on_swap": whether swapping plays the animation (default true).</summary>
        public bool HandAnimationOnSwap = true;
        public string ModelId;
        public bool FromJar;
    }

    /// <summary>
    /// Resolves the FIRST_PERSON_RIGHT_HAND display transform of an item from the user's Minecraft jar:
    /// assets/minecraft/items/&lt;id&gt;.json → model id → models/&lt;model&gt;.json → parent chain until a model defines
    /// display.firstperson_righthand (e.g. tools/swords/sticks → item/handheld, others → item/generated, blocks →
    /// block/block). Translations in the json are model pixels (1/16 block); the model format limits them to
    /// ±5 blocks and scales to ±4.
    /// </summary>
    internal static class ItemDisplayResolver
    {
        private static readonly Dictionary<string, ItemModelInfo> cache = new Dictionary<string, ItemModelInfo>();
        private static readonly Dictionary<string, ItemModelInfo> fallbackCache = new Dictionary<string, ItemModelInfo>();
        private static readonly Dictionary<string, JsonNode> modelJson = new Dictionary<string, JsonNode>();
        private static readonly ItemModelInfo emptyHand = new ItemModelInfo { FirstPersonRight = ItemDisplay.Generated };

        public static ItemModelInfo Get(string itemId)
        {
            if (string.IsNullOrEmpty(itemId)) return emptyHand;
            if (cache.TryGetValue(itemId, out var info)) return info;
            var assets = SC.Assets;
            bool canRead = assets != null && assets.Ready;
            if (!canRead)
            {
                // jar not available (yet): built-in defaults, cached separately and retried once the jar is ready
                if (!fallbackCache.TryGetValue(itemId, out info)) { info = Fallback(itemId); fallbackCache[itemId] = info; }
                return info;
            }
            info = ResolveFromJar(assets, itemId) ?? Fallback(itemId);
            cache[itemId] = info;
            return info;
        }

        private static ItemModelInfo Fallback(string itemId)
        {
            var def = Content.Item(itemId);
            var info = new ItemModelInfo();
            if (def != null && def.IsBlock) info.FirstPersonRight = ItemDisplay.Block;
            else if (def != null && (def.Tool != ToolType.None || def.Id == "minecraft:stick")) info.FirstPersonRight = ItemDisplay.Handheld;
            else info.FirstPersonRight = ItemDisplay.Generated;
            return info;
        }

        private static ItemModelInfo ResolveFromJar(IMcAssets assets, string itemId)
        {
            try
            {
                int colon = itemId.IndexOf(':');
                string ns = colon >= 0 ? itemId.Substring(0, colon) : "minecraft";
                string path = colon >= 0 ? itemId.Substring(colon + 1) : itemId;
                if (ns != "minecraft") return null;

                JsonNode itemDef = assets.ReadJarJson("assets/minecraft/items/" + path + ".json");
                if (itemDef == null || !itemDef.IsObject) return null;

                var info = new ItemModelInfo();
                if (itemDef.Has("hand_animation_on_swap")) info.HandAnimationOnSwap = itemDef["hand_animation_on_swap"].AsBool(true);
                if (itemDef.Has("swap_animation_scale")) info.SwapAnimationScale = itemDef["swap_animation_scale"].AsFloat(1f);

                string modelId = FindModelId(itemDef["model"], 0);
                info.ModelId = modelId;
                ItemDisplay disp;
                if (modelId != null && FindDisplay(assets, modelId, out disp))
                {
                    info.FirstPersonRight = disp;
                    info.FromJar = true;
                }
                else
                {
                    info.FirstPersonRight = Fallback(itemId).FirstPersonRight;
                    FpLog.WarnLimited("display:" + itemId, "No firstperson_righthand transform found for " + itemId + " (model " + (modelId ?? "?") + "), using defaults");
                }
                return info;
            }
            catch (Exception e)
            {
                FpLog.Error("ResolveDisplay " + itemId, e, 60f);
                return null;
            }
        }

        /// <summary>Walks an item model definition (model/condition/select/range_dispatch/composite/special) to the default plain model id.</summary>
        private static string FindModelId(JsonNode n, int depth)
        {
            if (n == null || !n.IsObject || depth > 8) return null;
            string type = n["type"].AsString("");
            if (type == "minecraft:model" || type == "model") return n["model"].AsString(null);
            if (type == "minecraft:special" || type == "special") return n["base"].AsString(null);
            string[] branchKeys = { "on_false", "fallback", "on_true" };
            foreach (var k in branchKeys)
            {
                var r = FindModelId(n[k], depth + 1);
                if (r != null) return r;
            }
            var cases = n["cases"];
            if (cases.IsArray && cases.Count > 0) { var r = FindModelId(cases[0]["model"], depth + 1); if (r != null) return r; }
            var entries = n["entries"];
            if (entries.IsArray && entries.Count > 0) { var r = FindModelId(entries[0]["model"], depth + 1); if (r != null) return r; }
            var models = n["models"];
            if (models.IsArray && models.Count > 0) { var r = FindModelId(models[0], depth + 1); if (r != null) return r; }
            return n["model"].IsString ? n["model"].AsString(null) : null;
        }

        private static bool FindDisplay(IMcAssets assets, string modelId, out ItemDisplay disp)
        {
            disp = default(ItemDisplay);
            string id = modelId;
            for (int i = 0; i < 16 && id != null; i++)
            {
                string p = StripNs(id);
                if (p.StartsWith("builtin/")) return false;
                JsonNode model = LoadModel(assets, p);
                if (model == null) return false;
                var t = model["display"]["firstperson_righthand"];
                if (t.IsObject)
                {
                    disp = Parse(t);
                    return true;
                }
                id = model["parent"].AsString(null);
            }
            return false;
        }

        private static JsonNode LoadModel(IMcAssets assets, string path)
        {
            if (modelJson.TryGetValue(path, out var n)) return n;
            n = assets.ReadJarJson("assets/minecraft/models/" + path + ".json");
            modelJson[path] = n;
            return n;
        }

        private static string StripNs(string id)
        {
            int c = id.IndexOf(':');
            return c >= 0 ? id.Substring(c + 1) : id;
        }

        private const float MaxTranslationBlocks = 5f;
        private const float MaxScale = 4f;

        private static ItemDisplay Parse(JsonNode t)
        {
            return new ItemDisplay
            {
                Scale = Limit(Vec(t["scale"], Vector3.one), MaxScale),
                Translation = Limit(Vec(t["translation"], Vector3.zero) / 16f, MaxTranslationBlocks),
                Rotation = Vec(t["rotation"], Vector3.zero),
            };
        }

        /// <summary>Clamps every component of <paramref name="v"/> into [-limit, limit].</summary>
        private static Vector3 Limit(Vector3 v, float limit)
        {
            for (int i = 0; i < 3; i++) v[i] = Mathf.Clamp(v[i], -limit, limit);
            return v;
        }

        private static Vector3 Vec(JsonNode a, Vector3 def)
        {
            if (a == null || !a.IsArray || a.Count != 3) return def;
            return new Vector3(a[0].AsFloat(), a[1].AsFloat(), a[2].AsFloat());
        }
    }
}
