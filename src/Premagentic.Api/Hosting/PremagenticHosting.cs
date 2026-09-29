using Premagentic.Core.Storage;
using Microsoft.Extensions.Configuration.Memory;
using Microsoft.Extensions.Hosting.WindowsServices;

namespace Premagentic.Api.Hosting;

/// <summary>
/// How the API runs once installed: as a console process, a Windows service or a
/// systemd unit from the one build, over HTTPS with the certificate setup made,
/// and behind a reverse proxy only when that proxy is named.
/// </summary>
internal static class PremagenticHosting
{
    public const string ServiceName = "Premagentic";

    /// <summary>
    /// The category the framework writes its two request lines in, "Request
    /// starting" and "Request finished", each with the request's whole query
    /// string, at Information.
    /// </summary>
    public const string RequestLineCategory = "Microsoft.AspNetCore.Hosting.Diagnostics";

    /// <summary>
    /// The request lines are off by default: the authorization flow's
    /// authorize address carries a client's state and PKCE challenge in its
    /// query, and the log is no place for either. Set for every provider and
    /// by name for each built-in one, so a provider's own default level set
    /// by an operator does not bring them back. The lowest source of all, so
    /// an operator who names the category, for any provider, gets the lines
    /// and their queries.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string?> RequestLineDefaults =
        new[] { "", "Console:", "Debug:", "EventSource:", "EventLog:" }
            .ToDictionary(provider => $"Logging:{provider}LogLevel:{RequestLineCategory}", _ => (string?)"Warning");

    /// <summary>Puts <see cref="RequestLineDefaults"/> under every other configuration source.</summary>
    public static void AddRequestLineDefaults(IConfigurationBuilder config) =>
        config.Sources.Insert(0, new MemoryConfigurationSource { InitialData = RequestLineDefaults });

    /// <summary>
    /// Service lifetimes, the HTTPS settings and the trusted proxies. The two
    /// service managers are each detected, and each registration does nothing
    /// unless the process runs under that manager; a console run is unchanged.
    /// <para>
    /// Settings are read from the host's configuration, which holds the
    /// environment, so a host started in process can be given its own.
    /// </para>
    /// <para>
    /// With no database configured, or a credentials file that cannot be read,
    /// this throws <see cref="StartupRefusedException"/>, which the entry point
    /// turns into the sentence that says what to set and exit code 2 (the code
    /// the systemd unit does not restart on).
    /// </para>
    /// <para>
    /// A Windows service starts in the system folder, so the working folder
    /// becomes the application's own, where relative paths such as the default
    /// model folder can be found. A systemd unit sets its own.
    /// </para>
    /// <para>
    /// HTTPS comes from the Kestrel settings file <c>prem setup</c> wrote beside
    /// the credentials file named by <c>PREM_CREDENTIALS_FILE</c>. Its relative
    /// paths are read as relative to that file. With no such file the endpoints
    /// are whatever the configuration says, as for development. The file may
    /// hold the <c>Kestrel</c> section and nothing else: it is read into the
    /// whole configuration, where anything else in it, such as the header
    /// sign-in, would be a setting nobody looks for there.
    /// </para>
    /// <para>
    /// Before anything is read from it, the folder of that credentials file, the
    /// file, and the files beside it the API reads must be owned by
    /// administrators, SYSTEM or the account the API runs as, and changeable by
    /// nobody else (<see cref="InstallAccess.HostRefusal"/>); on Windows the API
    /// refuses to start otherwise.
    /// </para>
    /// </summary>
    /// <exception cref="StartupRefusedException">A setting the API cannot start with.</exception>
    public static WebApplicationBuilder AddPremagenticHosting(this WebApplicationBuilder builder)
    {
        var config = builder.Configuration;
        AddRequestLineDefaults(config);
        if (InstallAccess.HostRefusal(config["PREM_CREDENTIALS_FILE"]) is { } untrusted)
            throw new StartupRefusedException(untrusted);
        PremagenticDatabase.ConnectionStringFrom(key => config[key]);
        TrustedProxies.AddTo(builder.Services, config);

        if (WindowsServiceHelpers.IsWindowsService())
            Directory.SetCurrentDirectory(AppContext.BaseDirectory);
        builder.Services.AddWindowsService(options => options.ServiceName = ServiceName);
        builder.Services.AddSystemd();

        if (InstallFiles.KestrelSettingsBeside(config["PREM_CREDENTIALS_FILE"]) is { } settings)
        {
            var own = new ConfigurationBuilder().AddJsonFile(settings, optional: false, reloadOnChange: false).Build();
            if (KestrelSettingsRefusal(settings, own.GetChildren().Select(section => section.Key)) is { } refusal)
                throw new StartupRefusedException(refusal);
            config.AddJsonFile(settings, optional: false, reloadOnChange: false);
            config.AddInMemoryCollection(InstallFiles.ResolveRelativePaths(own.AsEnumerable(), Path.GetDirectoryName(settings)!));
        }
        return builder;
    }

    /// <summary>
    /// Null when the Kestrel settings file at <paramref name="path"/> holds the
    /// <c>Kestrel</c> section alone, by its top-level keys; otherwise the
    /// refusal, naming what else it sets.
    /// </summary>
    internal static string? KestrelSettingsRefusal(string path, IEnumerable<string> topLevelKeys) =>
        InstallFiles.KestrelSettingsRefusal(path, topLevelKeys);
}
