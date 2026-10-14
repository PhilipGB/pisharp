using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace PiSharp.Runtime.Resources;

/// <summary>Preserves YAML key identity and ordered pairs while resolving resource metadata.</summary>
internal sealed class ResourceYamlDocument
{
    private sealed class Node(ParsingEvent origin)
    {
        internal ParsingEvent Origin { get; } = origin;
        internal object? ScalarValue { get; } = origin is Scalar scalar ? ResourceYamlScalar.Value(scalar) : null;
        internal List<(Node Key, Node Value)>? Mapping { get; set; }
        internal List<Node>? Sequence { get; set; }
        internal Node? AliasTarget { get; set; }
        internal string? AliasName { get; set; }
        internal bool Converted { get; set; }
        internal long AliasVisits { get; set; } = 1;
        internal long AliasWeight { get; set; }
        internal Scalar? Scalar => Origin as Scalar;
    }

    private readonly string _yaml;
    private readonly Scanner _scanner;
    private readonly Parser _parser;
    private readonly Stack<char> _flowCollections = new();
    private readonly Dictionary<string, Node> _anchors = new(StringComparer.Ordinal);
    private Node? _root;

    private ResourceYamlDocument(string yaml)
    {
        _yaml = yaml;
        _scanner = new(new StringReader(yaml.EndsWith('\n') ? yaml : yaml + "\n"));
        _parser = new(_scanner);
    }

    internal static ResourceYamlDocument Parse(string yaml)
    {
        var document = new ResourceYamlDocument(yaml);
        try
        {
            document.Next();
            document.Consume<StreamStart>();
            if (document._parser.Current is StreamEnd) return document;
            document.Consume<DocumentStart>();
            document._root = document.ReadNode();
            document.Consume<DocumentEnd>();
            if (document._parser.Current is not StreamEnd)
                throw new YamlException("Expected a single frontmatter document.");
            document.ConvertAliases(document._root);
            return document;
        }
        catch (YamlException error)
        {
            throw new YamlException(ResourceYamlDiagnostics.FromNative(yaml, error), error);
        }
    }

    internal Scalar? Field(string name)
    {
        if (_root is null || Resolve(_root).Mapping is not { } pairs) return null;
        for (var index = pairs.Count - 1; index >= 0; index--)
        {
            var pair = pairs[index];
            var key = Resolve(pair.Key);
            if (key.Scalar is not null && key.ScalarValue is string text && text == name)
                return Resolve(pair.Value).Scalar;
        }
        return null;
    }

    private Node ReadNode(bool mappingKey = false)
    {
        var current = _parser.Current ?? throw new YamlException("Unexpected end of YAML input.");
        var node = new Node(current);
        if (current is AnchorAlias alias)
        {
            var name = alias.Value.ToString();
            var end = (int)alias.End.Index;
            if (end < _yaml.Length && _yaml[end] == ':')
            {
                var start = (int)alias.Start.Index;
                var lineStart = start == 0 ? 0 : _yaml.LastIndexOf('\n', start - 1) + 1;
                if (mappingKey && !_yaml[lineStart..start].TrimStart().StartsWith('?'))
                    throw new YamlException(ResourceYamlDiagnostics.Format(_yaml,
                        "Implicit map keys need to be followed by map values", start, end - start + 1));
                name += ":";
            }
            _anchors.TryGetValue(name, out var target);
            node.AliasName = name;
            node.AliasTarget = target;
            Next();
            return node;
        }
        if (current is NodeEvent anchored && !anchored.Anchor.IsEmpty)
            _anchors[anchored.Anchor.ToString()] = node;
        switch (current)
        {
            case Scalar:
                Next();
                break;
            case MappingStart mapping:
                node.Mapping = [];
                if (mapping.Style == MappingStyle.Flow) _flowCollections.Push('}');
                Next();
                while (_parser.Current is not MappingEnd)
                {
                    var key = ReadNode(mappingKey: true);
                    var value = ReadNode();
                    if (key.Scalar is not null && node.Mapping.Any(pair =>
                            pair.Key.Scalar is not null && ResourceYamlScalar.EqualKeys(pair.Key.ScalarValue, key.ScalarValue)))
                        throw new YamlException(ResourceYamlDiagnostics.Format(_yaml, "Map keys must be unique",
                            (int)key.Origin.Start.Index));
                    node.Mapping.Add((key, value));
                }
                if (mapping.Tag.ToString() == "tag:yaml.org,2002:set" && node.Mapping.Any(pair =>
                        pair.Value.Scalar is null || pair.Value.ScalarValue is not null))
                {
                    var tagStart = _yaml.IndexOf('!', (int)mapping.Start.Index);
                    var tagEnd = tagStart;
                    while (tagEnd < _yaml.Length && !ResourceFrontmatter.IsWhitespace(_yaml[tagEnd]) &&
                           _yaml[tagEnd] is not ('{' or '[')) tagEnd++;
                    throw new YamlException(ResourceYamlDiagnostics.Format(_yaml,
                        "Set items must all have null values", tagStart, tagEnd - tagStart));
                }
                if (mapping.Style == MappingStyle.Flow) _flowCollections.Pop();
                Consume<MappingEnd>();
                break;
            case SequenceStart sequence:
                node.Sequence = [];
                if (sequence.Style == SequenceStyle.Flow) _flowCollections.Push(']');
                Next();
                while (_parser.Current is not SequenceEnd) node.Sequence.Add(ReadNode());
                if (sequence.Style == SequenceStyle.Flow) _flowCollections.Pop();
                Consume<SequenceEnd>();
                break;
            default:
                throw new YamlException(current.Start, current.End, "Unexpected YAML node.");
        }
        return node;
    }

    private static Node Resolve(Node node)
    {
        while (node.AliasTarget is { } target) node = target;
        return node;
    }

    private void ConvertAliases(Node node)
    {
        if (node.AliasName is { } name)
        {
            var target = node.AliasTarget ?? throw new YamlException(
                $"Unresolved alias (the anchor must be set before the alias): {name}");
            if (!target.Converted) ConvertAliases(target);
            target.AliasVisits++;
            if (target.AliasWeight == 0) target.AliasWeight = AliasWeight(target);
            if (target.AliasVisits * target.AliasWeight > 100)
                throw new YamlException("Excessive alias count indicates a resource exhaustion attack");
            return;
        }
        node.Converted = true;
        if (node.Mapping is { } pairs)
            foreach (var pair in pairs)
            {
                ConvertAliases(pair.Key);
                ConvertAliases(pair.Value);
            }
        if (node.Sequence is { } items)
            foreach (var item in items) ConvertAliases(item);
    }

    private static long AliasWeight(Node node)
    {
        if (node.AliasName is not null)
            return node.AliasTarget is { } target ? target.AliasVisits * target.AliasWeight : 0;
        if (node.Mapping is { } pairs)
            return pairs.Aggregate(0L, (count, pair) => Math.Max(count, Math.Max(AliasWeight(pair.Key), AliasWeight(pair.Value))));
        if (node.Sequence is { } items)
            return items.Aggregate(0L, (count, item) => Math.Max(count, AliasWeight(item)));
        return 1;
    }

    private void Consume<T>() where T : ParsingEvent
    {
        if (_parser.Current is not T) throw new YamlException($"Expected {typeof(T).Name}.");
        Next();
    }

    private void Next()
    {
        try { _parser.MoveNext(); }
        // YamlDotNet 18.1 throws an untyped scanner error for a missing flow terminator before a block key.
        catch (InvalidOperationException error) when (_flowCollections.TryPeek(out var delimiter))
        {
            var collection = delimiter == ']' ? "sequence" : "map";
            throw new YamlException(ResourceYamlDiagnostics.Format(_yaml,
                $"Flow {collection} in block collection must be sufficiently indented and end with a {delimiter}",
                (int)_scanner.CurrentPosition.Index), error);
        }
    }
}
