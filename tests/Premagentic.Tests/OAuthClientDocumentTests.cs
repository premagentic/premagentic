using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Premagentic.Cli.Admin;
using Premagentic.Core.Admin;
using Premagentic.Core.Identity;

namespace Premagentic.Tests;

/// <summary>
/// A client's metadata document stored by hand: read from a file once, bound
/// to the exact address the administrator gives, with every field the flow
/// does not use left out. Needs no database.
/// </summary>
public sealed class OAuthClientDocumentReadTests
{
    private const string Id = "https://app.example/client.json";

    internal static string Document(string id = Id, string extra = "") => $$"""
        { "client_id": "{{id}}", "client_name": "Invented Desk", "redirect_uris": ["http://127.0.0.1:3000/callback"],
          "grant_types": ["authorization_code", "refresh_token"], "response_types": ["code"],
          "token_endpoint_auth_method": "none"{{extra}} }
        """;

    [Theory]
    [InlineData("not json at all", Id, "The metadata document is not JSON.")]
    [InlineData("""["https://app.example/client.json"]""", Id, "The metadata document is a JSON object, and this file holds something else.")]
    [InlineData("""{ "client_name": "Invented Desk", "redirect_uris": ["http://127.0.0.1/cb"] }""", Id, "The metadata document has no client_id.")]
    [InlineData("""{ "client_id": "https://app.example/client.json", "client_id": "https://app.example/other.json" }""", Id,
        "The metadata document names 'client_id' twice.")]
    public void A_file_that_is_not_one_json_object_naming_itself_once_is_refused(string text, string id, string sentence)
    {
        var refused = Assert.Throws<ArgumentException>(() => OAuthClientDocument.Parse(Encoding.UTF8.GetBytes(text), id));
        Assert.Equal(sentence, refused.Message);
    }

    // Simple string comparison, as the specification says: no case folding,
    // no trailing slash, no percent decoding.
    [Theory]
    [InlineData("https://App.example/client.json")]
    [InlineData("https://app.example/client.json/")]
    [InlineData("https://app.example/%63lient.json")]
    public void The_document_must_name_itself_by_exactly_the_address_it_is_stored_under(string id)
    {
        var refused = Assert.Throws<ArgumentException>(() => OAuthClientDocument.Parse(Encoding.UTF8.GetBytes(Document()), id));
        Assert.StartsWith($"The document's client_id is '{Id}' and the client id given is '{id}'.", refused.Message);
        Assert.EndsWith("They must be the same address exactly.", refused.Message);
    }

    [Theory]
    [InlineData("client_secret_basic")]
    [InlineData("client_secret_post")]
    [InlineData("private_key_jwt")]
    public void A_document_that_authenticates_with_a_secret_or_a_key_is_refused(string method)
    {
        var text = Document().Replace("\"token_endpoint_auth_method\": \"none\"", $"\"token_endpoint_auth_method\": \"{method}\"");

        var refused = Assert.Throws<ArgumentException>(() => OAuthClientDocument.Parse(Encoding.UTF8.GetBytes(text), Id));

        Assert.Equal(
            $"The document asks to authenticate with '{method}'. This server issues no secret and holds no key, so it takes only an assistant that uses none.",
            refused.Message);
    }

    [Theory]
    [InlineData("http://app.example/client.json")]
    [InlineData("vscode://app.example/client.json")]
    [InlineData("https://user@app.example/client.json")]
    public void The_address_must_be_https_by_the_administrator_rules(string id)
    {
        var refused = Assert.Throws<ArgumentException>(() => OAuthClientDocument.Parse(Encoding.UTF8.GetBytes(Document(id)), id));
        Assert.StartsWith("The client id is the https address the assistant names itself by", refused.Message);
    }

    [Fact]
    public void A_document_over_64_KB_is_refused_before_it_is_parsed_and_one_at_the_bound_is_read()
    {
        var at = Encoding.UTF8.GetBytes(Document());
        var padded = at.Concat(Enumerable.Repeat((byte)' ', OAuthClientDocument.MaxBytes - at.Length)).ToArray();
        Assert.Equal(OAuthClientDocument.MaxBytes, padded.Length);
        Assert.Equal("Invented Desk", OAuthClientDocument.Parse(padded, Id).Name);

        var over = padded.Append((byte)' ').ToArray();
        var refused = Assert.Throws<ArgumentException>(() => OAuthClientDocument.Parse(over, Id));
        Assert.Equal("The metadata document is larger than 64 KB, which no client's document needs.", refused.Message);

        var file = Path.Combine(Directory.CreateTempSubdirectory("premagentic-document-").FullName, "client.json");
        File.WriteAllBytes(file, over);
        Assert.Equal(refused.Message, Assert.Throws<ArgumentException>(() => OAuthClientDocument.Read(file, Id)).Message);
    }

    [Fact]
    public void The_fields_the_flow_does_not_use_are_named_and_a_private_scheme_is_dropped()
    {
        var text = Document(extra: """
            , "logo_uri": "https://app.example/logo.png", "jwks_uri": "https://app.example/keys", "contacts": ["someone@app.example"]
            """).Replace("\"redirect_uris\": [\"http://127.0.0.1:3000/callback\"]",
            "\"redirect_uris\": [\"http://127.0.0.1:3000/callback\", \"invented-desk://callback\", \"https://app.example/cb\"]");
        var bytes = Encoding.UTF8.GetBytes(text);

        var document = OAuthClientDocument.Parse(bytes, Id);

        Assert.Equal(Id, document.ClientId);
        Assert.Equal(["http://127.0.0.1:3000/callback", "https://app.example/cb"], document.RedirectUris);
        Assert.Equal(["invented-desk://callback"], document.DroppedRedirects);
        Assert.Equal(["contacts", "jwks_uri", "logo_uri"], document.LeftOut);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), document.Sha256);
    }
}

/// <summary>
/// The server's side: a stored document is matched by its exact address and
/// nothing else, an address nobody stored is refused with the command that
/// stores it, and nothing is ever fetched. Requires a running Docker daemon.
/// </summary>
public sealed class OAuthClientDocumentFlowTests(DatastoreTestDatabase server) : IClassFixture<DatastoreTestDatabase>
{
    private const string Id = "https://app.example/client.json";
    private static readonly AdminActor Cli = new("cli", "test-account");

    private static OAuthClientDocument Parse(string text, string id) => OAuthClientDocument.Parse(Encoding.UTF8.GetBytes(text), id);

    private static string Command => "prem oauth clients add --metadata-file";

    [Theory]
    [InlineData("https://App.example/client.json")]
    [InlineData("https://app.example/client.json/")]
    [InlineData("https://app.example/%63lient.json")]
    public async Task A_stored_document_is_matched_by_its_exact_address_and_nothing_else(string presented)
    {
        await using var w = await OAuthWorld.NewAsync(server);
        await w.Clients.AddFromDocumentAsync(Cli, Parse(OAuthClientDocumentReadTests.Document(), Id), null, null, default);
        var (verifier, challenge) = OAuthWorld.Pkce();

        // The exact address goes the whole way.
        var connection = await w.ConnectAsync(w.Alice, Id, "http://127.0.0.1:3000/callback");
        Assert.Equal(CallerStatus.Resolved, await w.CallAsync(connection.Access));

        // Any other spelling is an assistant nobody stored, at every step.
        var check = await w.Consent.CheckAsync(w.Params(presented, "http://127.0.0.1:3000/callback", challenge), default);
        Assert.Equal(AuthorizationCheckOutcome.ShowError, check.Outcome);
        Assert.Equal(OAuthConsent.NotRegistered, check.Message);
        var token = await w.ExchangeAsync(presented, "prem_cod_0123", verifier);
        Assert.Equal((400, "invalid_client", OAuthTokenService.NotRegistered), (token.StatusCode, token.Body["error"], token.Body["error_description"]));
        var revoke = await w.Tokens.RevokeAsync(new Dictionary<string, string> { ["client_id"] = presented, ["token"] = connection.Refresh }, default);
        Assert.Equal((400, "invalid_client", OAuthTokenService.NotRegistered), (revoke.StatusCode, revoke.Body["error"], revoke.Body["error_description"]));
        Assert.Equal(CallerStatus.Resolved, await w.CallAsync(connection.Access));
    }

    [Fact]
    public async Task An_address_nobody_stored_is_refused_with_the_command_that_stores_it()
    {
        await using var w = await OAuthWorld.NewAsync(server);
        var (verifier, challenge) = OAuthWorld.Pkce();

        var check = await w.Consent.CheckAsync(w.Params(Id, "http://127.0.0.1:3000/callback", challenge), default);
        var token = await w.ExchangeAsync(Id, "prem_cod_0123", verifier);
        var revoke = await w.Tokens.RevokeAsync(new Dictionary<string, string> { ["client_id"] = Id, ["token"] = "x" }, default);
        var made = await w.ExchangeAsync("prem_cli_0123456789abcdef01234567", "prem_cod_0123", verifier);

        Assert.Contains(Command, check.Message);
        Assert.Contains("fetches nothing from the internet", check.Message);
        Assert.Contains(Command, (string)token.Body["error_description"]!);
        Assert.Contains(Command, (string)revoke.Body["error_description"]!);
        // An id this server made names no command: that assistant registers itself again.
        Assert.Equal("invalid_client", made.Body["error"]);
        Assert.False(made.Body.ContainsKey("error_description"));
    }

    [Fact]
    public async Task The_fields_left_out_are_recorded_by_name_and_no_value_of_theirs_is_stored_anywhere()
    {
        await using var w = await OAuthWorld.NewAsync(server);
        var document = Parse(OAuthClientDocumentReadTests.Document(extra: """
            , "jwks_uri": "https://app.example/zq-left-out-key", "logo_uri": "https://app.example/zq-left-out-logo"
            """), Id);

        await w.Clients.AddFromDocumentAsync(Cli, document, null, null, default);

        var listing = Assert.Single(await w.Clients.ListAsync(default));
        Assert.Equal(document.Sha256, listing.Document!.Sha256);
        await using (var cmd = w.Db.DataSource.CreateCommand(
                         "SELECT new_value::text FROM prem_config.admin_event WHERE kind = 'oauth.client.add'"))
        {
            var recorded = (string)(await cmd.ExecuteScalarAsync())!;
            using var json = JsonDocument.Parse(recorded);
            Assert.Equal(["jwks_uri", "logo_uri"], json.RootElement.GetProperty("left_out").EnumerateArray().Select(e => e.GetString()!));
            Assert.Equal(document.Sha256, json.RootElement.GetProperty("document_sha256").GetString());
        }
        Assert.Equal(0, await RowsHoldingAsync(w, "zq-left-out"));
        Assert.True(await RowsHoldingAsync(w, "Invented Desk") > 0, "the search for a stored value found nothing, so it cannot find anything");
    }

    [Fact]
    public async Task Nothing_is_fetched_for_a_stored_document_or_an_address_nobody_stored()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var connections = 0;
        using var stop = new CancellationTokenSource();
        var accepting = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    using var socket = await listener.AcceptSocketAsync(stop.Token);
                    Interlocked.Increment(ref connections);
                }
            }
            catch (OperationCanceledException) { }
        });

        // The control: the listener counts a connection when one is made.
        using (var probe = new TcpClient()) await probe.ConnectAsync(IPAddress.Loopback, port);
        await WaitForAsync(() => Volatile.Read(ref connections) == 1);

        var here = $"https://127.0.0.1:{port}";
        await using var w = await OAuthWorld.NewAsync(server);
        var document = Parse(OAuthClientDocumentReadTests.Document($"{here}/client.json", $$"""
            , "jwks_uri": "{{here}}/keys", "logo_uri": "{{here}}/logo.png", "client_uri": "{{here}}/"
            """), $"{here}/client.json");
        await w.Clients.AddFromDocumentAsync(Cli, document, null, null, default);
        await w.Clients.ListAsync(default);

        var connection = await w.ConnectAsync(w.Alice, document.ClientId, "http://127.0.0.1:3000/callback");
        var refreshed = await w.RefreshAsync(document.ClientId, connection.Refresh);
        Assert.Equal(200, refreshed.StatusCode);
        var revoked = await w.Tokens.RevokeAsync(new Dictionary<string, string>
            { ["client_id"] = document.ClientId, ["token"] = (string)refreshed.Body["refresh_token"]! }, default);
        Assert.Equal(200, revoked.StatusCode);

        var (verifier, challenge) = OAuthWorld.Pkce();
        var nobody = $"{here}/other.json";
        Assert.Equal(OAuthConsent.NotRegistered,
            (await w.Consent.CheckAsync(w.Params(nobody, "http://127.0.0.1:3000/callback", challenge), default)).Message);
        Assert.Equal("invalid_client", (await w.ExchangeAsync(nobody, "prem_cod_0123", verifier)).Body["error"]);

        await Task.Delay(500);
        Assert.Equal(1, Volatile.Read(ref connections));
        await stop.CancelAsync();
        await accepting;
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++) await Task.Delay(50);
        Assert.True(condition(), "the listener did not count the control's connection");
    }

    /// <summary>How many rows of any table of the deployment hold the text, read as each row's whole text.</summary>
    private static async Task<long> RowsHoldingAsync(OAuthWorld w, string text)
    {
        var tables = new List<string>();
        await using (var list = w.Db.DataSource.CreateCommand("""
                         SELECT quote_ident(table_schema) || '.' || quote_ident(table_name) FROM information_schema.tables
                         WHERE table_schema IN ('prem_config', 'prem_index') AND table_type = 'BASE TABLE'
                         """))
        await using (var reader = await list.ExecuteReaderAsync())
            while (await reader.ReadAsync()) tables.Add(reader.GetString(0));

        long rows = 0;
        foreach (var table in tables)
        {
            await using var count = w.Db.DataSource.CreateCommand($"SELECT count(*) FROM {table} t WHERE t::text LIKE @like");
            count.Parameters.AddWithValue("like", $"%{text}%");
            rows += (long)(await count.ExecuteScalarAsync())!;
        }
        return rows;
    }
}

/// <summary>
/// <c>prem oauth clients add --metadata-file</c> at the command line, with the
/// flow off as the command line finds it. Requires a running Docker daemon.
/// </summary>
[Collection(ConsoleCollection.Name)]
public sealed class OAuthClientDocumentCliTests(DatastoreTestDatabase server) : IClassFixture<DatastoreTestDatabase>
{
    private const string Id = "https://app.example/client.json";

    private static Task<(int Exit, string Out, string Err)> RunAsync(OAuthWorld w, params string[] args) =>
        ConsoleCapture.RunAsync(() => OAuthCommands.RunAsync(args, w.Db, w.Tenant));

    private static string File(string text)
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory("premagentic-document-").FullName, "client.json");
        System.IO.File.WriteAllText(path, text);
        return path;
    }

    [Fact]
    public async Task A_client_is_added_from_its_document_and_the_list_shows_the_hash_of_the_file()
    {
        await using var w = await OAuthWorld.NewAsync(server);
        var file = File(OAuthClientDocumentReadTests.Document(extra: """, "logo_uri": "https://app.example/logo.png" """));
        var sha = Convert.ToHexStringLower(SHA256.HashData(System.IO.File.ReadAllBytes(file)));

        var add = await RunAsync(w, "oauth", "clients", "add", "--metadata-file", file, "--id", Id, "--model", "local");
        var list = await RunAsync(w, "oauth", "clients", "list");

        Assert.True(add.Exit == 0, add.Err);
        Assert.Contains($"Client 'Invented Desk' registered from its metadata document, SHA-256 {sha}.", add.Out);
        Assert.Contains("left out, and never stored: logo_uri", add.Out);
        Assert.Contains("Nothing was fetched from its address.", add.Out);
        Assert.Equal(Id, add.Out.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Last());
        Assert.Contains($"from its metadata document, SHA-256 {sha}, stored ", list.Out);
        Assert.Contains("model: local", list.Out);
    }

    [Theory]
    [InlineData("Invented Desk", null, "The name comes from the metadata document; give no name beside --metadata-file.")]
    [InlineData(null, "http://127.0.0.1/cb", "The redirect addresses come from the metadata document; give no --redirect beside --metadata-file.")]
    public async Task A_name_or_a_redirect_beside_the_document_is_refused_and_nothing_is_written(string? name, string? redirect, string sentence)
    {
        await using var w = await OAuthWorld.NewAsync(server);
        var args = new List<string> { "oauth", "clients", "add" };
        if (name is not null) args.Add(name);
        args.AddRange(["--metadata-file", File(OAuthClientDocumentReadTests.Document()), "--id", Id]);
        if (redirect is not null) args.AddRange(["--redirect", redirect]);

        var add = await RunAsync(w, [.. args]);

        Assert.Equal(1, add.Exit);
        Assert.Contains(sentence, add.Err);
        Assert.Equal(0, await w.RowsAsync("oauth.client.add"));
        Assert.Empty(await w.Clients.ListAsync(default));
    }

    [Fact]
    public async Task A_document_without_its_address_or_naming_another_is_refused_and_nothing_is_written()
    {
        await using var w = await OAuthWorld.NewAsync(server);
        var file = File(OAuthClientDocumentReadTests.Document());

        var noId = await RunAsync(w, "oauth", "clients", "add", "--metadata-file", file);
        var other = await RunAsync(w, "oauth", "clients", "add", "--metadata-file", file, "--id", "https://app.example/other.json");

        Assert.Equal(1, noId.Exit);
        Assert.Contains("--metadata-file needs --id", noId.Err);
        Assert.Equal(1, other.Exit);
        Assert.Contains("They must be the same address exactly.", other.Err);
        Assert.Equal(0, await w.RowsAsync("oauth.client.add"));
    }
}
