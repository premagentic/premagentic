using System.Reflection;
using System.Security.Cryptography;
using Premagentic.Conformance;
using Premagentic.Core;
using Premagentic.Core.Extensions;
using Premagentic.Core.Identity.SignIn;
using Premagentic.Core.Ingestion.Readers;
using Premagentic.Core.Reminders;
using Premagentic.Core.Security;
using Premagentic.Core.Sources;
using Xunit.Sdk;

namespace Premagentic.Tests;

/// <summary>
/// The file system connector through the kit with the kit's own unreadable
/// item, as an author who overrides nothing gets it: the rule about one
/// unreadable item is proved here on every machine, whatever this one lets
/// its account read.
/// </summary>
public sealed class KitFakeSourceConformanceTests : DocumentSourceConformance, IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("prem-kit-fake-");

    public void Dispose()
    {
        try
        {
            _root.Delete(recursive: true);
        }
        catch (IOException)
        {
        }
    }

    protected override Task<IDocumentSource> SourceAsync()
    {
        File.WriteAllText(Path.Combine(_root.FullName, "a-gate.md"), "# Gate\n\nThe gate is locked at six.\n");
        File.WriteAllText(Path.Combine(_root.FullName, "b-door.md"), "# Door\n\nThe side door opens at eight.\n");
        return Task.FromResult<IDocumentSource>(new FileSystemSource(_root.FullName, DocumentAccess.Everyone, "yard"));
    }

    protected override Task<IDocumentSource?> WithPermissionsItCannotReadAsync()
    {
        File.WriteAllText(Path.Combine(_root.FullName, "unknown.md"), "# Unknown\n\nNobody could say who may read this.\n");
        return Task.FromResult<IDocumentSource?>(new FileSystemSource(_root.FullName, DocumentAccess.NoOne));
    }
}

public sealed class ConformanceKitTests
{
    [Fact]
    public async Task The_kits_fake_holds_the_first_item_a_reader_would_read_and_passes_the_rest_through()
    {
        var root = Directory.CreateTempSubdirectory("prem-kit-held-").FullName;
        File.WriteAllText(Path.Combine(root, "a.md"), "# A\n\nFirst.\n");
        File.WriteAllText(Path.Combine(root, "b.md"), "# B\n\nSecond.\n");
        var source = new OneUnreadableItem(new FileSystemSource(root, DocumentAccess.Everyone, "yard"));

        var reads = new List<SourceRead>();
        await foreach (var read in source.ReadThroughAsync(ReaderRegistry.BuiltIn)) reads.Add(read);

        Assert.Equal("yard/a.md", source.Held);
        var failure = Assert.Single(reads, r => r.Failure is not null).Failure!;
        Assert.Equal("yard/a.md", failure.Path);
        Assert.Contains(OneUnreadableItem.Reason, failure.Reason);
        Assert.Equal("yard/b.md", Assert.Single(reads, r => r.Document is not null).Document!.Path);
    }

    [Fact]
    public void The_kits_version_is_the_product_version_it_was_built_with()
    {
        static string Version(Assembly a) =>
            a.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+')[0];

        Assert.Equal(Version(typeof(SeamVersions).Assembly), KitSeams.KitVersion);
        Assert.Equal(Version(typeof(SeamVersions).Assembly), Version(typeof(DocumentSourceConformance).Assembly));
    }

    [Fact]
    public void The_seams_the_kit_says_it_proves_are_the_ones_in_the_tree_it_was_built_from()
    {
        Assert.Equal(
            new Dictionary<string, int>
            {
                [SeamVersions.ReaderName] = SeamVersions.Reader,
                [SeamVersions.ChunkerName] = SeamVersions.Chunker,
                [SeamVersions.SourceName] = SeamVersions.Source,
                [SeamVersions.EmbeddingName] = SeamVersions.Embedding,
                [SeamVersions.SignInName] = SeamVersions.SignIn,
                [SeamVersions.ReminderName] = SeamVersions.Reminder,
            },
            KitSeams.Proven);
        Assert.Equal(SeamVersions.Reader, KitSeams.Offered(nameof(SeamVersions.Reader)));
    }

    [Fact]
    public void A_kit_for_another_seam_version_fails_naming_the_kit_and_the_version_to_match()
    {
        Assert.Null(KitSeams.Mismatch("reader", 2, 2, "0.1.0"));

        var problem = KitSeams.Mismatch("reader", 2, 3, "0.1.0");

        Assert.Equal(
            "This kit, Premagentic.Conformance 0.1.0, proves version 2 of the \"reader\" seam, and the PremAgentic this " +
            "extension builds against offers version 3. Use the kit packed from that PremAgentic, whose \"reader\" " +
            "seam is version 3.",
            problem);
    }
}

/// <summary>The kit's own sign-in adapter through the kit's sign-in fixture, as an author's adapter goes through it.</summary>
public sealed class KitFakeSignInAdapterConformanceTests : SignInAdapterConformance
{
    protected override ISignInAdapter Adapter { get; } = new KitFakeSignInAdapter();
}

/// <summary>The kit's own reminder sink through the kit's reminder fixture.</summary>
public sealed class KitFakeReminderSinkConformanceTests : ReminderSinkConformance
{
    private readonly KitFakeReminderSink _sink = new(["group:Night shift"]);

    protected override IReminderSink Sink => _sink;
    protected override IReadOnlyList<string> Owners => ["user:alice", "group:Staff"];
    protected override string Unreachable => "group:Night shift";
    protected override IReadOnlyDictionary<string, string> Delivered() => _sink.Delivered;
}

/// <summary>
/// The mail-reminders sample through the kit's reminder fixture, loaded the way
/// a deployment loads it (its folder, its manifest, its hash) and delivering to
/// an SMTP stand-in on loopback, so the sample proves the seam it claims.
/// </summary>
public sealed class MailReminderSinkConformanceTests : ReminderSinkConformance, IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("prem-mail-kit-");
    private readonly MailRemindersTests.SmtpStandIn _smtp = new();
    private readonly IReminderSink _sink;

    public MailReminderSinkConformanceTests()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_root.FullName, "mail-reminders")).FullName;
        var assembly = Path.Combine(folder, "MailReminders.dll");
        File.Copy(typeof(MailReminderSinkConformanceTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(a => a.Key == "Premagentic.Sample.MailReminders.Path").Value!, assembly);
        File.WriteAllText(Path.Combine(folder, ExtensionManifest.FileName), $$"""
            { "name": "mail-reminders", "version": "0.1.0", "assemblyFile": "MailReminders.dll",
              "sha256": "{{Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(assembly)))}}",
              "seams": { "reminder": 1 } }
            """);
        File.WriteAllText(Path.Combine(folder, "mail.json"), $$"""
            { "host": "127.0.0.1", "port": {{_smtp.Port}}, "tls": false, "from": "premagentic@example.org",
              "userDomain": "example.org", "addresses": { "administrators": "it@example.org" } }
            """);
        var measured = ExtensionAllowList.Measure(folder);
        _sink = Assert.Single(ExtensionHost.Load(_root.FullName, [(measured.Name, measured.Sha256)]).ReminderSinks);
    }

    public void Dispose()
    {
        _smtp.Dispose();
        try
        {
            _root.Delete(recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An assembly a load context still holds open; the temp folder's to clean.
        }
    }

    protected override IReminderSink Sink => _sink;
    protected override IReadOnlyList<string> Owners => ["user:alice", "user:bob"];

    // A group has no address of its own, and the user domain is only for users.
    protected override string Unreachable => "group:Night shift";

    protected override IReadOnlyDictionary<string, string> Delivered() =>
        _smtp.Messages.ToArray()
            .GroupBy(m => m.To switch
            {
                "it@example.org" => ReminderSummary.Administrators,
                var to when to.EndsWith("@example.org", StringComparison.Ordinal) => "user:" + to[..to.IndexOf('@')],
                var to => to,
            })
            .ToDictionary(g => g.Key, g => string.Concat(g.Select(m => m.Data)));
}

/// <summary>
/// The kit's sign-in and reminder fixtures shown able to fail: each rule, run
/// on the kit's fake with the one flaw that breaks it, fails, and on the fake
/// with no flaw, passes.
/// </summary>
public sealed class KitFixturesCanFailTests
{
    private sealed class SignInFixture(ISignInAdapter adapter) : SignInAdapterConformance
    {
        protected override ISignInAdapter Adapter => adapter;
    }

    private sealed class SinkFixture(KitFakeReminderSink sink) : ReminderSinkConformance
    {
        protected override IReminderSink Sink => sink;
        protected override IReadOnlyList<string> Owners => ["user:alice", "group:Staff"];
        protected override string Unreachable => "group:Night shift";
        protected override IReadOnlyDictionary<string, string> Delivered() => sink.Delivered;
    }

    /// <summary>An adapter in an assembly that uses the identity store, as this test project does.</summary>
    private sealed class AdapterBesideTheStore : ISignInAdapter
    {
        public string Name => "beside-the-store";

        public Task<SignInResolution?> ResolveAsync(ISignInRequest request, CancellationToken ct = default) =>
            Task.FromResult<SignInResolution?>(null);
    }

    /// <summary>Runs one fact of a fixture and returns how it failed, or null when it passed.</summary>
    private static async Task<Exception?> RunAsync(object fixture, string fact)
    {
        var method = fixture.GetType().GetMethod(fact, BindingFlags.Public | BindingFlags.Instance)
                     ?? throw new InvalidOperationException($"No fact {fact}.");
        try
        {
            if (method.Invoke(fixture, null) is Task task) await task;
            return null;
        }
        catch (TargetInvocationException ex)
        {
            // A fact that is not async fails inside the call.
            return ex.InnerException;
        }
        catch (Exception ex)
        {
            // An async one fails when its task is awaited.
            return ex;
        }
    }

    private static IEnumerable<string> Facts(Type fixture) =>
        fixture.GetMethods().Where(m => m.GetCustomAttribute<FactAttribute>() is not null).Select(m => m.Name).Order();

    [Fact]
    public async Task Every_sign_in_rule_passes_on_the_kits_adapter_with_no_flaw()
    {
        foreach (var fact in Facts(typeof(SignInAdapterConformance)))
            Assert.Null(await RunAsync(new SignInFixture(new KitFakeSignInAdapter()), fact));
    }

    [Theory]
    [InlineData(KitFakeSignInAdapter.Flaw.UnusableName, nameof(SignInAdapterConformance.It_names_itself_as_settings_and_the_log_name_it))]
    [InlineData(KitFakeSignInAdapter.Flaw.SignsInAnEmptyRequest, nameof(SignInAdapterConformance.A_request_with_nothing_on_it_signs_in_nobody))]
    [InlineData(KitFakeSignInAdapter.Flaw.BelievesAnyCredential, nameof(SignInAdapterConformance.A_request_whose_every_header_and_cookie_is_forged_signs_in_nobody))]
    public async Task Each_sign_in_rule_fails_on_the_adapter_that_breaks_it(KitFakeSignInAdapter.Flaw flaw, string fact)
    {
        Assert.IsAssignableFrom<XunitException>(await RunAsync(new SignInFixture(new KitFakeSignInAdapter(flaw)), fact));
    }

    [Fact]
    public async Task The_store_rule_fails_on_an_adapter_whose_assembly_uses_the_identity_store()
    {
        Assert.IsAssignableFrom<XunitException>(await RunAsync(new SignInFixture(new AdapterBesideTheStore()),
            nameof(SignInAdapterConformance.It_holds_nothing_that_could_make_an_account_grant_a_role_or_change_a_rule)));

        // What it finds, and that it finds nothing in the kit, whose fake passes.
        Assert.Contains("Premagentic.Core.Identity.IdentityStore", SignInAdapterConformance.StoreReferences(typeof(KitFixturesCanFailTests).Assembly));
        Assert.Contains("Npgsql", SignInAdapterConformance.StoreReferences(typeof(Premagentic.Core.Storage.PremagenticDatabase).Assembly));
        Assert.Empty(SignInAdapterConformance.StoreReferences(typeof(KitFakeSignInAdapter).Assembly));
    }

    [Fact]
    public async Task The_kits_adapter_signs_in_the_name_its_credential_proves_and_nobody_from_one_altered()
    {
        var adapter = new KitFakeSignInAdapter();
        var credential = adapter.Issue("dan");

        var good = await adapter.ResolveAsync(new Request(credential));
        var altered = await adapter.ResolveAsync(new Request("eve" + credential[3..]));

        Assert.Equal("dan", good?.SignInName);
        Assert.Equal(["kit-directory-group"], good!.ExternalGroups);
        Assert.Same(SignInResolution.Nobody, altered);
    }

    private sealed class Request(string credential) : ISignInRequest
    {
        public string Header(string name) => name == KitFakeSignInAdapter.HeaderName ? credential : "";
        public string? Cookie(string name) => null;
    }

    [Fact]
    public async Task Every_reminder_rule_passes_on_the_kits_sink_with_no_flaw()
    {
        foreach (var fact in Facts(typeof(ReminderSinkConformance)))
            Assert.Null(await RunAsync(new SinkFixture(new KitFakeReminderSink(["group:Night shift"])), fact));
    }

    [Theory]
    [InlineData(KitFakeReminderSink.Flaw.TakesTheBuiltInName, nameof(ReminderSinkConformance.It_names_itself_and_not_as_the_built_in_sink))]
    [InlineData(KitFakeReminderSink.Flaw.DeliversWithoutARun, nameof(ReminderSinkConformance.Nothing_is_delivered_until_a_run_is_handed_to_it))]
    [InlineData(KitFakeReminderSink.Flaw.DeliversEveryoneEverything, nameof(ReminderSinkConformance.Each_owner_receives_their_own_reminders_and_nobody_elses))]
    [InlineData(KitFakeReminderSink.Flaw.DropsTheLastOwner, nameof(ReminderSinkConformance.Each_owner_receives_their_own_reminders_and_nobody_elses))]
    [InlineData(KitFakeReminderSink.Flaw.StopsAtTheFirstUnreachableOwner, nameof(ReminderSinkConformance.One_owner_it_cannot_deliver_to_does_not_stop_the_rest))]
    [InlineData(KitFakeReminderSink.Flaw.ThrowsOnAnEmptyRun, nameof(ReminderSinkConformance.An_empty_run_delivers_nothing_and_does_not_throw))]
    public async Task Each_reminder_rule_fails_on_the_sink_that_breaks_it(KitFakeReminderSink.Flaw flaw, string fact)
    {
        var failure = await RunAsync(new SinkFixture(new KitFakeReminderSink(["group:Night shift"], flaw)), fact);

        Assert.NotNull(failure);
        // A sink that throws on an empty run fails with its own exception;
        // every other rule fails with an assertion that says why.
        if (flaw != KitFakeReminderSink.Flaw.ThrowsOnAnEmptyRun) Assert.IsAssignableFrom<XunitException>(failure);
    }
}
