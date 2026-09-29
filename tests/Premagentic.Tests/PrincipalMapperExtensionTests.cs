using Premagentic.Cli.Admin;
using Premagentic.Core.Extensions;
using Premagentic.Core.Storage;

namespace Premagentic.Tests;

/// <summary>
/// A principal mapper through the real extension host: the test extension
/// that registers one and nothing else, built by this solution, copied into a
/// temp folder and loaded from there by hash. It loads; a second one is
/// refused for being a second mapper, since it shares nothing else with the
/// first; one that does not declare the seam is refused; and the loaded one
/// is named, with its extension, where an administrator looks: <c>prem
/// extensions list</c> and the health page. Requires a running Docker daemon
/// for the two that read a database.
/// </summary>
[Collection(ConsoleCollection.Name)]
public sealed class PrincipalMapperExtensionTests(DatastoreTestDatabase server) : IClassFixture<DatastoreTestDatabase>, IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("prem-mapper-extension-");

    public void Dispose()
    {
        try
        {
            _root.Delete(recursive: true);
        }
        catch (IOException)
        {
            // A folder a test could not remove is the temp folder's to clean.
        }
    }

    [Fact]
    public void An_allowed_extension_brings_its_principal_mapper_and_one_nobody_allowed_brings_none()
    {
        var allowed = InertMapperExtension.InstallInto(_root.FullName);

        var host = ExtensionHost.Load(_root.FullName, [allowed]);

        Assert.Empty(host.Refused);
        Assert.Equal(InertMapperExtension.Name, Assert.Single(host.Loaded).Name);
        Assert.Equal(InertMapperExtension.MapperName, host.PrincipalMapper?.Name);
        Assert.Equal(InertMapperExtension.Name, host.PrincipalMapperExtension);

        // The control: the same folder, allowed by nobody.
        var none = ExtensionHost.Load(_root.FullName, []);
        Assert.Equal(ExtensionRefusal.NotAllowed, Assert.Single(none.Refused).Reason);
        Assert.Null(none.PrincipalMapper);
        Assert.Null(none.PrincipalMapperExtension);
    }

    /// <summary>
    /// One principal mapper per deployment. The second extension is the same
    /// assembly under another name, so it brings a mapper and nothing else:
    /// it can be refused for its mapper only.
    /// </summary>
    [Fact]
    public void A_second_extension_that_brings_a_principal_mapper_is_refused_for_it()
    {
        var first = InertMapperExtension.InstallInto(_root.FullName, "a-first");
        var second = InertMapperExtension.InstallInto(_root.FullName, "b-second", manifestName: "inert-mapper-two");

        var host = ExtensionHost.Load(_root.FullName, [first, second]);

        Assert.Equal(Path.Combine(_root.FullName, "a-first"), Assert.Single(host.Loaded).Folder);
        var refused = Assert.Single(host.Refused);
        Assert.Equal("inert-mapper-two", refused.Name);
        Assert.Equal(ExtensionRefusal.NameTaken, refused.Reason);
        Assert.Equal(
            $"A principal mapper, \"{InertMapperExtension.MapperName}\", is already registered by the extension " +
            $"\"{InertMapperExtension.Name}\", and a deployment has one. This one brought \"{InertMapperExtension.MapperName}\".",
            refused.Detail);
        Assert.Equal(InertMapperExtension.Name, host.PrincipalMapperExtension);
    }

    /// <summary>
    /// A new seam is declared, as the command and setting seams are, so an
    /// older PremAgentic refuses the extension by the seam's name rather than
    /// failing it on a member it does not have. The control is the same folder
    /// declaring it, which loads.
    /// </summary>
    [Fact]
    public void An_extension_that_brings_a_principal_mapper_and_does_not_declare_the_seam_is_refused()
    {
        var allowed = InertMapperExtension.InstallInto(_root.FullName, "undeclared", declarePrincipals: false);

        var host = ExtensionHost.Load(_root.FullName, [allowed]);

        var refused = Assert.Single(host.Refused);
        Assert.Equal(ExtensionRefusal.DidNotLoad, refused.Reason);
        Assert.Equal(
            "It registers a principal mapper and its manifest does not declare the \"principals\" seam, which an older " +
            "PremAgentic needs to refuse it by name.",
            refused.Detail);
        Assert.Empty(host.Loaded);
        Assert.Null(host.PrincipalMapper);

        var declared = InertMapperExtension.InstallInto(_root.FullName, "undeclared");
        Assert.Equal(InertMapperExtension.MapperName, ExtensionHost.Load(_root.FullName, [declared]).PrincipalMapper?.Name);
    }

    [Fact]
    public async Task Prem_extensions_list_names_the_principal_mapper_and_the_extension_it_came_from()
    {
        var db = new PremagenticDatabase(await server.CreateDatabaseAsync());
        await using var _ = db;
        await db.InitializeAsync();
        var tenant = await db.EnsureTenantAsync("t", "T");
        var host = ExtensionHost.Load(_root.FullName, [InertMapperExtension.InstallInto(_root.FullName)]);

        var listed = await ConsoleCapture.RunAsync(() => ExtensionsCommands.RunAsync(["extensions", "list"], db, tenant, host));

        Assert.Equal(0, listed.Exit);
        Assert.Contains(
            $"    brings the principal mapper {InertMapperExtension.MapperName}, which decides what groups and principals from outside mean here",
            listed.Out.ReplaceLineEndings("\n").Split('\n'));
    }

    [Fact]
    public async Task The_health_page_names_the_principal_mapper_and_its_extension_and_says_when_there_is_none()
    {
        var host = ExtensionHost.Load(_root.FullName, [InertMapperExtension.InstallInto(_root.FullName)]);
        await using (var loaded = await PortalWorld.NewAsync(server, okfBundle: false, options: new ApiHostOptions { Extensions = host }))
        {
            var page = await loaded.TextAsync(await loaded.GetAsync("/portal/health", loaded.Auditor));
            Assert.Contains(
                $"<p>The principal mapper <code>{InertMapperExtension.MapperName}</code>, from the extension " +
                $"<code>{InertMapperExtension.Name}</code>, decides what groups and principals from outside mean here.</p>",
                page);
        }

        // The control: a folder whose extension loads and brings no mapper.
        var other = Directory.CreateTempSubdirectory("prem-no-mapper-").FullName;
        var sample = ExtensionHost.Load(other, [SampleExtension.InstallInto(other)]);
        await using var without = await PortalWorld.NewAsync(server, okfBundle: false, options: new ApiHostOptions { Extensions = sample });
        Assert.Contains(
            "<p>No principal mapper is loaded, so groups and principals from outside mean nothing here.</p>",
            await without.TextAsync(await without.GetAsync("/portal/health", without.Auditor)));
    }
}
