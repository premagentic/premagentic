using System.Reflection;
using Premagentic.Core.Extensions;

namespace Premagentic.Tests;

/// <summary>
/// Which copy of the contracts' library an extension gets, whatever version its
/// reference names. A release stamps its own version on that library, so a reader
/// built from the same tree with another version still loads when it asks for this
/// version or an older one, and is refused by name when it asks for a newer one.
/// </summary>
public sealed class ExtensionCoreVersionTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("premagentic-core-version-");

    public void Dispose() => _root.Delete(recursive: true);

    private static Assembly Host => typeof(IExtension).Assembly;
    private static Version HostVersion => Host.GetName().Version!;

    private ExtensionLoadContext NewContext() =>
        new("invented-reader", Path.Combine(_root.FullName, "Invented.Reader.dll"), []);

    private static Assembly? Load(ExtensionLoadContext context, AssemblyName name) =>
        (Assembly?)typeof(ExtensionLoadContext)
            .GetMethod("Load", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(context, [name]);

    private static AssemblyName Core(Version? version) => new(Host.GetName().Name!) { Version = version };

    [Fact]
    public void An_extension_built_against_this_version_or_an_older_one_gets_the_host_library()
    {
        foreach (var version in new[] { new Version(0, 0, 0, 0), HostVersion, null })
        {
            var context = NewContext();

            Assert.Same(Host, Load(context, Core(version)));
            Assert.Null(context.Refusal);
        }
    }

    [Fact]
    public void An_extension_built_against_a_newer_version_is_refused_by_name()
    {
        var newer = new Version(HostVersion.Major, HostVersion.Minor + 1, 0, 0);
        foreach (var version in new[] { newer, new Version(9, 9, 9, 9) })
        {
            var context = NewContext();

            Assert.Null(Load(context, Core(version)));
            Assert.Equal(ExtensionRefusal.CoreTooNew, context.Refusal?.Reason);
            Assert.Contains($"built against PremAgentic {version.Major}.{version.Minor}.{version.Build}", context.Refusal?.Detail);
            Assert.Contains($"this is {HostVersion.Major}.{HostVersion.Minor}.{HostVersion.Build}", context.Refusal?.Detail);
        }
    }

    [Fact]
    public void A_name_the_host_does_not_have_still_goes_to_the_extensions_own_files()
    {
        var context = NewContext();

        // Not the contracts' library, not in this process, and not listed: nothing
        // is loaded, and nothing is refused, because the file is not in the folder.
        Assert.Null(Load(context, new AssemblyName("Invented.Library") { Version = new Version(9, 9, 9, 9) }));
        Assert.Null(context.Refusal);
    }

    [Fact]
    public void The_refusal_has_its_own_words()
    {
        Assert.Equal("core too new", ExtensionHosting.Reason(ExtensionRefusal.CoreTooNew));
    }
}
