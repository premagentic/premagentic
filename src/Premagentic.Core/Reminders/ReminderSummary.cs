namespace Premagentic.Core.Reminders;

/// <summary>One document a reminder names.</summary>
/// <param name="Path">The document's indexed path.</param>
/// <param name="Title">Its title, when it has one.</param>
/// <param name="Since">
/// When it went stale (its <c>stale_after</c>) on the stale list, or when it
/// was last indexed on the other two; null when that is not known.
/// </param>
public sealed record ReminderItem(string Path, string? Title, DateTimeOffset? Since);

/// <summary>
/// What one owner is reminded of: the documents from the sources they own that
/// are past their stale date, and the ones waiting in the review queue. The
/// documents whose source has no owner go on the administrators' summary, as
/// unowned, and only there.
/// </summary>
/// <param name="OwnerPrincipal">
/// Who it is for, by principal: <c>user:alice</c>, <c>group:Staff</c>, or
/// <c>administrators</c> for the summary that carries the unowned documents.
/// </param>
/// <param name="Stale">Past <c>stale_after</c>.</param>
/// <param name="InReview">Waiting in the review queue.</param>
/// <param name="Unowned">From a source with no owner; empty on every summary but the administrators'.</param>
public sealed record ReminderSummary(
    string OwnerPrincipal,
    IReadOnlyList<ReminderItem> Stale,
    IReadOnlyList<ReminderItem> InReview,
    IReadOnlyList<ReminderItem> Unowned)
{
    /// <summary>The principal the administrators' summary is addressed to.</summary>
    public const string Administrators = "administrators";
}

/// <summary>
/// Where the summaries of a reminders run are delivered. The built-in sink,
/// named <c>table</c>, keeps the latest run in the database for the portal and
/// is always delivered to first; an extension can add another, such as one that
/// sends mail, and it is delivered to after. A sink that throws is reported and
/// does not stop the others.
/// </summary>
public interface IReminderSink
{
    /// <summary>The sink's name, as the run reports it; <c>table</c> is the built-in's.</summary>
    string Name { get; }

    Task DeliverAsync(IReadOnlyList<ReminderSummary> summaries, DateTimeOffset computedAt, CancellationToken ct);
}

/// <summary>What one reminders run found, and whether it was only planned.</summary>
/// <param name="Owners">How many summaries were computed, the administrators' included.</param>
/// <param name="Planned">True for <c>--plan</c>: computed and printed, delivered to nobody, nothing written.</param>
public sealed record ReminderRun(DateTimeOffset ComputedAt, int Owners, int Stale, int InReview, int Unowned, bool Planned);
