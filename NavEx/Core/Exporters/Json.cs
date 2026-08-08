using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace NavEx.Core.Exporters
{
    /// <summary>
    /// A minimal JSON DOM. glTF needs a few hundred lines of JSON with exact
    /// shape control, and pulling in Newtonsoft.Json would mean shipping a second
    /// assembly next to the plugin — one that Navisworks itself may already have
    /// loaded at a different version. Hand-rolling it keeps NavEx a single DLL.
    /// </summary>
    internal abstract class JVal
    {
        public abstract void Write(StringBuilder sb);

        public static implicit operator JVal(string value) { return new JStr(value); }
        public static implicit operator JVal(double value) { return new JNum(value); }
        public static implicit operator JVal(int value) { return new JNum(value); }
        public static implicit operator JVal(bool value) { return new JBool(value); }

        public override string ToString()
        {
            var sb = new StringBuilder();
            Write(sb);
            return sb.ToString();
        }
    }

    internal class JStr : JVal
    {
        private readonly string _value;
        public JStr(string value) { _value = value ?? ""; }

        public override void Write(StringBuilder sb)
        {
            sb.Append('"');
            foreach (char c in _value)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20 || c == 0x7f)
                            sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else
                            sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }
    }

    internal class JNum : JVal
    {
        private readonly double _value;
        public JNum(double value) { _value = value; }

        public override void Write(StringBuilder sb)
        {
            double value = _value;
            // JSON has no NaN or Infinity; a stray one would make the whole file
            // unreadable, so degrade to 0 rather than emit invalid output.
            if (double.IsNaN(value) || double.IsInfinity(value)) value = 0.0;

            if (value == Math.Floor(value) && Math.Abs(value) < 1e15)
                sb.Append(((long)value).ToString(CultureInfo.InvariantCulture));
            else
                sb.Append(value.ToString("R", CultureInfo.InvariantCulture));
        }
    }

    internal class JBool : JVal
    {
        private readonly bool _value;
        public JBool(bool value) { _value = value; }
        public override void Write(StringBuilder sb) { sb.Append(_value ? "true" : "false"); }
    }

    internal class JArr : JVal
    {
        private readonly List<JVal> _items = new List<JVal>();

        public int Count { get { return _items.Count; } }

        public JArr Add(JVal value) { _items.Add(value); return this; }

        public static JArr Of(params double[] values)
        {
            var array = new JArr();
            foreach (double v in values) array.Add(new JNum(v));
            return array;
        }

        public override void Write(StringBuilder sb)
        {
            sb.Append('[');
            for (int i = 0; i < _items.Count; i++)
            {
                if (i > 0) sb.Append(',');
                _items[i].Write(sb);
            }
            sb.Append(']');
        }
    }

    internal class JObj : JVal
    {
        private readonly List<KeyValuePair<string, JVal>> _members = new List<KeyValuePair<string, JVal>>();

        public int Count { get { return _members.Count; } }

        public JObj Set(string name, JVal value)
        {
            _members.Add(new KeyValuePair<string, JVal>(name, value));
            return this;
        }

        public override void Write(StringBuilder sb)
        {
            sb.Append('{');
            for (int i = 0; i < _members.Count; i++)
            {
                if (i > 0) sb.Append(',');
                new JStr(_members[i].Key).Write(sb);
                sb.Append(':');
                _members[i].Value.Write(sb);
            }
            sb.Append('}');
        }
    }
}
