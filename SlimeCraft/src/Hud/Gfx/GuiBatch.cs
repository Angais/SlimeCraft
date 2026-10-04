using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace SlimeCraft.HudUI
{
    /// <summary>
    /// One UGUI draw batch: a MaskableGraphic whose mesh is a list of textured quads produced by the
    /// immediate-mode <see cref="Gui"/> (sprites, fills and Minecraft bitmap-font glyphs with drop shadow).
    /// Batches are pooled; the mesh is only rebuilt when its content hash changes.
    /// </summary>
    internal sealed class GuiBatch : MaskableGraphic
    {
        internal Texture Tex;
        internal readonly List<UIVertex> Verts = new List<UIVertex>(64);
        internal int Hash;
        internal int CommittedHash = int.MinValue;
        internal Texture CommittedTex;
        internal bool CommittedEmpty = true;

        private static readonly List<int> s_Indices = new List<int>(1536);

        public override Texture mainTexture => Tex != null ? Tex : Texture2D.whiteTexture;

        protected override void Awake()
        {
            base.Awake();
            raycastTarget = false;
        }

        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();
            int quads = Verts.Count / 4;
            if (quads == 0) return;
            int need = quads * 6;
            if (s_Indices.Count > need) s_Indices.RemoveRange(need, s_Indices.Count - need);
            for (int q = s_Indices.Count / 6; q < quads; q++)
            {
                int b = q * 4;
                s_Indices.Add(b); s_Indices.Add(b + 1); s_Indices.Add(b + 2);
                s_Indices.Add(b + 2); s_Indices.Add(b + 3); s_Indices.Add(b);
            }
            vh.AddUIVertexStream(Verts, s_Indices);
        }

        /// <summary>Push this frame's geometry to the canvas if it changed.</summary>
        internal void Commit()
        {
            int h = Hash ^ (Tex != null ? Tex.GetInstanceID() * 486187739 : 0) ^ Verts.Count;
            bool empty = Verts.Count == 0;
            if (Tex != CommittedTex)
            {
                CommittedTex = Tex;
                SetMaterialDirty();
                SetVerticesDirty();
            }
            else if (h != CommittedHash || empty != CommittedEmpty)
            {
                SetVerticesDirty();
            }
            CommittedHash = h;
            CommittedEmpty = empty;
        }
    }
}
