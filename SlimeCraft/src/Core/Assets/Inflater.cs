// Inflater.cs - adapted from puff.c (zlib contrib/puff), translated to C# and modified for SlimeCraft.
//
// ALTERED SOURCE VERSION: this is not the original puff.c. It was translated to C# and changed (array-based
// input/output, exceptions instead of error codes, different structure and names). The original notice follows.
//
//   puff.c
//   Copyright (C) 2002-2013 Mark Adler, all rights reserved
//
//   This software is provided 'as-is', without any express or implied
//   warranty.  In no event will the author be held liable for any damages
//   arising from the use of this software.
//
//   Permission is granted to anyone to use this software for any purpose,
//   including commercial applications, and to alter it and redistribute it
//   freely, subject to the following restrictions:
//
//   1. The origin of this software must not be misrepresented; you must not
//      claim that you wrote the original software. If you use this software
//      in a product, an acknowledgment in the product documentation would be
//      appreciated but is not required.
//   2. Altered source versions must be plainly marked as such, and must not be
//      misrepresented as being the original software.
//   3. This notice may not be removed or altered from any source distribution.
//
//   Mark Adler    madler@alumni.caltech.edu
//
// This file is distributed under the zlib license above, not under SlimeCraft's MIT license.
// See THIRD_PARTY_NOTICES.md.

using System;
using System.IO;

namespace SlimeCraft.Core
{
    /// <summary>
    /// Small managed raw-DEFLATE (RFC 1951) decoder used only if System.IO.Compression.DeflateStream is
    /// unusable at runtime (it relies on the native MonoPosixHelper). Handles stored, fixed and dynamic blocks.
    /// Adapted from Mark Adler's puff.c (zlib license, see the header of this file).
    /// </summary>
    internal static class Inflater
    {
        private static readonly int[] LenBase = { 3, 4, 5, 6, 7, 8, 9, 10, 11, 13, 15, 17, 19, 23, 27, 31, 35, 43, 51, 59, 67, 83, 99, 115, 131, 163, 195, 227, 258 };
        private static readonly int[] LenExtra = { 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 0 };
        private static readonly int[] DistBase = { 1, 2, 3, 4, 5, 7, 9, 13, 17, 25, 33, 49, 65, 97, 129, 193, 257, 385, 513, 769, 1025, 1537, 2049, 3073, 4097, 6145, 8193, 12289, 16385, 24577 };
        private static readonly int[] DistExtra = { 0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, 8, 8, 9, 9, 10, 10, 11, 11, 12, 12, 13, 13 };
        private static readonly int[] ClOrder = { 16, 17, 18, 0, 8, 7, 9, 6, 10, 5, 11, 4, 12, 3, 13, 2, 14, 1, 15 };

        /// <summary>Canonical Huffman table: counts per length + symbols sorted by code.</summary>
        private sealed class Huffman
        {
            public readonly short[] Count = new short[16];
            public readonly short[] Symbol;
            public Huffman(int[] lengths, int n)
            {
                Symbol = new short[n];
                for (int i = 0; i < n; i++) Count[lengths[i]]++;
                Count[0] = 0;
                var offs = new short[16];
                for (int len = 1; len < 16; len++) offs[len] = (short)(offs[len - 1] + Count[len - 1]);
                for (int i = 0; i < n; i++) if (lengths[i] != 0) Symbol[offs[lengths[i]]++] = (short)i;
            }
        }

        private sealed class State
        {
            public byte[] In; public int InPos; public int BitBuf; public int BitCnt;
            public byte[] Out; public int OutPos;

            public int Bits(int need)
            {
                int val = BitBuf;
                while (BitCnt < need)
                {
                    if (InPos >= In.Length) throw new InvalidDataException("inflate: input exhausted");
                    val |= In[InPos++] << BitCnt;
                    BitCnt += 8;
                }
                BitBuf = val >> need;
                BitCnt -= need;
                return val & ((1 << need) - 1);
            }

            public int Decode(Huffman h)
            {
                int code = 0, first = 0, index = 0;
                for (int len = 1; len < 16; len++)
                {
                    code |= Bits(1);
                    int count = h.Count[len];
                    if (code - count < first) return h.Symbol[index + (code - first)];
                    index += count;
                    first += count;
                    first <<= 1;
                    code <<= 1;
                }
                throw new InvalidDataException("inflate: bad huffman code");
            }

            public void Put(byte b)
            {
                if (OutPos >= Out.Length) Array.Resize(ref Out, Math.Max(64, Out.Length * 2));
                Out[OutPos++] = b;
            }
        }

        private static Huffman fixedLit, fixedDist;

        public static byte[] Inflate(byte[] input, int expectedSize)
        {
            var s = new State { In = input, Out = new byte[Math.Max(16, expectedSize)] };
            bool last;
            do
            {
                last = s.Bits(1) == 1;
                int type = s.Bits(2);
                if (type == 0) Stored(s);
                else if (type == 1) { EnsureFixed(); Codes(s, fixedLit, fixedDist); }
                else if (type == 2) Dynamic(s);
                else throw new InvalidDataException("inflate: bad block type");
            } while (!last);
            if (s.OutPos == s.Out.Length) return s.Out;
            var res = new byte[s.OutPos];
            Buffer.BlockCopy(s.Out, 0, res, 0, s.OutPos);
            return res;
        }

        private static void Stored(State s)
        {
            s.BitBuf = 0; s.BitCnt = 0; // byte align
            if (s.InPos + 4 > s.In.Length) throw new InvalidDataException("inflate: truncated stored block");
            int len = s.In[s.InPos] | (s.In[s.InPos + 1] << 8);
            s.InPos += 4;
            if (s.InPos + len > s.In.Length) throw new InvalidDataException("inflate: truncated stored data");
            for (int i = 0; i < len; i++) s.Put(s.In[s.InPos++]);
        }

        private static void EnsureFixed()
        {
            if (fixedLit != null) return;
            var l = new int[288];
            int i = 0;
            for (; i < 144; i++) l[i] = 8;
            for (; i < 256; i++) l[i] = 9;
            for (; i < 280; i++) l[i] = 7;
            for (; i < 288; i++) l[i] = 8;
            var d = new int[30];
            for (i = 0; i < 30; i++) d[i] = 5;
            fixedDist = new Huffman(d, 30);
            fixedLit = new Huffman(l, 288);
        }

        private static void Dynamic(State s)
        {
            int nlen = s.Bits(5) + 257, ndist = s.Bits(5) + 1, ncode = s.Bits(4) + 4;
            var lengths = new int[320];
            for (int i = 0; i < ncode; i++) lengths[ClOrder[i]] = s.Bits(3);
            var lencode = new Huffman(lengths, 19);
            Array.Clear(lengths, 0, lengths.Length);
            int idx = 0;
            while (idx < nlen + ndist)
            {
                int sym = s.Decode(lencode);
                if (sym < 16) { lengths[idx++] = sym; continue; }
                int len = 0, rep;
                if (sym == 16)
                {
                    if (idx == 0) throw new InvalidDataException("inflate: repeat with no previous length");
                    len = lengths[idx - 1];
                    rep = 3 + s.Bits(2);
                }
                else if (sym == 17) rep = 3 + s.Bits(3);
                else rep = 11 + s.Bits(7);
                if (idx + rep > nlen + ndist) throw new InvalidDataException("inflate: too many lengths");
                while (rep-- > 0) lengths[idx++] = len;
            }
            var lit = new Huffman(lengths, nlen);
            var distLengths = new int[ndist];
            Array.Copy(lengths, nlen, distLengths, 0, ndist);
            var dist = new Huffman(distLengths, ndist);
            Codes(s, lit, dist);
        }

        private static void Codes(State s, Huffman lit, Huffman dist)
        {
            while (true)
            {
                int sym = s.Decode(lit);
                if (sym < 256) { s.Put((byte)sym); continue; }
                if (sym == 256) return;
                sym -= 257;
                if (sym >= 29) throw new InvalidDataException("inflate: bad length symbol");
                int len = LenBase[sym] + s.Bits(LenExtra[sym]);
                int dsym = s.Decode(dist);
                if (dsym >= 30) throw new InvalidDataException("inflate: bad distance symbol");
                int d = DistBase[dsym] + s.Bits(DistExtra[dsym]);
                if (d > s.OutPos) throw new InvalidDataException("inflate: distance too far");
                for (int i = 0; i < len; i++) s.Put(s.Out[s.OutPos - d]);
            }
        }
    }
}
