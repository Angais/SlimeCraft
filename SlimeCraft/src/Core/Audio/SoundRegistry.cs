using System;
using System.Collections.Generic;

namespace SlimeCraft.Core
{
    /// <summary>
    /// Read-only index of the player's own <c>minecraft/sounds.json</c> (loaded at runtime, never bundled).
    ///
    /// The file maps sound event ids (for example <c>block.stone.break</c>) to a list of weighted entries. An entry is
    /// either an .ogg file with its own volume, pitch, weight and attenuation distance, or a reference to another event
    /// (<c>"type": "event"</c>). A reference behaves as if the referenced event's files were listed in place: each
    /// of those files keeps its own weight, and the reference's volume and pitch multiply onto them.
    ///
    /// To keep playback cheap, every event is flattened once at parse time into a table of playable variants with
    /// the multipliers already applied plus a running weight total. Picking a sound is then one random number and a
    /// binary search, with no recursion and no allocation. Ids of block sounds that the installed version does not
    /// have fall back to the matching stone sound (<c>block.&lt;anything&gt;.step</c> plays <c>block.stone.step</c>).
    ///
    /// Instances are built on a worker thread and never change afterwards, so the main thread can read them freely.
    /// </summary>
    internal sealed class SoundRegistry
    {
        /// <summary>One concrete sound to play, with the multipliers of every reference on its path folded in.</summary>
        internal struct Variant
        {
            /// <summary>Asset-index path such as <c>minecraft/sounds/dig/stone1.ogg</c>.</summary>
            public string File;
            public float Volume, Pitch, Attenuation;
            public bool Stream;
        }

        private const string DefaultNamespace = "minecraft";
        private const string NamespacePrefix = "minecraft:";
        private const string BlockPrefix = "block.";
        private const string StoneFallbackPrefix = "block.stone.";

        /// <summary>How many event references may be followed below the requested event (also stops cycles).</summary>
        private const int MaxReferenceDepth = 8;

        /// <summary>Safety valve against absurd fan-out in hand-edited files; vanilla data is far below it.</summary>
        private const int MaxVariantsPerEvent = 65536;

        // ------------------------------------------------------------------ raw parse model

        private sealed class SourceEntry
        {
            public bool IsReference;
            public string Target;     // mapped file path, or event id for references
            public float Volume = 1f;
            public float Pitch = 1f;
            public int Weight = 1;
            public bool Stream;
            public int Attenuation = 16;
        }

        // ------------------------------------------------------------------ flattened, immutable model

        private sealed class Table
        {
            public static readonly Table Empty = new Table(new Variant[0], new int[0], 0);

            public readonly Variant[] Variants;   // declaration order, depth first (includes zero-weight rows)
            public readonly int[] RunningWeight;  // RunningWeight[i] = sum of weights of rows 0..i
            public readonly int Total;

            public Table(Variant[] variants, int[] running, int total)
            {
                Variants = variants;
                RunningWeight = running;
                Total = total;
            }
        }

        private readonly Dictionary<string, Table> tables;

        private SoundRegistry(Dictionary<string, Table> tables) { this.tables = tables; }

        /// <summary>Number of sound events declared in the file (every top-level key counts).</summary>
        public int Count => tables.Count;

        /// <summary>True if <paramref name="ev"/> is a declared event name (exact, case-sensitive).</summary>
        public bool Has(string ev) => ev != null && tables.ContainsKey(ev);

        /// <summary>All declared event names (debug helper).</summary>
        public IEnumerable<string> EventNames => tables.Keys;

        // ------------------------------------------------------------------ parsing

        /// <summary>Builds a registry from the parsed sounds.json root. Odd shapes give an empty or partial registry.</summary>
        public static SoundRegistry Parse(JsonNode root)
        {
            var result = new Dictionary<string, Table>(StringComparer.Ordinal);
            if (root == null || !root.IsObject) return new SoundRegistry(result);

            // Pass 1: read every event's entries as written.
            var declared = new Dictionary<string, List<SourceEntry>>(StringComparer.Ordinal);
            foreach (var member in root.Members)
            {
                if (member.Key == null) continue;
                declared[member.Key] = ReadEntries(member.Value);
            }

            // Pass 2: flatten each event now that every possible reference target is known.
            var builder = new TableBuilder(declared);
            foreach (var pair in declared)
                result[pair.Key] = builder.Build(pair.Key);

            return new SoundRegistry(result);
        }

        private static List<SourceEntry> ReadEntries(JsonNode eventNode)
        {
            var list = new List<SourceEntry>();
            if (eventNode == null || !eventNode.IsObject) return list;
            var sounds = eventNode["sounds"];
            if (sounds == null || !sounds.IsArray) return list;

            foreach (var item in sounds.Items)
            {
                if (item == null) continue;
                SourceEntry entry = null;
                if (item.IsString)
                {
                    string name = item.AsString();
                    if (!string.IsNullOrEmpty(name)) entry = new SourceEntry { Target = FilePathFor(name) };
                }
                else if (item.IsObject)
                {
                    string name = item["name"].AsString();
                    if (!string.IsNullOrEmpty(name))
                    {
                        bool isRef = item["type"].AsString("file") == "event";
                        entry = new SourceEntry
                        {
                            IsReference = isRef,
                            Target = isRef ? WithoutMinecraftNamespace(name) : FilePathFor(name),
                            Volume = item["volume"].AsFloat(1f),
                            Pitch = item["pitch"].AsFloat(1f),
                            Weight = item["weight"].AsInt(1),
                            Stream = item["stream"].AsBool(false),
                            Attenuation = item["attenuation_distance"].AsInt(16),
                        };
                    }
                }
                if (entry != null) list.Add(entry);
            }
            return list;
        }

        /// <summary>Expands events into flat variant tables, following references up to the depth limit.</summary>
        private sealed class TableBuilder
        {
            private readonly Dictionary<string, List<SourceEntry>> declared;
            private readonly List<Variant> rows = new List<Variant>();
            private readonly List<int> weights = new List<int>();

            public TableBuilder(Dictionary<string, List<SourceEntry>> declared) { this.declared = declared; }

            public Table Build(string ev)
            {
                rows.Clear();
                weights.Clear();
                Expand(ev, 0, 1f, 1f, false);
                if (rows.Count == 0) return Table.Empty;

                var variants = rows.ToArray();
                var running = new int[variants.Length];
                long sum = 0;
                for (int i = 0; i < variants.Length; i++)
                {
                    sum += weights[i];
                    if (sum > int.MaxValue) sum = int.MaxValue;
                    running[i] = (int)sum;
                }
                return new Table(variants, running, (int)sum);
            }

            private void Expand(string ev, int depth, float volumeScale, float pitchScale, bool streamed)
            {
                if (!declared.TryGetValue(ev, out var entries)) return;
                for (int i = 0; i < entries.Count; i++)
                {
                    if (rows.Count >= MaxVariantsPerEvent) return;
                    var e = entries[i];
                    if (e.IsReference)
                    {
                        if (depth + 1 > MaxReferenceDepth) continue;
                        Expand(e.Target, depth + 1, volumeScale * e.Volume, pitchScale * e.Pitch, streamed || e.Stream);
                        continue;
                    }
                    rows.Add(new Variant
                    {
                        File = e.Target,
                        Volume = volumeScale * e.Volume,
                        Pitch = pitchScale * e.Pitch,
                        Attenuation = e.Attenuation,
                        Stream = streamed || e.Stream,
                    });
                    weights.Add(e.Weight > 0 ? e.Weight : 0);
                }
            }
        }

        // ------------------------------------------------------------------ queries

        /// <summary>
        /// Maps a requested id to a declared event name: drops a leading <c>minecraft:</c>, then tries the id itself
        /// and, for <c>block.&lt;group&gt;.&lt;action&gt;</c> ids, the stone sound with the same action. Null if neither exists.
        /// </summary>
        public string Resolve(string ev)
        {
            if (ev == null) return null;
            string id = WithoutMinecraftNamespace(ev);
            if (tables.ContainsKey(id)) return id;

            if (id.StartsWith(BlockPrefix, StringComparison.Ordinal))
            {
                int lastDot = id.LastIndexOf('.');
                bool hasGroup = lastDot > BlockPrefix.Length;      // at least one character between the dots
                bool hasAction = lastDot < id.Length - 1;
                if (hasGroup && hasAction)
                {
                    string stone = StoneFallbackPrefix + id.Substring(lastDot + 1);
                    if (tables.ContainsKey(stone)) return stone;
                }
            }
            return null;
        }

        /// <summary>
        /// Chooses one file of the exact event <paramref name="ev"/>, each reachable file weighted by its own weight.
        /// False (and a default variant) when the event is unknown or has nothing playable.
        /// </summary>
        public bool Pick(string ev, Random rng, out Variant v)
        {
            v = default(Variant);
            if (ev == null || rng == null || !tables.TryGetValue(ev, out var table) || table.Total <= 0) return false;

            int roll = rng.Next(table.Total);
            var running = table.RunningWeight;

            // first row whose running weight is greater than the roll (zero-weight rows can never satisfy this first)
            int lo = 0, hi = running.Length - 1;
            while (lo < hi)
            {
                int mid = (lo + hi) >> 1;
                if (running[mid] > roll) hi = mid;
                else lo = mid + 1;
            }
            v = table.Variants[lo];
            return true;
        }

        /// <summary>
        /// Adds every distinct file the event can ever play (zero-weight ones included) to <paramref name="into"/>,
        /// skipping paths already present. Never removes anything.
        /// </summary>
        public void CollectFiles(string ev, ICollection<string> into)
        {
            if (ev == null || into == null || !tables.TryGetValue(ev, out var table)) return;
            var variants = table.Variants;
            for (int i = 0; i < variants.Length; i++)
            {
                string file = variants[i].File;
                if (!into.Contains(file)) into.Add(file);
            }
        }

        // ------------------------------------------------------------------ name helpers

        private static string WithoutMinecraftNamespace(string id)
        {
            return id.StartsWith(NamespacePrefix, StringComparison.Ordinal) ? id.Substring(NamespacePrefix.Length) : id;
        }

        /// <summary><c>dig/stone1</c> → <c>minecraft/sounds/dig/stone1.ogg</c>; <c>ns:path</c> → <c>ns/sounds/path.ogg</c>.</summary>
        private static string FilePathFor(string name)
        {
            string ns = DefaultNamespace;
            string path = name;
            int colon = name.IndexOf(':');
            if (colon >= 0)
            {
                ns = name.Substring(0, colon);
                path = name.Substring(colon + 1);
            }
            return ns + "/sounds/" + path + ".ogg";
        }
    }
}
