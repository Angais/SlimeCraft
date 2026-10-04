using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace SlimeCraft
{
    public enum JsonType { Null, Bool, Number, String, Array, Object }

    /// <summary>
    /// Minimal JSON DOM (Unity's JsonUtility cannot read arbitrary Minecraft JSON).
    /// Indexers never throw: missing members return <see cref="JsonNode.Missing"/> (Type Null).
    /// </summary>
    public sealed class JsonNode
    {
        public static readonly JsonNode Missing = new JsonNode(JsonType.Null);

        public JsonType Type { get; private set; }
        private bool boolValue;
        private double numberValue;
        private string stringValue;
        private List<JsonNode> array;
        private Dictionary<string, JsonNode> obj;
        private List<string> keyOrder;

        private JsonNode(JsonType t) { Type = t; }

        public static JsonNode NewObject() { return new JsonNode(JsonType.Object) { obj = new Dictionary<string, JsonNode>(), keyOrder = new List<string>() }; }
        public static JsonNode NewArray() { return new JsonNode(JsonType.Array) { array = new List<JsonNode>() }; }
        public static JsonNode Of(string s) => s == null ? new JsonNode(JsonType.Null) : new JsonNode(JsonType.String) { stringValue = s };
        public static JsonNode Of(double d) => new JsonNode(JsonType.Number) { numberValue = d };
        public static JsonNode Of(bool b) => new JsonNode(JsonType.Bool) { boolValue = b };

        public bool IsNull => Type == JsonType.Null;
        public bool IsObject => Type == JsonType.Object;
        public bool IsArray => Type == JsonType.Array;
        public bool IsString => Type == JsonType.String;
        public bool IsNumber => Type == JsonType.Number;

        public JsonNode this[string key]
        {
            get { return (obj != null && key != null && obj.TryGetValue(key, out var v)) ? v : Missing; }
            set
            {
                if (obj == null) return;
                if (!obj.ContainsKey(key)) keyOrder.Add(key);
                obj[key] = value ?? new JsonNode(JsonType.Null);
            }
        }

        public JsonNode this[int index] => (array != null && index >= 0 && index < array.Count) ? array[index] : Missing;

        public int Count => array != null ? array.Count : (obj != null ? obj.Count : 0);
        public bool Has(string key) => obj != null && obj.ContainsKey(key);
        public IEnumerable<string> Keys => keyOrder ?? (IEnumerable<string>)Array.Empty<string>();
        public IEnumerable<JsonNode> Items => array ?? (IEnumerable<JsonNode>)Array.Empty<JsonNode>();
        public IEnumerable<KeyValuePair<string, JsonNode>> Members
        {
            get { if (keyOrder != null) foreach (var k in keyOrder) yield return new KeyValuePair<string, JsonNode>(k, obj[k]); }
        }

        public void Add(JsonNode v) { array?.Add(v ?? new JsonNode(JsonType.Null)); }

        public string AsString(string def = null) => Type == JsonType.String ? stringValue : Type == JsonType.Number ? numberValue.ToString(CultureInfo.InvariantCulture) : Type == JsonType.Bool ? (boolValue ? "true" : "false") : def;
        public double AsDouble(double def = 0) => Type == JsonType.Number ? numberValue : (Type == JsonType.String && double.TryParse(stringValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) ? d : def;
        public float AsFloat(float def = 0) => (float)AsDouble(def);
        public int AsInt(int def = 0) => Type == JsonType.Number ? (int)numberValue : (int)AsDouble(def);
        public bool AsBool(bool def = false) => Type == JsonType.Bool ? boolValue : def;

        // ------------------------------------------------------------------ parsing
        public static JsonNode Parse(string text)
        {
            if (text == null) return null;
            var p = new Parser(text);
            try
            {
                p.SkipWs();
                var v = p.ParseValue();
                return v;
            }
            catch (Exception e)
            {
                SC.Log?.LogWarning("JSON parse error at " + p.pos + ": " + e.Message);
                return null;
            }
        }

        private sealed class Parser
        {
            private readonly string s; public int pos;
            public Parser(string s) { this.s = s; }

            public void SkipWs()
            {
                while (pos < s.Length)
                {
                    char c = s[pos];
                    if (c == ' ' || c == '\t' || c == '\n' || c == '\r' || c == '﻿') pos++;
                    else if (c == '/' && pos + 1 < s.Length && s[pos + 1] == '/') { while (pos < s.Length && s[pos] != '\n') pos++; }
                    else break;
                }
            }

            public JsonNode ParseValue()
            {
                SkipWs();
                if (pos >= s.Length) throw new FormatException("unexpected end");
                char c = s[pos];
                switch (c)
                {
                    case '{': return ParseObject();
                    case '[': return ParseArray();
                    case '"': return Of(ParseString());
                    case 't': Expect("true"); return Of(true);
                    case 'f': Expect("false"); return Of(false);
                    case 'n': Expect("null"); return new JsonNode(JsonType.Null);
                    default: return ParseNumber();
                }
            }

            private void Expect(string w)
            {
                if (string.CompareOrdinal(s, pos, w, 0, w.Length) != 0) throw new FormatException("expected " + w);
                pos += w.Length;
            }

            private JsonNode ParseObject()
            {
                var o = NewObject(); pos++;
                SkipWs();
                if (pos < s.Length && s[pos] == '}') { pos++; return o; }
                while (true)
                {
                    SkipWs();
                    string key = ParseString();
                    SkipWs();
                    if (s[pos] != ':') throw new FormatException("expected ':'");
                    pos++;
                    o[key] = ParseValue();
                    SkipWs();
                    if (s[pos] == ',') { pos++; continue; }
                    if (s[pos] == '}') { pos++; return o; }
                    throw new FormatException("expected ',' or '}'");
                }
            }

            private JsonNode ParseArray()
            {
                var a = NewArray(); pos++;
                SkipWs();
                if (pos < s.Length && s[pos] == ']') { pos++; return a; }
                while (true)
                {
                    a.Add(ParseValue());
                    SkipWs();
                    if (s[pos] == ',') { pos++; continue; }
                    if (s[pos] == ']') { pos++; return a; }
                    throw new FormatException("expected ',' or ']'");
                }
            }

            private string ParseString()
            {
                if (s[pos] != '"') throw new FormatException("expected string");
                pos++;
                var sb = new StringBuilder();
                while (pos < s.Length)
                {
                    char c = s[pos++];
                    if (c == '"') return sb.ToString();
                    if (c != '\\') { sb.Append(c); continue; }
                    char e = s[pos++];
                    switch (e)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u': sb.Append((char)Convert.ToInt32(s.Substring(pos, 4), 16)); pos += 4; break;
                        default: sb.Append(e); break;
                    }
                }
                throw new FormatException("unterminated string");
            }

            private JsonNode ParseNumber()
            {
                int start = pos;
                while (pos < s.Length && "+-0123456789.eE".IndexOf(s[pos]) >= 0) pos++;
                if (start == pos) throw new FormatException("unexpected char '" + s[pos] + "'");
                return Of(double.Parse(s.Substring(start, pos - start), NumberStyles.Float, CultureInfo.InvariantCulture));
            }
        }

        // ------------------------------------------------------------------ writing
        public override string ToString() { var sb = new StringBuilder(); Write(sb); return sb.ToString(); }

        public void Write(StringBuilder sb)
        {
            switch (Type)
            {
                case JsonType.Null: sb.Append("null"); break;
                case JsonType.Bool: sb.Append(boolValue ? "true" : "false"); break;
                case JsonType.Number: sb.Append(numberValue.ToString("R", CultureInfo.InvariantCulture)); break;
                case JsonType.String: WriteString(sb, stringValue); break;
                case JsonType.Array:
                    sb.Append('[');
                    for (int i = 0; i < array.Count; i++) { if (i > 0) sb.Append(','); array[i].Write(sb); }
                    sb.Append(']');
                    break;
                case JsonType.Object:
                    sb.Append('{');
                    bool first = true;
                    foreach (var k in keyOrder) { if (!first) sb.Append(','); first = false; WriteString(sb, k); sb.Append(':'); obj[k].Write(sb); }
                    sb.Append('}');
                    break;
            }
        }

        private static void WriteString(StringBuilder sb, string v)
        {
            sb.Append('"');
            foreach (char c in v)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4")); else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }
    }
}
