-- A client's metadata document, stored by hand.
--
-- An assistant that names itself by an https address (a client ID metadata
-- document) is registered by an administrator from a copy of that document,
-- with 'prem oauth clients add --metadata-file <file> --id <address>'.
-- Nothing is ever fetched from the address. The fields the flow uses go into
-- the row's own columns, as for any administrator's registration. The
-- document itself is not kept, so no address a browser might load is stored.
--
-- document_sha256: the SHA-256 of the file's bytes in lowercase hex, so the
-- stored copy can be compared with the one the vendor publishes.
-- document_stored_at: when it was stored. The two are set together, only for
-- an https id, and are NULL for every other client.

-- schema: prem_config

ALTER TABLE prem_config.oauth_client
    ADD COLUMN document_sha256 TEXT,
    ADD COLUMN document_stored_at TIMESTAMPTZ,
    ADD CONSTRAINT oauth_client_document_together
        CHECK ((document_sha256 IS NULL) = (document_stored_at IS NULL)),
    ADD CONSTRAINT oauth_client_document_sha256
        CHECK (document_sha256 IS NULL OR (document_sha256 ~ '^[0-9a-f]{64}$' AND id ~ '^https://'));
