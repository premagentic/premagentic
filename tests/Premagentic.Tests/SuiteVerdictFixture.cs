namespace Premagentic.Tests;

/// <summary>
/// A test that never returns, for proving that a run of the suite still ends
/// in a verdict when one test hangs: it waits for good only while
/// <see cref="Switch"/> is 1 in the test process's environment, and otherwise
/// passes at once. Nothing in the repository sets the switch; a proof sets it
/// for the one run it makes.
/// </summary>
public sealed class SuiteVerdictFixture
{
    public const string Switch = "PREM_TEST_HANG";

    [Fact]
    public async Task Hangs_only_while_its_switch_is_on()
    {
        if (Environment.GetEnvironmentVariable(Switch) != "1") return;
        await Task.Delay(Timeout.Infinite);
    }
}
