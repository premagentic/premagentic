-- A section fetch is recorded in the audit trail the way a search is: who
-- asked, what came back with its content hash, and the trust policy it was
-- served under. The two are told apart by kind.
--
--   kind               search, or section for a fetch of one document by its
--                      path. For a section fetch, query holds the path asked
--                      for.
--   requested_heading  For a section fetch, the heading asked for, or NULL
--                      for the whole document. NULL for a search.
--
-- Rows written before this migration are searches, which is what the default
-- says.

-- schema: prem_config

ALTER TABLE prem_config.retrieval_event
    ADD COLUMN kind TEXT NOT NULL DEFAULT 'search',
    ADD COLUMN requested_heading TEXT,
    ADD CONSTRAINT retrieval_event_kind_known CHECK (kind IN ('search', 'section'));
