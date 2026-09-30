namespace Premagentic.Cli;

/// <summary>
/// The folders the command line names by default, in one place, so a folder
/// one command writes to can be checked against the folder setup keeps private.
/// </summary>
internal static class InstallFolders
{
    /// <summary>The name of the credentials folder, per account or shared.</summary>
    public const string CredentialsName = "Premagentic";

    /// <summary>
    /// The name of the folder <c>prem profile apply</c> copies golden sets to,
    /// beside the credentials folder and never in it. Setup refuses a
    /// credentials folder any account but administrators, SYSTEM and its own
    /// may change, and a folder made the ordinary way in C:\ProgramData is one
    /// every user may add files to; a golden set copied into the credentials
    /// folder before setup made it would make it that way.
    /// </summary>
    public const string GoldenSetsName = "Premagentic-golden-sets";

    /// <summary>
    /// The folder shared by every account on the machine: C:\ProgramData on
    /// Windows, /usr/share on Linux.
    /// </summary>
    public static string Shared =>
        SharedForTests.Value ?? Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

    /// <summary>
    /// The credentials folder setup uses by default: in the shared folder with
    /// <c>--windows-service</c>, since the service's account has to reach it,
    /// else in this account's application data.
    /// </summary>
    public static string Credentials(bool windowsService) =>
        Path.Combine(windowsService
            ? Shared
            : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), CredentialsName);

    /// <summary>
    /// Where a profile's golden set is copied by default. It is shared rather
    /// than per account because the file is read by the account the server
    /// runs as, which is usually not the account applying the profile. Every
    /// account on the machine may read what is copied there, the service's
    /// included, from the rules C:\ProgramData gives the folders made in it.
    /// </summary>
    public static string GoldenSets => Path.Combine(Shared, GoldenSetsName);

    /// <summary>A stand-in for the shared folder, for a test only.</summary>
    internal static readonly AsyncLocal<string?> SharedForTests = new();
}
