using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace SlimeCraft.Entities.Model
{
    /// <summary>
    /// Reads the small text format the mob rigs are written in. One statement per line, words separated by blanks,
    /// <c>#</c> starts a comment:
    /// <code>
    /// skin 64 32                       texture size the layout assumes (required)
    /// origin-scale 0.9                 uniform scale of the rig origin (optional, flattened rigs only)
    /// bone NAME [on PARENT] [at X Y Z] [turn XDEG YDEG ZDEG] [scale S]
    /// box U V from X Y Z size W H D [pad P] [flip] [stretch-v K]
    /// </code>
    /// A <c>box</c> line belongs to the closest <c>bone</c> line above it. A parent must be declared before its
    /// children; without <c>on</c> a bone hangs from the rig origin. Angles are written in degrees and stored in
    /// radians. Mistakes throw a <see cref="FormatException"/> naming the rig and the line.
    /// </summary>
    internal static class RigSheet
    {
        private static readonly char[] Blanks = { ' ', '\t', '\r' };

        public static RigSpec Parse(string rigName, string sheet)
        {
            var state = new SheetState(rigName);
            string[] lines = sheet.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string text = lines[i];
                int comment = text.IndexOf('#');
                if (comment >= 0) text = text.Substring(0, comment);
                string[] words = text.Split(Blanks, StringSplitOptions.RemoveEmptyEntries);
                if (words.Length == 0) continue;
                state.Read(new Words(rigName, i + 1, words));
            }
            return state.Finish();
        }

        /// <summary>Everything collected while walking down a sheet.</summary>
        private sealed class SheetState
        {
            private readonly string rig;
            private readonly List<BoneSpec> done = new List<BoneSpec>();
            private readonly Dictionary<string, int> indexByName = new Dictionary<string, int>(StringComparer.Ordinal);
            private int skinW, skinH;
            private float originScale = 1f;

            // the bone currently receiving box lines
            private string openName;
            private int openParent;
            private BonePose openRest;
            private readonly List<RigBox> openBoxes = new List<RigBox>();

            public SheetState(string rig) { this.rig = rig; }

            public void Read(Words w)
            {
                string keyword = w.Take();
                switch (keyword)
                {
                    case "skin":
                        skinW = w.TakeInt();
                        skinH = w.TakeInt();
                        break;
                    case "origin-scale":
                        originScale = w.TakeFloat();
                        break;
                    case "bone":
                        Close();
                        Open(w);
                        break;
                    case "box":
                        if (openName == null) throw w.Fail("box before any bone");
                        openBoxes.Add(ReadBox(w));
                        break;
                    default:
                        throw w.Fail("unknown statement '" + keyword + "'");
                }
                w.ExpectEnd();
            }

            private void Open(Words w)
            {
                string name = w.Take();
                if (indexByName.ContainsKey(name)) throw w.Fail("bone '" + name + "' declared twice");
                int parent = -1;
                Vector3 at = Vector3.zero, turn = Vector3.zero;
                float scale = 1f;
                while (w.More)
                {
                    string option = w.Take();
                    switch (option)
                    {
                        case "on":
                            string parentName = w.Take();
                            if (!indexByName.TryGetValue(parentName, out parent)) throw w.Fail("unknown parent '" + parentName + "'");
                            break;
                        case "at": at = w.TakeVector(); break;
                        case "turn": turn = new Vector3(w.TakeDegrees(), w.TakeDegrees(), w.TakeDegrees()); break;
                        case "scale": scale = w.TakeFloat(); break;
                        default: throw w.Fail("unknown bone option '" + option + "'");
                    }
                }
                openName = name;
                openParent = parent;
                openRest = new BonePose(at, turn, scale);
                openBoxes.Clear();
            }

            private void Close()
            {
                if (openName == null) return;
                indexByName[openName] = done.Count;
                done.Add(new BoneSpec(openName, openParent, openRest, openBoxes.ToArray()));
                openName = null;
                openBoxes.Clear();
            }

            private static RigBox ReadBox(Words w)
            {
                int u = w.TakeInt();
                int v = w.TakeInt();
                bool haveFrom = false, haveSize = false, flip = false;
                Vector3 from = Vector3.zero, size = Vector3.zero;
                float pad = 0f, stretchV = 1f;
                while (w.More)
                {
                    string option = w.Take();
                    switch (option)
                    {
                        case "from": from = w.TakeVector(); haveFrom = true; break;
                        case "size": size = w.TakeVector(); haveSize = true; break;
                        case "pad": pad = w.TakeFloat(); break;
                        case "flip": flip = true; break;
                        case "stretch-v": stretchV = w.TakeFloat(); break;
                        default: throw w.Fail("unknown box option '" + option + "'");
                    }
                }
                if (!haveFrom || !haveSize) throw w.Fail("a box needs both 'from' and 'size'");
                return new RigBox(u, v, from, size, pad, flip, stretchV);
            }

            public RigSpec Finish()
            {
                Close();
                if (skinW <= 0 || skinH <= 0) throw new FormatException("rig '" + rig + "': missing or invalid 'skin' statement");
                return new RigSpec(rig, skinW, skinH, new BonePose(Vector3.zero, Vector3.zero, originScale), done);
            }
        }

        /// <summary>Cursor over the words of one line.</summary>
        private sealed class Words
        {
            private readonly string rig;
            private readonly int line;
            private readonly string[] items;
            private int next;

            public Words(string rig, int line, string[] items)
            {
                this.rig = rig;
                this.line = line;
                this.items = items;
            }

            public bool More => next < items.Length;

            public string Take()
            {
                if (!More) throw Fail("line ends too early");
                return items[next++];
            }

            public float TakeFloat()
            {
                string word = Take();
                if (!float.TryParse(word, NumberStyles.Float, CultureInfo.InvariantCulture, out float value))
                    throw Fail("'" + word + "' is not a number");
                return value;
            }

            public int TakeInt()
            {
                string word = Take();
                if (!int.TryParse(word, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
                    throw Fail("'" + word + "' is not a whole number");
                return value;
            }

            /// <summary>An angle written in degrees, returned in radians (converted in double precision).</summary>
            public float TakeDegrees()
            {
                string word = Take();
                if (!double.TryParse(word, NumberStyles.Float, CultureInfo.InvariantCulture, out double degrees))
                    throw Fail("'" + word + "' is not an angle");
                return (float)(degrees * Math.PI / 180.0);
            }

            public Vector3 TakeVector() => new Vector3(TakeFloat(), TakeFloat(), TakeFloat());

            public void ExpectEnd()
            {
                if (More) throw Fail("unexpected '" + items[next] + "'");
            }

            public FormatException Fail(string problem)
                => new FormatException("rig '" + rig + "' line " + line + ": " + problem);
        }
    }
}
