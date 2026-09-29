namespace Premagentic.Core.Identity;

/// <summary>
/// Who made an agent, as <c>prem_config.agent.created_by</c> stores it:
/// <c>cli:&lt;account&gt;</c>, <c>portal:&lt;user id&gt;</c>, <c>self:&lt;user id&gt;</c>,
/// <c>profile:&lt;name&gt;</c>, <c>oauth:&lt;client id&gt;</c>, or <c>unknown</c>
/// for an agent made before the column existed.
/// </summary>
/// <param name="Kind"><c>cli</c>, <c>portal</c>, <c>self</c>, <c>profile</c>, <c>oauth</c> or <c>unknown</c>.</param>
/// <param name="UserId">
/// The user id for <c>portal</c> and <c>self</c>; the account for <c>cli</c>;
/// the profile's name for <c>profile</c>; the client id for <c>oauth</c>;
/// null for <c>unknown</c>.
/// </param>
public sealed record AgentOrigin(string Kind, string? UserId)
{
    public const string CliKind = "cli";
    public const string PortalKind = "portal";
    public const string SelfKind = "self";
    public const string ProfileKind = "profile";
    public const string UnknownKind = "unknown";

    /// <summary>
    /// Made by a person approving an assistant through the authorization flow,
    /// stored as <c>oauth:&lt;client id&gt;</c>. Such an agent is issued no
    /// token: its credentials belong to its grant.
    /// </summary>
    public const string OAuthKind = "oauth";

    public static AgentOrigin Unknown { get; } = new(UnknownKind, null);

    /// <summary>Registered at the command line by the operating-system account <paramref name="account"/>.</summary>
    public static AgentOrigin Cli(string account) => new(CliKind, Require(account));

    /// <summary>Registered in the portal by the administrator <paramref name="userId"/>.</summary>
    public static AgentOrigin Portal(Guid userId) => new(PortalKind, CallerResolver.IdText(userId));

    /// <summary>Made on the connect page by the person <paramref name="userId"/>, for themself.</summary>
    public static AgentOrigin Self(Guid userId) => new(SelfKind, CallerResolver.IdText(userId));

    /// <summary>Made by applying the profile <paramref name="name"/>.</summary>
    public static AgentOrigin Profile(string name) => new(ProfileKind, Require(name));

    /// <summary>Made through the authorization flow by the client <paramref name="clientId"/>. See <see cref="OAuthKind"/>.</summary>
    public static AgentOrigin OAuth(string clientId) => new(OAuthKind, Require(clientId));

    /// <summary>The stored form, such as <c>self:0f1e...</c> or <c>unknown</c>.</summary>
    public string Text => UserId is null ? Kind : $"{Kind}:{UserId}";

    public override string ToString() => Text;

    /// <summary>Reads the stored form. Anything it does not recognize reads as <see cref="Unknown"/>.</summary>
    public static AgentOrigin Parse(string? text)
    {
        if (string.IsNullOrEmpty(text)) return Unknown;
        var colon = text.IndexOf(':');
        if (colon <= 0 || colon == text.Length - 1) return Unknown;
        var kind = text[..colon];
        return kind is CliKind or PortalKind or SelfKind or ProfileKind or OAuthKind ? new AgentOrigin(kind, text[(colon + 1)..]) : Unknown;
    }

    private static string Require(string value) =>
        string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("An agent's origin names who made it.") : value;
}
