using Premagentic.Core.Extensions;
using Premagentic.Core.Reminders;
using Premagentic.Core.Storage;

namespace Premagentic.Cli.Admin;

/// <summary>
/// <c>prem reminders</c>: what each source owner is reminded of, computed from
/// the index and handed to the built-in sink, which keeps it for the portal,
/// and to every sink an allowed extension added. Meant for a scheduler.
/// </summary>
internal static class RemindersCommands
{
    public const string Verb = "reminders";

    public const string Usage = """
        prem reminders run [--plan]

          Finds the documents past their stale date and the documents waiting in the review queue, groups
          them by the owner of the source each came from, and hands each owner's summary to every reminder
          sink. Documents whose source has no owner go to the administrators' summary as unowned. The
          built-in sink keeps the latest run in the database for the portal and calls nothing outside it;
          an extension can add another. Meant to run on a schedule, from Task Scheduler or cron. Exits 1
          when a sink could not take the run.

          --plan                 print what would be delivered, per owner, and deliver and write nothing
        """;

    public static async Task<int> RunAsync(string[] args, PremagenticDatabase db, Guid tenantId, ExtensionHost host)
    {
        if (args.ElementAtOrDefault(1) != "run")
        {
            Console.Error.WriteLine(Usage);
            return 1;
        }

        var plan = args.Contains("--plan");
        var job = new RemindersJob(db, tenantId, host.ReminderSinks, log: Console.WriteLine);
        var run = await job.RunAsync(plan, CancellationToken.None);

        if (plan)
        {
            Console.WriteLine(
                $"Planned, not delivered: {run.Owners} owner(s), {run.Stale} stale, {run.InReview} in review, " +
                $"{run.Unowned} unowned, computed {run.ComputedAt:u}. Nothing was written.");
            foreach (var summary in job.Summaries)
            {
                Console.WriteLine();
                Console.WriteLine(summary.OwnerPrincipal);
                foreach (var item in summary.Stale) Console.WriteLine($"  stale      {item.Path}{Since(item, "stale since")}");
                foreach (var item in summary.InReview) Console.WriteLine($"  in review  {item.Path}{Since(item, "indexed")}");
                foreach (var item in summary.Unowned) Console.WriteLine($"  unowned    {item.Path}{Since(item, "since")}");
            }
            return 0;
        }

        var sinks = string.Join(", ", new[] { TableReminderSink.SinkName }.Concat(host.ReminderSinks.Select(s => s.Name)));
        Console.WriteLine($"Delivered to {sinks}.");
        foreach (var failure in job.Failures)
            Console.Error.WriteLine($"WARNING: the reminder sink {failure.Sink} could not take the run: {failure.Message}");
        return job.Failures.Count == 0 ? 0 : 1;
    }

    private static string Since(ReminderItem item, string what) => item.Since is { } at ? $"  ({what} {at:u})" : "";
}
