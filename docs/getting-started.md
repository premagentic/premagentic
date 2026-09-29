# Getting started

This page takes you from a fresh clone to a cited answer, a gate holding and a green golden set on your own machine, in about ten minutes. It was written from two runs of the README's Quick start on a clone nobody had built before, one on Windows 11 in PowerShell 7 and one on a bare Ubuntu 24.04, and every command and every quoted line below is from those runs. The README keeps the eight steps; this page says what each one prints and what to have ready first.

## Before you start

What was on the two machines when the steps ran:

- **Docker with Compose.** Docker Desktop 28.4.0 with Compose v2.39 on Windows; on Linux, Docker Engine with the compose plugin. The database runs in it. The first `docker compose up` pulls `postgres:17.11`, which is 640 MB on disk.
- **The .NET SDK 10.** The projects target `net10.0`. Windows ran on SDK 10.0.303. On Ubuntu 24.04, `sudo apt-get install -y dotnet-sdk-10.0` installs it from Ubuntu's own archive (10.0.112 here, in 135 s, and it brings the ICU library the SDK needs).
- **git, and on Linux curl.** The model script on Linux downloads with curl. A bare Ubuntu 24.04 has neither git nor curl; `sudo apt-get install -y git curl ca-certificates` took 42 s.
- **On Windows, PowerShell 7.** The steps ran in PowerShell 7.6.6. Windows PowerShell 5.1 refuses step 3 at its default policy with `running scripts is disabled on this system`; either run the steps in PowerShell 7, or once run `Set-ExecutionPolicy -Scope CurrentUser RemoteSigned` in Windows PowerShell, after which the script runs there too.
- **Port 5434 free.** The compose file publishes PostgreSQL there, away from the 5432 and 5433 a PostgreSQL you already run would use.
- **Internet, for the first run only.** Hugging Face for the embedding model (90 MB), Docker Hub for the PostgreSQL image, and NuGet for the packages the first `dotnet run` restores.
- **Room.** The clone with the model and the build output came to about half a gigabyte.

## The ten-minute start

The eight steps, as the README gives them, with the lines each one printed here. Run them from the folder you cloned into. The Windows lines are from PowerShell 7; where Linux differs, its line follows.

**1. Start PostgreSQL.**

```
docker compose up -d
```

The first time, it prints the network, the volume and the container it made:

```
 Network linux_default  Creating
 Network linux_default  Created
 Volume linux_premagentic-pgdata-17  Creating
 Volume linux_premagentic-pgdata-17  Created
 Container premagentic-postgres  Creating
 Container premagentic-postgres  Created
 Container premagentic-postgres  Starting
 Container premagentic-postgres  Started
```

The network and volume names begin with the name of the folder you cloned into (`linux` here). On a later run it prints only ` Container premagentic-postgres  Running`. Nothing waits for the database to be ready; step 4 ran 14 seconds after this one and connected.

**2. Tell this shell to use that database.**

```
$env:PREM_DEV_DATABASE = "1"        # PowerShell
export PREM_DEV_DATABASE=1          # Linux or macOS
```

Prints nothing. It is per shell: a new window needs it again, and without it every `prem` command refuses to start rather than guess where the database is.

**3. Fetch the embedding model, once.**

```
./scripts/download-model.ps1        # Windows
./scripts/download-model.sh         # Linux or macOS
```

```
downloading https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2/resolve/main/onnx/model.onnx...
downloading https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2/resolve/main/vocab.txt...
Models ready.
```

12.7 s on Windows; 26 s on Linux, where curl shows its progress meter for the 86 MB file and the last line is `Models ready in /root/premagentic/models`. The files land in `models/minilm`, which git ignores. Running it again prints `exists:` for each file and downloads nothing.

**4. Make the two groups the sample corpus names.**

```
dotnet run --project src/Premagentic.Cli -- groups add hr
dotnet run --project src/Premagentic.Cli -- groups add engineering
```

```
Group 'hr' added.
Group 'engineering' added.
```

The first `dotnet run` restores the packages and builds before it prints anything, and it is silent while it does: 11 s on Windows with the packages already cached, 56 s on Ubuntu with an empty cache. On a machine where the SDK has never run, that first command also prints the SDK's own welcome banner (`Welcome to .NET 10.0!`, a note that it installed an HTTPS development certificate, and a few links) before `Group 'hr' added.`; that is the SDK's first-use notice, not PremAgentic. Every later `dotnet run` takes 3 to 4 seconds on Windows and 6 to 10 on the Ubuntu container.

**5. Ingest the sample corpus, one folder per rule.**

```
dotnet run --project src/Premagentic.Cli -- ingest ./sample-docs/open --public --prefix open
dotnet run --project src/Premagentic.Cli -- ingest ./sample-docs/hr --principals group:hr --prefix hr
```

```
Rule filesystem:open: allow everyone. 0 indexed document(s) moved to it.
Ingesting ./sample-docs/open using local:all-MiniLM-L6-v2+hctx, cut by the markdown chunker...
  open/handbook.md (2 chunks, active)
  open/travel-policy-old.md (1 chunks, superseded)
Scanned 2, ingested 2, unchanged 0, chunks embedded 3, orphans removed 0, unreadable 0, skipped 0.
Run 7d57decc-cc33-4e76-995e-fdd4001b645a recorded.
```

```
Rule filesystem:hr: allow group:hr. 0 indexed document(s) moved to it.
Ingesting ./sample-docs/hr using local:all-MiniLM-L6-v2+hctx, cut by the markdown chunker...
  hr/salary-bands.md (1 chunks, active)
Scanned 1, ingested 1, unchanged 0, chunks embedded 1, orphans removed 0, unreadable 0, skipped 0.
Run 039cbb85-b7e8-4f54-8b64-e08b2eb4c9cc recorded.
```

The first line of each is the folder's rule: who may read what is under that prefix. The 2024 travel policy is ingested as `superseded`, which matters in step 8.

**6. Ask it something.**

```
dotnet run --project src/Premagentic.Cli -- search "how long do I have to file an expense claim" --as group:engineering
```

```
2 hits in 231 ms (access=cli, historical=False):

[0.0246] open/handbook.md § Employee handbook > Expense claims  (lex=~1, vec=1, dist=0.262, active)
   trust=unverified authorship=unknown stale=no concept=-
   Submit an expense claim within thirty days of the trip. Claims older than thirty days need a director's written approval before finance will process them.

[0.0161] open/handbook.md § Employee handbook > Working hours  (lex=-, vec=2, dist=0.832, active)
   trust=unverified authorship=unknown stale=no concept=-
   Core hours are 10:00 to 16:00 local time. Outside core hours, work when you work best.
```

Each hit is a citation: the document path, the heading path after `§`, and the passage itself, with its trust tier, authorship and stale flag. The answer is the first hit.

**7. Prove the gate.**

```
dotnet run --project src/Premagentic.Cli -- search "band four compensation review" --as group:hr
dotnet run --project src/Premagentic.Cli -- search "band four compensation review" --as group:engineering
```

As HR, the salary bands come first:

```
3 hits in 200 ms (access=cli, historical=False):

[0.0328] hr/salary-bands.md § Salary bands > Band four  (lex=1, vec=1, dist=0.346, active)
   trust=unverified authorship=unknown stale=no concept=-
   Band four engineers sit between the senior and staff levels. Compensation review happens in the first quarter.
```

As engineering, the same question gets two hits, both from `open/handbook.md`, and no line from `hr/salary-bands.md` at all. Nothing announces the denial: the document is simply not in the list, because the access gate ran before ranking and the passage was never a candidate. The absence is the proof.

**8. Run the golden set.**

```
dotnet run --project src/Premagentic.Cli -- eval samples/profiles/starter/golden-set.json eval/report.md
```

```
5/5 passed.
Report written to eval/report.md
```

The report is git-ignored, like the model. The golden set is the starter profile's own file, so the questions the quick start runs are the ones the profile applies.

From step 1 to that line: 51 s on Windows with the packages and the image already cached; 136 s on Ubuntu from the model download to the report with an empty package cache (the clone itself, and the 177 s of `apt-get` for git, curl and the SDK, are not in that number). After both runs `git status` printed nothing: the model, the build output and the report are all ignored.

## What you just proved

- **A cited answer.** Step 6 returned `open/handbook.md § Employee handbook > Expense claims` and the sentence that answers the question. Nothing was generated; the passage is the document's own text.
- **The gate holds, both ways.** Step 7 served `hr/salary-bands.md § Salary bands > Band four` to HR and withheld it from engineering. The golden set's report makes the same pair a test:

  ```
  ## q03, PASS (access-denied)
  **Q:** What is the compensation review cycle for band four engineers?
  - access: eval:q03, latency: 29 ms, historical=False
    0.0164  open/handbook.md § Employee handbook > Expense claims  (lex=-, vec=1, dist=0.784)
    0.0161  open/handbook.md § Employee handbook > Working hours  (lex=-, vec=2, dist=0.855)

  ## q04, PASS (access-allowed)
  **Q:** What is the compensation review cycle for band four engineers?
  - access: eval:q04, latency: 16 ms, historical=False
    0.0246  hr/salary-bands.md § Salary bands > Band four  (lex=~1, vec=1, dist=0.338)
  ```

  q04 is the control for q03: without it, a build that returned nothing to anybody would pass q03 too.
- **The lifecycle gate.** q01 forbids `open/travel-policy-old.md` and passed; q02 asks for it with `archiveAllowed` and got it. Superseded material is gated, not hidden.
- **The golden set's line**, from the top of the report:

  ```
  **Result: 5/5 passed. Mean latency 50 ms.**
  ```

## From here

- **Install it on a PostgreSQL you already run.** `prem setup` makes the roles, the credentials files and an HTTPS certificate, and proves the install with a search: see [Installing](installing.md).
- **Connect an assistant.** Register an agent, issue it a token, and point an MCP client at the API, over HTTP or through the stdio bridge: see [Connect an assistant](connect-an-assistant.md).
- **Open the portal.** The API serves the administration portal, where the same searches, the review queue, the sources, tuning, health and the audit trail are pages: see [Administration](administration.md) and [Running it](running.md).
