-- How many principals a run met that mean nothing in this deployment.
--
-- A connector can name the principals a source system's permissions carry, and
-- they become groups here only through prem_config.identity_mapping. One that
-- nothing is mapped from reaches nobody, which is the safe reading and an
-- invisible one: a share whose groups were never mapped indexes cleanly and
-- then answers nothing at all, which looks exactly like a folder with nothing
-- in it. The count is what tells those two apart afterwards, on a run that
-- nobody watched.
--
-- Counted once per distinct principal per run, not once per document.
--
-- NULL on runs recorded before this migration, which counted nothing of the
-- kind. Zero would claim they had looked.

-- schema: prem_config

ALTER TABLE prem_config.ingest_run ADD COLUMN unmapped_principals INT;
