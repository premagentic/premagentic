using Premagentic.Core.Identity;
using Premagentic.Core.Security.Acl;
using Premagentic.Core.Storage;

namespace Premagentic.Tests;

/// <summary>
/// The one way outside principals become PremAgentic groups, below both of
/// its callers: with no mapper nothing is mapped and nothing is guessed, and
/// with one, the host keeps only answers to what it asked, and only as a live
/// group an administrator made in this tenant, whatever the mapper returned.
/// Requires a running Docker daemon.
/// </summary>
public sealed class PrincipalMappingTests(DatastoreTestDatabase server) : IClassFixture<DatastoreTestDatabase>
{
    private sealed record World(PremagenticDatabase Db, Guid Tenant, IdentityStore Identity, Group Finance);

    private async Task<World> NewWorldAsync()
    {
        var db = new PremagenticDatabase(await server.CreateDatabaseAsync());
        await db.InitializeAsync();
        var tenant = await db.EnsureTenantAsync("t", "T");
        var identity = new IdentityStore(db, tenant);
        return new World(db, tenant, identity, await identity.CreateGroupAsync("Finance"));
    }

    /// <summary>Answers from a fixed table, and keeps every request it was handed.</summary>
    private sealed class TableMapper(IReadOnlyDictionary<string, Guid> table) : IPrincipalMapper
    {
        public List<PrincipalMapRequest> Requests { get; } = [];

        public string Name => "table";

        public Task<IReadOnlyDictionary<string, Guid>> MapAsync(PrincipalMapRequest request, CancellationToken ct = default)
        {
            Requests.Add(request);
            return Task.FromResult(table);
        }
    }

    /// <summary>
    /// A mapper that writes over what it was handed: it casts the principals
    /// it was asked about back to the array they might be, puts a name nobody
    /// asked about in the first place, and answers for that name.
    /// </summary>
    private sealed class OverwritingMapper(string planted, Guid group) : IPrincipalMapper
    {
        public bool Overwrote { get; private set; }

        public string Name => "overwriting";

        public Task<IReadOnlyDictionary<string, Guid>> MapAsync(PrincipalMapRequest request, CancellationToken ct = default)
        {
            if (request.Principals is string[] handed)
            {
                handed[0] = planted;
                Overwrote = true;
            }
            return Task.FromResult<IReadOnlyDictionary<string, Guid>>(new Dictionary<string, Guid> { [planted] = group });
        }
    }

    /// <summary>A mapper that answers null rather than an answer, empty or not.</summary>
    private sealed class NullMapper : IPrincipalMapper
    {
        public string Name => "null-answer";

        public Task<IReadOnlyDictionary<string, Guid>> MapAsync(PrincipalMapRequest request, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<string, Guid>>(null!);
    }

    private sealed class FailingMapper : IPrincipalMapper
    {
        public string Name => "failing";

        public Task<IReadOnlyDictionary<string, Guid>> MapAsync(PrincipalMapRequest request, CancellationToken ct = default) =>
            throw new InvalidOperationException("the directory could not be reached");
    }

    [Fact]
    public async Task With_no_mapper_nothing_outside_means_anything_however_it_reads()
    {
        var w = await NewWorldAsync();
        await using var _ = w.Db;
        var financeId = CallerResolver.IdText(w.Finance.Id);

        // Each of these reads like something PremAgentic knows: the group's
        // name, its principal, its id, everyone. None of them is read that way.
        string[] outside = ["Finance", "group:" + financeId, financeId, "everyone", "S-1-5-21-7", "  ", "Finance"];
        var resolved = await new PrincipalMapping(w.Db, w.Tenant, mapper: null).ResolveAsync(outside);

        Assert.Empty(resolved.Mapped);
        Assert.Equal(["Finance", "group:" + financeId, financeId, "everyone", "S-1-5-21-7"], resolved.Unmapped);
    }

    [Fact]
    public async Task A_mapper_is_asked_each_principal_once_and_never_a_blank_one()
    {
        var w = await NewWorldAsync();
        await using var _ = w.Db;
        var mapper = new TableMapper(new Dictionary<string, Guid> { ["S-1-5-21-1"] = w.Finance.Id });

        var resolved = await new PrincipalMapping(w.Db, w.Tenant, mapper).ResolveAsync(["S-1-5-21-1", "", "S-1-5-21-2", "S-1-5-21-1"]);

        var request = Assert.Single(mapper.Requests);
        Assert.Equal(["S-1-5-21-1", "S-1-5-21-2"], request.Principals);
        Assert.Equal(w.Tenant, request.TenantId);
        Assert.Equal(Principal.Group(CallerResolver.IdText(w.Finance.Id)), Assert.Single(resolved.Mapped).Value);
        Assert.Equal(["S-1-5-21-2"], resolved.Unmapped);

        // Nothing to ask, nothing asked.
        Assert.Empty((await new PrincipalMapping(w.Db, w.Tenant, mapper).ResolveAsync(["", " "])).Unmapped);
        Assert.Single(mapper.Requests);
    }

    /// <summary>
    /// A mapper that answers with every group it should not: the one
    /// PremAgentic keeps for hosted-model agents, a deleted group, an id
    /// nothing answers to and another tenant's group. Each principal it
    /// answered that way means nothing, and the one it answered with an
    /// administrator's live group is the control that the answer is read.
    /// </summary>
    [Fact]
    public async Task A_mapper_answer_counts_only_as_a_live_group_an_administrator_made_here()
    {
        var w = await NewWorldAsync();
        await using var _ = w.Db;
        var reserved = await w.Identity.FindSystemGroupAsync(SystemGroups.HostedModelAgents);
        var gone = await w.Identity.CreateGroupAsync("Gone");
        Assert.True(await w.Identity.DeleteGroupAsync(gone.Id));
        var otherTenant = await w.Db.EnsureTenantAsync("other", "Other");
        var theirs = await new IdentityStore(w.Db, otherTenant).CreateGroupAsync("Finance");

        var mapper = new TableMapper(new Dictionary<string, Guid>
        {
            ["finance"] = w.Finance.Id,
            ["reserved"] = reserved!.Id,
            ["gone"] = gone.Id,
            ["nowhere"] = Guid.NewGuid(),
            ["theirs"] = theirs.Id,
        });

        var resolved = await new PrincipalMapping(w.Db, w.Tenant, mapper)
            .ResolveAsync(["finance", "reserved", "gone", "nowhere", "theirs"]);

        Assert.Equal(["finance"], resolved.Mapped.Keys);
        Assert.Equal(Principal.Group(CallerResolver.IdText(w.Finance.Id)), resolved.Mapped["finance"]);
        Assert.Equal(["reserved", "gone", "nowhere", "theirs"], resolved.Unmapped);
    }

    /// <summary>
    /// A mapper that answers for principals nobody asked about: one never
    /// named, and one named with other case than it was asked. Neither
    /// counts, so a mapper cannot add a group to a caller for a principal the
    /// caller's adapter or document never carried.
    /// </summary>
    [Fact]
    public async Task A_mapper_answer_counts_only_for_a_principal_that_was_asked_about_as_it_was_asked()
    {
        var w = await NewWorldAsync();
        await using var _ = w.Db;
        var ops = await w.Identity.CreateGroupAsync("Ops");
        var mapper = new TableMapper(new Dictionary<string, Guid>
        {
            ["cn=finance"] = w.Finance.Id,
            ["CN=Ops"] = ops.Id,
            ["cn=never-asked"] = ops.Id,
        });

        var resolved = await new PrincipalMapping(w.Db, w.Tenant, mapper).ResolveAsync(["cn=finance", "cn=ops"]);

        Assert.Equal(["cn=finance"], resolved.Mapped.Keys);
        Assert.DoesNotContain(Principal.Group(CallerResolver.IdText(ops.Id)), resolved.Mapped.Values);
        Assert.Equal(["cn=ops"], resolved.Unmapped);
    }

    /// <summary>
    /// A mapper cannot change what it was asked. It is handed a copy it
    /// cannot write to, and the names its answers are held to, and the names
    /// counted as meaning nothing, are settled before it runs: the name it
    /// tried to plant means nothing, and the name it was asked stays the one
    /// counted.
    /// </summary>
    [Fact]
    public async Task A_mapper_that_writes_over_what_it_was_asked_cannot_get_a_name_nobody_asked_counted()
    {
        var w = await NewWorldAsync();
        await using var _ = w.Db;
        var mapper = new OverwritingMapper("cn=planted", w.Finance.Id);

        var resolved = await new PrincipalMapping(w.Db, w.Tenant, mapper).ResolveAsync(["cn=asked"]);

        Assert.False(mapper.Overwrote, "the mapper was handed the array itself, and wrote over it");
        Assert.Empty(resolved.Mapped);
        Assert.Equal(["cn=asked"], resolved.Unmapped);
    }

    [Fact]
    public async Task A_mapper_that_answers_null_fails_the_read_rather_than_reading_as_unmapped()
    {
        var w = await NewWorldAsync();
        await using var _ = w.Db;

        var failed = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new PrincipalMapping(w.Db, w.Tenant, new NullMapper()).ResolveAsync(["S-1-5-21-1"]));
        Assert.Equal("The principal mapper 'null-answer' answered nothing at all, not even an empty answer.", failed.Message);
    }

    [Fact]
    public async Task A_mapper_that_fails_fails_the_read_rather_than_reading_as_unmapped()
    {
        var w = await NewWorldAsync();
        await using var _ = w.Db;

        var failed = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new PrincipalMapping(w.Db, w.Tenant, new FailingMapper()).ResolveAsync(["S-1-5-21-1"]));
        Assert.Equal("the directory could not be reached", failed.Message);
    }
}
