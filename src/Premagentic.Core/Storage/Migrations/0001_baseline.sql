-- The baseline. Plain PostgreSQL: no extension is created or required.
--
-- Two schemas with different promises:
--   prem_config holds what cannot be rebuilt from the customer's files: the
--             tenant, the audit trail, and later users, agents and rules.
--             It is backed up.
--   prem_index  holds what can: documents, chunks and their vectors. Dropping it
--             and re-ingesting is a supported rebuild, so nothing in prem_config
--             may reference it.
--
-- Each schema keeps its own schema_migration table, so dropping prem_index also
-- forgets which index migrations ran, and the next start rebuilds it.

-- schema: prem_config

CREATE TABLE prem_config.tenant(
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    key TEXT UNIQUE NOT NULL,
    name TEXT NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

-- One row per question. access_label records WHICH authorization the answer
-- was served under, so an audit can separate a normal read from an operator
-- read that bypassed the gate. passages holds, per returned passage, the
-- document path, heading path and content hash: enough to find the exact text
-- that was served even after a re-ingest has replaced every chunk id.
CREATE TABLE prem_config.retrieval_event(
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id UUID NOT NULL REFERENCES prem_config.tenant(id),
    query TEXT NOT NULL,
    access_label TEXT NOT NULL DEFAULT 'unknown',
    caller_user_id UUID,
    caller_agent_id UUID,
    include_historical BOOLEAN NOT NULL,
    passages JSONB NOT NULL DEFAULT '[]',
    elapsed_ms BIGINT NOT NULL,
    lexical_count INT,
    best_distance DOUBLE PRECISION,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT retrieval_event_passages_is_array CHECK (jsonb_typeof(passages) = 'array')
);
CREATE INDEX retrieval_event_tenant_created_idx
    ON prem_config.retrieval_event(tenant_id, created_at);

-- schema: prem_index

-- is_public and allowed_principals are the authorization gate.
-- The defaults deny: a row written without them reaches nobody.
CREATE TABLE prem_index.document(
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id UUID NOT NULL REFERENCES prem_config.tenant(id),
    path TEXT NOT NULL,
    title TEXT,
    doc_class TEXT,
    lifecycle_status TEXT NOT NULL DEFAULT 'active',
    is_public BOOLEAN NOT NULL DEFAULT false,
    allowed_principals TEXT[] NOT NULL DEFAULT '{}',
    source_name TEXT,
    source_modified_at TIMESTAMPTZ,
    content_hash TEXT NOT NULL,
    updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    UNIQUE(tenant_id, path)
);
CREATE INDEX document_principals_idx
    ON prem_index.document USING GIN(allowed_principals);

-- embedding is float32, little-endian, L2-normalized at ingest: 4 bytes per
-- dimension. embedding_model and embedding_dims are recorded per chunk so a
-- mixed vector space is detectable rather than silently fused.
--
-- A chunk row is never updated. A changed document has its chunks deleted and
-- inserted again with new ids, so an id's vector never changes, which is what
-- lets the search process hold vectors in memory keyed by chunk id.
CREATE TABLE prem_index.chunk(
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    document_id UUID NOT NULL REFERENCES prem_index.document(id) ON DELETE CASCADE,
    seq INT NOT NULL,
    heading_path TEXT NOT NULL DEFAULT '',
    doc_title TEXT NOT NULL DEFAULT '',
    content TEXT NOT NULL,
    tsv tsvector GENERATED ALWAYS AS (
        setweight(to_tsvector('english', doc_title), 'A') ||
        setweight(to_tsvector('english', heading_path), 'B') ||
        setweight(to_tsvector('english', content), 'C')
    ) STORED,
    embedding BYTEA NOT NULL,
    embedding_model TEXT NOT NULL,
    embedding_dims INT NOT NULL,
    embedded_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT chunk_embedding_dims_positive CHECK (embedding_dims > 0),
    CONSTRAINT chunk_embedding_length CHECK (octet_length(embedding) = embedding_dims * 4)
);
CREATE INDEX chunk_document_idx ON prem_index.chunk(document_id);
CREATE INDEX chunk_tsv_idx ON prem_index.chunk USING GIN(tsv);

-- Serves the vector leg's per-query read of permitted chunk ids for one
-- embedding model without visiting the wide chunk rows.
CREATE INDEX chunk_model_document_idx
    ON prem_index.chunk(embedding_model, document_id, id);
