using System.Diagnostics;
using System.Reflection;
using System.Text;
using Premagentic.Cli.Admin;
using Premagentic.Cli.Setup;
using Premagentic.Core.Identity;
using Premagentic.Core.Storage;

namespace Premagentic.Tests;

/// <summary>
/// <c>--password-file</c> on <c>prem users add</c> and <c>prem users set-password</c>:
/// the password is the file's only line, the file is the only source when it
/// is named, a file that cannot be used is refused in words that never quote
/// it, the password is shown and kept nowhere, and the file is only read.
/// <c>setup --admin-password-file</c> reads through the same reader and keeps
/// its documented first-line rule. Requires a running Docker daemon.
/// </summary>
[Collection(ConsoleCollection.Name)]
public sealed class PasswordFileTests(DatastoreTestDatabase server) : IClassFixture<DatastoreTestDatabase>
{
    private sealed record World(PremagenticDatabase Db, Guid Tenant, string ConnectionString) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    private async Task<World> NewAsync()
    {
        var connectionString = await server.CreateDatabaseAsync();
        var db = new PremagenticDatabase(connectionString);
        await db.InitializeAsync();
        var tenant = await db.EnsureTenantAsync("t", "T");
        await new IdentityStore(db, tenant).CreateUserAsync("alice", "Alice", Role.Member);
        return new World(db, tenant, connectionString);
    }

    private static string FileHolding(string text) => FileHolding(Encoding.UTF8.GetBytes(text));

    private static string FileHolding(byte[] bytes)
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory("premagentic-password-").FullName, "password.txt");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static Task<(int Exit, string Out, string Err)> RunAsync(World w, params string[] args) =>
        ConsoleCapture.RunAsync(() => AdminCommands.RunAsync(args, w.Db, w.Tenant));

    private static async Task<string?> HashOfAsync(World w, string name)
    {
        await using var cmd = w.Db.DataSource.CreateCommand("SELECT password_hash FROM prem_config.app_user WHERE sign_in_name = @name");
        cmd.Parameters.AddWithValue("name", name);
        return await cmd.ExecuteScalarAsync() as string;
    }

    private static async Task<string> RecordTextAsync(World w)
    {
        await using var cmd = w.Db.DataSource.CreateCommand(
            "SELECT coalesce(string_agg(kind || ' ' || target || ' ' || coalesce(new_value::text, ''), E'\\n' ORDER BY id), '') FROM prem_config.admin_event");
        return (string)(await cmd.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task Set_password_takes_the_files_only_line_and_shows_the_password_nowhere()
    {
        await using var w = await NewAsync();
        const string password = " a long password with spaces ";
        // A byte order mark and a Windows line ending, as an editor may save it.
        var path = FileHolding([.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(password + "\r\n")]);
        var (bytesBefore, writtenBefore) = (File.ReadAllBytes(path), File.GetLastWriteTimeUtc(path));

        var run = await RunAsync(w, "users", "set-password", "alice", "--password-file", path);

        Assert.True(run.Exit == 0, run.Err);
        Assert.True(new PasswordHasher().Verify(password, await HashOfAsync(w, "alice")));
        Assert.DoesNotContain(password.Trim(), run.Out + run.Err, StringComparison.Ordinal);
        var record = await RecordTextAsync(w);
        Assert.Contains("user.password alice", record, StringComparison.Ordinal);
        Assert.DoesNotContain(password.Trim(), record, StringComparison.Ordinal);
        Assert.Equal(bytesBefore, File.ReadAllBytes(path));
        Assert.Equal(writtenBefore, File.GetLastWriteTimeUtc(path));
    }

    [Fact]
    public async Task Add_takes_a_password_file_and_refuses_it_beside_the_password_flag()
    {
        await using var w = await NewAsync();
        var path = FileHolding("carols long password\n");

        var added = await RunAsync(w, "users", "add", "carol", "--password-file", path);
        Assert.True(added.Exit == 0, added.Err);
        Assert.True(new PasswordHasher().Verify("carols long password", await HashOfAsync(w, "carol")));
        Assert.Contains("\"password_set\": true", await RecordTextAsync(w), StringComparison.Ordinal);

        var both = await RunAsync(w, "users", "add", "dave", "--password", "--password-file", path);
        Assert.Equal(1, both.Exit);
        Assert.Contains("Give --password or --password-file, not both.", both.Err, StringComparison.Ordinal);
        await using var cmd = w.Db.DataSource.CreateCommand("SELECT count(*) FROM prem_config.app_user WHERE sign_in_name = 'dave'");
        Assert.Equal(0L, await cmd.ExecuteScalarAsync());
    }

    [Fact]
    public async Task A_file_that_cannot_be_used_is_refused_in_words_that_never_quote_it_and_nothing_is_written()
    {
        await using var w = await NewAsync();
        var directory = Directory.CreateTempSubdirectory("premagentic-password-dir-").FullName;
        var missing = Path.Combine(directory, "no-such-file.txt");
        var cases = new (string Path, string Said)[]
        {
            (missing, $"Cannot read the password file {missing}: "),
            (directory, $"Cannot read the password file {directory}: "),
            (FileHolding(""), "holds no password on its first line."),
            (FileHolding("\nsecond-line-secret\n"), "holds no password on its first line."),
            (FileHolding("first-line-secret\nsecond-line-secret\n"), "holds more than one line. Put the password alone in the file."),
            (FileHolding("first-line-secret\n\n"), "holds more than one line. Put the password alone in the file."),
        };

        foreach (var (path, said) in cases)
        {
            var run = await RunAsync(w, "users", "set-password", "alice", "--password-file", path);
            Assert.Equal(1, run.Exit);
            Assert.Contains(said, run.Err, StringComparison.Ordinal);
            Assert.DoesNotContain("secret", run.Out + run.Err, StringComparison.Ordinal);
        }

        var bare = await RunAsync(w, "users", "set-password", "alice", "--password-file");
        Assert.Equal(1, bare.Exit);
        Assert.Contains("--password-file needs a file.", bare.Err, StringComparison.Ordinal);

        Assert.Null(await HashOfAsync(w, "alice"));
        Assert.Equal("", await RecordTextAsync(w));
    }

    [Fact]
    public async Task The_file_is_the_only_source_even_with_a_password_piped_on_standard_input()
    {
        await using var w = await NewAsync();
        var path = FileHolding("the password in the file\n");

        var run = await CliAsync(w, "the password on standard input", "users", "set-password", "alice", "--password-file", path);

        Assert.True(run.Exit == 0, run.Err);
        var hash = await HashOfAsync(w, "alice");
        Assert.True(new PasswordHasher().Verify("the password in the file", hash));
        Assert.False(new PasswordHasher().Verify("the password on standard input", hash));
    }

    [Fact]
    public void Setup_reads_its_password_file_with_the_same_reader_and_keeps_its_first_line_rule()
    {
        // Its documented rule: the first line, whatever follows it.
        Assert.Equal("the first administrator's password",
            SetupCommand.ReadPasswordFile(FileHolding("the first administrator's password\nanything after it\n")));

        // The shared reader's own refusal, which a reader of setup's own would not
        // give: it would hand back the empty first line and say nothing.
        var empty = FileHolding("\nthe password on the wrong line\n");
        var saved = Console.Error;
        var said = new StringWriter();
        Console.SetError(said);
        string? read;
        try
        {
            read = SetupCommand.ReadPasswordFile(empty);
        }
        finally
        {
            Console.SetError(saved);
        }
        Assert.Null(read);
        Assert.Contains($"The password file {empty} holds no password on its first line.", said.ToString(), StringComparison.Ordinal);
    }

    /// <summary>The CLI's own build output on the world's database, with <paramref name="input"/> on its standard input, and a hard limit.</summary>
    private static async Task<(int Exit, string Out, string Err)> CliAsync(World w, string input, params string[] args)
    {
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(CliPath());
        foreach (var arg in args) start.ArgumentList.Add(arg);
        foreach (var name in new[] { "PREM_CREDENTIALS_FILE", "PREM_SEARCH_CONNECTION_STRING" })
            start.Environment.Remove(name);
        start.Environment["PREM_CONNECTION_STRING"] = w.ConnectionString;
        start.Environment["PREM_TENANT_KEY"] = "t";
        start.Environment["PREM_TENANT_NAME"] = "T";

        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.StandardInput.WriteLineAsync(input);
        process.StandardInput.Close();
        using var limit = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try
        {
            await process.WaitForExitAsync(limit.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            Assert.Fail($"'prem {string.Join(' ', args)}' did not end within 2 minutes.");
        }
        return (process.ExitCode, await stdout, await stderr);
    }

    // The CLI's own build output, as the test project records it (see its project file).
    private static string CliPath()
    {
        var path = typeof(PasswordFileTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "Premagentic.Cli.Path")
            .Value;
        Assert.True(File.Exists(path), "the CLI was not built where the test project recorded it: " + path);
        return path!;
    }
}
