using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace SlimeCraft.Core
{
    /// <summary>
    /// Minimal read-only zip/jar reader (System.IO.Compression.ZipArchive is not available in Unity 2019 Mono).
    /// Parses the End-Of-Central-Directory record (zip64 aware), indexes every file of the central directory
    /// into a dictionary and reads entries on demand (method 0 stored, method 8 deflate). The file handle is
    /// kept open; reads are serialized with a lock, decompression happens outside of it. Thread-safe.
    /// </summary>
    internal sealed class ZipReader : IDisposable
    {
        private struct Entry
        {
            public long LocalHeaderOffset;
            public long DataOffset; // resolved lazily (-1 = unknown)
            public long CompressedSize;
            public long UncompressedSize;
            public int Method;
        }

        private readonly FileStream stream;
        private readonly object ioLock = new object();
        private readonly Dictionary<string, Entry> entries;
        private readonly List<string> sortedNames;
        private static bool deflateStreamBroken; // DeflateStream needs MonoPosixHelper; fall back to managed inflate

        public string Path { get; }
        public int Count => entries.Count;

        public ZipReader(string path)
        {
            Path = path;
            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.RandomAccess);
            try
            {
                entries = new Dictionary<string, Entry>(32768, StringComparer.Ordinal);
                ReadCentralDirectory();
                sortedNames = new List<string>(entries.Keys);
                sortedNames.Sort(StringComparer.Ordinal);
            }
            catch
            {
                stream.Dispose();
                throw;
            }
        }

        public bool Contains(string name)
        {
            if (name == null) return false;
            lock (ioLock) return entries.ContainsKey(name);
        }

        /// <summary>All entry names starting with prefix (ordinal order).</summary>
        public IEnumerable<string> List(string prefix)
        {
            prefix = prefix ?? "";
            int lo = 0, hi = sortedNames.Count;
            while (lo < hi)
            {
                int mid = (lo + hi) >> 1;
                if (string.CompareOrdinal(sortedNames[mid], prefix) < 0) lo = mid + 1; else hi = mid;
            }
            for (int i = lo; i < sortedNames.Count; i++)
            {
                var n = sortedNames[i];
                if (!n.StartsWith(prefix, StringComparison.Ordinal)) yield break;
                yield return n;
            }
        }

        /// <summary>Returns the decompressed bytes of an entry or null if missing/corrupt.</summary>
        public byte[] Read(string name)
        {
            if (name == null) return null;
            byte[] comp;
            Entry e;
            lock (ioLock) // the entry table is also mutated (lazy data offsets) so lookups happen under the lock
            {
                if (!entries.TryGetValue(name, out e)) return null;
                if (e.DataOffset < 0)
                {
                    var hdr = new byte[30];
                    stream.Seek(e.LocalHeaderOffset, SeekOrigin.Begin);
                    ReadFully(hdr, 0, 30);
                    if (U32(hdr, 0) != 0x04034b50u) throw new InvalidDataException("bad local header for " + name);
                    int nameLen = U16(hdr, 26), extraLen = U16(hdr, 28);
                    e.DataOffset = e.LocalHeaderOffset + 30 + nameLen + extraLen;
                    entries[name] = e;
                }
                if (e.CompressedSize > int.MaxValue) throw new InvalidDataException("entry too large: " + name);
                comp = new byte[e.CompressedSize];
                stream.Seek(e.DataOffset, SeekOrigin.Begin);
                ReadFully(comp, 0, comp.Length);
            }
            if (e.Method == 0) return comp;
            if (e.Method != 8) throw new InvalidDataException("unsupported zip method " + e.Method + " for " + name);
            return InflateBytes(comp, (int)e.UncompressedSize);
        }

        private static byte[] InflateBytes(byte[] comp, int size)
        {
            if (!deflateStreamBroken)
            {
                try
                {
                    var outBuf = new byte[size];
                    using (var ms = new MemoryStream(comp, false))
                    using (var ds = new DeflateStream(ms, CompressionMode.Decompress))
                    {
                        int total = 0;
                        while (total < size)
                        {
                            int n = ds.Read(outBuf, total, size - total);
                            if (n <= 0) break;
                            total += n;
                        }
                        if (total != size) throw new InvalidDataException("short inflate " + total + "/" + size);
                    }
                    return outBuf;
                }
                catch (Exception ex) when (ex is DllNotFoundException || ex is EntryPointNotFoundException || ex is TypeInitializationException || ex is NotImplementedException || ex is NotSupportedException)
                {
                    deflateStreamBroken = true;
                    CoreLog.Warn("System DeflateStream unavailable (" + ex.GetType().Name + "), using managed inflater.");
                }
            }
            return Inflater.Inflate(comp, size);
        }

        private void ReadFully(byte[] buf, int off, int len)
        {
            while (len > 0)
            {
                int n = stream.Read(buf, off, len);
                if (n <= 0) throw new EndOfStreamException();
                off += n; len -= n;
            }
        }

        private void ReadCentralDirectory()
        {
            long fileLen = stream.Length;
            int tailLen = (int)Math.Min(fileLen, 22 + 65535 + 20);
            var tail = new byte[tailLen];
            stream.Seek(fileLen - tailLen, SeekOrigin.Begin);
            ReadFully(tail, 0, tailLen);

            int eocd = -1;
            for (int i = tailLen - 22; i >= 0; i--)
                if (tail[i] == 0x50 && tail[i + 1] == 0x4b && tail[i + 2] == 0x05 && tail[i + 3] == 0x06) { eocd = i; break; }
            if (eocd < 0) throw new InvalidDataException("not a zip file (no EOCD): " + Path);

            long total = U16(tail, eocd + 10);
            long cdSize = U32(tail, eocd + 12);
            long cdOffset = U32(tail, eocd + 16);

            if (total == 0xFFFF || cdSize == 0xFFFFFFFFL || cdOffset == 0xFFFFFFFFL)
            {
                // zip64: locator sits right before the EOCD
                int loc = eocd - 20;
                if (loc >= 0 && U32(tail, loc) == 0x07064b50u)
                {
                    long z64Off = (long)U64(tail, loc + 8);
                    var z = new byte[56];
                    stream.Seek(z64Off, SeekOrigin.Begin);
                    ReadFully(z, 0, 56);
                    if (U32(z, 0) != 0x06064b50u) throw new InvalidDataException("bad zip64 EOCD");
                    total = (long)U64(z, 32);
                    cdSize = (long)U64(z, 40);
                    cdOffset = (long)U64(z, 48);
                }
            }

            if (cdSize > int.MaxValue) throw new InvalidDataException("central directory too large");
            var cd = new byte[cdSize];
            stream.Seek(cdOffset, SeekOrigin.Begin);
            ReadFully(cd, 0, cd.Length);

            int p = 0;
            for (long i = 0; i < total && p + 46 <= cd.Length; i++)
            {
                if (U32(cd, p) != 0x02014b50u) throw new InvalidDataException("bad central directory entry #" + i);
                int flags = U16(cd, p + 8);
                int method = U16(cd, p + 10);
                long comp = U32(cd, p + 20);
                long uncomp = U32(cd, p + 24);
                int nameLen = U16(cd, p + 28), extraLen = U16(cd, p + 30), commentLen = U16(cd, p + 32);
                long localOff = U32(cd, p + 42);
                var enc = (flags & 0x800) != 0 ? Encoding.UTF8 : Encoding.UTF8; // jars are ASCII anyway
                string name = enc.GetString(cd, p + 46, nameLen);

                // zip64 extended information extra field
                int ex = p + 46 + nameLen, exEnd = ex + extraLen;
                while (ex + 4 <= exEnd)
                {
                    int id = U16(cd, ex), sz = U16(cd, ex + 2);
                    if (id == 0x0001)
                    {
                        int q = ex + 4;
                        if (uncomp == 0xFFFFFFFFL && q + 8 <= ex + 4 + sz) { uncomp = (long)U64(cd, q); q += 8; }
                        if (comp == 0xFFFFFFFFL && q + 8 <= ex + 4 + sz) { comp = (long)U64(cd, q); q += 8; }
                        if (localOff == 0xFFFFFFFFL && q + 8 <= ex + 4 + sz) { localOff = (long)U64(cd, q); }
                    }
                    ex += 4 + sz;
                }

                if (name.Length > 0 && name[name.Length - 1] != '/')
                    entries[name] = new Entry { LocalHeaderOffset = localOff, DataOffset = -1, CompressedSize = comp, UncompressedSize = uncomp, Method = method };
                p += 46 + nameLen + extraLen + commentLen;
            }
        }

        private static int U16(byte[] b, int o) => b[o] | (b[o + 1] << 8);
        private static uint U32(byte[] b, int o) => (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24));
        private static ulong U64(byte[] b, int o) => U32(b, o) | ((ulong)U32(b, o + 4) << 32);

        public void Dispose()
        {
            lock (ioLock) { stream.Dispose(); }
        }
    }
}
