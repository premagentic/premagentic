-- What a run did with documents indexed earlier at paths it skipped.
--
-- A skipped file is one no reader here reads, or one a reader read and found
-- nothing to index in. The two mean opposite things for a document indexed
-- earlier at that path, so a run keeps one and removes the other, and both
-- are counted here so a run nobody watched can still be read afterwards.
--
-- kept_no_reader: documents indexed earlier in a format no reader in the
-- process reads now (a reader refused at start, disallowed, or not installed).
-- They are kept, and still found by search, unless the run was asked to
-- remove them.
--
-- removed_now_skipped: documents indexed earlier and removed because the file
-- is now skipped. They are included in orphans_removed, which counts every
-- removal; this says how many of those were skips rather than deletions at
-- the source.
--
-- NULL on runs recorded before this migration, which counted neither. Zero
-- would claim they had looked.

-- schema: prem_config

ALTER TABLE prem_config.ingest_run ADD COLUMN kept_no_reader INT;
ALTER TABLE prem_config.ingest_run ADD COLUMN removed_now_skipped INT;
