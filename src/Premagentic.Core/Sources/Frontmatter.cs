using System.Text;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.RepresentationModel;

namespace Premagentic.Core.Sources;

/// <summary>
/// Whether a document opened with a frontmatter block, and whether that block
/// could be read. The numbers are stored, so they never change.
/// </summary>
public enum FrontmatterState
{
    /// <summary>The document does not start with a frontmatter block.</summary>
    Absent = 0,

    /// <summary>A block was found and read.</summary>
    Parsed = 1,

    /// <summary>
    /// A block was found and could not be read: invalid YAML, no closing
    /// delimiter, or a structure refused by the limits in <see cref="Frontmatter"/>.
    /// </summary>
    Unparseable = 2,
}

/// <param name="Fields">Scalar top-level fields only, keyed case-insensitively.</param>
/// <param name="Body">The document after the frontmatter block, or all of it when there is no readable block.</param>
public sealed record FrontmatterResult(IReadOnlyDictionary<string, string> Fields, string Body)
{
    internal static readonly IReadOnlyDictionary<string, object> NoValues =
        new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

    public FrontmatterState State { get; init; } = FrontmatterState.Absent;

    /// <summary>
    /// Every top-level field, nested ones included. A scalar is a
    /// <see cref="string"/>, a sequence an <see cref="IReadOnlyList{T}"/> of
    /// values, and a mapping an <see cref="IReadOnlyDictionary{TKey, TValue}"/>
    /// keyed case-insensitively like <see cref="Fields"/>. Entries whose key is
    /// not a scalar are left out. Values are text as written; nothing here is
    /// interpreted or run.
    /// </summary>
    public IReadOnlyDictionary<string, object> Values { get; init; } = NoValues;
}

public static class Frontmatter
{
    // Limits on the shape of a block, checked on the parser's event stream
    // before the block is loaded. Anchors and aliases let a few bytes describe
    // a structure that contains itself or expands exponentially, and the
    // loader resolves them into a node graph that hashing and walking cannot
    // finish. A key that refers to itself, or a few thousand levels of
    // nesting, overflows the stack inside the loader, which no catch block can
    // recover from. Honest frontmatter is a few dozen values a few levels
    // deep, so these limits sit far above any real document. The bytes of text
    // are counted with every alias expanded too: a few aliases of one long
    // anchored value are few nodes and a great deal of text for everything
    // that reads the values afterwards.
    private const int MaxDepth = 32;
    private const long MaxNodes = 100_000;
    private const long MaxExpandedBytes = 1_048_576;

    /// <summary>
    /// Splits a leading YAML frontmatter block (--- ... ---) from a Markdown document.
    /// <see cref="FrontmatterResult.Fields"/> holds scalar top-level fields and
    /// <see cref="FrontmatterResult.Values"/> holds every top-level field.
    /// Malformed YAML degrades to no-frontmatter rather than throwing, since a bad note
    /// must not abort a whole ingest run.
    /// </summary>
    public static FrontmatterResult Parse(string markdown)
    {
        var empty = new FrontmatterResult(new Dictionary<string, string>(), markdown);
        if (!markdown.StartsWith("---", StringComparison.Ordinal)) return empty;

        var unparseable = empty with { State = FrontmatterState.Unparseable };

        var afterFirst = markdown.IndexOf('\n');
        if (afterFirst < 0) return unparseable;

        var end = markdown.IndexOf("\n---", afterFirst, StringComparison.Ordinal);
        if (end < 0) return unparseable;

        var yamlText = markdown[(afterFirst + 1)..end];
        var bodyStart = markdown.IndexOf('\n', end + 1);
        var body = bodyStart < 0 ? string.Empty : markdown[(bodyStart + 1)..];

        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var values = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!WithinLimits(yamlText)) return unparseable;

            var yaml = new YamlStream();
            yaml.Load(new StringReader(yamlText));
            if (yaml.Documents.Count > 0 && yaml.Documents[0].RootNode is YamlMappingNode map)
            {
                foreach (var (key, value) in map.Children)
                {
                    if (key is not YamlScalarNode { Value: { } k }) continue;
                    if (value is YamlScalarNode v) fields[k] = v.Value ?? string.Empty;
                    if (ToValue(value) is { } converted) values[k] = converted;
                }
            }
        }
        catch (YamlException)
        {
            return unparseable;
        }

        return new FrontmatterResult(fields, body) { State = FrontmatterState.Parsed, Values = values };
    }

    /// <summary>
    /// Walks the parser's events without building nodes and reports whether the
    /// block stays inside <see cref="MaxDepth"/> as it is parsed, and inside
    /// <see cref="MaxNodes"/> and <see cref="MaxExpandedBytes"/> with every
    /// alias expanded, and whether any alias refers to a node that is still
    /// open, which is a cycle.
    /// </summary>
    private static bool WithinLimits(string yamlText)
    {
        var parser = new Parser(new StringReader(yamlText));

        // Each open collection, with its anchor and its expanded size so far.
        var open = new Stack<(AnchorName Anchor, Size Size)>();
        // Expanded size of each anchored node, or null while it is still open.
        var anchors = new Dictionary<AnchorName, Size?>();
        long nodes = 0, bytes = 0;

        while (parser.MoveNext())
        {
            Size added;
            switch (parser.Current)
            {
                case Scalar scalar:
                    added = new Size(1, Encoding.UTF8.GetByteCount(scalar.Value));
                    if (!scalar.Anchor.IsEmpty) anchors[scalar.Anchor] = added;
                    break;

                case AnchorAlias alias:
                    if (!anchors.TryGetValue(alias.Value, out var size) || size is null) return false;
                    added = size.Value;
                    break;

                case NodeEvent start when start is SequenceStart or MappingStart:
                    if (open.Count >= MaxDepth) return false;
                    if (!start.Anchor.IsEmpty) anchors[start.Anchor] = null;
                    open.Push((start.Anchor, new Size(1, 0)));
                    if (++nodes > MaxNodes) return false;
                    continue;

                case SequenceEnd or MappingEnd:
                    var closed = open.Pop();
                    if (!closed.Anchor.IsEmpty) anchors[closed.Anchor] = closed.Size;
                    AddToParent(open, closed.Size);
                    continue;

                default:
                    continue;
            }

            nodes += added.Nodes;
            if (nodes > MaxNodes) return false;
            bytes += added.Bytes;
            if (bytes > MaxExpandedBytes) return false;
            AddToParent(open, added);
        }

        return true;
    }

    /// <summary>A node's expanded size: how many nodes, and how many UTF-8 bytes of scalar text.</summary>
    private readonly record struct Size(long Nodes, long Bytes);

    private static void AddToParent(Stack<(AnchorName Anchor, Size Size)> open, Size size)
    {
        if (open.Count == 0) return;
        var parent = open.Pop();
        open.Push((parent.Anchor, new Size(parent.Size.Nodes + size.Nodes, parent.Size.Bytes + size.Bytes)));
    }

    private static object? ToValue(YamlNode node)
    {
        switch (node)
        {
            case YamlScalarNode scalar:
                return scalar.Value ?? string.Empty;

            case YamlSequenceNode sequence:
                var items = new List<object>(sequence.Children.Count);
                foreach (var child in sequence.Children)
                    if (ToValue(child) is { } item) items.Add(item);
                return items;

            case YamlMappingNode mapping:
                var entries = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                foreach (var (key, value) in mapping.Children)
                    if (key is YamlScalarNode { Value: { } k } && ToValue(value) is { } v) entries[k] = v;
                return entries;

            default:
                return null;
        }
    }
}
