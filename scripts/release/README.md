# The release scripts, and the release checklist

The files in this folder build PremAgentic's release archives and check them.
The release workflow (`.github/workflows/release.yml`) runs them on GitHub's
runner; each also runs from bash on Linux and from Git Bash on Windows.

| File | What it does |
|---|---|
| `build.sh` | Builds one archive per platform from HEAD, with SHA256SUMS. Its header says every option and refusal. |
| `verify-archive.sh` | Checks one finished archive without unpacking it; `build.sh` runs it on every archive it writes. |
| `test.sh` | Shows each of the build's refusals fire, each beside a control that passes. |
| `ReleaseTool.cs` | The jobs that must give the same bytes on Windows and on Linux: packing an archive, the license texts, the extensions, laying the three programs into one `bin/`, and taking the Visual C++ runtime files out of the pinned redistributable. |
| `lock-files.sh` | Writes or checks the NuGet lock files. |
| `packages-folder.sh` | Prints the NuGet packages folder the license texts are read from. |
| `install-text.sh`, `install-linux.txt`, `install-windows.txt` | Each archive's `INSTALL.txt`. |
| `model-notice.txt`, `model-license-apache-2.0.txt`, `vc-runtime-license.txt`, `licenses/` | Texts the archives carry. |
| `prove-linux-archive.sh`, `prove-windows-archive.ps1` | Install an archive by its `INSTALL.txt` on a machine with no .NET. |

The pins live beside the scripts that download what they pin:
`scripts/download-model.sh` (the embedding model) and
`scripts/download-vc-runtime.sh` (the Microsoft Visual C++ Redistributable the
Windows archive takes its four runtime files from).

## Making a release

1. Push a version tag, `vX.Y.Z` (or `vX.Y.Z-rc.1`), on the commit to release.
2. The workflow's `build` job builds both archives and runs `test.sh` on them;
   its `prove` job then proves the Linux archive the build uploaded, on the
   runner's Docker: the clean install with no network
   (`scripts/clean-install/run.sh --offline`), the install with the upgrade
   from the previous release and the rollback (`run.sh`, which says in one line
   when there is no earlier release and the upgrade did not run), and the
   offline proof's control (`control.sh`). Only then does the `publish` job
   make a **draft** release. Nothing is published until a person publishes the
   draft.
3. Before publishing the draft, check the list below.

**The upgrade leg and the SDK.** The previous release is the nearest earlier
`v` tag (a pre-release tag counts only for a pre-release), and the upgrade leg
builds its archive from that tag with the .NET SDK the tag's own `global.json`
pins; `build.sh` refuses any other. The runner installs only the SDK the new
release pins, so a release that raises the SDK pin must also install the
previous pin on the runner, or pass the previous release's published Linux
archive (with its SHA256SUMS beside it) as `PREM_UPGRADE_FROM`.

## The checklist, before a draft is published

- [ ] The run's `prove` job passed, and the SHA256SUMS lines in its summary are
      the draft's. Those are the Linux bytes that were proven.
- [ ] After the first release, the upgrade leg ran: the summary's line reads
      `CLEAN INSTALL PASSED on ... (upgrade from ...)`, and there is no
      `UPGRADE NOT RUN` line. Only the first release may pass without it.
- [ ] The draft's own Windows archive passed the clean Windows proof: a Windows
      that has never had .NET, a build or a network (Windows Sandbox), where
      the zip is installed by its `INSTALL.txt` from a prompt and as a Windows
      service, with the Visual C++ runtime files it ships as the only ones its
      programs can find. Take the archive from the run, not from a local build
      (`gh run download <run id> --name release-archives`), check it against
      its SHA256SUMS, and record the SHA256SUMS line the proof ran on. The
      runner cannot run this proof: a hosted runner has no Windows Sandbox.
- [ ] Both records name the draft's SHA256SUMS lines, so the proven bytes are
      the released bytes.
