using Microsoft.AspNetCore.Http;

namespace Premagentic.Portal.Pages;

/// <summary>
/// Serves the portal's stylesheet, script, fonts, logo mark, logo loop and
/// favicon from the assembly. Nothing else is served, and nothing from another
/// origin is ever needed, so the portal works on a network with no route out.
/// <para>
/// The two Geist faces are the website's, under the SIL Open Font License,
/// whose text ships beside them as <c>OFL-Geist.txt</c>.
/// </para>
/// </summary>
internal static class Assets
{
    private static readonly Dictionary<string, (byte[] Bytes, string ContentType)> Files = Load();

    public static IResult Serve(string name) =>
        Files.TryGetValue(name, out var file) ? Results.Bytes(file.Bytes, file.ContentType) : Results.NotFound();

    private static Dictionary<string, (byte[], string)> Load()
    {
        const string prefix = "Premagentic.Portal.Assets.";
        var assembly = typeof(Assets).Assembly;
        var files = new Dictionary<string, (byte[], string)>(StringComparer.Ordinal);
        foreach (var resource in assembly.GetManifestResourceNames().Where(r => r.StartsWith(prefix, StringComparison.Ordinal)))
        {
            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            var name = resource[prefix.Length..];
            var type = Path.GetExtension(name) switch
            {
                ".css" => "text/css; charset=utf-8",
                ".js" => "text/javascript; charset=utf-8",
                ".woff2" => "font/woff2",
                ".png" => "image/png",
                ".mp4" => "video/mp4",
                ".txt" => "text/plain; charset=utf-8",
                _ => "application/octet-stream",
            };
            files[name] = (copy.ToArray(), type);
        }
        return files;
    }
}
