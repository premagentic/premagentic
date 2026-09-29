-- The chunker a source's documents are cut with, chosen by name from the
-- chunkers the process registers. markdown is built in and the default, so
-- every source and document stored before this migration keeps it.
--
-- source.chunker       A name the process does not register is refused when
--                      the source is added or changed, and fails the run at
--                      ingest before anything is chunked.
-- ingest_run.chunker   What the run cut with, copied at the start like the
--                      other settings. Not held to the name's shape, so a run
--                      given a name no chunker can have is still recorded.
-- document.chunker     What cut the stored chunks. The unchanged check compares
--                      it, so a source switched to another chunker has each of
--                      its documents cut, embedded and stored again at its next
--                      run, which moves the document's updated_at.

-- schema: prem_config

ALTER TABLE prem_config.source
    ADD COLUMN chunker TEXT NOT NULL DEFAULT 'markdown',
    ADD CONSTRAINT source_chunker_shape CHECK (chunker ~ '^[A-Za-z0-9][A-Za-z0-9_.-]{0,63}$');

ALTER TABLE prem_config.ingest_run
    ADD COLUMN chunker TEXT NOT NULL DEFAULT 'markdown';

-- schema: prem_index

ALTER TABLE prem_index.document
    ADD COLUMN chunker TEXT NOT NULL DEFAULT 'markdown';
