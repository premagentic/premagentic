using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Premagentic.Cli.Admin;
using Premagentic.Core.Admin;
using Premagentic.Core.Identity;

namespace Premagentic.Tests;

/// <summary>
/// The clients seam's document add and in-place replace: the document's bytes
/// are checked by the same parse the command line's file goes through, a
/// replace moves the document's own fields and nothing else, every grant
/// stands, and a code is exchanged only for an address the new document
/// lists. Requires a running Docker daemon.
/// </summary>
public sealed class OAuthClientDocumentSeamTests(DatastoreTestDatabase server) : IClassFixture<DatastoreTestDatabase>
{
    private const string Id = "https://app.example/client.json";
    private const string OldRedirect = "http://127.0.0.1:3000/callback";
    private const string NewRedirect = "http://127.0.0.1/done";
    private static readonly AdminActor Cli = new("cli", "test-account");

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    private static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>The vendor's changed document: a new name, a new redirect address in place of the old, one more field left out.</summary>
    private static string Changed(string id = Id) => OAuthClientDocumentReadTests.Document(id, """, "software_version": "2.0", "tos_uri": "https://app.example/terms" """)
        .Replace("\"Invented Desk\"", "\"Invented Desk Two\"").Replace(OldRedirect, NewRedirect);

    /// <summary>The seam with the flow on, as the portal's host has it, or off, as the command line finds it.</summary>
    private static OAuthClients Clients(OAuthWorld w, bool flowOn) => new(w.Db, w.Tenant, flowOn ? w.Oauth : null, w.Clock);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_client_is_added_through_the_seam_from_the_documents_bytes_with_the_flow_off_and_on(bool flowOn)
    {
        await using var w = await OAuthWorld.NewAsync(server);
        var bytes = Bytes(OAuthClientDocumentReadTests.Document(extra: """, "logo_uri": "https://app.example/logo.png" """));

        var read = await Clients(w, flowOn).AddFromDocumentAsync(Cli, Id, bytes, ModelLocation.Hosted, "A Vendor", default);

        Assert.Equal((Id, "Invented Desk", Sha(bytes)), (read.ClientId, read.Name, read.Sha256));
        Assert.Equal(["logo_uri"], read.LeftOut);
        var listing = Assert.Single(await w.Clients.ListAsync(default));
        Assert.Equal(new OAuthStoredDocument(Sha(bytes), w.Clock.Now), listing.Document);
        Assert.Equal([OldRedirect], listing.Client.RedirectUris);
        Assert.Equal((ModelLocation.Hosted, "A Vendor"), (listing.Client.StatedModelLocation!.Value, listing.Client.StatedModelVendor));
        Assert.Equal(1, await w.RowsAsync("oauth.client.add"));

        // Registered while the flow is off or on, it connects once the flow is on.
        var connection = await w.ConnectAsync(w.Alice, Id, OldRedirect);
        Assert.Equal(CallerStatus.Resolved, await w.CallAsync(connection.Access));
    }

    /// <summary>Every document <see cref="OAuthClientDocumentReadTests"/> refuses, by a name, with its bytes and the address given.</summary>
    private static readonly Dictionary<string, (byte[] Bytes, string Id)> Refused = new(StringComparer.Ordinal)
    {
        ["not json"] = (Bytes("not json at all"), Id),
        ["not an object"] = (Bytes("""["https://app.example/client.json"]"""), Id),
        ["no client_id"] = (Bytes("""{ "client_name": "Invented Desk", "redirect_uris": ["http://127.0.0.1/cb"] }"""), Id),
        ["a field named twice"] = (Bytes("""{ "client_id": "https://app.example/client.json", "client_id": "https://app.example/other.json" }"""), Id),
        ["another case"] = (Bytes(OAuthClientDocumentReadTests.Document()), "https://App.example/client.json"),
        ["a trailing slash"] = (Bytes(OAuthClientDocumentReadTests.Document()), "https://app.example/client.json/"),
        ["percent-encoding"] = (Bytes(OAuthClientDocumentReadTests.Document()), "https://app.example/%63lient.json"),
        ["client_secret_basic"] = (Bytes(OAuthClientDocumentReadTests.Document().Replace("\"none\"", "\"client_secret_basic\"")), Id),
        ["client_secret_post"] = (Bytes(OAuthClientDocumentReadTests.Document().Replace("\"none\"", "\"client_secret_post\"")), Id),
        ["private_key_jwt"] = (Bytes(OAuthClientDocumentReadTests.Document().Replace("\"none\"", "\"private_key_jwt\"")), Id),
        ["an http id"] = (Bytes(OAuthClientDocumentReadTests.Document("http://app.example/client.json")), "http://app.example/client.json"),
        ["a private scheme id"] = (Bytes(OAuthClientDocumentReadTests.Document("vscode://app.example/client.json")), "vscode://app.example/client.json"),
        ["a user name in the id"] = (Bytes(OAuthClientDocumentReadTests.Document("https://user@app.example/client.json")), "https://user@app.example/client.json"),
        ["64 KB and one byte"] = (Padded(OAuthClientDocument.MaxBytes + 1), Id),
    };

    private static byte[] Padded(int length)
    {
        var at = Bytes(OAuthClientDocumentReadTests.Document());
        return at.Concat(Enumerable.Repeat((byte)' ', length - at.Length)).ToArray();
    }

    public static TheoryData<string> RefusedCases()
    {
        var data = new TheoryData<string>();
        foreach (var name in Refused.Keys) data.Add(name);
        return data;
    }

    [Theory]
    [MemberData(nameof(RefusedCases))]
    public async Task Every_document_a_file_is_refused_for_the_seam_refuses_with_the_same_sentence_and_writes_nothing(string name)
    {
        var (bytes, id) = Refused[name];
        var sentence = Assert.Throws<ArgumentException>(() => OAuthClientDocument.Parse(bytes, id)).Message;
        var file = Path.Combine(Directory.CreateTempSubdirectory("premagentic-document-").FullName, "client.json");
        await File.WriteAllBytesAsync(file, bytes);
        Assert.Equal(sentence, Assert.Throws<ArgumentException>(() => OAuthClientDocument.Read(file, id)).Message);

        await using var w = await OAuthWorld.NewAsync(server);
        var stored = Bytes(OAuthClientDocumentReadTests.Document());
        await w.Clients.AddFromDocumentAsync(Cli, Id, stored, null, null, default);

        var add = await Assert.ThrowsAsync<ArgumentException>(() => w.Clients.AddFromDocumentAsync(Cli, id, bytes, null, null, default));
        var replace = await Assert.ThrowsAsync<ArgumentException>(() => w.Clients.ReplaceDocumentAsync(Cli, id, bytes, default));

        Assert.Equal(sentence, add.Message);
        Assert.Equal(sentence, replace.Message);
        Assert.Equal(1, await w.RowsAsync("oauth.client.add"));
        Assert.Equal(0, await w.RowsAsync("oauth.client.replace"));
        Assert.Equal(Sha(stored), Assert.Single(await w.Clients.ListAsync(default)).Document!.Sha256);
    }

    [Fact]
    public async Task A_document_of_exactly_64_KB_is_taken_at_the_seam()
    {
        await using var w = await OAuthWorld.NewAsync(server);
        var at = Padded(OAuthClientDocument.MaxBytes);

        var read = await w.Clients.AddFromDocumentAsync(Cli, Id, at, null, null, default);

        Assert.Equal(Sha(at), read.Sha256);
        using var upload = new MemoryStream(Padded(OAuthClientDocument.MaxBytes + 1000));
        Assert.Equal(OAuthClientDocument.MaxBytes + 1, OAuthClientDocument.ReadBytes(upload).Length);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_replace_takes_the_name_and_redirects_from_the_new_document_and_moves_the_hash_and_the_date_together(bool flowOn)
    {
        await using var w = await OAuthWorld.NewAsync(server);
        var first = Bytes(OAuthClientDocumentReadTests.Document());
        await w.Clients.AddFromDocumentAsync(Cli, Id, first, ModelLocation.Hosted, "A Vendor", default);
        var added = w.Clock.Now;
        w.Clock.Now += TimeSpan.FromDays(1);
        var second = Bytes(Changed());

        var replaced = await Clients(w, flowOn).ReplaceDocumentAsync(new AdminActor("cli", "another-account"), Id, second, default);

        Assert.True(replaced.Changed);
        Assert.Equal((Sha(first), Sha(second)), (replaced.PreviousSha256, replaced.Document.Sha256));
        var listing = Assert.Single(await w.Clients.ListAsync(default));
        Assert.Equal("Invented Desk Two", listing.Client.Name);
        Assert.Equal([NewRedirect], listing.Client.RedirectUris);
        Assert.Equal(new OAuthStoredDocument(Sha(second), added + TimeSpan.FromDays(1)), listing.Document);
        // Everything that is not the document's stays as the client was added.
        Assert.Equal(added, listing.Client.CreatedAt);
        Assert.Equal("cli:test-account", listing.Client.RegisteredByActor);
        Assert.Equal((ModelLocation.Hosted, "A Vendor"), (listing.Client.StatedModelLocation!.Value, listing.Client.StatedModelVendor));

        Assert.Equal(1, await w.RowsAsync("oauth.client.replace"));
        await using var cmd = w.Db.DataSource.CreateCommand(
            "SELECT old_value::text, new_value::text, actor_surface FROM prem_config.admin_event WHERE kind = 'oauth.client.replace'");
        await using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        using var before = JsonDocument.Parse(reader.GetString(0));
        using var after = JsonDocument.Parse(reader.GetString(1));
        Assert.Equal(Sha(first), before.RootElement.GetProperty("document_sha256").GetString());
        Assert.Equal(Sha(second), after.RootElement.GetProperty("document_sha256").GetString());
        Assert.Equal([NewRedirect], after.RootElement.GetProperty("redirect_uris").EnumerateArray().Select(e => e.GetString()!));
        Assert.Equal(["tos_uri"], after.RootElement.GetProperty("left_out").EnumerateArray().Select(e => e.GetString()!));
        Assert.Equal("cli", reader.GetString(2));
    }

    [Fact]
    public async Task A_grant_given_under_the_old_document_stands_and_a_code_is_exchanged_only_for_an_address_the_new_one_lists()
    {
        await using var w = await OAuthWorld.NewAsync(server);
        await w.Clients.AddFromDocumentAsync(Cli, Id, Bytes(OAuthClientDocumentReadTests.Document()), null, null, default);
        var alice = await w.ConnectAsync(w.Alice, Id, OldRedirect);
        // Bob approves under the old document and has not exchanged his code yet.
        var (verifier, challenge) = OAuthWorld.Pkce();
        var code = OAuthWorld.Query(await w.ApproveAsync(w.Bob, Id, OldRedirect, challenge), "code");

        await w.Clients.ReplaceDocumentAsync(Cli, Id, Bytes(Changed()), default);

        // Alice's grant stands: her token reaches the index and her refresh token rotates.
        Assert.Equal(CallerStatus.Resolved, await w.CallAsync(alice.Access));
        Assert.Equal(200, (await w.RefreshAsync(Id, alice.Refresh)).StatusCode);
        Assert.Null(await w.ReasonAsync(alice.GrantId));

        // Bob's code was issued for an address the new document dropped.
        var exchange = await w.ExchangeAsync(Id, code, verifier);
        Assert.Equal((400, "invalid_grant", "code_redirect_not_listed"), (exchange.StatusCode, exchange.Body["error"], exchange.LogReason));

        // The authorize request is checked against the new document too.
        var (_, again) = OAuthWorld.Pkce();
        Assert.Equal(AuthorizationCheckOutcome.ShowError, (await w.Consent.CheckAsync(w.Params(Id, OldRedirect, again), default)).Outcome);
        var bob = await w.ConnectAsync(w.Bob, Id, NewRedirect);
        Assert.Equal(CallerStatus.Resolved, await w.CallAsync(bob.Access));
    }

    [Fact]
    public async Task A_replace_is_refused_for_a_document_naming_another_address_or_a_client_with_nothing_to_replace_and_writes_nothing()
    {
        await using var w = await OAuthWorld.NewAsync(server);
        var stored = Bytes(OAuthClientDocumentReadTests.Document());
        await w.Clients.AddFromDocumentAsync(Cli, Id, stored, null, null, default);
        const string Typed = "https://app.example/typed.json";
        await w.Clients.AddAsync(Cli, Typed, "Typed Desk", [OldRedirect], null, null, default);
        const string Gone = "https://app.example/gone.json";
        await w.Clients.AddFromDocumentAsync(Cli, Gone, Bytes(OAuthClientDocumentReadTests.Document(Gone)), null, null, default);
        await w.Clients.RemoveAsync(Cli, Gone, default);
        const string Other = "https://app.example/other.json";

        var mismatch = await Assert.ThrowsAsync<ArgumentException>(() => w.Clients.ReplaceDocumentAsync(Cli, Id, Bytes(Changed(Other)), default));
        var nobody = await Assert.ThrowsAsync<InvalidOperationException>(() => w.Clients.ReplaceDocumentAsync(Cli, Other, Bytes(Changed(Other)), default));
        var typed = await Assert.ThrowsAsync<InvalidOperationException>(() => w.Clients.ReplaceDocumentAsync(Cli, Typed, Bytes(Changed(Typed)), default));
        var gone = await Assert.ThrowsAsync<InvalidOperationException>(() => w.Clients.ReplaceDocumentAsync(Cli, Gone, Bytes(Changed(Gone)), default));

        Assert.Equal($"The document's client_id is '{Other}' and the client id given is '{Id}'. They must be the same address exactly.", mismatch.Message);
        Assert.Equal($"No client has the id '{Other}'.", nobody.Message);
        Assert.StartsWith($"The client '{Typed}' was not registered from a metadata document", typed.Message);
        Assert.Equal($"No client has the id '{Gone}'.", gone.Message);
        Assert.Equal(0, await w.RowsAsync("oauth.client.replace"));
        var listings = await w.Clients.ListAsync(default);
        var kept = listings.Single(l => l.Client.Id == Id);
        Assert.Equal(("Invented Desk", Sha(stored)), (kept.Client.Name, kept.Document!.Sha256));
        Assert.Equal([OldRedirect], kept.Client.RedirectUris);
        Assert.Null(listings.Single(l => l.Client.Id == Typed).Document);
    }

    [Fact]
    public async Task A_file_that_is_the_stored_document_already_changes_nothing_and_records_nothing()
    {
        await using var w = await OAuthWorld.NewAsync(server);
        var stored = Bytes(OAuthClientDocumentReadTests.Document());
        await w.Clients.AddFromDocumentAsync(Cli, Id, stored, null, null, default);
        var at = w.Clock.Now;
        w.Clock.Now += TimeSpan.FromHours(1);

        var replaced = await w.Clients.ReplaceDocumentAsync(Cli, Id, stored, default);

        Assert.False(replaced.Changed);
        Assert.Equal(Sha(stored), replaced.PreviousSha256);
        Assert.Equal(new OAuthStoredDocument(Sha(stored), at), Assert.Single(await w.Clients.ListAsync(default)).Document);
        Assert.Equal(0, await w.RowsAsync("oauth.client.replace"));
    }
}

/// <summary>
/// <c>prem oauth clients replace</c> at the command line, with the flow off as
/// the command line finds it. Requires a running Docker daemon.
/// </summary>
[Collection(ConsoleCollection.Name)]
public sealed class OAuthClientDocumentReplaceCliTests(DatastoreTestDatabase server) : IClassFixture<DatastoreTestDatabase>
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

    private static string Sha(string file) => Convert.ToHexStringLower(SHA256.HashData(System.IO.File.ReadAllBytes(file)));

    [Fact]
    public async Task A_document_is_replaced_and_the_list_shows_the_new_hash_and_the_same_file_again_changes_nothing()
    {
        await using var w = await OAuthWorld.NewAsync(server);
        var first = File(OAuthClientDocumentReadTests.Document());
        var second = File(OAuthClientDocumentReadTests.Document(extra: """, "logo_uri": "https://app.example/logo.png" """)
            .Replace("\"Invented Desk\"", "\"Invented Desk Two\""));
        var add = await RunAsync(w, "oauth", "clients", "add", "--metadata-file", first, "--id", Id);
        Assert.True(add.Exit == 0, add.Err);

        var replace = await RunAsync(w, "oauth", "clients", "replace", "--metadata-file", second, "--id", Id);
        var list = await RunAsync(w, "oauth", "clients", "list");
        var again = await RunAsync(w, "oauth", "clients", "replace", "--metadata-file", second, "--id", Id);

        Assert.True(replace.Exit == 0, replace.Err);
        Assert.Contains($"Client {Id}'s metadata document replaced: SHA-256 {Sha(first)} is now {Sha(second)}.", replace.Out);
        Assert.Contains("  name: Invented Desk Two", replace.Out);
        Assert.Contains("left out, and never stored: logo_uri", replace.Out);
        Assert.Contains("Every grant stands.", replace.Out);
        Assert.Contains($"from its metadata document, SHA-256 {Sha(second)}, stored ", list.Out);
        Assert.Contains("Invented Desk Two", list.Out);
        Assert.True(again.Exit == 0, again.Err);
        Assert.Contains("nothing changed", again.Out);
        Assert.Equal(1, await w.RowsAsync("oauth.client.replace"));
    }

    [Theory]
    [InlineData("Invented Desk", null)]
    [InlineData("--redirect", "http://127.0.0.1/cb")]
    [InlineData("--model", "local")]
    [InlineData("--vendor", "A Vendor")]
    public async Task A_replace_takes_nothing_but_the_document_and_its_address(string first, string? second)
    {
        string[] extra = second is null ? [first] : [first, second];
        await using var w = await OAuthWorld.NewAsync(server);
        var file = File(OAuthClientDocumentReadTests.Document());
        Assert.Equal(0, (await RunAsync(w, "oauth", "clients", "add", "--metadata-file", file, "--id", Id)).Exit);

        var replace = await RunAsync(w, ["oauth", "clients", "replace", .. extra, "--metadata-file", File(OAuthClientDocumentReadTests.Document()
            .Replace("\"Invented Desk\"", "\"Invented Desk Two\"")), "--id", Id]);

        Assert.Equal(1, replace.Exit);
        Assert.Contains("clients replace takes --metadata-file and --id only", replace.Err);
        Assert.Equal(0, await w.RowsAsync("oauth.client.replace"));
    }
}
