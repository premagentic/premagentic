using Premagentic.Core.Admin;
using Premagentic.Core.Storage;

namespace Premagentic.Core.Identity;

/// <summary>
/// Listing and revoking grants, for the person who approved them and for an
/// administrator. Works whether the flow is on or off; with it off, a grant's
/// audience cannot be judged, so a list shows what the rest decides.
/// </summary>
public sealed class OAuthGrants(PremagenticDatabase db, Guid tenantId, OAuthDeployment? oauth = null, TimeProvider? time = null) : IOAuthGrants
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public async Task<IReadOnlyList<OAuthGrantView>> ListOwnAsync(Guid userId, CancellationToken ct)
    {
        await using var conn = await db.DataSource.OpenConnectionAsync(ct);
        var now = _time.GetUtcNow();
        return (await OAuthStore.ListGrantsAsync(conn, null, tenantId, "g.user_id = @user", now, ct, ("user", userId)))
            .Select(g => OAuthStore.View(g, now, oauth?.Resource)).ToArray();
    }

    public async Task<IReadOnlyList<OAuthGrantView>> ListAllAsync(OAuthGrantFilter filter, CancellationToken ct)
    {
        await using var conn = await db.DataSource.OpenConnectionAsync(ct);
        var now = _time.GetUtcNow();
        var all = (await OAuthStore.ListGrantsAsync(conn, null, tenantId, "true", now, ct))
            .Select(g => OAuthStore.View(g, now, oauth?.Resource));
        return (filter == OAuthGrantFilter.All ? all : all.Where(g => g.Status == OAuthGrantStatus.Live)).ToArray();
    }

    public async Task RevokeOwnAsync(Guid userId, string grantId, CancellationToken ct)
    {
        await new AdminChanges(db, tenantId, _time).RunAsync(new AdminActor("portal", null, userId), async change =>
        {
            var now = _time.GetUtcNow();
            var grant = await OAuthStore.FindGrantAsync(change.Transaction.Connection!, change.Transaction, tenantId, grantId, now, ct);
            // One answer for a grant that does not exist and one that is not
            // theirs, so the question cannot be used to learn who has which.
            if (grant is null || grant.UserId != userId) throw new InvalidOperationException("You have no connected assistant with this id.");
            return await OAuthStore.RevokeAsync(change, tenantId, grant, OAuthStore.RevokedByOwner, disableAgent: true, now, ct);
        }, ct);
    }

    public async Task RevokeAsync(AdminActor actor, string grantId, CancellationToken ct)
    {
        await new AdminChanges(db, tenantId, _time).RunAsync(actor, async change =>
        {
            var now = _time.GetUtcNow();
            var grant = await OAuthStore.FindGrantAsync(change.Transaction.Connection!, change.Transaction, tenantId, grantId, now, ct)
                ?? throw new InvalidOperationException($"No grant has the id '{grantId}'.");
            return await OAuthStore.RevokeAsync(change, tenantId, grant, OAuthStore.RevokedByAdministrator, disableAgent: true, now, ct);
        }, ct);
    }

    /// <summary>Revokes every unrevoked grant, or every one of one person: the stop that needs no restart.</summary>
    /// <returns>How many this call revoked.</returns>
    public async Task<int> RevokeManyAsync(AdminActor actor, Guid? userId, CancellationToken ct) =>
        await new AdminChanges(db, tenantId, _time).RunAsync(actor, async change =>
        {
            var now = _time.GetUtcNow();
            var grants = userId is { } user
                ? await OAuthStore.ListGrantsAsync(change.Transaction.Connection!, change.Transaction, tenantId,
                    "g.revoked_at IS NULL AND g.user_id = @user", now, ct, ("user", user))
                : await OAuthStore.ListGrantsAsync(change.Transaction.Connection!, change.Transaction, tenantId,
                    "g.revoked_at IS NULL", now, ct);
            var revoked = 0;
            foreach (var grant in grants)
                if (await OAuthStore.RevokeAsync(change, tenantId, grant, OAuthStore.RevokedByAdministrator, disableAgent: true, now, ct))
                    revoked++;
            return revoked;
        }, ct);
}
