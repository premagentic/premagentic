-- The sources registry and the record of every ingest run. Both are config:
-- they say what the deployment reads and what each run found, and neither can
-- be rebuilt from the files. Neither references prem_index; a source's documents
-- are the ones under its path prefix.
--
-- source
--   folder                 The folder read, as an absolute path.
--   path_prefix            Where its documents sit in the index. Every run
--                          reconciles under it, so two live sources never
--                          overlap; the registry refuses it.
--   okf_bundle             Read as an Open Knowledge Format bundle.
--   undeclared_is_machine  In bundle mode, a concept that does not say who
--                          wrote it is stored as machine-written and
--                          unverified. Off by default.
--   deleted_at             Removing a source keeps its row, so its runs keep
--                          their source. Its documents are not touched.
--
-- ingest_run
--   source_id              NULL for a run of a folder given on the command
--                          line, which registers nothing.
--   folder .. undeclared_is_machine
--                          What the run read with, copied at the start, so a
--                          later change to the source does not rewrite
--                          history.
--   outcome                running, completed, reconciliation_refused or
--                          failed. A run whose process died stays running
--                          with no finished_at.
--   The counts             As in the run's summary; NULL until it finishes, and
--                          on a failed run.
--   skipped_by_extension   Files seen and not read because this version does
--                          not read their format: {".pdf": 3}.
--   unreadable_paths       [{"path", "reason"}] for paths that exist and could
--                          not be read.
--   conformance_issues     [{"path", "problem", "detail"}] from a bundle run.

-- schema: prem_config

CREATE TABLE prem_config.source(
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id UUID NOT NULL REFERENCES prem_config.tenant(id),
    name TEXT NOT NULL,
    folder TEXT NOT NULL,
    path_prefix TEXT NOT NULL DEFAULT '',
    okf_bundle BOOLEAN NOT NULL DEFAULT false,
    undeclared_is_machine BOOLEAN NOT NULL DEFAULT false,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    deleted_at TIMESTAMPTZ,
    UNIQUE(tenant_id, id),
    CONSTRAINT source_name_shape CHECK (name ~ '^[A-Za-z0-9][A-Za-z0-9_.-]{0,63}$'),
    CONSTRAINT source_prefix_shape CHECK (path_prefix !~ '(^/|/$|\\)')
);
CREATE UNIQUE INDEX source_live_name_idx
    ON prem_config.source(tenant_id, lower(name)) WHERE deleted_at IS NULL;

CREATE TABLE prem_config.ingest_run(
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id UUID NOT NULL REFERENCES prem_config.tenant(id),
    source_id UUID,
    connector TEXT NOT NULL,
    folder TEXT NOT NULL,
    path_prefix TEXT NOT NULL,
    okf_bundle BOOLEAN NOT NULL,
    undeclared_is_machine BOOLEAN NOT NULL,
    started_at TIMESTAMPTZ NOT NULL DEFAULT clock_timestamp(),
    finished_at TIMESTAMPTZ,
    outcome TEXT NOT NULL DEFAULT 'running',
    error TEXT,
    scanned INT,
    ingested INT,
    unchanged INT,
    chunks_embedded INT,
    orphans_removed INT,
    denied_to_everyone INT,
    unreadable INT,
    skipped INT,
    undeclared_authorship INT,
    skipped_by_extension JSONB,
    unreadable_paths JSONB,
    okf_version TEXT,
    okf_version_known BOOLEAN,
    conformance_issues JSONB,
    FOREIGN KEY (tenant_id, source_id) REFERENCES prem_config.source(tenant_id, id),
    CONSTRAINT ingest_run_outcome_known
        CHECK (outcome IN ('running', 'completed', 'reconciliation_refused', 'failed')),
    CONSTRAINT ingest_run_finished_when_done
        CHECK ((outcome = 'running') = (finished_at IS NULL))
);
CREATE INDEX ingest_run_source_idx ON prem_config.ingest_run(tenant_id, source_id, started_at);
