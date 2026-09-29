# Mutants: breaking the guards on purpose

A test that cannot fail proves nothing. Every guard in PremAgentic that
decides what a caller may get, or that refuses something, is checked by
breaking it: a one-place change to the source (a mutant) is applied, the
tests that can reach it are run, and at least one of them must fail. Then the
file is put back.

The lists in this folder hold those mutants, and `run.py` runs them. It needs
Python 3.8 or later and nothing outside Python's standard library, plus the
.NET SDK the repository builds with and Docker for the tests that start their
own PostgreSQL.

The historical counts in [`docs/status.md`](../../docs/status.md) ("checked by
breaking them") were hand runs made before this runner was in
the repository; these lists were converted from them, and a list's own count
can differ from the one the page gives. From here on a list is counted by running it here.

## Running

Check every list against today's tree, read-only. This builds nothing and
changes nothing; it is what CI runs on every push:

```sh
python tests/mutants/run.py --check tests/mutants/*.json
```

Run one list:

```sh
python tests/mutants/run.py tests/mutants/<list>.json
```

Run some of its mutants again, by name:

```sh
python tests/mutants/run.py tests/mutants/<list>.json --only "<mutant name>" "<another>"
```

Check the runner's own rules on canned output, in seconds:

```sh
python tests/mutants/run.py --self-test
```

A run writes a folder under `tests/mutants/runs/` (ignored by git) holding
`run.log` and the output of every build and test run. Each mutant is a clean
build of the test project and a filtered test run, so a list of forty takes
about as many minutes. Run one list at a time on a machine, and not beside
another heavy build or test run.

## What a run does, and what it refuses

1. **It refuses to start while any `*.mutant-backup` file exists** anywhere in
   the tree. Before a file is mutated, its original bytes are written beside
   it as `<file>.mutant-backup`, and that sidecar is deleted only after the
   original is written back and checked byte for byte. A run that is killed
   never reaches its restore, so a sidecar left on disk means the tree may
   still hold a mutant, and a control run on it would pass on mutated code.
   `python tests/mutants/run.py --restore` writes each sidecar back over its
   file and deletes it; then check `git status`.
2. **It checks every anchor before it touches anything.** Each `old` must
   occur exactly once in its file. A list that does not anchor on today's
   tree is refused whole.
3. **The unmutated control runs first and must pass**: a clean build, then
   every test any mutant's filter reaches, with at least one test run and none
   failed. If it fails, nothing is mutated.
4. **Each mutant runs alone.** The runner writes the sidecar, makes the
   change, runs `dotnet build -c Release --no-incremental` on the test
   project, then `dotnet test -c Release --no-build --filter <the mutant's
   filter>`, and puts the file back.
   - `--no-incremental` because a file put back from a copy can carry an old
     timestamp, and an incremental build would skip it and keep testing the
     mutant. It is a build flag; `dotnet test` refuses it.
   - Exit codes are the child process's own, never a shell pipeline's (in
     `cmd | tail` the status is `tail`'s), and output is read only after its
     stream has ended.
   - Each build and each test run has a time limit (`--timeout-minutes`,
     default 30). A run past it is ended with its process tree, and its
     output is waited for 10 s more at most: a process outside the tree, such
     as a grandchild whose parent had already exited, can hold the output
     open, and the runner does not wait for it. The run has no verdict.
5. **The verdict:**

   | Verdict | When |
   |---|---|
   | CAUGHT | the build succeeded AND a named test failed; or, for a mutant marked `"expect": "crash"`, the test host died |
   | SURVIVED | the build succeeded, tests ran, and every one passed |
   | NO VERDICT | anything else, with the reason: the mutant did not compile, the filter reached no test (`dotnet test` exits 0 then), the run printed no summary, a failure no test was named for, a crash the mutant does not expect, or the time limit |
   | SKIPPED | the mutant is for the other platform; it is named in the log and never counted caught |

   A failed test is named from its display name cut at the first `(`, since a
   theory's arguments may hold dots and any text.
6. **Free memory is checked before the control and before each mutant.**
   Under `--min-free-gb` (default 4; 0 turns it off), or where it cannot be
   read, the run stops with every file already put back, and says which
   mutants did not run.
7. **The control runs again after the last mutant** and must pass.
8. **The counts are read back from `run.log`**, not kept in the runner's
   memory, and every mutant with no verdict line is reported as not run.

Exit codes:

| Code | Meaning |
|---|---|
| 0 | every mutant that ran was caught, and both controls passed |
| 1 | a mutant survived or had no verdict, one did not run, or the control after failed |
| 2 | the list is malformed or does not anchor; nothing was changed |
| 3 | a sidecar is in the tree; nothing was changed |
| 4 | stopped for low memory |
| 5 | no mutant in the list is for this platform |
| 6 | the control before failed; nothing was mutated |

## Writing a list

A list is one JSON file in this folder:

```json
{
  "name": "access gate",
  "commit": "hand run, 2026-09-28",
  "mutants": [
    {
      "name": "a deny rule no longer wins over an allow rule",
      "file": "src/Premagentic.Core/Example/Gate.cs",
      "old": "if (rule.Deny)\n    return Decision.Deny;",
      "new": "if (false)\n    return Decision.Deny;",
      "filter": "FullyQualifiedName~Premagentic.Tests.AccessGateTests"
    }
  ]
}
```

At the top:

- `name`: what the list covers.
- `commit`: when the list was last run in full, as `hand run, <date>` (the
  day, in UTC). Change it when you rerun the list.
- `platform` (optional): `linux` or `windows`, for a list whose every mutant
  can only be caught there. A mutant may also carry its own `platform`.
- `project` (optional): the test project the filters run in, relative to
  the repository root. The default is `tests/Premagentic.Tests/Premagentic.Tests.csproj`.

Each mutant:

- `name`: one line, unique in the list. The log is read by name.
- `file`, `old`, `new`: the file relative to the repository root, the exact
  text to replace and what replaces it. Write line breaks as `\n`; they match
  a file with CRLF endings too.
- `filter`: a `dotnet test --filter` expression naming the tests that can
  reach the change.
- `expect` (optional): `crash`, for a guard against a stack overflow, where
  the only failure the test host can report is its own death.
- `edits` (optional, in place of `file`, `old` and `new`): two or more
  changes made together, each with its own `file`, `old` and `new`, for a
  guard that holds when either of two places holds.

Unknown keys are refused, so a list in another shape fails the check and not
a run.

The rules for a good mutant:

- **The `old` must be unique.** Make it long enough to occur once, and check
  with `--check` before running.
- **The filter must reach the code.** A filter that runs no test gives NO
  VERDICT, not a catch.
- **Every new guard gets one.** When you add a check that refuses or limits
  something, add the mutant that removes it, run it, and see the test you
  wrote for the guard fail.
- **A mutant must compile.** One that does not proves nothing and is reported
  NO VERDICT.
- **One idea per mutant.** Remove the guard, flip the comparison, widen the
  bound by one. A mutant that breaks several things at once shows only that
  something noticed.
- **Invented text only.** Fixtures and anchors carry no real document, person
  or organization.
