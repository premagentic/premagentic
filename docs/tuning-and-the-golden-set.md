# Tuning and the golden set

The retrieval settings that are deliberately per deployment, how to change them and judge a change with the golden set, and why the golden set is the deliverable.

## Tuning is per deployment, not per product

Two things are deliberately not constants:

- `AuthorityWeights` is flat by default. Weighting one class of document above
  another is a claim about one organization's filing habits, and a table
  carried over from another deployment silently demotes real answers.
- `RetrievalTuning.NoAnswerDistanceFloor` defaults to 0.55, measured on the
  corpus the retrieval core was developed against, with MiniLM and heading
  context. It moves with the
  embedding model and with the corpus, so it must be recalibrated against a
  deployment's own golden set before "no results" is allowed to mean "there is
  no answer."

Four retrieval settings can be changed per deployment, with `prem settings
set` or on the portal's tuning page, and apply to the next search:

- `retrieval.rrf_k`, a whole number from 1 to 1000 (default 60). The
  constant in Reciprocal Rank Fusion: lower lets the passages ranked first
  by either leg dominate, higher flattens the fused order.
- `retrieval.fallback_rrf_weight`, a number from 0 to 1 (default 0.5). How
  much a text match found only when the words were searched separately
  counts beside one that matched them all. At 0 it counts only its vector
  rank.
- `retrieval.authority_weights`, an object such as `{"default": 1.0,
  "by_class": {"runbook": 1.2}}`, every weight greater than 0, class names
  compared ignoring case. A document's class is its front matter's `type`.
  `default` applies to every document with no class and every class not
  named. Flat by default, and it should stay flat until a deployment's own
  golden set shows a table helps.
- `retrieval.no_answer_distance_floor`, a number greater than 0 and less
  than 2 (default 0.55). Read by the golden-set evaluation to judge whether
  a question with no expected answer got none. It does not change what a
  search returns.

A stored value that cannot be used keeps its default and is reported once:
a warning in the API's log, a warning line from the CLI, and `prem settings
list` and the Tuning page say why. `prem settings unset <key>` or "Use the
default" on the page puts the default back; every change is in the change
record. Unlike the trust settings, where an unreadable value counts as the
strictest, a retrieval setting only changes how results are ordered, never
what a caller may see. Every number here depends on the corpus and the
embedding model: change them only against this deployment's own golden set,
never by copying another's.

**Judging a change with the golden set.** `prem settings set
evaluation.golden_set_path <absolute path>` names the golden question set on
the machine that runs PremAgentic; it is set from the command line only,
because the server reads whatever file it names. `prem profile apply` sets it
to its own copy of the profile's golden set, in `Premagentic-golden-sets` in
the shared application data folder unless `--golden-set-dir` names another
(`C:\ProgramData\Premagentic-golden-sets` on Windows, which every account
may read, the service's included). The account the server runs as has to be
able to read the file. "Run the golden set" on the
Tuning page runs it under the settings saved now and shows the result beside
the run before it: how many questions passed, how many no-answer questions
were answered correctly, the questions that failed by id, and the settings
each run used. One run at a time; a run stops after two minutes. Each run is
recorded in the change record and nowhere else. `prem eval <file>` runs the
same set from the command line, under the same stored settings, and writes a
Markdown report. A case's `as` principals resolve by name when the run
happens, so a renamed group fails its case until the set is updated.

## The golden set is the deliverable

`prem eval` runs a JSON set of questions against the index, writes a report
and exits non-zero on any failure, so no change reaches a customer without
passing. The set is written per deployment against that corpus, because a set
written for someone else's documents proves nothing about these ones.

A case can also assert authorization: give it `as` principals and list what it
must not reach in `forbiddenSourcePaths`. That turns "the HR folder does not
leak" into a test that fails loudly rather than a sentence in a proposal.

Reports contain the questions, and the path and heading of every result, but not
passage text. A heading such as "Acquisition of a named client" or a question
about a specific matter is still sensitive, so reports are customer content and
are gitignored.
