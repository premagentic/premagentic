using System.Collections.Concurrent;
using System.Net;
using Premagentic.Api.Callers;
using Premagentic.Core.Identity;
using Premagentic.Core.Identity.SignIn;

namespace Premagentic.Tests;

/// <summary>
/// The groups a sign-in adapter's directory reports, read through the
/// deployment's principal mapper, at the one moment they matter: a request.
/// Both ways: with a mapper, a group it maps reaches that group's folder, and
/// with none, the same group reaches nothing and is said once.
/// <para>
/// The cases go through the real API and the real gate rather than asking the
/// mapper what it holds, because what is being asserted is not that a mapping
/// exists but that it decides what comes back.
/// </para>
/// Requires a running Docker daemon.
/// </summary>
public sealed class SignInMappingTests(DatastoreTestDatabase server) : IClassFixture<DatastoreTestDatabase>
{
    private const string DirectoryGroup = "CONTOSO-Finance-f41b";

    /// <summary>An adapter that signs one name in and reports what its directory said.</summary>
    private sealed class DirectoryAdapter(string signInName, params string[] groups) : ISignInAdapter
    {
        public string Name => "directory";

        public Task<SignInResolution?> ResolveAsync(ISignInRequest request, CancellationToken ct = default) =>
            Task.FromResult<SignInResolution?>(new SignInResolution(signInName, groups));
    }

    /// <summary>
    /// A mapper an administrator's word is written into as the test goes, the
    /// way the add-on's table is: a principal means the group it is set to,
    /// from the next request on.
    /// </summary>
    private sealed class WrittenMapper : IPrincipalMapper
    {
        private readonly ConcurrentDictionary<string, Guid> _written = new(StringComparer.Ordinal);

        public string Name => "written";

        public void Map(string principal, Guid groupId) => _written[principal] = groupId;

        public Task<IReadOnlyDictionary<string, Guid>> MapAsync(PrincipalMapRequest request, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<string, Guid>>(
                request.Principals.Where(_written.ContainsKey).ToDictionary(p => p, p => _written[p], StringComparer.Ordinal));
    }

    private static async Task<ApiWorld> WorldAsync(
        DatastoreTestDatabase server, IPrincipalMapper? mapper, List<string>? logs, params ISignInAdapter[] adapters) =>
        await ApiWorld.NewAsync(server, options: new ApiHostOptions
        {
            ExtraSignInAdapters = adapters, PrincipalMapper = mapper, Logs = logs,
        });

    [Fact]
    public async Task A_directory_group_reaches_what_it_is_mapped_to_and_nothing_when_it_is_mapped_to_nothing()
    {
        // Dan is in no Premagentic group, and his directory says he is in one.
        var mapper = new WrittenMapper();
        await using var w = await WorldAsync(server, mapper, null, new DirectoryAdapter("dan", DirectoryGroup));
        using var client = w.Host.Client();
        var staff = await w.Identity.FindGroupByNameAsync("Staff");
        Assert.NotNull(staff);

        // Unmapped: the group his directory reports means nothing, so he reads
        // exactly what he read before, which is what anyone reads.
        var before = await Api.PathsAsync(await Api.SearchAsync(client));
        Assert.DoesNotContain(ApiWorld.Plan, before);

        mapper.Map(DirectoryGroup, staff.Id);

        // Mapped: the staff folder is his on the very next request. Nothing was
        // re-ingested and he was added to no group by hand.
        var after = await Api.PathsAsync(await Api.SearchAsync(client));
        Assert.Contains(ApiWorld.Plan, after);
        Assert.Contains(ApiWorld.Handbook, after);

        // And it is the mapping and not the sign-in that did it: his stored
        // memberships are still none.
        Assert.Empty(await w.Identity.GroupsOfUserAsync(w.Dan.Id));
    }

    [Fact]
    public async Task A_group_nobody_mapped_is_ignored_rather_than_guessed_at()
    {
        var mapper = new WrittenMapper();
        await using var w = await WorldAsync(server, mapper, null, new DirectoryAdapter("dan", DirectoryGroup, "CONTOSO-Everyone"));
        using var client = w.Host.Client();
        var staff = await w.Identity.FindGroupByNameAsync("Staff");
        mapper.Map(DirectoryGroup, staff!.Id);

        // One of the two means something. The other is ignored: it does not
        // refuse the sign-in, and it does not become a group of its own.
        using (var session = await client.GetAsync("/api/session"))
            Assert.Equal(HttpStatusCode.OK, session.StatusCode);
        Assert.Contains(ApiWorld.Plan, await Api.PathsAsync(await Api.SearchAsync(client)));
        Assert.Null(await w.Identity.FindGroupByNameAsync("CONTOSO-Everyone"));
    }

    [Fact]
    public async Task A_disabled_account_holds_nothing_whatever_its_directory_says()
    {
        var mapper = new WrittenMapper();
        await using var w = await WorldAsync(server, mapper, null, new DirectoryAdapter("dan", DirectoryGroup));
        using var client = w.Host.Client();
        var staff = await w.Identity.FindGroupByNameAsync("Staff");
        mapper.Map(DirectoryGroup, staff!.Id);

        // The control first: while he is enabled, the mapping reaches the folder.
        Assert.Contains(ApiWorld.Plan, await Api.PathsAsync(await Api.SearchAsync(client)));

        Assert.True(await w.Identity.SetUserDisabledAsync(w.Dan.Id, disabled: true));

        // Disabled, the request is refused outright. A directory group is added
        // to what an account holds, and a disabled account holds nothing at all,
        // so there is nothing for it to be added to.
        using var refused = await Api.SearchAsync(client);
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
    }

    [Fact]
    public async Task A_name_the_deployment_does_not_know_reaches_nothing_however_many_groups_it_brings()
    {
        var mapper = new WrittenMapper();
        await using var w = await WorldAsync(server, mapper, null, new DirectoryAdapter("nobody-here", DirectoryGroup));
        using var client = w.Host.Client();
        var staff = await w.Identity.FindGroupByNameAsync("Staff");
        mapper.Map(DirectoryGroup, staff!.Id);

        // The mapping is good and the group is real. The account is not, and an
        // adapter never makes one.
        using var refused = await Api.SearchAsync(client);
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        Assert.Null(await w.Identity.FindUserByNameAsync("nobody-here"));
    }

    /// <summary>
    /// The deployment without the add-on: no principal mapper at all. The
    /// directory's group reaches nothing, the request is not refused for it,
    /// and the log names it once however many requests carry it.
    /// </summary>
    [Fact]
    public async Task Without_a_mapper_a_directory_group_reaches_nothing_and_is_said_once()
    {
        var logs = new List<string>();
        await using var w = await WorldAsync(server, mapper: null, logs, new DirectoryAdapter("dan", DirectoryGroup));
        using var client = w.Host.Client();

        var first = await Api.PathsAsync(await Api.SearchAsync(client));
        var second = await Api.PathsAsync(await Api.SearchAsync(client));

        // What anyone reads, and not the staff folder.
        Assert.Equal([ApiWorld.Handbook], first);
        Assert.Equal(first, second);

        string[] said;
        lock (logs)
            said = [.. logs.Where(l => l.Contains(DirectoryGroup, StringComparison.Ordinal))];
        Assert.Equal(
            $"Information {typeof(UnmappedPrincipalLog).FullName}: Sign-in adapter directory reported the group {DirectoryGroup}, " +
            "which means nothing here because no principal mapper is loaded, so it was ignored.",
            Assert.Single(said));
    }

    /// <summary>
    /// No permissive fallback: with no mapper, a group whose outside name is
    /// the Staff group's name, its principal or its id still means nothing,
    /// so Dan, who is in no group, reads no more than anyone. Read any of them
    /// as a PremAgentic name and the staff folder would be his.
    /// </summary>
    [Fact]
    public async Task Without_a_mapper_an_outside_group_that_reads_like_a_premagentic_group_reaches_nothing()
    {
        // The Staff group's id exists only once the world does, so the adapter
        // is told what to report after it starts.
        var adapter = new SettableAdapter("dan");
        await using var w = await WorldAsync(server, mapper: null, null, adapter);
        var id = CallerResolver.IdText((await w.Identity.FindGroupByNameAsync("Staff"))!.Id);
        adapter.Groups = ["Staff", "group:" + id, id, "everyone"];
        using var client = w.Host.Client();

        Assert.Equal([ApiWorld.Handbook], await Api.PathsAsync(await Api.SearchAsync(client)));
    }

    /// <summary>Signs one name in and reports whatever groups the test has set by then.</summary>
    private sealed class SettableAdapter(string signInName) : ISignInAdapter
    {
        public IReadOnlyList<string> Groups { get; set; } = [];

        public string Name => "directory";

        public Task<SignInResolution?> ResolveAsync(ISignInRequest request, CancellationToken ct = default) =>
            Task.FromResult<SignInResolution?>(new SignInResolution(signInName, Groups));
    }
}
