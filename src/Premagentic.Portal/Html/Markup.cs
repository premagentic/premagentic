using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Encodings.Web;

namespace Premagentic.Portal.Html;

/// <summary>
/// HTML that is already safe to write as it is. The only ways to make one are
/// <see cref="M.H"/>, which encodes every value interpolated into it, and the
/// few helpers here that build tags from encoded parts, so text from a user, a
/// file or the database can never become markup by accident.
/// </summary>
public readonly struct Markup
{
    private readonly string? _value;

    internal Markup(string value) => _value = value;

    public static Markup Empty => new("");

    public static Markup Join(IEnumerable<Markup> parts) => new(string.Concat(parts.Select(p => p._value)));

    public override string ToString() => _value ?? "";
}

/// <summary>
/// Builds a <see cref="Markup"/> from an interpolated string: the literal parts
/// are written as they are, and every value is HTML-encoded unless it is itself
/// a <see cref="Markup"/>.
/// </summary>
[InterpolatedStringHandler]
public ref struct MarkupHandler
{
    private readonly StringBuilder _builder;

    public MarkupHandler(int literalLength, int formattedCount) =>
        _builder = new StringBuilder(literalLength + formattedCount * 16);

    public void AppendLiteral(string literal) => _builder.Append(literal);

    public void AppendFormatted(Markup markup) => _builder.Append(markup.ToString());

    public void AppendFormatted<T>(T value) =>
        _builder.Append(HtmlEncoder.Default.Encode(Convert.ToString(value, CultureInfo.InvariantCulture) ?? ""));

    public void AppendFormatted<T>(T value, string? format) where T : IFormattable =>
        _builder.Append(HtmlEncoder.Default.Encode(value.ToString(format, CultureInfo.InvariantCulture)));

    internal readonly Markup Build() => new(_builder.ToString());
}

/// <summary>The one way to write markup: <c>M.H($"&lt;td&gt;{name}&lt;/td&gt;")</c> encodes <c>name</c>.</summary>
public static class M
{
    public static Markup H(ref MarkupHandler handler) => handler.Build();

    /// <summary>One piece of markup per item, joined.</summary>
    public static Markup Each<T>(IEnumerable<T> items, Func<T, Markup> render) => Markup.Join(items.Select(render));

    /// <summary>A link to a portal path, with the query values escaped.</summary>
    public static string Url(string path, params (string Name, string? Value)[] query)
    {
        var present = query.Where(q => !string.IsNullOrEmpty(q.Value)).ToArray();
        return present.Length == 0
            ? path
            : path + "?" + string.Join("&", present.Select(q => $"{Uri.EscapeDataString(q.Name)}={Uri.EscapeDataString(q.Value!)}"));
    }

    /// <summary>One path segment, escaped, for a name inside a URL.</summary>
    public static string Segment(string value) => Uri.EscapeDataString(value);
}
