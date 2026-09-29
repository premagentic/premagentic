namespace Premagentic.Core.Extensions;

/// <summary>
/// The version of each seam this process offers. An extension declares in its
/// manifest the version of every seam it was built for, and one above what is
/// here is refused: the extension expects something this process does not have,
/// and loading it anyway is how a plug-in half works.
/// <para>
/// A version is the least an extension needs, so a host offering version N
/// loads an extension built for any version up to N. It goes up when the
/// contract changes in a way an extension built for the older one cannot meet,
/// and also when a member is added that an extension may call: an extension
/// that uses it declares the new version, and an older host then refuses it
/// with a reason instead of failing it on the missing member partway through a
/// run. Adding a member an extension neither implements nor calls is not such a
/// change.
/// </para>
/// <para>
/// Reader version 2 added <see cref="Ingestion.Readers.ReadDocument.Skipped"/>
/// and <see cref="Ingestion.Readers.UnreadableDocumentException"/>. A reader
/// built for version 1 loads and reads as before.
/// </para>
/// <para>
/// Command version 1 and setting version 1 are
/// <see cref="ExtensionRegistrations.AddCommand"/> and
/// <see cref="ExtensionRegistrations.AddSetting"/>: a <c>prem</c> command an
/// extension adds, and a setting it defines. An extension that registers
/// either declares that seam, so an older version refuses it by name.
/// </para>
/// <para>
/// Principals version 1 is <see cref="Identity.IPrincipalMapper"/> and
/// <see cref="ExtensionRegistrations.AddPrincipalMapper"/>. A host without it
/// refuses an extension that declares it, naming the seam.
/// </para>
/// </summary>
public static class SeamVersions
{
    public const int Reader = 2;
    public const int Chunker = 1;
    public const int Source = 1;
    public const int Embedding = 1;
    public const int SignIn = 1;
    public const int Reminder = 1;
    public const int Command = 1;
    public const int Setting = 1;
    public const int Principals = 1;

    public const string ReaderName = "reader";
    public const string ChunkerName = "chunker";
    public const string SourceName = "source";
    public const string EmbeddingName = "embedding";
    public const string SignInName = "signin";
    public const string ReminderName = "reminder";
    public const string CommandName = "command";
    public const string SettingName = "setting";
    public const string PrincipalsName = "principals";

    private static readonly Dictionary<string, int> ByName = new(StringComparer.OrdinalIgnoreCase)
    {
        [ReaderName] = Reader,
        [ChunkerName] = Chunker,
        [SourceName] = Source,
        [EmbeddingName] = Embedding,
        [SignInName] = SignIn,
        [ReminderName] = Reminder,
        [CommandName] = Command,
        [SettingName] = Setting,
        [PrincipalsName] = Principals,
    };

    /// <summary>Every seam name, in the order they are listed above.</summary>
    public static IReadOnlyList<string> Names { get; } =
        [ReaderName, ChunkerName, SourceName, EmbeddingName, SignInName, ReminderName, CommandName, SettingName, PrincipalsName];

    /// <summary>
    /// The version this process offers for <paramref name="seam"/>, compared
    /// ignoring case, or null when this process has no seam by that name.
    /// </summary>
    public static int? Of(string seam) => ByName.TryGetValue(seam, out var version) ? version : null;
}
