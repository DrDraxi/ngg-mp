using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace NGGMultiplayer.Testing
{
    /// <summary>
    /// A minimal JSON writer for the harness's dumps (no dependency, deterministic output, one line). Comma placement
    /// is tracked per nesting level; misuse (a value without a name inside an object) throws.
    /// No Unity types (unit tested).
    /// </summary>
    public sealed class JsonBuilder
    {
        private readonly StringBuilder _sb = new StringBuilder(1024);
        private readonly Stack<Level> _levels = new Stack<Level>();

        public JsonBuilder BeginObject(string name = null)
        {
            Prefix(name);
            _sb.Append('{');
            _levels.Push(new Level(true));
            return this;
        }

        public JsonBuilder EndObject()
        {
            Pop(true);
            _sb.Append('}');
            return this;
        }

        public JsonBuilder BeginArray(string name = null)
        {
            Prefix(name);
            _sb.Append('[');
            _levels.Push(new Level(false));
            return this;
        }

        public JsonBuilder EndArray()
        {
            Pop(false);
            _sb.Append(']');
            return this;
        }

        public JsonBuilder Value(string name, string value)
        {
            Prefix(name);
            if (value == null)
                _sb.Append("null");
            else
                AppendString(value);
            return this;
        }

        public JsonBuilder Value(string name, bool value)
        {
            Prefix(name);
            _sb.Append(value ? "true" : "false");
            return this;
        }

        public JsonBuilder Value(string name, long value)
        {
            Prefix(name);
            _sb.Append(value.ToString(CultureInfo.InvariantCulture));
            return this;
        }

        public JsonBuilder Value(string name, ulong value)
        {
            Prefix(name);
            _sb.Append(value.ToString(CultureInfo.InvariantCulture));
            return this;
        }

        public JsonBuilder Value(string name, double value)
        {
            Prefix(name);
            if (double.IsNaN(value) || double.IsInfinity(value))
                _sb.Append("null");
            else
                _sb.Append(value.ToString("0.###", CultureInfo.InvariantCulture));
            return this;
        }

        /// <summary>A 64-bit id as a string, so JSON readers that use doubles keep every digit.</summary>
        public JsonBuilder Id(string name, ulong value) => Value(name, value.ToString(CultureInfo.InvariantCulture));

        public JsonBuilder Item(string value) => Value(null, value);

        /// <summary>Open objects and arrays.</summary>
        public int Depth => _levels.Count;

        /// <summary>Closes open objects and arrays until <paramref name="depth"/> remain (after a writer failed mid-way).</summary>
        public JsonBuilder CloseTo(int depth)
        {
            while (_levels.Count > Math.Max(0, depth))
            {
                bool isObject = _levels.Peek().IsObject;
                _levels.Pop();
                _sb.Append(isObject ? '}' : ']');
            }
            return this;
        }

        public override string ToString()
        {
            if (_levels.Count != 0)
                throw new InvalidOperationException("The JSON document is not closed.");
            return _sb.ToString();
        }

        private void Prefix(string name)
        {
            if (_levels.Count == 0)
            {
                if (_sb.Length > 0)
                    throw new InvalidOperationException("A JSON document has one root value.");
                if (name != null)
                    throw new InvalidOperationException("The root value has no name.");
                return;
            }
            var level = _levels.Pop();
            if (level.IsObject && name == null)
                throw new InvalidOperationException("Values inside an object need a name.");
            if (!level.IsObject && name != null)
                throw new InvalidOperationException("Values inside an array have no name.");
            if (level.Count > 0)
                _sb.Append(',');
            level.Count++;
            _levels.Push(level);
            if (name != null)
            {
                AppendString(name);
                _sb.Append(':');
            }
        }

        private void Pop(bool isObject)
        {
            if (_levels.Count == 0 || _levels.Peek().IsObject != isObject)
                throw new InvalidOperationException(isObject ? "No object to end." : "No array to end.");
            _levels.Pop();
        }

        private void AppendString(string value)
        {
            _sb.Append('"');
            foreach (char c in value)
            {
                switch (c)
                {
                    case '"': _sb.Append("\\\""); break;
                    case '\\': _sb.Append("\\\\"); break;
                    case '\n': _sb.Append("\\n"); break;
                    case '\r': _sb.Append("\\r"); break;
                    case '\t': _sb.Append("\\t"); break;
                    default:
                        if (c < 0x20)
                            _sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else
                            _sb.Append(c);
                        break;
                }
            }
            _sb.Append('"');
        }

        private struct Level
        {
            public Level(bool isObject)
            {
                IsObject = isObject;
                Count = 0;
            }

            public readonly bool IsObject;
            public int Count;
        }
    }
}
