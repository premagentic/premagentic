using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Premagentic.Core.Admin;
using Premagentic.Core.Extensions;
using Premagentic.Core.Identity;
using Premagentic.Core.Storage;

namespace Premagentic.Tests;

/// <summary>
/// <c>prem extensions</c> as a person runs it, against the sample extension in
/// a folder of its own. This is also where the composition point is proven: the
/// console gets its chunkers from the one extension host, so an extension an
/// administrator allows is usable from <c>prem sources chunkers</c> without
/// being registered anywhere by hand. Requires a running Docker daemon.
/// </summary>
public sealed class ExtensionsCommandTests(DatastoreTestDatabase server) : IClassFixture<DatastoreTestDatabase>
{
    [Fact]
    public async Task An_extension_is_refused_until_it_is_allowed_and_then_the_console_can_cut_with_it()
    {
        var connection = await server.CreateDatabaseAsync();
        await using var db = new PremagenticDatabase(connection);
        await db.InitializeAsync();
        var tenant = await db.EnsureTenantAsync("t", "T");

        var extensions = Directory.CreateTempSubdirectory("prem-cli-extensions-");
        try
        {
            var allowed = SampleExtension.InstallInto(extensions.FullName);
            var folder = Path.Combine(extensions.FullName, SampleExtension.Name);

            // Installed and allowed by nobody. The deployment runs, says why,
            // and has exactly the built-in chunkers.
            var refused = await RunAsync(connection, extensions.FullName, "extensions", "list");
            Assert.Contains("not allowed", refused);
            Assert.Contains("Loaded: none.", refused);
            Assert.DoesNotContain(SampleExtension.ChunkerName, await RunAsync(connection, extensions.FullName, "sources", "chunkers"));

            // Allowing measures the folder rather than believing its manifest.
            var allow = await RunAsync(connection, extensions.FullName, "extensions", "allow", folder);
            Assert.Contains($"Allowed {SampleExtension.Name} with hash {allowed.Sha256}", allow);

            var stored = await new SettingsStore(db, tenant).GetAsync(ExtensionSettings.Allowed);
            Assert.Equal([allowed], ExtensionSettings.AllowedFrom(stored, out var problem));
            Assert.Null(problem);
            Assert.Contains(
                await new ChangeRecord(db, tenant).ListAsync(),
                e => e.Target == ExtensionSettings.Allowed && e.Kind == ExtensionAllowList.ChangeKind);

            // Allowing again changes nothing and records nothing.
            Assert.Contains("was already allowed", await RunAsync(connection, extensions.FullName, "extensions", "allow", folder));
            Assert.Single(await new ChangeRecord(db, tenant).ListAsync());

            // The composition point: one host builds the registries, so the
            // console has the chunker the extension brought.
            var listed = await RunAsync(connection, extensions.FullName, "extensions", "list");
            Assert.Contains($"{SampleExtension.Name} 0.1.0", listed);
            Assert.Contains("Refused: none.", listed);
            var chunkers = await RunAsync(connection, extensions.FullName, "sources", "chunkers");
            Assert.Contains(SampleExtension.ChunkerName, chunkers);
            Assert.Contains("markdown (the default)", chunkers);

            // Disallowed, and it stops loading at the next start.
            Assert.Contains("No longer allowed", await RunAsync(connection, extensions.FullName, "extensions", "disallow", SampleExtension.Name));
            Assert.DoesNotContain(SampleExtension.ChunkerName, await RunAsync(connection, extensions.FullName, "sources", "chunkers"));
            Assert.Contains("not allowed", await RunAsync(connection, extensions.FullName, "extensions", "list"));
        }
        finally
        {
            extensions.Delete(recursive: true);
        }
    }

    /// <summary>
    /// The reader half of the composition point, which is the half that was
    /// missing: an extension's reader has to reach the ingest pipeline, or the
    /// extension loads, is listed as loaded, and indexes nothing. The control
    /// is the same folder and the same file with the extension not allowed,
    /// where the file is counted as one the run did not read.
    /// </summary>
    [Fact]
    public async Task An_extensions_reader_is_used_by_ingest_and_without_it_the_file_is_not_read()
    {
        var connection = await server.CreateDatabaseAsync();
        await using var db = new PremagenticDatabase(connection);
        await db.InitializeAsync();
        await db.EnsureTenantAsync("t", "T");

        var extensions = Directory.CreateTempSubdirectory("prem-reader-extensions-");
        var corpus = Directory.CreateTempSubdirectory("prem-reader-corpus-");
        try
        {
            SampleExtension.InstallInto(extensions.FullName);
            var folder = Path.Combine(extensions.FullName, SampleExtension.Name);

            // One file a built-in reader claims, so the run always indexes
            // something, and one only the extension can read.
            await File.WriteAllTextAsync(Path.Combine(corpus.FullName, "yard.md"),
                "# Yard\n\nThe gate is locked at six.\n");
            await File.WriteAllTextAsync(Path.Combine(corpus.FullName, "rates.csv"),
                "code,description\nBX7,\"the blue hangar, north end\"\n");

            // Allowed by nobody: nothing reads .csv, the file is counted as one
            // the run did not read, and no search can reach it.
            var without = await RunAsync(connection, extensions.FullName, "ingest", corpus.FullName, "--public");
            Assert.Contains("skipped 1", without);
            Assert.Contains(SampleExtension.ReaderExtension, without);
            Assert.DoesNotContain("rates.csv", await RunAsync(connection, extensions.FullName, "search", "blue hangar north end"));

            // The same folder and the same file, with the extension allowed.
            await RunAsync(connection, extensions.FullName, "extensions", "allow", folder);
            var with = await RunAsync(connection, extensions.FullName, "ingest", corpus.FullName, "--public");
            Assert.Contains("skipped 0", with);
            Assert.Contains("rates.csv", await RunAsync(connection, extensions.FullName, "search", "blue hangar north end"));
        }
        finally
        {
            extensions.Delete(recursive: true);
            corpus.Delete(recursive: true);
        }
    }

    /// <summary>
    /// A managed library built for one platform makes the host refuse the
    /// extension at every start, so the allow refuses it first, with the
    /// host's sentence, and writes nothing, whether the manifest lists the
    /// library or not.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_managed_library_built_for_one_platform_is_refused_at_the_allow_and_nothing_is_written(bool listed)
    {
        var connection = await server.CreateDatabaseAsync();
        await using var db = new PremagenticDatabase(connection);
        await db.InitializeAsync();
        var tenant = await db.EnsureTenantAsync("t", "T");

        // Outside any extensions folder, so the host at the command's start
        // sees nothing to refuse, and a sentence on standard error can only be
        // the allow's.
        var staging = Directory.CreateTempSubdirectory("prem-cli-platform-");
        try
        {
            SampleExtension.InstallInto(staging.FullName);
            var folder = Path.Combine(staging.FullName, SampleExtension.Name);
            const string platformFile = "runtimes/linux-x64/lib/net10.0/Helper.dll";
            Directory.CreateDirectory(Path.Combine(folder, "runtimes", "linux-x64", "lib", "net10.0"));
            File.WriteAllBytes(Path.Combine([folder, .. platformFile.Split('/')]), Helper);
            if (listed) ListFiles(folder, platformFile);

            var (exit, output, errors) = await RunCliAsync(connection, extensionsFolder: null, "extensions", "allow", folder);

            Assert.NotEqual(0, exit);
            Assert.DoesNotContain("Allowed", output, StringComparison.Ordinal);
            Assert.Contains(ExtensionManifest.PlatformManagedRefusal(platformFile), errors, StringComparison.Ordinal);
            Assert.Contains(
                $"\"{platformFile}\" is a managed library built for one platform, and this version loads managed libraries " +
                "from beside the extension's assembly only.", errors, StringComparison.Ordinal);
            Assert.Empty(ExtensionSettings.AllowedFrom(await new SettingsStore(db, tenant).GetAsync(ExtensionSettings.Allowed), out _));
            Assert.Empty(await new ChangeRecord(db, tenant).ListAsync());
        }
        finally
        {
            staging.Delete(recursive: true);
        }
    }

    /// <summary>
    /// A file under <c>runtimes/</c> that the manifest does not list makes the
    /// host refuse the extension at every start, so the allow refuses it first,
    /// in the host's words, and writes nothing. The same file listed is the
    /// way round: allowed, and loaded at the next start.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_file_under_runtimes_the_manifest_does_not_list_is_refused_at_the_allow_and_a_listed_one_is_allowed(bool listed)
    {
        var connection = await server.CreateDatabaseAsync();
        await using var db = new PremagenticDatabase(connection);
        await db.InitializeAsync();
        var tenant = await db.EnsureTenantAsync("t", "T");

        var extensions = Directory.CreateTempSubdirectory("prem-cli-runtimes-");
        try
        {
            SampleExtension.InstallInto(extensions.FullName);
            var folder = Path.Combine(extensions.FullName, SampleExtension.Name);
            const string nativeFile = "runtimes/linux-x64/native/libhelper.so";
            Directory.CreateDirectory(Path.Combine(folder, "runtimes", "linux-x64", "native"));
            File.WriteAllBytes(Path.Combine([folder, .. nativeFile.Split('/')]), Helper);
            if (listed) ListFiles(folder, nativeFile);

            if (!listed)
            {
                // With no extensions folder set, the host at the command's
                // start sees nothing to refuse, and the sentence on standard
                // error can only be the allow's.
                var (exit, output, errors) = await RunCliAsync(connection, extensionsFolder: null, "extensions", "allow", folder);

                Assert.NotEqual(0, exit);
                Assert.DoesNotContain("Allowed", output, StringComparison.Ordinal);
                Assert.Contains(ExtensionManifest.UnlistedRuntimeRefusal(nativeFile), errors, StringComparison.Ordinal);
                Assert.Contains($"\"{nativeFile}\" is in its folder under runtimes/ and not in its manifest's \"files\" list.", errors, StringComparison.Ordinal);
                Assert.Empty(ExtensionSettings.AllowedFrom(await new SettingsStore(db, tenant).GetAsync(ExtensionSettings.Allowed), out _));
                Assert.Empty(await new ChangeRecord(db, tenant).ListAsync());
                return;
            }

            Assert.Contains($"Allowed {SampleExtension.Name} with hash", await RunAsync(connection, extensions.FullName, "extensions", "allow", folder));
            var loaded = await RunAsync(connection, extensions.FullName, "extensions", "list");
            Assert.Contains($"{SampleExtension.Name} 0.1.0", loaded);
            Assert.Contains("Refused: none.", loaded);
        }
        finally
        {
            extensions.Delete(recursive: true);
        }
    }

    /// <summary>
    /// The same library published for the platform, which puts it beside the
    /// assembly, is the way round the refusal names: allowed, and loaded at the
    /// next start.
    /// </summary>
    [Fact]
    public async Task The_same_library_published_beside_the_assembly_is_allowed_and_loads()
    {
        var connection = await server.CreateDatabaseAsync();
        await using var db = new PremagenticDatabase(connection);
        await db.InitializeAsync();
        await db.EnsureTenantAsync("t", "T");

        var extensions = Directory.CreateTempSubdirectory("prem-cli-published-");
        try
        {
            SampleExtension.InstallInto(extensions.FullName);
            var folder = Path.Combine(extensions.FullName, SampleExtension.Name);
            File.WriteAllBytes(Path.Combine(folder, "Helper.dll"), Helper);
            ListFiles(folder, "Helper.dll");

            Assert.Contains($"Allowed {SampleExtension.Name} with hash", await RunAsync(connection, extensions.FullName, "extensions", "allow", folder));
            var loaded = await RunAsync(connection, extensions.FullName, "extensions", "list");
            Assert.Contains($"{SampleExtension.Name} 0.1.0", loaded);
            Assert.Contains("Refused: none.", loaded);
        }
        finally
        {
            extensions.Delete(recursive: true);
        }
    }

    /// <summary>Invented bytes standing for a helper library, which nothing here loads.</summary>
    private static readonly byte[] Helper = "not a real helper library"u8.ToArray();

    /// <summary>Lists these files, each holding <see cref="Helper"/>, in the manifest of the extension in <paramref name="folder"/>.</summary>
    private static void ListFiles(string folder, params string[] files)
    {
        var path = Path.Combine(folder, ExtensionManifest.FileName);
        var manifest = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        var sha256 = Convert.ToHexStringLower(SHA256.HashData(Helper));
        manifest["files"] = new JsonArray(files.Select(f => (JsonNode)new JsonObject { ["file"] = f, ["sha256"] = sha256 }).ToArray());
        File.WriteAllText(path, manifest.ToJsonString());
    }

    [Fact]
    public async Task With_no_extensions_folder_the_console_says_so_and_has_the_built_ins()
    {
        var connection = await server.CreateDatabaseAsync();
        await using var db = new PremagenticDatabase(connection);
        await db.InitializeAsync();

        var listed = await RunAsync(connection, extensionsFolder: null, "extensions", "list");

        Assert.Contains("No extensions folder is set", listed);
        Assert.Contains("Loaded: none.", listed);
        Assert.Contains("Refused: none.", listed);
        Assert.Contains("markdown (the default)", await RunAsync(connection, null, "sources", "chunkers"));
    }

    /// <summary>
    /// Runs the real CLI and returns what it wrote to standard output. Errors
    /// are kept for the failure message and out of the returned text: every
    /// command warns there about each refused extension, and that warning names
    /// the extension, which a test asking whether a chunker is absent would
    /// find.
    /// </summary>
    private static async Task<string> RunAsync(string connection, string? extensionsFolder, params string[] arguments)
    {
        var (exit, output, errors) = await RunCliAsync(connection, extensionsFolder, arguments);
        Assert.True(exit == 0, $"exit {exit}{Environment.NewLine}{output}{errors}");
        return output;
    }

    /// <summary>Runs the real CLI and returns its exit code and both streams, for a command that is meant to fail.</summary>
    private static async Task<(int Exit, string Output, string Errors)> RunCliAsync(
        string connection, string? extensionsFolder, params string[] arguments)
    {
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(CliPath());
        foreach (var argument in arguments) start.ArgumentList.Add(argument);

        // Nothing from this process's own settings reaches the CLI.
        foreach (var name in start.Environment.Keys.Where(k => k.StartsWith("PREM_", StringComparison.OrdinalIgnoreCase)).ToArray())
            start.Environment.Remove(name);
        start.Environment["PREM_CONNECTION_STRING"] = connection;
        start.Environment["PREM_TENANT_KEY"] = "t";
        start.Environment["PREM_TENANT_NAME"] = "T";
        start.Environment["PREM_EMBEDDING_PROVIDER"] = "hash";
        if (extensionsFolder is not null) start.Environment[ExtensionSettings.FolderVariable] = extensionsFolder;

        using var process = Process.Start(start)!;
        // Both streams are read to their end before the exit code is looked at:
        // under load a tool's last line arrives after it exits.
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await stdout, await stderr);
    }

    private static string CliPath()
    {
        var path = typeof(ExtensionsCommandTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "Premagentic.Cli.Path")
            .Value;
        Assert.True(File.Exists(path), "the CLI was not built where the test project recorded it: " + path);
        return path!;
    }
}
