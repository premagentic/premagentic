# Installing

Installing the database side of PremAgentic on a PostgreSQL you already run, the roles and files setup makes, HTTPS from the first start, the bundled PostgreSQL on Windows, and installing from an archive built from the source.

## Installing on an existing PostgreSQL

`prem setup` installs the database side of PremAgentic on a PostgreSQL 14 or
later that you already run (tested on 17), then searches to prove it works. Give it a role that can create
roles and databases, through `PREM_SETUP_ADMIN_CONNECTION` or
`--admin-connection-file`; that connection is used and never stored.

It creates two roles. The owner owns the schema and is used only for migrations
and `rebuild-index`. The application role reads and writes rows and can do
nothing else: no DDL, no ownership, no role creation. Each gets a generated
password, sent to the server only as a SCRAM verifier and written only to its
own credentials file (`app.credentials`, `owner.credentials`), readable only by
the account that ran setup. Point PremAgentic at the application's file with
`PREM_CREDENTIALS_FILE`, and at the owner's only to migrate or rebuild.

A third role, `premagentic_search`, is what searches read through: it may read
documents and their chunks, which row-level security limits to what the
caller may see, and nothing else. Its password goes to `search.credentials`,
beside the application's file. Setup also has the server stop any statement
this role runs after 15 seconds (its `statement_timeout` in this database),
unless one is already set.

On Windows, setup makes the folder these files go to before it writes any
(`--credentials-dir`; by default the Premagentic folder in your local
application data, or `C:\ProgramData\Premagentic` with `--windows-service`),
with inheritance off, so only this account, administrators and SYSTEM may
open it. It never takes the rules of the folder above it: `C:\ProgramData`
lets every user make folders and files, so a folder made the ordinary way
there could have been made first, and filled, by anyone. Before it reads
anything, setup checks the folder and each file it would use: it must be
neither a junction nor a symbolic link, it must be owned by administrators,
SYSTEM or the account running setup, and nobody else may change it. Otherwise
setup stops and says which path it is and what is wrong, before reading it.
A folder setup made is read back once it is made, so one another account made
first is found then. An install made by an earlier setup is refused until it
is put right as [Upgrading and removing](upgrading-and-removing.md) shows.

A credentials file that other accounts could read keeps its role but never
its password: setup sets a new one on the role, writes it to the file only
once the role has it, and says to restart the API and anything else that
connects as that role. The bundled server's superuser gets a new password the
same way; its file is never to be moved aside once the cluster exists, since
it holds the only copy of that password. From an elevated prompt, setup hands
that file to administrators: owned by them and read through their one rule,
so a second administrator's setup run finds it as the first one's did.

On Linux, the folder is mode 700 and every file mode 600, as before. Two
things change there too: a credentials file that is not mode 600 gets a new
password, where it used to keep its old one with its mode put right; and
`kestrel.json` may hold the `Kestrel` section alone.

The same run can make the first administrator, with `--admin-user <name>`:
the password is asked for at the prompt, or read from the first line of
`--admin-password-file`, and never taken as an argument. It is made only when
no administrator exists; there is never a default account.

It also makes HTTPS work from the first start: a self-signed certificate for
`--host-name` (default: this computer's name), written with its key to
`https.pfx` under a generated password, and `kestrel.json`, which tells the
API to listen on `https://*:8443` (`--https-port` to change) with that
certificate. The API reads `kestrel.json` from the folder of its
`PREM_CREDENTIALS_FILE`. Give clients `https.crt` to trust. To use your own
certificate, point `Certificate` in `kestrel.json` at your PFX and its
password, or at a PEM certificate and key, and restart the API; setup never
replaces a certificate it did not make. The file explains this too. It holds
the `Kestrel` section and nothing else: the API reads it into its
configuration, so it refuses to start when the file sets anything more, and
setup refuses such a file before it says it is complete. A certificate
issued by a certificate authority may make the runtime fetch its revocation
status (OCSP) and any missing intermediate certificates from the addresses
the certificate names, more than once; those are outbound connections, and
the self-signed certificate names no such address.

Setup checks first that this computer handles Unicode text the way password
hashing needs, and stops if it does not. Published builds carry their own
Unicode library, so do not set `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT`.

Run it again at any time: a finished install is left as it is, and one that
stopped half way is completed. `prem setup --plan` shows every step and
changes nothing.

## The bundled PostgreSQL on Windows

On Windows, PremAgentic can bring its own PostgreSQL 17.
`installer/windows/fetch-postgresql.ps1` lays out the minimal set of stock
binaries from EDB's archive (checked against a pinned hash) with its license
notices. It needs the Microsoft Visual C++ 2015 to 2022 x64 runtime
(`vc_redist.x64.exe`). Then

```powershell
prem setup --bundled-postgres build\postgresql\pgsql --data-dir D:\Premagentic\data --windows-service ...
```

makes the database cluster in the folder you choose, listening on this
computer only, keeps its superuser's generated password in
`postgres.credentials` (readable by administrators only), and runs it as the
service `PremagenticDb` under its own account. Without `--windows-service` it
starts the server as you, until you stop it with `pg_ctl stop`.

## Installing from an archive

No release has been published yet, so an archive is built from the source
with `scripts/release/build.sh`:

```bash
scripts/release/build.sh --version <X.Y.Z> --out <folder>
```

It builds the commit that is checked out, never the working tree, into a
self-contained `.tar.gz` for Linux x64 and a `.zip` for Windows x64, with a
`SHA256SUMS` beside them (`--rid linux-x64` or `--rid win-x64` builds one).
It needs bash, git, exactly the .NET SDK that the repository's `global.json`
names, GNU tar, gzip, unzip and sha256sum, and runs on Linux or from Git Bash
on Windows. The SDK decides the .NET runtime the archives carry, so the build
refuses any other; a runtime security patch is taken by raising the version in
`global.json`, and `licenses/PACKAGES.txt` in each archive names the runtime it
carries. It takes the embedding model from
`models/minilm`, fetching it first when that folder is empty, and refuses it
unless both of its files match their pinned SHA-256 values.

The Windows archive also carries the four Visual C++ runtime files its
programs need in `bin\`, taken unmodified from a pinned Microsoft
redistributable (`scripts/download-vc-runtime.sh` fetches it when its folder
is empty and refuses it unless its size and SHA-256 match their pins), so
nothing else needs installing on a clean Windows; a PostgreSQL you already
run brings its own.

Every package comes from a committed lock file: each project's
`packages.lock.json`, and for the projects a release publishes, one lock file
per platform (`packages.linux-x64.lock.json`, `packages.win-x64.lock.json`),
because a published program also carries the ICU package. The build and CI
restore in locked mode, so a package id or version that changed without its
lock file stops the build. After changing a package, run
`scripts/release/lock-files.sh` and commit the lock files it writes;
`scripts/release/lock-files.sh --check` restores against all of them. A NuGet
update pull request from Dependabot fails that check until someone runs the
script on its branch, so whoever merges it runs the script there first.

An archive holds the command line (`bin/prem`), the API with the portal
(`bin/Premagentic.Api`) and the MCP bridge (`bin/Premagentic.McpServer`),
side by side in `bin/` with the one .NET runtime and Unicode library they
share; the embedding model in `models/minilm`, with its license and the
revision it came from; the starter profile and its sample documents; the
first-party PDF, Word and Excel readers in `extensions/`, which load only
once an administrator allows them; the systemd unit in the Linux archive;
and, in `licenses/`, the license text of every package and runtime pack the
three programs and the readers deploy. The programs find the model from where
they are, so installing from an archive needs no .NET install and no internet
connection. It carries no database: setup uses a PostgreSQL 14 or later that
you already run, as above, and the Windows archive does not carry the bundled
PostgreSQL.

The three programs are published self-contained one at a time and then laid
into one folder. Where they carry different copies of one library, the build
keeps the copy with the higher file version, and on a tie the .NET runtime's
own precompiled copy, as the SDK does inside one program; it refuses anything
it cannot order that way. With the runtime `global.json` pins at the same
patch as the NuGet packages the programs reference, the archive's
Microsoft.Extensions libraries are the runtime's own copies.
`licenses/PACKAGES.txt` names every version the programs carry.

Each archive's `INSTALL.txt` gives the steps: unpack it where it will live
(on Linux into `/opt/premagentic`, where the service unit expects it), write
an admin connection file, run `prem setup` with the first administrator and
the host name, start the API, and apply the starter profile and ingest its
two sources, or ingest your own folders; to read PDF, Word and Excel files,
point `extensions.folder` at the archive's `extensions/` and allow each
reader with `prem extensions allow`. The [Known state](status.md) page says
how the archives were proven, and what is not proven yet, such as the bundled
database as a service and a managed PostgreSQL.