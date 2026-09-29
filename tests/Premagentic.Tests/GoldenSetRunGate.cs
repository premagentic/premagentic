namespace Premagentic.Tests;

/// <summary>
/// The test classes that run a golden set in this process. GoldenSetRun lets
/// one run at a time per process, through a static gate it takes before it
/// judges the golden set, and xUnit runs classes in parallel in one process:
/// two of these side by side turned each other's refusals and runs into
/// "already in progress". Classes in one collection never run in parallel
/// with each other, so they take the gate one after another. A class that runs
/// GoldenSetRun, or holds GoldenSetRun.InProgress, joins this collection.
/// </summary>
[CollectionDefinition(Name)]
public sealed class GoldenSetRunGate
{
    public const string Name = "GoldenSetRun's process-wide one-run gate";
}
