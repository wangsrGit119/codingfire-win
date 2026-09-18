//
//  Json.cs — CodingFire for Windows
//
//  极简 JSON 解析器 + 类型化取值包装。
//  不依赖 System.Web.Extensions / System.Text.Json，保证在 .NET Framework 4.x 上零外部依赖。
//  Parse 返回值形状与 Swift JSONSerialization 一致：
//    object -> JObj, array -> JArr, string -> string, number -> double, true/false -> bool, null -> null
//

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace CodingFire.Core
{
    public static class Json
    {
        /// <summary>解析一段 JSON 文本；失败返回 null。</summary>
        public static object Parse(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            var p = new Parser(text);
            try
            {
                p.SkipWs();
                object v = p.ReadValue();
                p.SkipWs();
                return v;
            }
            catch (Exception)
            {
                return null;
            }
        }

        // ------------------------------------------------------------------
        private sealed class Parser
        {
            private readonly string _s;
            private int _i;

            public Parser(string s) { _s = s; _i = 0; }

            public void SkipWs()
            {
                while (_i < _s.Length)
                {
                    char c = _s[_i];
                    if (c == ' ' || c == '\t' || c == '\n' || c == '\r') { _i++; continue; }
                    break;
                }
            }

            public object ReadValue()
            {
                SkipWs();
                if (_i >= _s.Length) throw new FormatException("eof");
                char c = _s[_i];
                switch (c)
                {
                    case '{': return ReadObject();
                    case '[': return ReadArray();
                    case '"': return ReadString();
                    case 't':
                        Expect("true"); return true;
                    case 'f':
                        Expect("false"); return false;
                    case 'n':
                        Expect("null"); return null;
                    default:
                        return ReadNumber();
                }
            }

            private void Expect(string lit)
            {
                if (_i + lit.Length > _s.Length) throw new FormatException("eof");
                if (string.CompareOrdinal(_s, _i, lit, 0, lit.Length) != 0) throw new FormatException("literal");
                _i += lit.Length;
            }

            private JObj ReadObject()
            {
                var map = new Dictionary<string, object>(StringComparer.Ordinal);
                _i++; // {
                SkipWs();
                if (_i < _s.Length && _s[_i] == '}') { _i++; return new JObj(map); }
                while (true)
                {
                    SkipWs();
                    if (_i >= _s.Length || _s[_i] != '"') throw new FormatException("key");
                    string key = ReadString();
                    SkipWs();
                    if (_i >= _s.Length || _s[_i] != ':') throw new FormatException("colon");
                    _i++;
                    object val = ReadValue();
                    map[key] = val;
                    SkipWs();
                    if (_i >= _s.Length) throw new FormatException("eof");
                    char c = _s[_i];
                    if (c == ',') { _i++; continue; }
                    if (c == '}') { _i++; break; }
                    throw new FormatException("sep");
                }
                return new JObj(map);
            }

            private JArr ReadArray()
            {
                var list = new List<object>();
                _i++; // [
                SkipWs();
                if (_i < _s.Length && _s[_i] == ']') { _i++; return new JArr(list); }
                while (true)
                {
                    object val = ReadValue();
                    list.Add(val);
                    SkipWs();
                    if (_i >= _s.Length) throw new FormatException("eof");
                    char c = _s[_i];
                    if (c == ',') { _i++; continue; }
                    if (c == ']') { _i++; break; }
                    throw new FormatException("sep");
                }
                return new JArr(list);
            }

            private string ReadString()
            {
                _i++; // opening quote
                var sb = new StringBuilder();
                while (true)
                {
                    if (_i >= _s.Length) throw new FormatException("eof in string");
                    char c = _s[_i++];
                    if (c == '"') break;
                    if (c != '\\') { sb.Append(c); continue; }

                    if (_i >= _s.Length) throw new FormatException("eof escape");
                    char e = _s[_i++];
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
                        case 'u':
                            if (_i + 4 > _s.Length) throw new FormatException("eof \\u");
                            int cp = int.Parse(_s.Substring(_i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                            _i += 4;
                            // 代理对：低位紧随其后时合并，避免出现孤立代理字符
                            if (cp >= 0xD800 && cp <= 0xDBFF && _i + 6 <= _s.Length && _s[_i] == '\\' && _s[_i + 1] == 'u')
                            {
                                int lo = int.Parse(_s.Substring(_i + 2, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                                if (lo >= 0xDC00 && lo <= 0xDFFF)
                                {
                                    _i += 6;
                                    sb.Append(char.ConvertFromUtf32(0x10000 + ((cp - 0xD800) << 10) + (lo - 0xDC00)));
                                    break;
                                }
                            }
                            sb.Append((char)cp);
                            break;
                        default: throw new FormatException("escape");
                    }
                }
                return sb.ToString();
            }

            private object ReadNumber()
            {
                int start = _i;
                if (_i < _s.Length && (_s[_i] == '-' || _s[_i] == '+')) _i++;
                while (_i < _s.Length)
                {
                    char c = _s[_i];
                    if ((c >= '0' && c <= '9') || c == '.' || c == 'e' || c == 'E' || c == '+' || c == '-') { _i++; continue; }
                    break;
                }
                if (_i == start) throw new FormatException("number");
                string raw = _s.Substring(start, _i - start);
                double d;
                if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out d))
                    throw new FormatException("number");
                return d;
            }
        }
    }

    /// <summary>类型化 JSON 对象访问器。所有取值方法对缺失/类型不符都返回安全默认值。</summary>
    public sealed class JObj
    {
        private readonly Dictionary<string, object> _map;

        public JObj(Dictionary<string, object> map) { _map = map ?? new Dictionary<string, object>(StringComparer.Ordinal); }

        public static readonly JObj Empty = new JObj(null);

        public static JObj Of(object raw) { return raw as JObj ?? Empty; }

        public bool Has(string key) { return _map.ContainsKey(key); }

        /// <summary>顶层键名集合（用于遍历 map 型 JSON）。</summary>
        public IEnumerable<string> Keys { get { return _map.Keys; } }

        public object Raw(string key)
        {
            object v;
            return _map.TryGetValue(key, out v) ? v : null;
        }

        public string Str(string key)
        {
            object v = Raw(key);
            if (v == null) return null;
            string s = v as string;
            if (s != null) return s;
            return null;
        }

        public JObj Obj(string key) { return Of(Raw(key)); }

        public JArr Arr(string key)
        {
            var a = Raw(key) as JArr;
            return a ?? JArr.Empty;
        }

        public bool? Bool(string key)
        {
            object v = Raw(key);
            if (v is bool) return (bool)v;
            return null;
        }

        public long? Long(string key)
        {
            double? d = Num(key);
            if (!d.HasValue) return null;
            double v = d.Value;
            if (v >= 9.22e18 || v <= -9.22e18) return null;
            return (long)v;
        }

        public int? Int(string key)
        {
            long? l = Long(key);
            if (!l.HasValue) return null;
            long v = l.Value;
            if (v > int.MaxValue) return int.MaxValue;
            if (v < int.MinValue) return int.MinValue;
            return (int)v;
        }

        public double? Num(string key)
        {
            object v = Raw(key);
            if (v is double) return (double)v;
            string s = v as string;
            if (s != null)
            {
                double parsed;
                if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed)) return parsed;
            }
            return null;
        }

        public string StrOr(string key, string fallback) { return Str(key) ?? fallback; }
    }

    /// <summary>类型化 JSON 数组访问器。</summary>
    public sealed class JArr
    {
        private readonly List<object> _list;

        public JArr(List<object> list) { _list = list ?? new List<object>(); }

        public static readonly JArr Empty = new JArr(null);

        public static JArr Of(object raw) { return raw as JArr ?? Empty; }

        public int Count { get { return _list.Count; } }

        public object RawAt(int i) { return (i >= 0 && i < _list.Count) ? _list[i] : null; }

        public JObj ObjAt(int i) { return JObj.Of(RawAt(i)); }

        public string StrAt(int i)
        {
            object v = RawAt(i);
            return v as string;
        }

        public IEnumerable<JObj> Objects
        {
            get
            {
                for (int i = 0; i < _list.Count; i++)
                {
                    var o = _list[i] as JObj;
                    if (o != null) yield return o;
                }
            }
        }
    }
}
