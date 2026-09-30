using Premagentic.Core.Admin;
using Premagentic.Core.Identity;
using Premagentic.Core.Ingestion;
using Premagentic.Core.Profiles;
using Premagentic.Core.Storage;

namespace Premagentic.Cli.Admin;

/// <summary>
/// <c>prem profile</c>: a whole configuration as a folder of plain files.
/// <para>
/// A profile carries what an installation is set up with, so a second office
/// or a rebuilt server is configured from the same files rather than from
/// somebody's memory of which settings were changed. It can set what an
/// administrator can set and nothing more: there is no path here to create a
/// user, grant a role, or write a rule naming somebody who does not exist.
/// </para>
/// </summary>
internal static class ProfileCommands
{
    public const string Verb = "profile";

    public const string Usage = """
        prem profile validate <folder> [--golden-set-dir dir]
        prem profile apply <folder> [--golden-set-dir dir]
        prem profile show [<folder>] [--golden-set-dir dir]

          A profile is a folder of plain files carrying a whole configuration. validate checks it against
          this deployment and changes nothing; apply applies all of it or refuses all of it; show says what
          is applied, and how this deployment differs from a profile.

          --golden-set-dir dir   where a profile's golden set is copied; the server has to be able to read
                                 it (default: Premagentic-golden-sets in the shared application data,
                                 C:\ProgramData on Windows, beside setup's credentials folder, never in it)
        """;

    /// <param name="chunkers">
    /// The chunkers this process has, from the composition point. A profile may
    /// name one an extension registered, so validating against the built-in set
    /// alone would refuse a profile this deployment can in fact apply.
    /// </param>
    /// <param name="settings">
    /// The settings this process defines, with the ones its extensions added, so
    /// a profile that names an extension's setting is told to set it with
    /// 'prem settings' rather than that no such setting exists.
    /// </param>
    public static async Task<int> RunAsync(
        string[] args, PremagenticDatabase db, Guid tenantId, ChunkerRegistry? chunkers = null,
        DeploymentSettings? settings = null)
    {
        try
        {
            return args.ElementAtOrDefault(1) switch
            {
                "validate" => await ValidateAsync(args, db, tenantId, chunkers, settings),
                "apply" => await ApplyAsync(args, db, tenantId, chunkers, settings),
                "show" => await ShowAsync(args, db, tenantId, chunkers, settings),
                _ => Fail("Usage:\n" + Usage),
            };
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return Fail(ex.Message);
        }
    }

    private static async Task<int> ValidateAsync(
        string[] args, PremagenticDatabase db, Guid tenantId, ChunkerRegistry? chunkers, DeploymentSettings? settings)
    {
        var (plan, problems) = await PlanAsync(args, db, tenantId, chunkers, settings);
        if (plan is null) return Refuse(problems);

        Console.WriteLine($"{plan.Profile.Describe()} is a profile this deployment can apply.");
        Print(plan);
        Console.WriteLine();
        Console.WriteLine(plan.UpToDate
            ? "Nothing was checked against a change, because there is nothing to change."
            : "Nothing was applied. Run 'prem profile apply' to make these changes.");
        return 0;
    }

    private static async Task<int> ApplyAsync(
        string[] args, PremagenticDatabase db, Guid tenantId, ChunkerRegistry? chunkers, DeploymentSettings? settings)
    {
        var (plan, problems) = await PlanAsync(args, db, tenantId, chunkers, settings);
        if (plan is null) return Refuse(problems);

        var report = await ProfileApply.RunAsync(plan, db, tenantId, AdminActor.Cli(), chunkers);
        foreach (var change in report.Applied) Console.WriteLine($"  {change}");
        if (report.Applied.OfType<SettingChange>().Any(c => OAuthSettings.ReadAtStart.Contains(c.Key)))
            Console.WriteLine(
                "The MCP authorization flow's settings above are read when the server starts; restart it for them to apply.");

        if (report.Complete)
            return Ok(report.Applied.Count == 0
                ? $"{plan.Profile.Describe()} applied. This deployment already matched it, so nothing was changed."
                : $"{plan.Profile.Describe()} applied. {report.Applied.Count} change(s).");

        Console.Error.WriteLine();
        Console.Error.WriteLine($"Stopped: {report.Problem}");
        Console.Error.WriteLine($"{report.Applied.Count} change(s) were applied. These were NOT:");
        foreach (var change in report.NotApplied) Console.Error.WriteLine($"  {change}");
        Console.Error.WriteLine(
            "The deployment is part way to the profile. Fix what stopped it and apply again: the changes already " +
            "made will be left alone, because a change that is already in force is not made twice.");
        return 1;
    }

    private static async Task<int> ShowAsync(
        string[] args, PremagenticDatabase db, Guid tenantId, ChunkerRegistry? chunkers, DeploymentSettings? settings)
    {
        var applied = await ProfileApply.LatestAsync(db, tenantId);
        Console.WriteLine(applied is null
            ? "No profile has been applied to this deployment."
            : $"Applied: {applied.Name} {applied.Version}, on {applied.AppliedAt:u} by {applied.AppliedBy}.");

        var folder = CliArgs.Positionals(args, 2, "--golden-set-dir").FirstOrDefault();
        if (folder is null) return 0;

        Console.WriteLine();
        var (plan, problems) = await PlanAsync(args, db, tenantId, chunkers, settings);
        if (plan is null) return Refuse(problems);

        Console.WriteLine(plan.UpToDate
            ? $"This deployment matches {plan.Profile.Describe()}."
            : $"This deployment differs from {plan.Profile.Describe()} in {plan.Changes.Count} place(s):");
        Print(plan);
        return 0;
    }

    private static async Task<(ProfilePlan? Plan, IReadOnlyList<ProfileProblem> Problems)> PlanAsync(
        string[] args, PremagenticDatabase db, Guid tenantId, ChunkerRegistry? chunkers, DeploymentSettings? settings)
    {
        var folder = CliArgs.Positionals(args, 2, "--golden-set-dir").FirstOrDefault()
            ?? throw new ArgumentException("Usage:\n" + Usage);

        if (!ProfileReader.TryRead(folder, out var profile, out var problems)) return (null, problems);

        return await ProfilePlanner.BuildAsync(profile!, db, tenantId, GoldenSetFolder(args), chunkers, settings: settings);
    }

    /// <summary>
    /// Where a profile's golden set is copied to. The default is a folder in
    /// the shared application data, because the file is read by the account
    /// the SERVER runs as, which is usually not the account applying the
    /// profile; a per-account folder would apply cleanly and then fail to be
    /// read. It is beside setup's credentials folder, never in it
    /// (<see cref="InstallFolders.GoldenSets"/>).
    /// </summary>
    private static string GoldenSetFolder(string[] args) =>
        CliArgs.Value(args, "--golden-set-dir") ?? InstallFolders.GoldenSets;

    private static void Print(ProfilePlan plan)
    {
        foreach (var change in plan.Changes) Console.WriteLine($"  {change}");
    }

    private static int Refuse(IReadOnlyList<ProfileProblem> problems)
    {
        Console.Error.WriteLine($"The profile was refused. Nothing was changed. {problems.Count} problem(s):");
        foreach (var problem in problems) Console.Error.WriteLine($"  {problem}");
        return 1;
    }

    private static int Ok(string message) { Console.WriteLine(message); return 0; }

    private static int Fail(string message) { Console.Error.WriteLine(message); return 1; }
}
