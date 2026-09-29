using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Premagentic.Core.Embeddings;
using Premagentic.Core.Extensions;
using Premagentic.Core.Identity;
using Premagentic.Core.Identity.SignIn;
using Premagentic.Core.Ingestion;
using Premagentic.Core.Ingestion.Readers;
using Premagentic.Core.Storage;

namespace Premagentic.Tests;

/// <summary>
/// The API side of the composition point: the service it serves from builds
/// its chunker registry out of the one extension host, which is the registry
/// the portal reads from request services. With the CLI's half in
/// <see cref="ExtensionsCommandTests"/>, this is what "one composition point"
/// means in the two places that used to default to the built-ins on their own.
/// Requires a running Docker daemon.
/// </summary>
public sealed class ApiExtensionsTests(DatastoreTestDatabase server) : IClassFixture<DatastoreTestDatabase>
{
    [Fact]
    public async Task The_api_serves_the_chunkers_an_allowed_extension_brought()
    {
        var connection = await server.CreateDatabaseAsync();
        await using var db = new PremagenticDatabase(connection);
        await db.InitializeAsync();
        var tenant = await db.EnsureTenantAsync(ApiTestHost.TenantKey, "T");

        var folder = Directory.CreateTempSubdirectory("prem-api-extensions-");
        try
        {
            var allowed = SampleExtension.InstallInto(folder.FullName);
            // Through the settings, not the environment: this is how an
            // installed deployment says where its extensions are.
            var settings = new SettingsStore(db, tenant);
            await settings.SetAsync(ExtensionSettings.Folder, JsonSerializer.SerializeToElement(folder.FullName));
            await settings.SetAsync(ExtensionSettings.Allowed, ExtensionSettings.ToStored([allowed]));

            await using var api = ApiTestHost.Start(connection, TimeProvider.System, new HashEmbeddingProvider());

            var extensions = api.Services.GetRequiredService<ExtensionHost>();
            Assert.Empty(extensions.Refused);
            Assert.Equal(SampleExtension.Name, Assert.Single(extensions.Loaded).Name);

            var chunkers = api.Services.GetRequiredService<ChunkerRegistry>();
            Assert.Contains(SampleExtension.ChunkerName, chunkers.Names);
            Assert.Contains(ChunkerRegistry.DefaultName, chunkers.Names);

            // And the readers, which is the half that was missing: the registry
            // the portal takes from request services reads the extension's
            // format as well as the built-in ones.
            var readers = api.Services.GetRequiredService<ReaderRegistry>();
            Assert.Contains(SampleExtension.ReaderName, readers.Names);
            Assert.NotNull(readers.ForPath("prices" + SampleExtension.ReaderExtension));
            foreach (var builtIn in ReaderRegistry.BuiltIn.Names) Assert.Contains(builtIn, readers.Names);
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    /// <summary>
    /// The sign-in seam, end to end. The sample's adapter claims nothing, so
    /// the thing to prove is not what it decided but that it was asked: a host
    /// that collected an adapter and never put it in front of a request would
    /// pass every assertion about the host and still leave the seam dead. The
    /// adapter counts the requests it is asked about, and the count is read
    /// through the load context it lives in.
    /// </summary>
    [Fact]
    public async Task A_sign_in_adapter_an_extension_brought_is_asked_about_a_request()
    {
        var connection = await server.CreateDatabaseAsync();
        await using var db = new PremagenticDatabase(connection);
        await db.InitializeAsync();
        var tenant = await db.EnsureTenantAsync(ApiTestHost.TenantKey, "T");

        var folder = Directory.CreateTempSubdirectory("prem-api-signin-");
        try
        {
            var allowed = SampleExtension.InstallInto(folder.FullName);
            var settings = new SettingsStore(db, tenant);
            await settings.SetAsync(ExtensionSettings.Folder, JsonSerializer.SerializeToElement(folder.FullName));
            await settings.SetAsync(ExtensionSettings.Allowed, ExtensionSettings.ToStored([allowed]));

            await using var api = ApiTestHost.Start(connection, TimeProvider.System, new HashEmbeddingProvider());

            var adapter = Assert.Single(api.Services.GetRequiredService<ExtensionHost>().SignInAdapters);
            Assert.Equal(SampleExtension.SignInAdapterName, adapter.Name);
            var before = TimesAsked(adapter);

            // A request carrying no session, no token and no trusted header:
            // neither built-in way claims it, so the extension's adapter is
            // asked, which is exactly the case a dead seam would sail through.
            using var client = api.Client();
            var response = await Api.SearchAsync(client);

            Assert.True(TimesAsked(adapter) > before,
                "the extension's sign-in adapter was never asked about the request, so the seam is not wired");
            // And it claimed nothing, so the request has no caller and is
            // refused, exactly as on a deployment with no extension at all.
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    /// <summary>
    /// The sample adapter's own count. It lives in the extension's load
    /// context, so this test assembly has no type for it and asks by name.
    /// </summary>
    private static int TimesAsked(ISignInAdapter adapter) =>
        (int)adapter.GetType().GetProperty("TimesAsked")!.GetValue(adapter)!;

    /// <summary>An adapter that signs Dan in and reports one group from his directory.</summary>
    private sealed class DirectoryAdapter : ISignInAdapter
    {
        public const string Group = "CONTOSO-Night-shift-7c2e";

        public string Name => "directory";

        public Task<SignInResolution?> ResolveAsync(ISignInRequest request, CancellationToken ct = default) =>
            Task.FromResult<SignInResolution?>(new SignInResolution("dan", [Group]));
    }

    /// <summary>
    /// The principal mapper seam, end to end, through the test extension that
    /// registers a mapper and nothing else. Its mapper maps nothing, so the thing to prove is that the host composed it and put it
    /// in front of a request whose adapter reported a group, and that the
    /// start and the log name it. A host that collected the mapper and never
    /// asked it would pass every assertion about the host and leave the seam
    /// dead.
    /// </summary>
    [Fact]
    public async Task A_principal_mapper_an_extension_brought_is_asked_about_a_reported_group_and_named()
    {
        var folder = Directory.CreateTempSubdirectory("prem-api-mapper-");
        try
        {
            var allowed = InertMapperExtension.InstallInto(folder.FullName);
            var logs = new List<string>();
            await using var w = await ApiWorld.NewAsync(server,
                options: new ApiHostOptions { ExtraSignInAdapters = [new DirectoryAdapter()], Logs = logs },
                beforeStart: async (db, tenant) =>
                {
                    var settings = new SettingsStore(db, tenant);
                    await settings.SetAsync(ExtensionSettings.Folder, JsonSerializer.SerializeToElement(folder.FullName));
                    await settings.SetAsync(ExtensionSettings.Allowed, ExtensionSettings.ToStored([allowed]));
                });

            var mapper = w.Host.Services.GetRequiredService<ExtensionHost>().PrincipalMapper;
            Assert.Equal(InertMapperExtension.MapperName, mapper?.Name);
            var before = InertMapperExtension.TimesAsked(mapper!);

            using var client = w.Host.Client();
            var paths = await Api.PathsAsync(await Api.SearchAsync(client));

            Assert.True(InertMapperExtension.TimesAsked(mapper!) > before,
                "the extension's principal mapper was never asked about the reported group, so the seam is not wired");
            // It mapped nothing, so Dan reads what anyone reads.
            Assert.Equal([ApiWorld.Handbook], paths);

            string[] lines;
            lock (logs) lines = [.. logs];
            // Named in a note, which the API logs as information and the
            // command line does not print on every call.
            Assert.Contains(lines, l => l.StartsWith("Information ", StringComparison.Ordinal) && l.EndsWith(
                "What groups and principals from outside mean here is decided by an extension, the principal mapper " +
                $"{InertMapperExtension.MapperName}. It can map them only into live groups an administrator made.",
                StringComparison.Ordinal));
            Assert.Contains(lines, l => l.EndsWith(
                $"Sign-in adapter directory reported the group {DirectoryAdapter.Group}, which the principal mapper " +
                $"{InertMapperExtension.MapperName} maps to no group here, so it was ignored.",
                StringComparison.Ordinal));
            Assert.DoesNotContain(lines, l => l.Contains(ExtensionHosting.NoMapperSentence, StringComparison.Ordinal));
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task With_nothing_allowed_the_api_serves_exactly_the_built_in_chunkers()
    {
        var connection = await server.CreateDatabaseAsync();
        await using var db = new PremagenticDatabase(connection);
        await db.InitializeAsync();
        var tenant = await db.EnsureTenantAsync(ApiTestHost.TenantKey, "T");

        var folder = Directory.CreateTempSubdirectory("prem-api-extensions-");
        try
        {
            SampleExtension.InstallInto(folder.FullName);
            await new SettingsStore(db, tenant)
                .SetAsync(ExtensionSettings.Folder, JsonSerializer.SerializeToElement(folder.FullName));

            await using var api = ApiTestHost.Start(connection, TimeProvider.System, new HashEmbeddingProvider());

            var extensions = api.Services.GetRequiredService<ExtensionHost>();
            Assert.Equal(ExtensionRefusal.NotAllowed, Assert.Single(extensions.Refused).Reason);
            Assert.Empty(extensions.Loaded);
            Assert.Equal(ChunkerRegistry.BuiltIn.Names, api.Services.GetRequiredService<ChunkerRegistry>().Names);
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }
}
