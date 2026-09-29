namespace Premagentic.Cli.Setup;

internal enum StepOutcome
{
    /// <summary>Already as it should be; nothing was changed.</summary>
    Done,
    /// <summary>Changed on this run.</summary>
    Applied,
    /// <summary>Would change, in plan mode; nothing was changed.</summary>
    WouldApply,
    /// <summary>A check that ran and passed and changes nothing, such as the health check.</summary>
    Checked,
    Warning,
    Skipped,
    Failed,
}

internal sealed record SetupStep(string Name, StepOutcome Outcome, string Detail);

internal sealed class SetupReport
{
    private readonly List<SetupStep> _steps = [];

    public IReadOnlyList<SetupStep> Steps => _steps;

    public bool Failed => _steps.Any(s => s.Outcome == StepOutcome.Failed);

    /// <summary>True when this run changed the install.</summary>
    public bool Changed => _steps.Any(s => s.Outcome == StepOutcome.Applied);

    public SetupStep? Find(string name) => _steps.LastOrDefault(s => s.Name == name);

    internal void Add(SetupStep step) => _steps.Add(step);
}
