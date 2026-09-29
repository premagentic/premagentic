-- The latest reminders run, as the built-in reminder sink keeps it.
--
-- prem reminders run computes, from the index, the documents past their stale
-- date and the documents waiting in the review queue, grouped by the owner of
-- the source each came from, and hands the summaries to every reminder sink.
-- The built-in sink writes them here and nowhere else, so the portal can show
-- an owner what they are being reminded of without calling out to anything.
--
-- Only the latest run is kept: a new run replaces the one before it, in one
-- transaction, so the portal never reads half of one run. What changed from
-- one run to the next is in the index itself, not here.
--
--   reminder_run   One row per tenant: when the run was computed, and its
--                  counts.
--   reminder_item  One row per document per list of that run.
--     owner        Who the summary is for, as a principal: user:<sign-in
--                  name>, or administrators for the documents whose source
--                  has no owner.
--     list         stale, review or unowned.
--     since        When it went stale on the stale list; when it was last
--                  indexed on the other two.
--
-- Paths are copied, not referenced: this is a record of what a run said,
-- and a document deleted since is still what that run named.

-- schema: prem_config

CREATE TABLE prem_config.reminder_run(
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id UUID NOT NULL REFERENCES prem_config.tenant(id),
    computed_at TIMESTAMPTZ NOT NULL,
    owners INT NOT NULL,
    stale INT NOT NULL,
    in_review INT NOT NULL,
    unowned INT NOT NULL,
    CONSTRAINT reminder_run_one_per_tenant UNIQUE (tenant_id)
);

CREATE TABLE prem_config.reminder_item(
    run_id UUID NOT NULL REFERENCES prem_config.reminder_run(id) ON DELETE CASCADE,
    owner TEXT NOT NULL,
    list TEXT NOT NULL,
    path TEXT NOT NULL,
    title TEXT NULL,
    since TIMESTAMPTZ NULL,
    CONSTRAINT reminder_item_list CHECK (list IN ('stale', 'review', 'unowned')),
    PRIMARY KEY (run_id, owner, list, path)
);
