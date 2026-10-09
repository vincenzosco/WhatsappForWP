using System.Collections;
using System.Collections.Generic;

namespace WhatsappApp.Protocol.WABinary
{
    /// <summary>
    /// One WhatsApp binary node: what the reference calls a `BinaryNode` and the
    /// protocol calls a stanza.
    ///
    /// <see cref="Attrs"/> preserves the order the attributes were added in, because
    /// that order **is** the encoding: the writer emits them as they come and the
    /// reader hands them back the same way. A `Dictionary` would make the byte stream
    /// depend on hash order - still parseable, but different every run, which turns
    /// every vector comparison into a coin toss.
    ///
    /// <see cref="Content"/> is one of: `null` (no content), a `string`, a `byte[]`
    /// (binary content, carried as its own length-prefixed run), or a
    /// `List&lt;BinaryNode&gt;` of children.
    /// </summary>
    public sealed class BinaryNode
    {
        /// <summary>The attributes of a node, in the order they were added.</summary>
        public sealed class AttributeMap : IEnumerable<KeyValuePair<string, string>>
        {
            private readonly List<string> _order = new List<string>();
            private readonly Dictionary<string, string> _values =
                new Dictionary<string, string>(System.StringComparer.Ordinal);

            public int Count
            {
                get { return _order.Count; }
            }

            /// <summary>Adds or replaces a value, keeping a new key at the end.</summary>
            public void Add(string key, string value)
            {
                if (!_values.ContainsKey(key)) _order.Add(key);
                _values[key] = value;
            }

            /// <summary>The value of a key, or null when the node does not carry it.</summary>
            public string this[string key]
            {
                get
                {
                    string value;
                    return _values.TryGetValue(key, out value) ? value : null;
                }
                set { Add(key, value); }
            }

            public bool Contains(string key)
            {
                return _values.ContainsKey(key);
            }

            /// <summary>Removes a key, so a node can be narrowed before it is sent.</summary>
            public bool Remove(string key)
            {
                if (!_values.Remove(key)) return false;

                _order.Remove(key);
                return true;
            }

            public IEnumerator<KeyValuePair<string, string>> GetEnumerator()
            {
                for (int i = 0; i < _order.Count; i++)
                {
                    yield return new KeyValuePair<string, string>(_order[i], _values[_order[i]]);
                }
            }

            IEnumerator IEnumerable.GetEnumerator()
            {
                return GetEnumerator();
            }
        }

        public string Tag { get; set; }

        public AttributeMap Attrs { get; private set; }

        public object Content { get; set; }

        public BinaryNode(string tag)
        {
            Tag = tag;
            Attrs = new AttributeMap();
        }

        /// <summary>The children, or null when the content is not a list.</summary>
        public IList<BinaryNode> Children()
        {
            return Content as IList<BinaryNode>;
        }

        /// <summary>A child by tag, or null: the one lookup that is used constantly.</summary>
        public BinaryNode Child(string tag)
        {
            IList<BinaryNode> children = Children();
            if (children == null) return null;

            for (int i = 0; i < children.Count; i++)
            {
                if (children[i] != null && children[i].Tag == tag) return children[i];
            }

            return null;
        }

        /// <summary>The content read as text, or null when it is not text.</summary>
        public string Text()
        {
            return Content as string;
        }

        /// <summary>The content read as bytes, or null when it is not a byte run.</summary>
        public byte[] Bytes()
        {
            return Content as byte[];
        }

        /// <summary>A one-line description for the app's log: tag and attributes.</summary>
        public override string ToString()
        {
            var text = new System.Text.StringBuilder(Tag);
            foreach (KeyValuePair<string, string> attribute in Attrs)
            {
                text.Append(' ').Append(attribute.Key).Append("=\"").Append(attribute.Value).Append('"');
            }

            return text.ToString();
        }
    }
}
