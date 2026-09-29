# Writing an extension

This page takes you from an empty folder to an extension PremAgentic loads:
a reader for a made-up kind of file, proven with the conformance kit,
installed, allowed by hash, shown as loaded, then upgraded, with the
refusals you meet on the way. Every command and file was run as written, on
Windows in PowerShell; on Linux or macOS the slashes turn. How the host
hashes, loads and refuses an extension is on the [Extensions](extensions.md)
page. This page is the doing.

An extension is a folder holding one assembly and an `extension.json` beside
it; the assembly has a public class implementing `IExtension`, and nothing
else marks it. The layout used here:

```text
work\
  premagentic\        your clone of this repository
  note-reader\        the extension project
  note-reader-tests\  its conformance tests
  kit\                the conformance kit, packed from your clone
  packages\           the test project's own packages folder
  extensions\         stands in for a deployment's extensions folder
```

You need the .NET 10 SDK, Docker, and the README's quick start done once in
`premagentic`, so that `prem` reaches a database from your shell.

## The extension

`note-reader\NoteReader.csproj`. The one reference is `Premagentic.Core`,
with `Private="false"` so no copy lands beside your assembly: the host
supplies it. The target writes the manifest after every build, with the hash
of the assembly just built.

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <AssemblyName>NoteReader</AssemblyName>
    <Version>1.0.0</Version>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\premagentic\src\Premagentic.Core\Premagentic.Core.csproj" Private="false" />
  </ItemGroup>
  <Target Name="WriteExtensionManifest" AfterTargets="Build">
    <GetFileHash Files="$(TargetPath)" Algorithm="SHA256" HashEncoding="hex">
      <Output TaskParameter="Items" ItemName="HashedAssembly" />
    </GetFileHash>
    <PropertyGroup>
      <AssemblyHash>@(HashedAssembly->'%(FileHash)')</AssemblyHash>
      <ManifestText>{
  "name": "note-reader",
  "version": "$(Version)",
  "assemblyFile": "$(TargetFileName)",
  "sha256": "$(AssemblyHash)",
  "seams": { "reader": 1 }
}</ManifestText>
    </PropertyGroup>
    <WriteLinesToFile File="$(TargetDir)extension.json" Lines="$(ManifestText)" Overwrite="true" WriteOnlyWhenDifferent="true" />
  </Target>
</Project>
```

Set the version here, never with `-p:Version=` on the command line: that is
a global property, it stamps the `Premagentic.Core` reference too, and the
host then refuses the assembly with `did not load` for asking a newer core.

`note-reader\NoteReaderExtension.cs`: the extension, and the reader it
registers. A `.note` file is plain UTF-8 text whose first line is the title;
no built-in reader claims it, and one reader gives every connector the
format at once, because connectors find paths and readers read them.

```csharp
using System.Text;
using Premagentic.Core.Extensions;
using Premagentic.Core.Ingestion.Readers;

namespace NoteReader;

public sealed class NoteReaderExtension : IExtension
{
    public string Name => "note-reader";

    public void Register(ExtensionRegistrations registrations) =>
        registrations.AddReader(new NoteFileReader());
}

public sealed class NoteFileReader : IDocumentReader
{
    public string Name => "note";

    public IReadOnlyList<string> Extensions { get; } = [".note"];

    public async Task<ReadDocument> ReadAsync(Stream content, string path, CancellationToken ct)
    {
        using var reader = new StreamReader(content, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        var text = await reader.ReadToEndAsync(ct);
        var firstLine = text.Split('\n', 2)[0].Trim();
        return new ReadDocument(text, firstLine.Length == 0 ? null : firstLine, null);
    }
}
```

The stream a reader is handed is read-only, positioned at the start, and
holds the whole file; from the pipeline it is a `MemoryStream` whose buffer a
reader may take with `TryGetBuffer` instead of copying the file, and only
read.

The whole text is indexed, title line included, so a search can cite it. A
reader returns text taken from the file and writes nothing of its own,
because what it returns is what a search serves and cites.

A reader built for reader seam 2 may return `ReadDocument.Skipped("reason")`
for a file it recognizes and will not index, and throw
`UnreadableDocumentException("reason")` for one it cannot read, such as a
password-protected file. The run counts a skipped file under its extension
and the reason, as `.note (<reason>)`, and reports an unreadable one with the
reason, keeping its existing index entry. The reason is shown to an
administrator, so it says what is wrong with the file in a few words and
quotes none of its content. Declare `"seams": { "reader": 2 }` if you use
either; a reader that uses neither, like this one, stays on 1, and an
extension built for reader 1 loads on a host that offers reader 2. Whatever
else a reader throws on a file is contained to that file: it is reported
unreadable, and the run goes on. Build it:

```powershell
dotnet build .\note-reader -c Release
```

## Prove it keeps the contract

The fixtures come as the package `Premagentic.Conformance`, the conformance
kit. No package feed carries it, so pack it from your clone into `kit`, which
then serves as a local package feed. From `work`:

```powershell
dotnet pack .\premagentic\tests\Premagentic.Conformance -c Release -o .\kit
```

That writes `kit\Premagentic.Conformance.0.1.0.nupkg`. The kit's version is
the product version it is built with: the `Version` in your clone's
`Directory.Build.props`, 0.1.0 here, or the value given to `dotnet pack` as
`-p:Version=<version>`. The kit carries the fixtures and no copy of the core,
and brings `xunit` 2.9.3 with it.

`note-reader-tests\nuget.config` sends every `Premagentic.*` package to that
folder and every other package to nuget.org:

```xml
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="local-kit" value="..\kit" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="local-kit">
      <package pattern="Premagentic.*" />
    </packageSource>
    <packageSource key="nuget.org">
      <package pattern="*" />
    </packageSource>
  </packageSourceMapping>
</configuration>
```

`note-reader-tests\NoteReader.Tests.csproj` takes the kit by package
reference, and `Premagentic.Core` and the extension from their projects.
Since the kit carries no core, the test project references the core of the
same clone the extension builds against:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <RestorePackagesPath>..\packages</RestorePackagesPath>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Premagentic.Conformance" Version="0.1.0" />
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="18.10.1" />
    <PackageReference Include="xunit.runner.visualstudio" Version="3.1.4" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\premagentic\src\Premagentic.Core\Premagentic.Core.csproj" />
    <ProjectReference Include="..\note-reader\NoteReader.csproj" />
  </ItemGroup>
</Project>
```

`RestorePackagesPath` gives the test project a packages folder of its own.
NuGet does not restore a package id and version again once it holds a copy,
so a kit packed again at the same version is used only after you delete
`packages\premagentic.conformance`.

`note-reader-tests\NoteFileReaderConformanceTests.cs` inherits
`DocumentReaderConformance`, hands it the reader, and gives it one invented
file with the words its text must carry:

```csharp
using System.Text;
using Premagentic.Conformance;
using Premagentic.Core.Ingestion.Readers;

namespace NoteReader.Tests;

public sealed class NoteFileReaderConformanceTests : DocumentReaderConformance
{
    protected override IDocumentReader Reader => new NoteFileReader();

    protected override IReadOnlyList<ReaderSample> Samples { get; } =
    [
        new("closing.note",
            Encoding.UTF8.GetBytes("Closing the yard\nThe last van leaves at six.\nThe gate is locked after it.\n"),
            ["Closing the yard", "The last van leaves at six.", "The gate is locked after it."]),
    ];
}
```

```powershell
dotnet test .\note-reader-tests
```

It runs four tests: the kit proves the reader seam your PremAgentic offers,
the reader names itself and the extensions it reads, it returns what is in
the file, and an empty file does not throw and has no text. Break the reader
once to see the fixture work: return a description instead of the text and
`It_returns_what_is_in_the_file` fails with "A reader returns the text of the
file, not a description of it." (so does the empty-file test, since the
description comes back for an empty file too).

The seam test, `The_kit_proves_the_seams_this_extension_builds_against`, ties
the kit to the PremAgentic you build against. A kit packed from a version
whose reader seam differs from your core's fails it, with a sentence naming
the kit's version, the reader seam version it proves and the version your
core offers.

Pack the kit from the clone whose core you reference, as above, and the two
agree. Chunkers, connectors, embedding providers, sign-in adapters and
reminder sinks have fixtures of their own in the same kit.

A sign-in adapter inherits `SignInAdapterConformance` and gives it one thing,
the adapter, set up as a deployment would set it up. The kit carries an
adapter written the way one should be, `KitFakeSignInAdapter`, which stands
in for yours here:

```csharp
using Premagentic.Conformance;
using Premagentic.Core.Identity.SignIn;

namespace MyAdapter.Tests;

public sealed class MyAdapterConformanceTests : SignInAdapterConformance
{
    protected override ISignInAdapter Adapter { get; } = new KitFakeSignInAdapter();
}
```

It checks what only an adapter can get wrong. A request with no headers and
no cookies signs in nobody. A request whose every header and cookie holds a
value nobody issued signs in nobody, because an adapter verifies what it
reads; the failure names the headers and cookies your adapter read. And the
adapter's assembly uses nothing that could make an account, grant a role or
change a rule: PremAgentic's identity store, its database, its access rules,
its administration or the PostgreSQL driver. The rest holds for every adapter
because the host keeps it: the name an adapter returns is looked up among the
accounts an administrator made, so a name nobody made reaches nothing, and the
groups it reports count only through the deployment's principal mapper, and
not at all when there is none.

A reminder sink inherits `ReminderSinkConformance` and gives it the sink, two
owners it can deliver to, one it cannot, and what it has delivered so far, read
back from whatever it delivers to. With the kit's `KitFakeReminderSink`, which
keeps what it delivers in memory, standing in for yours:

```csharp
using Premagentic.Conformance;
using Premagentic.Core.Reminders;

namespace MySink.Tests;

public sealed class MySinkConformanceTests : ReminderSinkConformance
{
    private readonly KitFakeReminderSink _sink = new(["group:Night shift"]);

    protected override IReminderSink Sink => _sink;
    protected override IReadOnlyList<string> Owners => ["user:alice", "group:Staff"];
    protected override string Unreachable => "group:Night shift";
    protected override IReadOnlyDictionary<string, string> Delivered() => _sink.Delivered;
}
```

It hands the sink runs it makes from invented documents. Nothing is delivered
before a run is handed over, which is what keeps `prem reminders run --plan`
silent, since a plan hands a run to no sink. Each owner receives their own
reminders and nobody else's, and every reminder in the run reaches its owner.
One owner the sink cannot reach does not keep the run from the owners after
it; the sink may throw once the others have theirs, and the run reports that.
The mail-reminders sample, `samples/extensions/mail-reminders`, is held to
this fixture in this repository's tests, delivering to an SMTP stand-in on
loopback. The connector fixture needs no unreadable item from you:
leave `WithAnUnreadableItemAsync` alone and the kit holds one of your items
unreadable itself. Override it with a real unreadable item where your system
can make one, which also proves what your connector does when it is refused.
Returning null from it fails the test, and so does a connector that reads its
items itself, which has to give a real one.

## Install it

An administrator installs an extension by putting its folder under the
extensions folder and allowing it. From `premagentic`:

```powershell
cd .\premagentic
New-Item -ItemType Directory -Force ..\extensions\note-reader | Out-Null
$env:PREM_EXTENSIONS_DIR = (Resolve-Path ..\extensions).Path
Copy-Item ..\note-reader\bin\Release\net10.0\NoteReader.dll, ..\note-reader\bin\Release\net10.0\extension.json ..\extensions\note-reader
dotnet run --project src/Premagentic.Cli -- extensions list
```

`PREM_EXTENSIONS_DIR` names the extensions folder for this shell; a
deployment sets `extensions.folder` instead, as a full path. Nothing loads
yet, and that is the default: the list shows `note-reader  (not allowed)`
under Refused, and the deployment runs with the built-ins. `allow` reads the
manifest, hashes the assembly itself, and writes the name and that hash to
`extensions.allowed`, with an entry in the change record. Every `prem`
command is a new process, so the list that follows shows it loaded, and from
then on every source that holds `.note` files reads them.

```powershell
dotnet run --project src/Premagentic.Cli -- extensions allow ..\extensions\note-reader
dotnet run --project src/Premagentic.Cli -- extensions list
```

```text
Allowed note-reader with hash cfda9c98c71fb0b25977aa72bb5898556f0e8378f5daddb134281543ec017af9. It loads the next time a Premagentic process starts.
Loaded:
  note-reader 1.0.0  cfda9c98c71fb0b25977aa72bb5898556f0e8378f5daddb134281543ec017af9
```

## When the hash is wrong

Change the version in `NoteReader.csproj` to `1.0.1`, build again, and copy
only the assembly, which is the mistake an upgrade invites:

```powershell
dotnet build ..\note-reader -c Release
Copy-Item ..\note-reader\bin\Release\net10.0\NoteReader.dll ..\extensions\note-reader
dotnet run --project src/Premagentic.Cli -- extensions list
```

```text
Refused:
  note-reader  (hash mismatch)
    "NoteReader.dll" hashes to fed3c30bcc6cc1c2662f20c18703cc35ec3c018faaa7e8018ae3685d50e51936 and the manifest says cfda9c98c71fb0b25977aa72bb5898556f0e8378f5daddb134281543ec017af9.
```

Copy the manifest too and the reason changes to `not allowed`: the pair is
consistent, but nobody allowed this hash. A new assembly is a new hash, and
an upgrade is allowed again on purpose:

```powershell
Copy-Item ..\note-reader\bin\Release\net10.0\extension.json ..\extensions\note-reader
dotnet run --project src/Premagentic.Cli -- extensions allow ..\extensions\note-reader
dotnet run --project src/Premagentic.Cli -- extensions list
```

```text
Loaded:
  note-reader 1.0.1  fed3c30bcc6cc1c2662f20c18703cc35ec3c018faaa7e8018ae3685d50e51936

Allowed (extensions.allowed):
  note-reader  cfda9c98c71fb0b25977aa72bb5898556f0e8378f5daddb134281543ec017af9  (not in the extensions folder)
  note-reader  fed3c30bcc6cc1c2662f20c18703cc35ec3c018faaa7e8018ae3685d50e51936  (loaded)
```

The earlier hash stays allowed, so the old build can go back without a new
allow, and `prem extensions disallow note-reader` drops every hash under the
name. Every other refusal reads the same way, and none stops the deployment.

What you ship is the folder: the assembly, its manifest, and any other file
the manifest lists under `files`, each with its SHA-256. A file in the folder
the manifest does not list is never loaded, and the allowed hash an
administrator writes covers every listed file, so a library replaced later
refuses the whole extension until it is allowed again.
`samples/extensions/paragraph-chunker` ships a library of its own that way.
A native library goes under `runtimes/<rid>/native/` and is listed by its
path, as `samples/extensions/native-lines` does; your build can write the
list, as both samples do. If your extension keeps a settings file, read it
from `registrations.Folder` when it registers; the assembly is loaded from
its bytes, so its own location is empty. An extension can also add a `prem`
command and a setting of its own: `samples/extensions/greeting-command` does
both, and [Extensions](extensions.md) says what the host holds them to.
The administrator copies it under their extensions folder, runs `prem
extensions allow` on it, and restarts. Build against the version they run or
an older one, keep every fixture invented, and if the seam you need does not
exist, that is a proposal for an issue; `CONTRIBUTING.md` says how.
