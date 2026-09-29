using System.Text.Json;
using Premagentic.Cli.Admin;
using Premagentic.Core.Identity;
using Premagentic.Core.Storage;

namespace Premagentic.Tests;

/// <summary>
/// The flow's settings and its redirect rules, which hold before any of it is
/// served: the one spelling of the public address, redirect addresses checked
/// and matched as strings, and the start that falls back to off.
/// </summary>
public sealed class OAuthConfigurationTests
{
    [Theory]
    [InlineData("https://Host", "https://host")]
    [InlineData("https://host/", "https://host")]
    [InlineData("https://host:443", "https://host")]
    [InlineData("https://host/x", "https://host")]
    [InlineData("HTTPS://host:8443", "https://host:8443")]
    [InlineData("https://host:08443", "https://host:8443")]
    public void A_public_address_in_another_spelling_is_refused_naming_the_canonical_one(string given, string canonical)
    {
        var problem = PublicUrls.Problem(given);

        Assert.NotNull(problem);
        Assert.EndsWith($"Set it to {canonical}.", problem);
    }

    [Theory]
    [InlineData("http://host")]
    [InlineData("https://user:pass@host")]
    [InlineData("https://host:0")]
    [InlineData("https://host:65536")]
    [InlineData("https://ho st")]
    [InlineData("https://[::1]:8443")]
    [InlineData("")]
    public void A_public_address_that_cannot_be_made_canonical_is_refused(string given)
    {
        Assert.Null(PublicUrls.Canonical(given, out var problem));
        Assert.NotNull(problem);
        Assert.StartsWith($"{OAuthSettings.PublicUrl} is refused", PublicUrls.Problem(given));
    }

    // Several checks refuse some of these on their own (an '@' is never a host
    // or port character), so only the sentence shows which check spoke: an
    // operator who typed a user name is told about the user name.
    [Theory]
    [InlineData("https://user:pass@host", "it must not carry a user name or password")]
    [InlineData("https://user@host", "it must not carry a user name or password")]
    [InlineData("https://user@host:8443", "it must not carry a user name or password")]
    [InlineData("http://host", "it must be an https address; the flow is never served over plain HTTP")]
    [InlineData("https://host:0", "its port must be a number from 1 to 65535")]
    [InlineData("https://ho_st", "its host must be a name or an IPv4 address in ASCII letters, digits, hyphens and dots")]
    public void Each_refusal_of_a_public_address_names_its_own_reason(string given, string reason)
    {
        Assert.Null(PublicUrls.Canonical(given, out var problem));
        Assert.Equal(reason, problem);
        Assert.Equal($"{OAuthSettings.PublicUrl} is refused: {reason}.", PublicUrls.Problem(given));
    }

    [Fact]
    public void The_canonical_address_is_accepted_and_the_resource_is_made_from_it()
    {
        Assert.Null(PublicUrls.Problem("https://host:8443"));
        Assert.Null(PublicUrls.Problem("https://prem.example.internal"));

        var deployment = new OAuthDeployment("https://host:8443", true, []);
        Assert.Equal("https://host:8443/mcp", deployment.Resource);
        Assert.Equal("https://host:8443", deployment.Issuer);
        Assert.Equal("https://host:8443/.well-known/oauth-protected-resource/mcp", deployment.ResourceMetadataUrl);
    }

    // The clients' own registrations, and the ports they ask with.
    [Theory]
    [InlineData("http://127.0.0.1/", "http://127.0.0.1:33418/")]
    [InlineData("http://127.0.0.1/", "http://127.0.0.1:51004/")]
    [InlineData("http://127.0.0.1:33418/", "http://127.0.0.1:33418/")]
    [InlineData("http://127.0.0.1:33418/", "http://127.0.0.1:51004/")]
    [InlineData("http://127.0.0.1/callback", "http://127.0.0.1:61023/callback")]
    [InlineData("http://localhost:8787/callback", "http://localhost:8787/callback")]
    [InlineData("http://[::1]/cb", "http://[::1]:61023/cb")]
    [InlineData("https://vscode.dev/redirect", "https://vscode.dev/redirect")]
    public void A_registered_redirect_matches_the_request_a_client_makes(string registered, string requested)
    {
        Assert.NotEqual(RedirectUriKind.Malformed, RedirectUris.Classify(registered, OAuthSettings.AdministratorRedirectUriMaxLength, out _));
        Assert.True(RedirectUris.Matches(registered, requested));
    }

    [Theory]
    [InlineData("http://127.0.0.1:33418/", "http://localhost:33418/")]
    [InlineData("http://localhost:8787/callback", "http://localhost:8787/other")]
    [InlineData("http://localhost:8787/callback", "http://localhost:8787/callback?x=1")]
    [InlineData("https://app.example/cb", "https://app.example:8443/cb")]
    [InlineData("https://app.example/cb", "https://app.example/cb/")]
    [InlineData("http://127.0.0.1/cb", "http://127.0.0.1:65536/cb")]
    [InlineData("http://127.0.0.1/cb", "http://127.0.0.1:0080/cb")]
    [InlineData("http://127.0.0.1/cb", "http://127.0.0.1.evil.example/cb")]
    public void A_request_that_differs_in_anything_but_a_loopback_port_does_not_match(string registered, string requested)
    {
        Assert.False(RedirectUris.Matches(registered, requested));
    }

    [Theory]
    [InlineData("http://loopback:1234/cb")]
    [InlineData("http://localhost.evil.example/cb")]
    [InlineData("http://127.0.0.1.evil.example/cb")]
    [InlineData("http://app.example/cb")]
    [InlineData("https://app.example/cb#frag")]
    [InlineData("https://user@app.example/cb")]
    [InlineData("https://app%2eexample/cb")]
    [InlineData("https://*.example/cb")]
    [InlineData("https://app.example\\cb")]
    [InlineData("https://app.example/c b")]
    [InlineData("https://app.example/c\tb")]
    [InlineData("https://app.example/c\nb")]
    [InlineData("https://\u00e4pp.example/cb")]
    [InlineData("http://127.0.0.1:65536/cb")]
    [InlineData("http://127.0.0.1:080/cb")]
    [InlineData("https://app.example:65536/cb")]
    [InlineData("HTTPS://app.example/cb")]
    [InlineData("/callback")]
    [InlineData("")]
    public void A_malformed_redirect_is_refused(string uri)
    {
        Assert.Equal(RedirectUriKind.Malformed, RedirectUris.Classify(uri, OAuthSettings.AdministratorRedirectUriMaxLength, out var problem));
        Assert.NotNull(problem);
    }

    [Fact]
    public void The_localhost_form_is_a_loopback_redirect_where_its_lookalikes_are_not()
    {
        Assert.Equal(RedirectUriKind.Loopback, RedirectUris.Classify("http://localhost:1234/cb", 512, out _));
        Assert.Equal(RedirectUriKind.Malformed, RedirectUris.Classify("http://loopback:1234/cb", 512, out _));
        Assert.True(RedirectUris.IsLoopback("http://localhost:1234/cb"));
        Assert.False(RedirectUris.IsLoopback("https://localhost:1234/cb"));
        Assert.Equal("http://[::1]:61023", RedirectUris.Origin("http://[::1]:61023/cb?x=1"));
    }

    [Theory]
    [InlineData("cursor://anysphere.cursor-mcp/oauth/callback", "cursor://anysphere.cursor-mcp")]
    [InlineData("vscode://vscode.github-authentication/did-authenticate", "vscode://vscode.github-authentication")]
    [InlineData("com.example.app:/oauth2redirect", "com.example.app://oauth2redirect")]
    public void A_private_use_scheme_is_recognized_so_it_can_be_dropped(string uri, string recorded)
    {
        Assert.Equal(RedirectUriKind.PrivateUse, RedirectUris.Classify(uri, 512, out _));
        Assert.Equal(recorded, RedirectUris.SchemeAndHost(uri));
    }

    [Fact]
    public void A_self_registration_redirect_is_at_most_512_characters_and_an_administrators_2000()
    {
        var uri = "https://app.example/" + new string('a', 600);
        Assert.Equal(RedirectUriKind.Malformed, RedirectUris.Classify(uri, OAuthSettings.DynamicRedirectUriMaxLength, out _));
        Assert.Equal(RedirectUriKind.Https, RedirectUris.Classify(uri, OAuthSettings.AdministratorRedirectUriMaxLength, out _));
    }

    [Fact]
    public void The_redirect_list_takes_only_https_addresses_that_pass_the_rules()
    {
        Assert.Null(OAuthSettingRules.DynamicRedirectUrisProblem(Json("""["https://claude.ai/api/mcp/auth_callback"]""")));
        Assert.Null(OAuthSettingRules.DynamicRedirectUrisProblem(Json("[]")));
        Assert.NotNull(OAuthSettingRules.DynamicRedirectUrisProblem(Json("""["http://127.0.0.1/cb"]""")));
        Assert.NotNull(OAuthSettingRules.DynamicRedirectUrisProblem(Json("""["cursor://x/cb"]""")));
        Assert.NotNull(OAuthSettingRules.DynamicRedirectUrisProblem(Json("""["https://a.example/cb","https://a.example/cb"]""")));
        Assert.NotNull(OAuthSettingRules.DynamicRedirectUrisProblem(Json("\"https://a.example/cb\"")));
        Assert.NotNull(OAuthSettingRules.DynamicRedirectUrisProblem(
            Json(JsonSerializer.Serialize(Enumerable.Range(0, 21).Select(i => $"https://a.example/cb{i}")))));
        Assert.NotNull(OAuthSettingRules.DynamicRedirectUrisProblem(
            Json(JsonSerializer.Serialize(new[] { "https://a.example/" + new string('a', 600) }))));
    }

    [Fact]
    public void Every_setting_of_the_flow_is_in_the_catalog_with_its_default()
    {
        Assert.Equal(JsonValueKind.False, SettingsCatalog.Find(OAuthSettings.Enabled)!.Default!.Value.ValueKind);
        Assert.Null(SettingsCatalog.Find(OAuthSettings.PublicUrl)!.Default);
        Assert.Equal(60, SettingsCatalog.Find(OAuthSettings.AccessTokenMinutes)!.Default!.Value.GetInt32());
        Assert.NotNull(SettingsCatalog.Find(OAuthSettings.CodeSeconds)!.Problem(Json("601")));
        Assert.Null(SettingsCatalog.Find(OAuthSettings.CodeSeconds)!.Problem(Json("600")));
        Assert.All(
            new[]
            {
                OAuthSettings.Enabled, OAuthSettings.PublicUrl, OAuthSettings.DynamicRegistration, OAuthSettings.DynamicRedirectUris,
                OAuthSettings.MaxPendingClients, OAuthSettings.PendingClientsPerAddress, OAuthSettings.RegistrationsPerHour,
                OAuthSettings.AccessTokenMinutes, OAuthSettings.RefreshTokenDays, OAuthSettings.GrantDays, OAuthSettings.CodeSeconds,
            },
            key => Assert.True(SettingsCatalog.Contains(key), key));
    }

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();
}

/// <summary>What the server makes of the flow's settings at start, and the command line's checks that span two of them. Requires a running Docker daemon.</summary>
[Collection(ConsoleCollection.Name)]
public sealed class OAuthStartAndSettingsTests(DatastoreTestDatabase server) : IClassFixture<DatastoreTestDatabase>
{
    private async Task<(PremagenticDatabase Db, Guid Tenant)> NewAsync()
    {
        var db = new PremagenticDatabase(await server.CreateDatabaseAsync());
        await db.InitializeAsync();
        return (db, await db.EnsureTenantAsync("t", "T"));
    }

    private static Task SetRawAsync(PremagenticDatabase db, Guid tenant, string key, string json) =>
        new SettingsStore(db, tenant).SetAsync(key, JsonDocument.Parse(json).RootElement.Clone());

    [Fact]
    public async Task With_the_flag_off_the_flow_is_off_and_nothing_is_said()
    {
        var (db, tenant) = await NewAsync();
        await using var _ = db;
        var warnings = new List<string>();

        Assert.Null(await OAuthDeploymentLoader.LoadAsync(new SettingsStore(db, tenant), warnings.Add));
        Assert.Empty(warnings);
    }

    [Fact]
    public async Task With_the_flag_on_and_no_address_the_flow_is_off_and_one_warning_names_both_remedies()
    {
        var (db, tenant) = await NewAsync();
        await using var _ = db;
        await SetRawAsync(db, tenant, OAuthSettings.Enabled, "true");
        var warnings = new List<string>();

        Assert.Null(await OAuthDeploymentLoader.LoadAsync(new SettingsStore(db, tenant), warnings.Add));

        var warning = Assert.Single(warnings);
        Assert.Contains($"set {OAuthSettings.PublicUrl}", warning);
        Assert.Contains($"set {OAuthSettings.Enabled} to false", warning);
    }

    [Fact]
    public async Task With_the_flag_on_and_a_bad_stored_value_the_flow_is_off()
    {
        var (db, tenant) = await NewAsync();
        await using var _ = db;
        await SetRawAsync(db, tenant, OAuthSettings.Enabled, "true");
        await SetRawAsync(db, tenant, OAuthSettings.PublicUrl, "\"https://host:8443\"");
        // Written straight to the store, past the check a command runs.
        await SetRawAsync(db, tenant, OAuthSettings.DynamicRedirectUris, """["http://app.example/cb"]""");
        var warnings = new List<string>();

        Assert.Null(await OAuthDeploymentLoader.LoadAsync(new SettingsStore(db, tenant), warnings.Add));
        Assert.Single(warnings);

        await SetRawAsync(db, tenant, OAuthSettings.DynamicRedirectUris, "[]");
        await SetRawAsync(db, tenant, OAuthSettings.PublicUrl, "\"https://Host:8443/\"");
        Assert.Null(await OAuthDeploymentLoader.LoadAsync(new SettingsStore(db, tenant), warnings.Add));
        Assert.Equal(2, warnings.Count);
    }

    [Fact]
    public async Task With_the_flag_on_and_usable_settings_the_flow_runs_as_they_say()
    {
        var (db, tenant) = await NewAsync();
        await using var _ = db;
        await SetRawAsync(db, tenant, OAuthSettings.Enabled, "true");
        await SetRawAsync(db, tenant, OAuthSettings.PublicUrl, "\"https://host:8443\"");
        await SetRawAsync(db, tenant, OAuthSettings.DynamicRegistration, "false");
        await SetRawAsync(db, tenant, OAuthSettings.DynamicRedirectUris, """["https://claude.ai/api/mcp/auth_callback"]""");
        var warnings = new List<string>();

        var deployment = await OAuthDeploymentLoader.LoadAsync(new SettingsStore(db, tenant), warnings.Add);

        Assert.Empty(warnings);
        Assert.NotNull(deployment);
        Assert.Equal("https://host:8443", deployment.PublicUrl);
        Assert.False(deployment.DynamicRegistration);
        Assert.Equal(new[] { "https://claude.ai/api/mcp/auth_callback" }, deployment.DynamicRedirectUris);
    }

    [Fact]
    public async Task The_flag_is_not_turned_on_without_a_usable_address_and_the_address_stays_while_it_is_on()
    {
        var (db, tenant) = await NewAsync();
        await using var _ = db;

        var refused = await ConsoleCapture.RunAsync(() =>
            SettingsCommands.RunAsync(["settings", "set", OAuthSettings.Enabled, "true"], db, tenant));
        Assert.Equal(1, refused.Exit);
        Assert.Contains($"cannot be turned on until {OAuthSettings.PublicUrl}", refused.Err);
        Assert.Null(await new SettingsStore(db, tenant).GetAsync(OAuthSettings.Enabled));

        var respelled = await ConsoleCapture.RunAsync(() =>
            SettingsCommands.RunAsync(["settings", "set", OAuthSettings.PublicUrl, "https://Host:8443/"], db, tenant));
        Assert.Equal(1, respelled.Exit);
        Assert.Contains("Set it to https://host:8443.", respelled.Err);

        var url = await ConsoleCapture.RunAsync(() =>
            SettingsCommands.RunAsync(["settings", "set", OAuthSettings.PublicUrl, "https://host:8443"], db, tenant));
        Assert.Equal(0, url.Exit);
        Assert.Contains("applies when the server next starts; restart it", url.Out);

        var on = await ConsoleCapture.RunAsync(() =>
            SettingsCommands.RunAsync(["settings", "set", OAuthSettings.Enabled, "true"], db, tenant));
        Assert.Equal(0, on.Exit);
        Assert.Contains("prem oauth grants revoke --all", on.Out);

        var unset = await ConsoleCapture.RunAsync(() =>
            SettingsCommands.RunAsync(["settings", "unset", OAuthSettings.PublicUrl], db, tenant));
        Assert.Equal(1, unset.Exit);
        Assert.Contains($"cannot be unset while {OAuthSettings.Enabled} is true", unset.Err);

        var off = await ConsoleCapture.RunAsync(() =>
            SettingsCommands.RunAsync(["settings", "set", OAuthSettings.Enabled, "false"], db, tenant));
        Assert.Equal(0, off.Exit);
        Assert.Contains("0 grant(s) are live. Turning the flow off suspends them and ends none", off.Out);
    }

    // A stored address the settings verbs would never write, such as one left
    // by an older build or a hand edit, does not let the flag on either: the
    // check is the address's whole spelling, not that some text is there.
    [Fact]
    public async Task A_stored_address_that_fails_the_one_spelling_does_not_let_the_flag_on()
    {
        var (db, tenant) = await NewAsync();
        await using var _ = db;
        await SetRawAsync(db, tenant, OAuthSettings.PublicUrl, "\"https://Host:8443/\"");

        var refused = await ConsoleCapture.RunAsync(() =>
            SettingsCommands.RunAsync(["settings", "set", OAuthSettings.Enabled, "true"], db, tenant));

        Assert.Equal(1, refused.Exit);
        Assert.Contains(OAuthSettingRules.EnableNeedsAddress, refused.Err);
        Assert.Null(await new SettingsStore(db, tenant).GetAsync(OAuthSettings.Enabled));
    }
}
