-- The second line: row-level security on the index, bound to the access lists
-- one caller may read.
--
-- The SQL gate in front of every read already filters on the permitted list
-- ids. This makes the database refuse the same rows itself, so a read path
-- that forgets the gate still cannot return a row the caller may not read.
--
-- How a read is bound. For each search, code running as a role that may write
-- the index computes the caller's permitted list ids, exactly as the gate
-- does, and opens a caller session holding them (open_caller_session). The
-- read then runs on one connection, in one transaction, as a role that may
-- only read, and binds the session by its id (bind_caller_session). The
-- connection holds only that id, which is 32 random bytes the reader cannot
-- guess; the ids themselves never live on the connection, so nothing on it can
-- set them by hand. The policies look the ids up through caller_acl_sets.
--
-- Who the policies bind. A role named in index_writer reads all of the index:
-- ingest runs as the application role and must read and write every document,
-- so setup names that role there. Every other role, the search role among
-- them, reads only what its bound caller session permits, and with no session
-- bound, nothing. Only the owner can change index_writer, so no grant made
-- later can loosen the policy. The owner and a superuser bypass row-level
-- security altogether; only migrate and rebuild use the owner.

-- schema: prem_config

-- One row per bound read, holding what the caller may read at that moment.
-- Rows are opened and closed by the functions below and live minutes at most.
-- Row-level security is on with no policy, so nobody but the owner, through
-- those functions, reads or writes the table. Unlogged, because a row outlives
-- nothing: a crash that empties the table costs only the reads in flight, and
-- no read waits for the write-ahead log twice.
CREATE UNLOGGED TABLE prem_config.caller_session(
    id_sha256 BYTEA PRIMARY KEY,
    tenant_id UUID NOT NULL REFERENCES prem_config.tenant(id),
    user_id UUID,
    agent_id UUID,
    acl_set_ids BIGINT[] NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    expires_at TIMESTAMPTZ NOT NULL,
    CONSTRAINT caller_session_hash_length CHECK (octet_length(id_sha256) = 32),
    CONSTRAINT caller_session_expires_after_created CHECK (expires_at > created_at)
);
CREATE INDEX caller_session_expires_idx ON prem_config.caller_session(expires_at);
ALTER TABLE prem_config.caller_session ENABLE ROW LEVEL SECURITY;

-- The roles that read the whole index because they write it: the application
-- role, named here by setup. Row-level security is on with no policy, so only
-- the owner reads or changes the list.
CREATE TABLE prem_config.index_writer(
    role_name NAME PRIMARY KEY
);
ALTER TABLE prem_config.index_writer ENABLE ROW LEVEL SECURITY;

-- Whether a role reads the whole index. The policies pass their own
-- current_user, so a role cannot ask on another's behalf and gain anything.
CREATE FUNCTION prem_config.reads_whole_index(p_role NAME)
RETURNS BOOLEAN
LANGUAGE sql
STABLE
SECURITY DEFINER
SET search_path = pg_catalog, pg_temp
AS $$
    SELECT EXISTS (SELECT 1 FROM prem_config.index_writer w WHERE w.role_name = p_role)
$$;

-- Opens a caller session: the id as 64 lower-case hex characters (only its
-- SHA-256 is stored), whose caller it is, the list ids that caller may read,
-- and how long it lives, at most ten minutes. Expired rows are swept on the
-- way. Executable only by the roles setup grants it to: the application role.
CREATE FUNCTION prem_config.open_caller_session(
    p_session TEXT, p_tenant UUID, p_user UUID, p_agent UUID, p_acl_set_ids BIGINT[], p_lifetime INTERVAL)
RETURNS void
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, pg_temp
AS $$
BEGIN
    IF p_session IS NULL OR p_session !~ '^[0-9a-f]{64}$' THEN
        RAISE EXCEPTION 'A caller session id is 64 lower-case hexadecimal characters.';
    END IF;
    IF p_lifetime IS NULL OR p_lifetime <= interval '0' OR p_lifetime > interval '10 minutes' THEN
        RAISE EXCEPTION 'A caller session lives more than no time and at most ten minutes.';
    END IF;
    DELETE FROM prem_config.caller_session WHERE expires_at <= now();
    INSERT INTO prem_config.caller_session(id_sha256, tenant_id, user_id, agent_id, acl_set_ids, created_at, expires_at)
    VALUES (sha256(decode(p_session, 'hex')), p_tenant, p_user, p_agent, COALESCE(p_acl_set_ids, '{}'),
            now(), now() + p_lifetime);
END
$$;
REVOKE ALL ON FUNCTION prem_config.open_caller_session(TEXT, UUID, UUID, UUID, BIGINT[], INTERVAL) FROM PUBLIC;

-- Closes a caller session when its read is done. Executable only by the roles
-- setup grants it to: the application role.
CREATE FUNCTION prem_config.close_caller_session(p_session TEXT)
RETURNS void
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, pg_temp
AS $$
BEGIN
    IF p_session ~ '^[0-9a-f]{64}$' THEN
        DELETE FROM prem_config.caller_session WHERE id_sha256 = sha256(decode(p_session, 'hex'));
    END IF;
END
$$;
REVOKE ALL ON FUNCTION prem_config.close_caller_session(TEXT) FROM PUBLIC;

-- The list ids the caller session bound to this transaction may read: empty
-- when none is bound, when the bound id is malformed or unknown, or when the
-- session has expired.
CREATE FUNCTION prem_config.caller_acl_sets()
RETURNS BIGINT[]
LANGUAGE plpgsql
STABLE
SECURITY DEFINER
SET search_path = pg_catalog, pg_temp
AS $$
DECLARE
    bound TEXT := current_setting('premagentic.caller_session', true);
    ids BIGINT[];
BEGIN
    IF bound IS NULL OR bound !~ '^[0-9a-f]{64}$' THEN
        RETURN '{}';
    END IF;
    SELECT s.acl_set_ids INTO ids
    FROM prem_config.caller_session s
    WHERE s.id_sha256 = sha256(decode(bound, 'hex')) AND s.expires_at > now();
    RETURN COALESCE(ids, '{}');
END
$$;

-- Binds a caller session to the current transaction, and ends with it. Refuses
-- a session that is not open, so a read with a wrong id fails loudly rather
-- than quietly finding nothing. Setting the same value by hand binds nothing
-- more: the ids are always looked up again by caller_acl_sets.
CREATE FUNCTION prem_config.bind_caller_session(p_session TEXT)
RETURNS void
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, pg_temp
AS $$
BEGIN
    IF p_session IS NULL OR p_session !~ '^[0-9a-f]{64}$' OR NOT EXISTS (
        SELECT 1 FROM prem_config.caller_session s
        WHERE s.id_sha256 = sha256(decode(p_session, 'hex')) AND s.expires_at > now()) THEN
        RAISE EXCEPTION 'There is no open caller session with that id.';
    END IF;
    PERFORM set_config('premagentic.caller_session', p_session, true);
END
$$;

-- schema: prem_index

-- A role named in index_writer reads all of the index; any other role reads
-- only documents under the lists its bound caller session permits. The two
-- scalar subqueries run once per statement, not once per row; the cast makes
-- the second an array to compare with, not a subquery to compare against.
ALTER TABLE prem_index.document ENABLE ROW LEVEL SECURITY;
CREATE POLICY document_caller_read ON prem_index.document
    USING ((SELECT prem_config.reads_whole_index(current_user))
           OR acl_set_id = ANY ((SELECT prem_config.caller_acl_sets())::BIGINT[]));

-- A chunk is readable exactly when its document is: the subquery on the
-- document table is itself under the policy above. Correlated, so a read of a
-- few chunks probes the document index for each rather than first collecting
-- every document the caller may read.
ALTER TABLE prem_index.chunk ENABLE ROW LEVEL SECURITY;
CREATE POLICY chunk_caller_read ON prem_index.chunk
    USING ((SELECT prem_config.reads_whole_index(current_user))
           OR EXISTS (SELECT 1 FROM prem_index.document d WHERE d.id = document_id));

-- The top chunks of one tenant that match a text query, ranked, under the
-- gates, among the documents the bound caller session permits, or among all
-- of them for a role the policies do not bind (a superuser, a role with
-- BYPASSRLS, or one named in index_writer). The session's lists apply
-- whatever the caller passes; the g_ arguments are the gates' parameters.
--
-- Why it exists: under any row policy on a table, PostgreSQL will not use an
-- index for a condition whose operator is not leakproof, and the full-text
-- match is not, so every text search would read every chunk. This function
-- runs as the owner, where the index is used.
--
-- Only the signature is written here. The body is generated from the gates
-- (TextMatchFunction in Premagentic.Core) and installed by migrate as the owner
-- at the end of every run; until then it refuses. Adding a gate, or a
-- parameter to one, means a new migration that drops this function and
-- creates it with the new arguments. Executable only by the roles setup grants
-- it to: the application role and the search role.
CREATE FUNCTION prem_index.text_matches(
    p_tenant UUID,
    p_query TSQUERY,
    g_unrestricted BOOLEAN,
    g_permitted BIGINT[],
    g_historical BOOLEAN,
    g_trust_min_tier SMALLINT,
    g_freshness_include_stale BOOLEAN,
    g_freshness_now TIMESTAMPTZ,
    p_exclude UUID[],
    p_limit INT)
RETURNS TABLE(chunk_id UUID, document_id UUID, seq INT, rank REAL, path TEXT)
LANGUAGE plpgsql
STABLE
SECURITY DEFINER
SET search_path = pg_catalog, pg_temp
AS $$
BEGIN
    RAISE EXCEPTION 'prem_index.text_matches is not installed yet. Run prem migrate as the owner.';
END
$$;
REVOKE ALL ON FUNCTION prem_index.text_matches FROM PUBLIC;
