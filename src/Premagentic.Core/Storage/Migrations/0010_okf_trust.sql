-- Open Knowledge Format values per document, for the trust gate and the
-- freshness gate. Derived from the file at ingest, so like everything in
-- prem_index they are rebuilt by re-ingest.
--
-- The defaults describe an ordinary file with no OKF frontmatter: no concept
-- id, unverified, unknown authorship, never stale. Neither gate ever holds
-- such a file back.
--
--   trust_tier         0 unverified, 1 machine-confirmed, 2 human-reviewed.
--                      The order is the trust order, so a minimum is ">=".
--   authorship         0 unknown, 1 human, 2 machine. The trust gate acts
--                      only on machine.
--   stale_after        The instant the content goes stale, or NULL for never.
--                      '-infinity' when the file gave one that could not be
--                      read, so a date nobody can read is stale at every
--                      instant and staleness stays one comparison.
--   frontmatter_state  0 no block, 1 read, 2 present but unreadable. Outside
--                      an OKF bundle a file whose frontmatter could not be
--                      read carries no metadata, so it stores 0, the same as
--                      a file with no block.

-- schema: prem_index

ALTER TABLE prem_index.document
    ADD COLUMN okf_concept_id TEXT,
    ADD COLUMN trust_tier SMALLINT NOT NULL DEFAULT 0,
    ADD COLUMN authorship SMALLINT NOT NULL DEFAULT 0,
    ADD COLUMN stale_after TIMESTAMPTZ,
    ADD COLUMN generated_at TIMESTAMPTZ,
    ADD COLUMN last_verified_at TIMESTAMPTZ,
    ADD COLUMN frontmatter_state SMALLINT NOT NULL DEFAULT 0,
    ADD CONSTRAINT document_trust_tier_known CHECK (trust_tier BETWEEN 0 AND 2),
    ADD CONSTRAINT document_authorship_known CHECK (authorship BETWEEN 0 AND 2),
    ADD CONSTRAINT document_frontmatter_state_known CHECK (frontmatter_state BETWEEN 0 AND 2);
