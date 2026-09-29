using Premagentic.Core.Identity;
using Premagentic.Core.Security.Acl;

namespace Premagentic.Tests;

public class IdentityCallerResolverTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly InMemoryDirectory _dir = new();
    private readonly FixedClock _clock = new(Now);
    private readonly CallerResolver _resolver;

    private readonly User _alice = new(Guid.NewGuid(), "alice", Role.Member, Disabled: false);
    private readonly User _admin = new(Guid.NewGuid(), "admin", Role.Administrator, Disabled: false);
    private readonly Group _staff = new(Guid.NewGuid(), "staff");
    private readonly Group _contractors = new(Guid.NewGuid(), "contractors");
    private readonly Group _auditors = new(Guid.NewGuid(), "auditors");

    public IdentityCallerResolverTests()
    {
        _resolver = new CallerResolver(_dir, _clock);
        _dir.Add(_alice, _staff, _contractors);
        _dir.Add(_admin, _staff);
    }

    private static Principal U(User u) => Principal.User(CallerResolver.IdText(u.Id));
    private static Principal G(Group g) => Principal.Group(CallerResolver.IdText(g.Id));
    private static Principal A(Agent a) => Principal.Agent(CallerResolver.IdText(a.Id));

    private static void AssertHoldsExactly(ResolvedCaller caller, params Principal[] expected)
    {
        Assert.Equal(CallerStatus.Resolved, caller.Status);
        Assert.Equal(
            expected.Select(p => p.ToString()).Order(StringComparer.Ordinal),
            caller.Principals.Select(p => p.ToString()).Order(StringComparer.Ordinal));
    }

    private static void AssertRefused(ResolvedCaller caller, CallerStatus status)
    {
        Assert.Equal(status, caller.Status);
        Assert.False(caller.IsResolved);
        Assert.Same(PrincipalSet.Empty, caller.Principals);
        Assert.False(caller.Principals.Contains(Principal.Everyone));
    }

    private Agent AddAgent(AgentMode mode, User owner, bool disabled = false, params Group[] granted) =>
        AddAgent(mode, owner, ModelLocation.Local, disabled, granted);

    private Agent AddAgent(AgentMode mode, User owner, ModelLocation model, bool disabled = false, params Group[] granted)
    {
        var agent = new Agent(
            Guid.NewGuid(), "agent", owner.Id, mode, disabled, RequestsPerMinute: 60, MinimumTrustTier: "reviewed",
            ModelLocation: model, ModelVendor: model == ModelLocation.Hosted ? "a vendor" : null);
        _dir.Agents[agent.Id] = agent;
        _dir.AgentGrants[agent.Id] = granted.Select(g => g.Id).ToList();
        foreach (var g in granted) _dir.Groups[g.Id] = g;
        return agent;
    }

    private string AddToken(Agent agent, DateTimeOffset? expires = null, DateTimeOffset? revoked = null)
    {
        var issued = AgentTokens.Issue(agent.Id, Now.AddDays(-1), expires ?? Now.AddDays(30));
        _dir.Tokens[issued.Record.Id] = issued.Record with { RevokedAt = revoked };
        return issued.PlainText;
    }

    // Users

    [Fact]
    public async Task A_user_holds_their_id_their_groups_and_everyone()
    {
        var caller = await _resolver.ResolveAsync(CallerIdentity.SignedInUser(_alice.Id));

        AssertHoldsExactly(caller, U(_alice), G(_staff), G(_contractors), Principal.Everyone);
        Assert.Equal(_alice.Id, caller.UserId);
        Assert.Null(caller.AgentId);
    }

    [Fact]
    public async Task A_role_adds_nothing_to_what_a_user_holds()
    {
        var caller = await _resolver.ResolveAsync(CallerIdentity.SignedInUser(_admin.Id));
        AssertHoldsExactly(caller, U(_admin), G(_staff), Principal.Everyone);
    }

    [Fact]
    public async Task A_disabled_user_holds_nothing()
    {
        _dir.Users[_alice.Id] = _alice with { Disabled = true };
        AssertRefused(await _resolver.ResolveAsync(CallerIdentity.SignedInUser(_alice.Id)), CallerStatus.UserDisabled);
    }

    [Fact]
    public async Task An_unknown_user_holds_nothing()
    {
        AssertRefused(await _resolver.ResolveAsync(CallerIdentity.SignedInUser(Guid.NewGuid())), CallerStatus.UnknownUser);
    }

    [Fact]
    public async Task A_directory_that_returns_the_wrong_user_grants_nothing()
    {
        var asked = Guid.NewGuid();
        _dir.Users[asked] = _alice;
        AssertRefused(await _resolver.ResolveAsync(CallerIdentity.SignedInUser(asked)), CallerStatus.InconsistentDirectory);
    }

    [Fact]
    public async Task Removing_a_membership_applies_to_the_next_call()
    {
        Assert.True((await _resolver.ResolveAsync(CallerIdentity.SignedInUser(_alice.Id))).Principals.Contains(G(_staff)));

        _dir.Memberships[_alice.Id].Remove(_staff.Id);

        Assert.False((await _resolver.ResolveAsync(CallerIdentity.SignedInUser(_alice.Id))).Principals.Contains(G(_staff)));
    }

    // Group identity: rules name the id, never the name.

    [Fact]
    public async Task A_renamed_group_still_matches_its_deny_entry()
    {
        var acl = AclSet.Of(AclEntry.Deny(G(_contractors)), AclEntry.Allow(G(_staff)));
        Assert.False(AclEvaluator.CanRead(acl, (await _resolver.ResolveAsync(CallerIdentity.SignedInUser(_alice.Id))).Principals));

        _dir.Groups[_contractors.Id] = _contractors with { Name = "vendors" };

        var after = await _resolver.ResolveAsync(CallerIdentity.SignedInUser(_alice.Id));
        Assert.True(after.Principals.Contains(G(_contractors)));
        Assert.False(AclEvaluator.CanRead(acl, after.Principals));
    }

    [Fact]
    public async Task A_new_group_that_reuses_an_old_name_matches_nothing_the_old_one_did()
    {
        var allowOld = AclSet.Of(AclEntry.Allow(G(_contractors)));
        var denyOld = AclSet.Of(AclEntry.Deny(G(_contractors)), AclEntry.Allow(G(_staff)));

        // The old group is deleted and a new one takes its name.
        _dir.Groups.Remove(_contractors.Id);
        _dir.Memberships[_alice.Id].Remove(_contractors.Id);
        var reused = new Group(Guid.NewGuid(), _contractors.Name);
        var bob = new User(Guid.NewGuid(), "bob", Role.Member, Disabled: false);
        _dir.Add(bob, reused, _staff);

        var caller = (await _resolver.ResolveAsync(CallerIdentity.SignedInUser(bob.Id))).Principals;

        Assert.False(caller.Contains(G(_contractors)));
        Assert.False(AclEvaluator.CanRead(allowOld, caller));
        Assert.True(AclEvaluator.CanRead(denyOld, caller));
    }

    // Agents that act for a user

    [Fact]
    public async Task An_agent_acting_for_a_user_holds_exactly_the_users_principals()
    {
        var agent = AddAgent(AgentMode.ActsForUser, _alice);
        var token = AddToken(agent);

        var asAgent = await _resolver.ResolveAsync(CallerIdentity.AgentToken(token));
        var asUser = await _resolver.ResolveAsync(CallerIdentity.SignedInUser(_alice.Id));

        AssertHoldsExactly(asAgent, [.. asUser.Principals]);
        Assert.False(asAgent.Principals.Contains(A(agent)));
        Assert.Equal(_alice.Id, asAgent.UserId);
        Assert.Equal(agent.Id, asAgent.AgentId);
    }

    [Fact]
    public async Task An_agent_acting_for_a_user_narrows_when_the_user_does()
    {
        var agent = AddAgent(AgentMode.ActsForUser, _alice);
        var token = AddToken(agent);
        var staffOnly = AclSet.Of(AclEntry.Allow(G(_staff)));

        Assert.True(AclEvaluator.CanRead(staffOnly, (await _resolver.ResolveAsync(CallerIdentity.AgentToken(token))).Principals));

        _dir.Memberships[_alice.Id].Remove(_staff.Id);

        Assert.False(AclEvaluator.CanRead(staffOnly, (await _resolver.ResolveAsync(CallerIdentity.AgentToken(token))).Principals));
    }

    [Fact]
    public async Task An_agent_acting_for_a_user_ignores_groups_granted_to_the_agent()
    {
        var agent = AddAgent(AgentMode.ActsForUser, _alice, granted: [_auditors]);
        var caller = await _resolver.ResolveAsync(CallerIdentity.AgentToken(AddToken(agent)));

        Assert.False(caller.Principals.Contains(G(_auditors)));
    }

    [Fact]
    public async Task An_agent_whose_user_is_disabled_holds_nothing()
    {
        var agent = AddAgent(AgentMode.ActsForUser, _alice);
        var token = AddToken(agent);
        _dir.Users[_alice.Id] = _alice with { Disabled = true };

        AssertRefused(await _resolver.ResolveAsync(CallerIdentity.AgentToken(token)), CallerStatus.OwnerDisabled);
    }

    [Fact]
    public async Task An_agent_whose_user_is_gone_holds_nothing()
    {
        var agent = AddAgent(AgentMode.ActsForUser, _alice);
        var token = AddToken(agent);
        _dir.Users.Remove(_alice.Id);

        AssertRefused(await _resolver.ResolveAsync(CallerIdentity.AgentToken(token)), CallerStatus.OwnerUnknown);
    }

    // Service agents

    [Fact]
    public async Task A_service_agent_holds_its_id_its_granted_groups_and_everyone()
    {
        var agent = AddAgent(AgentMode.Service, _admin, granted: [_auditors]);
        var caller = await _resolver.ResolveAsync(CallerIdentity.AgentToken(AddToken(agent)));

        AssertHoldsExactly(caller, A(agent), G(_auditors), Principal.Everyone);
        Assert.Null(caller.UserId);
        Assert.Equal(agent.Id, caller.AgentId);
    }

    [Fact]
    public async Task A_service_agent_does_not_inherit_its_owners_access()
    {
        var agent = AddAgent(AgentMode.Service, _admin);
        var caller = await _resolver.ResolveAsync(CallerIdentity.AgentToken(AddToken(agent)));

        Assert.False(caller.Principals.Contains(U(_admin)));
        Assert.False(caller.Principals.Contains(G(_staff)));
    }

    [Fact]
    public async Task A_service_agent_whose_owner_is_disabled_holds_nothing()
    {
        var agent = AddAgent(AgentMode.Service, _admin, granted: [_auditors]);
        var token = AddToken(agent);
        _dir.Users[_admin.Id] = _admin with { Disabled = true };

        AssertRefused(await _resolver.ResolveAsync(CallerIdentity.AgentToken(token)), CallerStatus.OwnerDisabled);
    }

    [Fact]
    public async Task A_disabled_agent_holds_nothing()
    {
        var agent = AddAgent(AgentMode.Service, _admin, disabled: true, granted: [_auditors]);
        AssertRefused(await _resolver.ResolveAsync(CallerIdentity.AgentToken(AddToken(agent))), CallerStatus.AgentDisabled);
    }

    [Fact]
    public async Task A_token_for_a_missing_agent_holds_nothing()
    {
        var agent = AddAgent(AgentMode.Service, _admin);
        var token = AddToken(agent);
        _dir.Agents.Remove(agent.Id);

        AssertRefused(await _resolver.ResolveAsync(CallerIdentity.AgentToken(token)), CallerStatus.UnknownAgent);
    }

    [Fact]
    public async Task An_agent_with_an_undefined_mode_holds_nothing()
    {
        var agent = AddAgent(AgentMode.Service, _admin);
        var token = AddToken(agent);
        _dir.Agents[agent.Id] = agent with { Mode = (AgentMode)0 };

        AssertRefused(await _resolver.ResolveAsync(CallerIdentity.AgentToken(token)), CallerStatus.InconsistentDirectory);
    }

    // Tokens

    [Fact]
    public async Task A_token_is_refused_at_the_exact_instant_it_expires()
    {
        var agent = AddAgent(AgentMode.Service, _admin);
        var token = AddToken(agent, expires: Now);

        _clock.Now = Now.AddTicks(-1);
        Assert.True((await _resolver.ResolveAsync(CallerIdentity.AgentToken(token))).IsResolved);

        _clock.Now = Now;
        AssertRefused(await _resolver.ResolveAsync(CallerIdentity.AgentToken(token)), CallerStatus.TokenExpired);

        _clock.Now = Now.AddDays(1);
        AssertRefused(await _resolver.ResolveAsync(CallerIdentity.AgentToken(token)), CallerStatus.TokenExpired);
    }

    [Fact]
    public async Task A_revoked_token_holds_nothing()
    {
        var agent = AddAgent(AgentMode.Service, _admin);
        var token = AddToken(agent, revoked: Now.AddHours(-1));

        AssertRefused(await _resolver.ResolveAsync(CallerIdentity.AgentToken(token)), CallerStatus.TokenRevoked);
    }

    [Fact]
    public async Task A_wrong_secret_is_refused_before_anything_about_the_token_is_reported()
    {
        var agent = AddAgent(AgentMode.Service, _admin);
        var token = AddToken(agent, revoked: Now.AddHours(-1));
        var wrong = token[..^2] + (token[^2] == 'A' ? "BA" : "AA");

        AssertRefused(await _resolver.ResolveAsync(CallerIdentity.AgentToken(wrong)), CallerStatus.TokenMismatch);
    }

    [Fact]
    public async Task An_unknown_token_id_holds_nothing()
    {
        var agent = AddAgent(AgentMode.Service, _admin);
        var token = AddToken(agent);
        _dir.Tokens.Clear();

        AssertRefused(await _resolver.ResolveAsync(CallerIdentity.AgentToken(token)), CallerStatus.UnknownToken);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a token")]
    [InlineData("prem_agt_")]
    [InlineData("Bearer prem_agt_0123456789abcdef01234567_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public async Task A_malformed_token_holds_nothing_and_does_not_throw(string text)
    {
        AssertRefused(await _resolver.ResolveAsync(CallerIdentity.AgentToken(text)), CallerStatus.MalformedToken);
        Assert.Empty(_dir.TokenLookups);
    }

    [Fact]
    public async Task A_null_token_is_malformed()
    {
        AssertRefused(await _resolver.ResolveAsync(CallerIdentity.AgentToken(null!)), CallerStatus.MalformedToken);
    }

    [Fact]
    public void A_presented_token_never_appears_in_a_log_line()
    {
        var identity = CallerIdentity.AgentToken("prem_agt_0123456789abcdef01234567_secretsecret");
        Assert.DoesNotContain("secret", identity.ToString());
    }

    // End to end with the evaluator

    [Fact]
    public async Task A_disabled_user_is_denied_even_by_an_allow_for_everyone()
    {
        var everyone = AclSet.Of(AclEntry.Allow(Principal.Everyone));
        Assert.True(AclEvaluator.CanRead(everyone, (await _resolver.ResolveAsync(CallerIdentity.SignedInUser(_alice.Id))).Principals));

        _dir.Users[_alice.Id] = _alice with { Disabled = true };

        Assert.False(AclEvaluator.CanRead(everyone, (await _resolver.ResolveAsync(CallerIdentity.SignedInUser(_alice.Id))).Principals));
    }

    [Fact]
    public async Task An_agent_acting_for_a_user_never_reads_what_the_user_cannot()
    {
        var agent = AddAgent(AgentMode.ActsForUser, _alice, granted: [_auditors]);
        var asAgent = (await _resolver.ResolveAsync(CallerIdentity.AgentToken(AddToken(agent)))).Principals;
        var asUser = (await _resolver.ResolveAsync(CallerIdentity.SignedInUser(_alice.Id))).Principals;

        var sets = AclConformanceCorpus.Cases
            .Select(c => AclSet.TryParse(c.Entries, out var s) ? s : AclSet.Empty)
            .Append(AclSet.Of(AclEntry.Allow(A(agent))))
            .Append(AclSet.Of(AclEntry.Allow(G(_auditors))))
            .Append(AclSet.Of(AclEntry.Deny(G(_contractors)), AclEntry.Allow(G(_staff))))
            .Append(AclSet.Of(AclEntry.Allow(G(_staff))));

        foreach (var set in sets)
        {
            Assert.Equal(AclEvaluator.CanRead(set, asUser), AclEvaluator.CanRead(set, asAgent));
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class InMemoryDirectory : IIdentityDirectory
    {
        public Dictionary<Guid, User> Users { get; } = [];
        public Dictionary<Guid, Group> Groups { get; } = [];
        public Dictionary<Guid, List<Guid>> Memberships { get; } = [];
        public Dictionary<Guid, Agent> Agents { get; } = [];
        public Dictionary<Guid, List<Guid>> AgentGrants { get; } = [];
        public Dictionary<string, AgentTokenRecord> Tokens { get; } = new(StringComparer.Ordinal);
        public List<string> TokenLookups { get; } = [];

        public void Add(User user, params Group[] groups)
        {
            Users[user.Id] = user;
            foreach (var g in groups) Groups[g.Id] = g;
            Memberships[user.Id] = groups.Select(g => g.Id).ToList();
        }

        public Task<User?> FindUserAsync(Guid userId, CancellationToken ct = default) =>
            Task.FromResult(Users.GetValueOrDefault(userId));

        public Task<IReadOnlyList<Group>> GroupsOfUserAsync(Guid userId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Group>>(
                Memberships.GetValueOrDefault(userId, []).Where(Groups.ContainsKey).Select(id => Groups[id]).ToList());

        public Task<Agent?> FindAgentAsync(Guid agentId, CancellationToken ct = default) =>
            Task.FromResult(Agents.GetValueOrDefault(agentId));

        // Split the same way the store splits them: what an administrator
        // granted is held, what Premagentic maintains only narrows.
        public Task<IReadOnlyList<Group>> GroupsGrantedToAgentAsync(Guid agentId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Group>>(GrantsOf(agentId).Where(g => !g.IsSystem).ToList());

        public Task<IReadOnlyList<Group>> SystemGroupsOfAgentAsync(Guid agentId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Group>>(GrantsOf(agentId).Where(g => g.IsSystem).ToList());

        private IEnumerable<Group> GrantsOf(Guid agentId) =>
            AgentGrants.GetValueOrDefault(agentId, []).Where(Groups.ContainsKey).Select(id => Groups[id]);

        public Task<AgentTokenRecord?> FindTokenAsync(string tokenId, CancellationToken ct = default)
        {
            TokenLookups.Add(tokenId);
            return Task.FromResult(Tokens.GetValueOrDefault(tokenId));
        }
    }
}
