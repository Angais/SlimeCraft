using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SlimeCraft.Testing
{
    /// <summary>
    /// Helpers for the interaction steps (11-20): safe standing spots, item-entity / SR-actor scans around a point,
    /// inventory snapshots and surface descriptions.
    /// </summary>
    internal static partial class G
    {
        /// <summary>Name prefix of Minecraft dropped-item entities (Entities module: "MC Item &lt;id&gt;", scene root objects).</summary>
        public const string ItemEntityPrefix = "MC Item ";

        public static float Now => Time.realtimeSinceStartup;

        public static string F1(float v) => v.ToString("0.0", CultureInfo.InvariantCulture);
        public static string F2(float v) => v.ToString("0.00", CultureInfo.InvariantCulture);

        public static float HorizontalDistance(Vector3 a, Vector3 b)
        {
            a.y = 0f; b.y = 0f;
            return Vector3.Distance(a, b);
        }

        public static Vector3 Horizontal(Vector3 v) { v.y = 0f; return v; }

        public static float YawOf(Vector3 dir) => Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg;

        // ------------------------------------------------------------------ standing spots
        /// <summary>
        /// A place where the player can stand near <paramref name="near"/>: walkable ground (normal.y &gt;= 0.7) found from
        /// 3 m above, with a free 1.8 m capsule (no SR geometry, no blocks). Tries the point itself, then two rings.
        /// </summary>
        public static bool FindStandSpot(Vector3 near, out Vector3 feet, float maxRadius = 2.5f)
        {
            for (int ring = 0; ring <= 2; ring++)
            {
                int n = ring == 0 ? 1 : 8;
                for (int k = 0; k < n; k++)
                {
                    Vector3 c = near;
                    if (ring > 0)
                    {
                        float a = (k + (ring == 2 ? 0.5f : 0f)) * Mathf.PI * 2f / n;
                        c += new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * (maxRadius * ring / 2f);
                    }
                    if (TryStandAt(c, out feet)) return true;
                }
            }
            feet = near;
            return false;
        }

        private static bool TryStandAt(Vector3 column, out Vector3 feet)
        {
            feet = column;
            var hits = Physics.RaycastAll(column + Vector3.up * 3f, Vector3.down, 12f, Mask, QueryTriggerInteraction.Ignore);
            if (hits == null || hits.Length == 0) return false;
            var player = Player;
            float best = float.MaxValue;
            RaycastHit bh = default(RaycastHit);
            bool found = false;
            foreach (var h in hits)
            {
                var c = h.collider;
                if (c == null) continue;
                if (player != null && c.transform.IsChildOf(player.transform)) continue;
                if (c.attachedRigidbody != null && !c.attachedRigidbody.isKinematic) continue;
                if (h.distance < best) { best = h.distance; bh = h; found = true; }
            }
            if (!found || bh.normal.y < 0.7f) return false;
            feet = bh.point + Vector3.up * 0.05f;
            return CapsuleFree(feet);
        }

        /// <summary>True when a player-sized capsule at these feet does not touch SR geometry or our blocks.</summary>
        public static bool CapsuleFree(Vector3 feet, float radius = 0.4f, float height = 1.8f)
        {
            var cols = Physics.OverlapCapsule(feet + Vector3.up * (radius + 0.1f), feet + Vector3.up * (height - radius), radius, Mask, QueryTriggerInteraction.Ignore);
            var player = Player;
            foreach (var c in cols)
            {
                if (c == null) continue;
                if (player != null && c.transform.IsChildOf(player.transform)) continue;
                if (c.attachedRigidbody != null && !c.attachedRigidbody.isKinematic) continue;
                return false;
            }
            return true;
        }

        /// <summary>Teleports and holds the player at the feet position for a few frames so the controller settles there.</summary>
        public static IEnumerator MoveAndSettle(Vector3 feet, float settle = 0.3f)
        {
            Teleport(feet);
            for (int i = 0; i < 6; i++) { yield return null; HoldPlayerAt(feet); }
            yield return Wait(settle);
        }

        // ------------------------------------------------------------------ scans
        /// <summary>
        /// Active Minecraft dropped-item entities (root objects named "MC Item &lt;id&gt;") within the radius.
        /// itemIds null = any item. Allocates: for tests only (a few times per second at most).
        /// </summary>
        public static List<GameObject> ItemEntities(string[] itemIds, Vector3 center, float radius)
        {
            var result = new List<GameObject>();
            HashSet<string> want = null;
            if (itemIds != null)
            {
                want = new HashSet<string>(StringComparer.Ordinal);
                foreach (var id in itemIds) want.Add(ItemEntityPrefix + Content.Norm(id));
            }
            float r2 = radius * radius;
            for (int s = 0; s < SceneManager.sceneCount; s++)
            {
                Scene scene;
                try { scene = SceneManager.GetSceneAt(s); } catch { continue; }
                if (!scene.IsValid() || !scene.isLoaded) continue;
                GameObject[] roots;
                try { roots = scene.GetRootGameObjects(); } catch { continue; }
                foreach (var go in roots)
                {
                    if (go == null || !go.activeInHierarchy) continue;
                    string n = go.name;
                    if (n == null || !n.StartsWith(ItemEntityPrefix, StringComparison.Ordinal)) continue;
                    if (want != null && !want.Contains(n)) continue;
                    if ((go.transform.position - center).sqrMagnitude > r2) continue;
                    result.Add(go);
                }
            }
            return result;
        }

        public static List<GameObject> ItemEntities(string itemId, Vector3 center, float radius)
            => ItemEntities(itemId == null ? null : new[] { itemId }, center, radius);

        public static string ItemIdOfEntity(GameObject go)
        {
            if (go == null || go.name == null) return "?";
            return go.name.StartsWith(ItemEntityPrefix, StringComparison.Ordinal) ? go.name.Substring(ItemEntityPrefix.Length) : go.name;
        }

        /// <summary>Slime Rancher actors (Identifiable) with a collider inside the sphere, counted by Identifiable.Id name.</summary>
        public static Dictionary<string, int> SRActorsNear(Vector3 center, float radius)
        {
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            var seen = new HashSet<int>();
            Collider[] cols;
            try { cols = Physics.OverlapSphere(center, radius, ~0, QueryTriggerInteraction.Collide); }
            catch { return counts; }
            foreach (var c in cols)
            {
                if (c == null) continue;
                Identifiable ident = null;
                try { ident = c.GetComponentInParent<Identifiable>(); } catch { }
                if (ident == null || !seen.Add(ident.GetInstanceID())) continue;
                if (ident.id == Identifiable.Id.NONE || ident.id == Identifiable.Id.PLAYER) continue;
                string k = ident.id.ToString();
                counts.TryGetValue(k, out int n);
                counts[k] = n + 1;
            }
            return counts;
        }

        public static int CountWhere(Dictionary<string, int> counts, Func<string, bool> pred)
        {
            int n = 0;
            foreach (var kv in counts) if (pred(kv.Key)) n += kv.Value;
            return n;
        }

        public static int Count(Dictionary<string, int> counts, string key) => counts != null && counts.TryGetValue(key, out int n) ? n : 0;

        // ------------------------------------------------------------------ inventory
        public static int CountOf(string itemId)
        {
            var inv = SC.Inventory != null ? SC.Inventory.Inv : null;
            return inv != null ? inv.CountOf(Content.Norm(itemId)) : 0;
        }

        public static int CountOf(string[] itemIds)
        {
            int n = 0;
            foreach (var id in itemIds) n += CountOf(id);
            return n;
        }

        public static Dictionary<string, int> InvCounts()
        {
            var d = new Dictionary<string, int>(StringComparer.Ordinal);
            var inv = SC.Inventory != null ? SC.Inventory.Inv : null;
            if (inv == null) return d;
            for (int i = 0; i < inv.Size; i++)
            {
                var s = inv.Get(i);
                if (s.IsEmpty) continue;
                d.TryGetValue(s.Id, out int n);
                d[s.Id] = n + s.Count;
            }
            return d;
        }

        /// <summary>"+3 minecraft:cobblestone, +1 minecraft:apple" (only gains), "" when nothing was gained.</summary>
        public static string Gains(Dictionary<string, int> before, Dictionary<string, int> after)
        {
            var sb = new StringBuilder();
            foreach (var kv in after)
            {
                before.TryGetValue(kv.Key, out int b);
                if (kv.Value > b)
                {
                    if (sb.Length > 0) sb.Append(", ");
                    sb.Append('+').Append(kv.Value - b).Append(' ').Append(kv.Key);
                }
            }
            return sb.ToString();
        }

        /// <summary>Puts the item in the hotbar (giving it if needed, topping the total up to minCount) and selects it.</summary>
        public static bool SelectItem(string itemId, int minCount = 1)
        {
            if (SC.Inventory == null || SC.Inventory.Inv == null) return false;
            string id = Content.Norm(itemId);
            int slot = EnsureInHotbar(id, 4);
            if (slot < 0) return false;
            int have = SC.Inventory.Inv.CountOf(id);
            if (have < minCount) SC.Inventory.Give(new ItemStack(id, minCount - have));
            SC.Inventory.SelectedSlot = slot;
            var sel = SC.Inventory.Selected;
            return sel != null && !sel.IsEmpty && sel.Id == id;
        }

        // ------------------------------------------------------------------ surfaces
        /// <summary>Object / mesh / material / parent names of a collider (lowercase), for logs and rock detection.</summary>
        public static string DescribeSurface(Collider col)
        {
            if (col == null) return "null";
            var sb = new StringBuilder();
            try
            {
                sb.Append("obj '").Append(col.gameObject.name).Append('\'');
                if (col is MeshCollider mc && mc.sharedMesh != null) sb.Append(" mesh '").Append(mc.sharedMesh.name).Append('\'');
                if (col is TerrainCollider) sb.Append(" terrain");
                Renderer rend = col.GetComponent<Renderer>();
                if (rend == null && col.transform.parent != null) rend = col.transform.parent.GetComponentInChildren<Renderer>();
                if (rend != null)
                {
                    sb.Append(" mats '");
                    var ms = rend.sharedMaterials;
                    for (int i = 0; i < ms.Length && i < 4; i++)
                    {
                        if (ms[i] == null) continue;
                        if (i > 0) sb.Append(',');
                        sb.Append(ms[i].name);
                    }
                    sb.Append('\'');
                }
                var p = col.transform.parent;
                if (p != null) sb.Append(" parent '").Append(p.name).Append('\'');
                sb.Append(" layer ").Append(LayerMask.LayerToName(col.gameObject.layer));
            }
            catch (Exception e) { sb.Append(" (").Append(e.Message).Append(')'); }
            return sb.ToString();
        }
    }
}
