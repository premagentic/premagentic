namespace Premagentic.Extensions.Shared;

/// <summary>A size as a reason states it: whole megabytes when it is one, bytes otherwise.</summary>
internal static class Sizes
{
    internal static string Of(long bytes) =>
        bytes >= 1024 * 1024 && bytes % (1024 * 1024) == 0 ? $"{bytes / (1024 * 1024)} MB" : $"{bytes} bytes";
}
