using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Premagentic.Api.Hosting;

namespace Premagentic.Tests;

/// <summary>
/// The framework's request lines, which carry each request's whole query
/// string, are off unless an operator names their category: a default level an
/// operator sets for every category, or for one provider, does not bring them
/// back. Built on the host builder an install runs, with its default providers.
/// </summary>
public sealed class RequestLineLoggingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(false, "--Logging:LogLevel:Default=Trace")]
    [InlineData(false, "--Logging:Console:LogLevel:Default=Information")]
    [InlineData(false, "--Logging:Debug:LogLevel:Default=Trace")]
    [InlineData(false, "--Logging:EventSource:LogLevel:Default=Information")]
    // Named for every provider at once, the category stays off: each built-in
    // provider has the default by name, and a provider's own rule is the one
    // it follows. An operator turns the lines on for the provider that writes
    // the log they read.
    [InlineData(false, "--Logging:LogLevel:Microsoft.AspNetCore.Hosting.Diagnostics=Information")]
    [InlineData(true, "--Logging:Console:LogLevel:Microsoft.AspNetCore.Hosting.Diagnostics=Information")]
    public void The_request_lines_are_off_unless_an_operator_names_their_category(bool on, params string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        PremagenticHosting.AddRequestLineDefaults(builder.Configuration);
        using var app = builder.Build();

        var requestLines = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(PremagenticHosting.RequestLineCategory);

        Assert.Equal(on, requestLines.IsEnabled(LogLevel.Information));
        Assert.True(requestLines.IsEnabled(LogLevel.Warning));
    }
}
