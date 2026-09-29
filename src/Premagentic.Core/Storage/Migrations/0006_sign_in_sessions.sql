-- Sign-in sessions for people.
--
-- A session id is 32 random bytes that only the browser holds, in a cookie.
-- Only its SHA-256 is stored, so a copy of this table cannot be replayed as a
-- sign-in. A session ends at whichever comes first: its absolute expiry, a
-- stretch with no request longer than the idle timeout, sign-out, or its user
-- being disabled or given a new password, which delete the row outright so a
-- later enable does not bring it back.

-- schema: prem_config

CREATE TABLE prem_config.user_session(
    id_sha256 BYTEA PRIMARY KEY,
    tenant_id UUID NOT NULL,
    user_id UUID NOT NULL,
    created_at TIMESTAMPTZ NOT NULL,
    last_seen_at TIMESTAMPTZ NOT NULL,
    expires_at TIMESTAMPTZ NOT NULL,
    FOREIGN KEY(tenant_id, user_id) REFERENCES prem_config.app_user(tenant_id, id),
    CONSTRAINT user_session_hash_length CHECK (octet_length(id_sha256) = 32),
    CONSTRAINT user_session_expires_after_created CHECK (expires_at > created_at)
);
CREATE INDEX user_session_user_idx ON prem_config.user_session(tenant_id, user_id);
CREATE INDEX user_session_expires_idx ON prem_config.user_session(expires_at);
