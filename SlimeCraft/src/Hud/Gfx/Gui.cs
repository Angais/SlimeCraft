using System;
using System.Collections.Generic;
using UnityEngine;

namespace SlimeCraft.HudUI
{
    /// <summary>
    /// Immediate-mode drawing surface shared by every HUD element and screen.
    ///
    /// Callers issue rectangles, gradients, sprites, sheet cut-outs and text each frame in GUI pixels
    /// (origin top-left, y down; one GUI pixel = GuiScale screen pixels, applied by the canvas scaler).
    /// Requests become vertex-coloured quads collected into pooled <see cref="GuiBatch"/> children of the root
    /// transform. A new batch starts whenever the texture changes (or after <see cref="LayerBreak"/>), and batch
    /// order equals sibling order, so the canvas paints everything in the order it was requested.
    ///
    /// Parameter convention of the image calls: destination rectangle first (x, y, w, h), then the source
    /// rectangle inside the image, then the logical size of the image the source is measured in.
    ///
    /// Item icons, item overlays and tooltips live in the other half of this class (GuiItems.cs).
    /// </summary>
    internal sealed partial class Gui
    {
        /// <summary>GUI-space size of the current frame.</summary>
        public int Width, Height;
        public readonly GuiAtlas Atlas;
        public readonly McFont Font;

        private const int BatchHashSeed = 17;

        private readonly RectTransform root;
        private readonly List<GuiBatch> pool = new List<GuiBatch>();
        private int batchesInUse;
        private GuiBatch active;
        private bool breakRequested;

        /// <summary>2D affine transform: screen = (M00*x + M01*y + Tx, M10*x + M11*y + Ty).</summary>
        private struct Pose
        {
            public float M00, M01, M10, M11, Tx, Ty;
            public static Pose Identity => new Pose { M00 = 1f, M11 = 1f };
        }

        private Pose pose = Pose.Identity;
        private readonly Stack<Pose> savedPoses = new Stack<Pose>(8);

        /// <summary>One stretch of a nine-slice axis: where it lands on screen and where it is read in the sprite.</summary>
        private struct AxisPiece
        {
            public int At, Length, From, SourceLength;
        }

        private readonly AxisPiece[] columnPieces = new AxisPiece[3];
        private readonly AxisPiece[] rowPieces = new AxisPiece[3];

        public Gui(RectTransform root, GuiAtlas atlas, McFont font)
        {
            this.root = root;
            Atlas = atlas;
            Font = font;
        }

        // ------------------------------------------------------------------ frame lifecycle

        public void Begin(int w, int h)
        {
            Width = w;
            Height = h;
            pose = Pose.Identity;
            savedPoses.Clear();
            active = null;
            batchesInUse = 0;
            breakRequested = false;
        }

        public void End()
        {
            for (int i = 0; i < pool.Count; i++)
            {
                var batch = pool[i];
                if (batch == null) continue; // destroyed by Unity; dropped the next time its slot is needed
                if (i >= batchesInUse && (batch.Verts.Count > 0 || !batch.CommittedEmpty))
                {
                    // idle this frame: empty mesh, but keep the last committed texture to avoid a material rebuild
                    batch.Verts.Clear();
                    batch.Hash = BatchHashSeed;
                    batch.Tex = batch.CommittedTex;
                }
                batch.Commit();
            }
            active = null;
        }

        /// <summary>Closes the current batch: everything requested afterwards renders above everything before.</summary>
        public void LayerBreak()
        {
            breakRequested = true;
        }

        // ------------------------------------------------------------------ pose stack

        public void Push()
        {
            savedPoses.Push(pose);
        }

        public void Pop()
        {
            if (savedPoses.Count > 0) pose = savedPoses.Pop();
        }

        public void Translate(float x, float y)
        {
            pose.Tx += pose.M00 * x + pose.M01 * y;
            pose.Ty += pose.M10 * x + pose.M11 * y;
        }

        public void Scale(float sx, float sy)
        {
            pose.M00 *= sx;
            pose.M10 *= sx;
            pose.M01 *= sy;
            pose.M11 *= sy;
        }

        /// <summary>Rotates the local axes; with y pointing down a positive angle turns clockwise on screen.</summary>
        public void Rotate(float rad)
        {
            float c = Mathf.Cos(rad), s = Mathf.Sin(rad);
            float a = pose.M00, b = pose.M10, cc = pose.M01, d = pose.M11;
            pose.M00 = a * c + cc * s;
            pose.M10 = b * c + d * s;
            pose.M01 = cc * c - a * s;
            pose.M11 = d * c - b * s;
        }

        // ------------------------------------------------------------------ batching

        private GuiBatch BatchFor(Texture tex)
        {
            if (!breakRequested && active != null && active.Tex == tex) return active;
            return StartBatch(tex);
        }

        private GuiBatch StartBatch(Texture tex)
        {
            while (batchesInUse < pool.Count && pool[batchesInUse] == null) pool.RemoveAt(batchesInUse);
            GuiBatch batch;
            if (batchesInUse < pool.Count)
            {
                batch = pool[batchesInUse];
            }
            else
            {
                batch = CreateBatch(pool.Count);
                pool.Add(batch);
            }
            batchesInUse++;
            batch.Tex = tex;
            batch.Verts.Clear();
            batch.Hash = BatchHashSeed;
            active = batch;
            breakRequested = false;
            return batch;
        }

        private GuiBatch CreateBatch(int number)
        {
            var go = new GameObject("GuiBatch" + number, typeof(RectTransform));
            go.layer = root.gameObject.layer;
            var rt = (RectTransform)go.transform;
            rt.SetParent(root, false);
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.pivot = new Vector2(0f, 1f);
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
            var batch = go.AddComponent<GuiBatch>();
            batch.raycastTarget = false;
            return batch;
        }

        /// <summary>Transforms a local point, appends the vertex and folds it into the batch's change hash.</summary>
        private void AddVertex(GuiBatch batch, float x, float y, float u, float v, Color32 col)
        {
            float sx = pose.M00 * x + pose.M01 * y + pose.Tx;
            float sy = pose.M10 * x + pose.M11 * y + pose.Ty;
            var vert = UIVertex.simpleVert;
            vert.position = new Vector3(sx, -sy, 0f);
            vert.color = col;
            vert.uv0 = new Vector2(u, v);
            batch.Verts.Add(vert);
            unchecked
            {
                int h = batch.Hash;
                h = h * 31 + (int)(sx * 64f);
                h = h * 31 + (int)(sy * 64f);
                h = h * 31 + (int)(u * 65536f);
                h = h * 31 + (int)(v * 65536f);
                h = h * 31 + (col.r | (col.g << 8) | (col.b << 16) | (col.a << 24));
                batch.Hash = h;
            }
        }

        /// <summary>One quad with a colour for its top edge and one for its bottom edge.</summary>
        private void AddQuad(Texture tex, float x0, float y0, float x1, float y1,
            float u0, float vTop, float u1, float vBottom, Color32 top, Color32 bottom, float skewTop, float skewBottom)
        {
            var batch = BatchFor(tex);
            AddVertex(batch, x0 + skewTop, y0, u0, vTop, top);
            AddVertex(batch, x1 + skewTop, y0, u1, vTop, top);
            AddVertex(batch, x1 + skewBottom, y1, u1, vBottom, bottom);
            AddVertex(batch, x0 + skewBottom, y1, u0, vBottom, bottom);
        }

        /// <summary>
        /// Textured quad. Corners are emitted top-left, top-right, bottom-right, bottom-left. The skew values shift
        /// the top and bottom edges horizontally (italic text).
        /// </summary>
        public void Quad(Texture tex, float x0, float y0, float x1, float y1, float u0, float vTop, float u1, float vBottom,
            uint argb, float skewTop = 0f, float skewBottom = 0f)
        {
            var c = Argb.ToColor32(argb);
            AddQuad(tex, x0, y0, x1, y1, u0, vTop, u1, vBottom, c, c, skewTop, skewBottom);
        }

        // ------------------------------------------------------------------ flat colour

        private bool SolidUV(out Texture tex, out float u, out float v)
        {
            var white = Atlas != null ? Atlas.White : null;
            if (white == null || white.TexW <= 0 || white.TexH <= 0)
            {
                tex = null; u = v = 0f;
                return false;
            }
            tex = white.Tex;
            u = (white.X + 2f) / white.TexW;
            v = (white.Y + 2f) / white.TexH;
            return true;
        }

        /// <summary>Solid rectangle between two corners given in any order. Fully transparent colours draw nothing.</summary>
        public void Fill(float x0, float y0, float x1, float y1, uint argb)
        {
            if ((argb >> 24) == 0) return;
            if (!SolidUV(out var tex, out float u, out float v)) return;
            float left = Math.Min(x0, x1), right = Math.Max(x0, x1);
            float top = Math.Min(y0, y1), bottom = Math.Max(y0, y1);
            var c = Argb.ToColor32(argb);
            AddQuad(tex, left, top, right, bottom, u, v, u, v, c, c, 0f, 0f);
        }

        /// <summary>Rectangle (x, y, w, h) blending vertically from <paramref name="topArgb"/> to <paramref name="bottomArgb"/>.</summary>
        public void DrawGradient(int x, int y, int w, int h, uint topArgb, uint bottomArgb)
        {
            if (!SolidUV(out var tex, out float u, out float v)) return;
            AddQuad(tex, x, y, x + w, y + h, u, v, u, v, Argb.ToColor32(topArgb), Argb.ToColor32(bottomArgb), 0f, 0f);
        }

        // ------------------------------------------------------------------ images

        /// <summary>
        /// Stretches the source rectangle (sx, sy, sw, sh) of a region, measured in a spaceW x spaceH coordinate space
        /// laid over the whole region, into the destination rectangle (x, y, w, h).
        /// </summary>
        public void DrawRegion(GuiRegion r, float x, float y, float w, float h, float sx, float sy, float sw, float sh,
            float spaceW, float spaceH, uint color = Argb.White)
        {
            if (r == null || w <= 0f || h <= 0f) return;
            r.UV(sx, sy, sw, sh, spaceW, spaceH, out float u0, out float vTop, out float u1, out float vBottom);
            Quad(r.Tex, x, y, x + w, y + h, u0, vTop, u1, vBottom, color);
        }

        /// <summary>Draws a whole GUI sprite into (x, y, w, h), honouring its stretch / tile / nine-slice metadata.</summary>
        public void DrawSprite(string path, int x, int y, int w, int h, uint color = Argb.White)
        {
            if (w <= 0 || h <= 0 || Atlas == null) return;
            var r = Atlas.Get(path);
            if (r == null) return;
            if (r.Scaling == GuiRegion.ScaleNineSlice) NineSlice(r, x, y, w, h, color);
            else if (r.Scaling == GuiRegion.ScaleTile) TileRect(r, x, y, w, h, 0, 0, r.SW, r.SH, color);
            else DrawRegion(r, x, y, w, h, 0, 0, r.SW, r.SH, r.SW, r.SH, color);
        }

        /// <summary>
        /// Copies the w x h block found at (srcX, srcY) of an image whose logical size is sheetW x sheetH to (x, y),
        /// unscaled. Used for partial sprites and for the 256x256 container sheets.
        /// </summary>
        public void DrawSheetPart(string path, int x, int y, int w, int h, int srcX, int srcY, int sheetW, int sheetH,
            uint color = Argb.White)
        {
            if (w <= 0 || h <= 0 || Atlas == null) return;
            DrawRegion(Atlas.Get(path), x, y, w, h, srcX, srcY, w, h, sheetW, sheetH, color);
        }

        /// <summary>Any Unity texture with normalised UVs in Unity orientation (vMax at the top edge).</summary>
        public void DrawTexture(Texture tex, float x, float y, float w, float h, float uMin, float vMin, float uMax, float vMax,
            uint color = Argb.White)
        {
            if (tex == null) return;
            Quad(tex, x, y, x + w, y + h, uMin, vMax, uMax, vMin, color);
        }

        /// <summary>A Unity sprite (possibly packed in a sprite atlas) stretched into the rectangle. Never throws.</summary>
        public void DrawSRSprite(Sprite s, float x, float y, float w, float h, uint color = Argb.White)
        {
            if (s == null) return;
            Texture tex;
            Vector4 outer;
            try
            {
                tex = s.texture;
                outer = UnityEngine.Sprites.DataUtility.GetOuterUV(s);
            }
            catch
            {
                return;
            }
            if (tex == null) return;
            Quad(tex, x, y, x + w, y + h, outer.x, outer.w, outer.z, outer.y, color);
        }

        // ------------------------------------------------------------------ tiling and nine-slice

        /// <summary>
        /// Covers (x, y, w, h) with 1:1 copies of the source cell (tx, ty, tw, th); the last column and row are
        /// cropped to their top-left part. Separate quads are used because atlas regions cannot wrap.
        /// </summary>
        private void TileRect(GuiRegion r, int x, int y, int w, int h, int tx, int ty, int tw, int th, uint color)
        {
            if (w <= 0 || h <= 0 || tw <= 0 || th <= 0) return;
            for (int oy = 0; oy < h; oy += th)
            {
                int cellH = Math.Min(th, h - oy);
                for (int ox = 0; ox < w; ox += tw)
                {
                    int cellW = Math.Min(tw, w - ox);
                    DrawRegion(r, x + ox, y + oy, cellW, cellH, tx, ty, cellW, cellH, r.SW, r.SH, color);
                }
            }
        }

        /// <summary>Fills one destination cell from one source cell: 1:1 when sizes match, else stretched or tiled.</summary>
        private void SliceCell(GuiRegion r, int dx, int dy, int dw, int dh, int sx, int sy, int sw, int sh, uint color)
        {
            if (dw <= 0 || dh <= 0) return;
            if ((dw == sw && dh == sh) || r.StretchInner)
                DrawRegion(r, dx, dy, dw, dh, sx, sy, sw, sh, r.SW, r.SH, color);
            else
                TileRect(r, dx, dy, dw, dh, sx, sy, sw, sh, color);
        }

        /// <summary>
        /// Splits one axis of a nine-slice sprite into a fixed leading border, a flexible middle and a fixed trailing
        /// border (each border limited to half the drawn length, so they never overlap). An axis drawn at its natural
        /// length needs no split and stays a single piece. Returns the number of pieces written.
        /// </summary>
        private static int SplitAxis(int drawn, int natural, int lead, int trail, AxisPiece[] pieces)
        {
            if (drawn == natural)
            {
                pieces[0] = new AxisPiece { At = 0, Length = drawn, From = 0, SourceLength = natural };
                return 1;
            }
            lead = Math.Min(lead, drawn / 2);
            trail = Math.Min(trail, drawn / 2);
            pieces[0] = new AxisPiece { At = 0, Length = lead, From = 0, SourceLength = lead };
            pieces[1] = new AxisPiece { At = lead, Length = drawn - lead - trail, From = lead, SourceLength = natural - lead - trail };
            pieces[2] = new AxisPiece { At = drawn - trail, Length = trail, From = natural - trail, SourceLength = trail };
            return 3;
        }

        /// <summary>
        /// Nine-slice: the grid of cells formed by the column pieces and the row pieces. Cells whose drawn size equals
        /// their source size are copied 1:1, the others are stretched or tiled per the sprite's metadata.
        /// </summary>
        private void NineSlice(GuiRegion r, int x, int y, int w, int h, uint color)
        {
            int columns = SplitAxis(w, r.SW, r.BL, r.BR, columnPieces);
            int rows = SplitAxis(h, r.SH, r.BT, r.BB, rowPieces);
            for (int j = 0; j < rows; j++)
            {
                var row = rowPieces[j];
                for (int i = 0; i < columns; i++)
                {
                    var col = columnPieces[i];
                    SliceCell(r, x + col.At, y + row.At, col.Length, row.Length, col.From, row.From, col.SourceLength, row.SourceLength, color);
                }
            }
        }

        // ------------------------------------------------------------------ text

        /// <summary>Draws one line of formatted text and returns the x where it ends (start x if nothing drew).</summary>
        public int Text(string s, float x, float y, uint color, bool shadow = true)
        {
            if (string.IsNullOrEmpty(s) || Font == null || !Font.Loaded) return (int)x;
            return (int)Font.Draw(this, s, x, y, color, shadow);
        }

        /// <summary>
        /// Draws one line so that it straddles the vertical line x = <paramref name="axisX"/>: it starts half its
        /// width (rounded toward zero) to the left of the axis.
        /// </summary>
        public void TextOnAxis(string s, int axisX, int y, uint color, bool shadow = true)
        {
            int half = (Font != null ? Font.Width(s) : 0) / 2;
            Text(s, axisX - half, y, color, shadow);
        }
    }
}
