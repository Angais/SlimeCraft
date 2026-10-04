using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace SlimeCraft.BlocksMod
{
    /// <summary>
    /// Compact binary save of the block world, stored as "SCB1:" + base64(deflate(payload)).
    /// Payload: magic, version, palette (block ids), then per chunk: cx,cy,cz,n and n×(ushort local index,
    /// ushort palette index, byte facing). Unknown block ids (removed from Content) are skipped on load.
    /// </summary>
    internal static class BlockPersistence
    {
        private const string Prefix = "SCB1:";
        private const int Magic = 0x53434231; // "SCB1"
        private const int Version = 1;

        public static string Save(BlockWorld w)
        {
            using (var ms = new MemoryStream())
            {
                using (var deflate = new DeflateStream(ms, CompressionMode.Compress, true))
                using (var bw = new BinaryWriter(deflate, Encoding.UTF8))
                {
                    bw.Write(Magic);
                    bw.Write(Version);
                    var pal = w.Palette;
                    bw.Write(pal.Count);
                    for (int i = 0; i < pal.Count; i++) bw.Write(pal[i].Id ?? "");

                    var list = new List<Chunk>();
                    foreach (var c in w.Chunks) if (c.Count > 0) list.Add(c);
                    bw.Write(list.Count);
                    foreach (var c in list)
                    {
                        bw.Write(c.Cx); bw.Write(c.Cy); bw.Write(c.Cz);
                        bw.Write(c.Count);
                        int written = 0;
                        for (int i = 0; i < Chunk.Volume && written < c.Count; i++)
                        {
                            if (c.Ids[i] == 0) continue;
                            bw.Write((ushort)i);
                            bw.Write(c.Ids[i]);
                            bw.Write(c.Facing[i]);
                            written++;
                        }
                        // keep the stream consistent even if Count was off
                        for (; written < c.Count; written++) { bw.Write((ushort)0); bw.Write((ushort)0); bw.Write((byte)0); }
                    }
                }
                return Prefix + Convert.ToBase64String(ms.ToArray());
            }
        }

        public static void Load(BlockWorld w, string data)
        {
            w.Clear();
            if (string.IsNullOrEmpty(data) || !data.StartsWith(Prefix)) { w.FinishBulkLoad(); return; }
            int loaded = 0, skipped = 0;
            try
            {
                var bytes = Convert.FromBase64String(data.Substring(Prefix.Length));
                using (var ms = new MemoryStream(bytes))
                using (var deflate = new DeflateStream(ms, CompressionMode.Decompress))
                using (var br = new BinaryReader(deflate, Encoding.UTF8))
                {
                    if (br.ReadInt32() != Magic) throw new InvalidDataException("bad magic");
                    int ver = br.ReadInt32();
                    if (ver > Version) throw new InvalidDataException("unsupported version " + ver);
                    int pc = br.ReadInt32();
                    var map = new ushort[pc];
                    for (int i = 0; i < pc; i++)
                    {
                        string id = br.ReadString();
                        var def = string.IsNullOrEmpty(id) ? null : Content.Block(id);
                        map[i] = def == null ? (ushort)0 : w.PaletteOf(def);
                    }
                    int cc = br.ReadInt32();
                    for (int k = 0; k < cc; k++)
                    {
                        int cx = br.ReadInt32(), cy = br.ReadInt32(), cz = br.ReadInt32();
                        int n = br.ReadInt32();
                        for (int j = 0; j < n; j++)
                        {
                            int li = br.ReadUInt16();
                            int pi = br.ReadUInt16();
                            byte f = br.ReadByte();
                            ushort idx = pi < map.Length ? map[pi] : (ushort)0;
                            if (idx == 0 || li >= Chunk.Volume) { skipped++; continue; }
                            int x = cx * 16 + (li & 15), y = cy * 16 + (li >> 8), z = cz * 16 + ((li >> 4) & 15);
                            w.SetRaw(x, y, z, idx, f > 5 ? (byte)0 : f);
                            loaded++;
                        }
                    }
                }
            }
            catch (Exception e)
            {
                SC.Log?.LogError("[Blocks] could not load saved blocks: " + e.Message);
            }
            w.FinishBulkLoad();
            SC.Log?.LogInfo("[Blocks] loaded " + loaded + " blocks" + (skipped > 0 ? " (" + skipped + " unknown skipped)" : ""));
        }
    }
}
