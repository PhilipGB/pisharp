using System.Collections;

namespace PiSharp.Extensions.Abstractions;

/// <summary>A request-local mutable view of the structured system-prompt sections.</summary>
/// <remarks>
/// Section values are raw text; the host wraps named sections when it builds the prompt.
/// The <c>preamble</c> section is emitted without a wrapper. Removing a key or assigning
/// <see langword="null"/> or an empty value omits that section. Sections added by extensions
/// follow the order in which they are added.
/// </remarks>
public sealed class PiSharpSystemPromptOptions
{
    /// <summary>Gets the ordered section map for this agent run.</summary>
    public IDictionary<string, string?> Sections { get; } = new InsertionOrderedDictionary();

    private sealed class InsertionOrderedDictionary : IDictionary<string, string?>
    {
        private readonly List<KeyValuePair<string, string?>> _entries = [];

        public string? this[string key]
        {
            get
            {
                var index = IndexOf(key);
                return index < 0 ? throw new KeyNotFoundException() : _entries[index].Value;
            }
            set
            {
                ArgumentNullException.ThrowIfNull(key);
                var index = IndexOf(key);
                var entry = new KeyValuePair<string, string?>(key, value);
                if (index < 0) _entries.Add(entry);
                else _entries[index] = entry;
            }
        }

        public ICollection<string> Keys => _entries.Select(entry => entry.Key).ToArray();
        public ICollection<string?> Values => _entries.Select(entry => entry.Value).ToArray();
        public int Count => _entries.Count;
        public bool IsReadOnly => false;

        public void Add(string key, string? value)
        {
            ArgumentNullException.ThrowIfNull(key);
            if (ContainsKey(key)) throw new ArgumentException("A section with the same name already exists.", nameof(key));
            _entries.Add(new(key, value));
        }

        public bool ContainsKey(string key) => IndexOf(key) >= 0;

        public bool Remove(string key)
        {
            var index = IndexOf(key);
            if (index < 0) return false;
            _entries.RemoveAt(index);
            return true;
        }

        public bool TryGetValue(string key, out string? value)
        {
            var index = IndexOf(key);
            if (index < 0)
            {
                value = null;
                return false;
            }

            value = _entries[index].Value;
            return true;
        }

        public void Add(KeyValuePair<string, string?> item) => Add(item.Key, item.Value);
        public void Clear() => _entries.Clear();
        public bool Contains(KeyValuePair<string, string?> item) =>
            TryGetValue(item.Key, out var value) && EqualityComparer<string?>.Default.Equals(value, item.Value);
        public void CopyTo(KeyValuePair<string, string?>[] array, int arrayIndex) => _entries.CopyTo(array, arrayIndex);
        public bool Remove(KeyValuePair<string, string?> item) => Contains(item) && Remove(item.Key);
        public IEnumerator<KeyValuePair<string, string?>> GetEnumerator() => _entries.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        private int IndexOf(string key)
        {
            ArgumentNullException.ThrowIfNull(key);
            for (var index = 0; index < _entries.Count; index++)
                if (StringComparer.Ordinal.Equals(_entries[index].Key, key)) return index;
            return -1;
        }
    }
}
