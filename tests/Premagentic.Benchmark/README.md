# Premagentic.Benchmark

Times the search path and the ingest path on a synthetic index, so anyone can
take its numbers on their own machine. It is a console program, not a test
project: `dotnet test` never runs it, and no push does; a manual workflow
runs it on GitHub's hosted Linux runner, whose numbers describe that runner. It is in the
solution only so that it keeps building.

**Two modes, and which one a figure is.** By default the benchmark connects
as the database user you give it (the container's superuser below, whom
PostgreSQL exempts from row-level security) and reads with no search role and
no caller session, so its figures are the SQL gate alone, without the
database's second line. Those are not an installed deployment's speed. With
`--search-role`, `measure` and `spin` read as an installed deployment does: as
the search role `prem setup` made, opening a caller session for every search,
with the row policies judging every row read. Every table's title says which
mode it ran in. `ingest` writes as whichever role it connects as, and its
title names that role: the application role from the credentials file
`prem setup` wrote is how a deployment ingests, and a superuser is not.

**Every number it prints depends on the machine.** The processor, its cores,
the memory, the disk, and whatever else is running on the same cores all move
them. Each table ends with a line naming the machine, the operating system,
the .NET runtime and the commit, and a figure is worth quoting only with that
line under it. Keep the machine otherwise idle while it runs.

## What it needs

- The .NET 10 SDK, and Docker for a throwaway PostgreSQL.
- The local embedding model, downloaded as for any installation
  (`scripts/download-model.sh` or `scripts/download-model.ps1`). The
  benchmark embeds its questions with it, as a search does.
- A database nobody uses. `synth` refuses a database whose index holds any
  document, and `measure` and `spin` refuse one holding a document `synth`
  did not make. Never point it at a deployment.

Nothing it loads comes from a real document. Every chunk is twelve invented
words and a random vector, and the questions come from the same words: 4,096
of them, each three syllables, so a question's word is in about one chunk in
three hundred, as a real question's words tend to be. The generator seeds from
a fixed number, so two loads of the same size hold the same chunks.

## Running it

Start a throwaway PostgreSQL on loopback, on a network of its own, with a
password nothing else uses:

```bash
docker network create prem-bench
docker run -d --name prem-bench --network prem-bench -p 127.0.0.1:5442:5432 \
  -e POSTGRES_USER=prem -e POSTGRES_PASSWORD=<a password> -e POSTGRES_DB=bench postgres:17
export PREM_CONNECTION_STRING="Host=127.0.0.1;Port=5442;Database=bench;Username=prem;Password=<the password>;Command Timeout=0"
export PREM_EMBEDDING_PROVIDER=local
```

Then, from the repository root:

```bash
dotnet run -c Release --project tests/Premagentic.Benchmark -- synth --chunks 25000
dotnet run -c Release --project tests/Premagentic.Benchmark -- measure
dotnet run -c Release --project tests/Premagentic.Benchmark -- spin --off
dotnet run -c Release --project tests/Premagentic.Benchmark -- spin --on
```

To measure as a deployment reads, prepare the same kind of database with the
product's own setup before `synth`, from a Release build of the CLI. Setup
takes the administrator's connection from a file, makes the owner, application
and search roles and the row policies, and writes the application's and the
search role's credentials into a folder:

```bash
dotnet build src/Premagentic.Cli -c Release
( umask 077; printf 'connection=%s\n' "$PREM_CONNECTION_STRING" > admin.credentials )
src/Premagentic.Cli/bin/Release/net10.0/prem setup --admin-connection-file admin.credentials --credentials-dir credentials
# setup made its own database, premagentic, owned by its owner role; synth writes there
export PREM_CONNECTION_STRING="${PREM_CONNECTION_STRING/Database=bench/Database=premagentic}"
dotnet run -c Release --project tests/Premagentic.Benchmark -- synth --chunks 25000
unset PREM_CONNECTION_STRING
export PREM_CREDENTIALS_FILE=$PWD/credentials/app.credentials
dotnet run -c Release --project tests/Premagentic.Benchmark -- ingest
dotnet run -c Release --project tests/Premagentic.Benchmark -- measure --search-role
dotnet run -c Release --project tests/Premagentic.Benchmark -- spin --off --search-role
dotnet run -c Release --project tests/Premagentic.Benchmark -- spin --on --search-role
```

`synth` still writes as the administrator; `ingest` then writes as the
application role, as a deployment's ingest does, and `measure` and `spin`
connect as the application role and read as the search role beside it. They
refuse to run if the search role is not bound by the row policies. Delete the
two credentials files with the container.

For another size, remove the container and start again: `synth` loads only an
empty index. Remove it when done:

```bash
docker rm -f -v prem-bench && docker network rm prem-bench
```

A load of 500,000 chunks takes a few minutes and a little over a gigabyte of
disk, and a search holds every vector in memory, about 1.5 KB a chunk, so
check the free memory before it.

## What it measures

`synth --chunks N [--seed S]` fills the empty index with N chunks, ten to a
document, every document readable by everyone as `prem ingest --public`
makes it, the vectors labeled with the local model's name so a real query
embedding searches them.

`measure [--search-role] [--rounds R] [--seconds T]` warms the search (every vector loaded,
every question asked five times, then a pause so the runtime finishes
compiling the hot paths), then times, over R rounds of twenty questions
(five by default):

| Row | What it is |
|---|---|
| Opening a caller session | With `--search-role` only: what every search pays before its first read, a caller session and the search role's connection bound to it. |
| Permitted read: document ids | The one gated read a search makes: every document the caller may read, with its version. |
| Permitted read: chunk ids | The read the vector leg made before it read documents: every permitted chunk id, through the same gates. Kept so the difference stays measurable. |
| Vector leg alone | The nearest 20 among the permitted chunks, its own permitted read included. |
| Query embedding alone | One question through the local model. |
| Whole search | A public-only search, as an agent's search runs, for the top 5 of a pool of 20. |
| Five searchers at once | Five searches in parallel for T seconds (20 by default): each search's time, and the searches a second. |

`spin --on|--off [--search-role] [--rounds R]` warms the same way, then times the whole search
and the query embedding with ONNX Runtime's spin-waiting on or off. The product builds its
inference session with spinning off and has no setting for it; `--on`
rebuilds the benchmark's own session with spinning allowed, which is how
ONNX Runtime runs unless told otherwise, and changes nothing else.

`ingest [--documents D] [--rounds R] [--spin off|on|both]` times the ingest
path a deployment runs, into the index `synth` filled, so the index is the
size under test. It writes D Markdown files (200 by default) of ten sections
each, the same invented words under each heading, into a temporary folder,
sets a folder rule letting everyone read them, and ingests the folder under
the prefix `synthetic/ingest` as `prem ingest` does: through the file system
connector and the pipeline's own steps (the read with its 256 MB cap, the
check that the text can be stored, the Markdown chunker, the local model's
embedding with the heading prefix unless `PREM_HEADING_PREFIX` is 0, and the
insert), recorded as an ingest run. A round is one ingest of the whole
folder, timed; after it the pipeline removes the batch again, untimed, by
ingesting an empty folder under the same prefix, so every round writes into
the same index and `measure` still finds only what `synth` made. One untimed
round comes first, so the model's session and the runtime's compiled code are
ready. A round that did not read every file into ten chunks stops the run.
With `--spin both` (the default) it runs with spinning off, then on; the
product builds its session with spinning off. It needs the local model, as
`spin` does.

| Row | What it is |
|---|---|
| Ingest, spinning off or on: a document of ten chunks | A round's time divided by the documents it read, over R rounds; the note gives the documents a second over every round. |
| Ingest, spinning off or on: one chunk | The same time divided by the chunks it embedded; the note gives the chunks a second. |

The size of the index is in the title, so a figure says what it was
measured against.

Each prints a Markdown table of medians and 90th percentiles that can be
pasted as it is, with the machine line under it.
