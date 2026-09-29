-- The trust policy each answer was served under, beside its passages, so a bad
-- answer can be traced to the setting that let it through and not only to the
-- file it came from.
--
--   trust_minimum_tier  The lowest trust tier at which machine-written content
--                       was served: 0 unverified, 1 machine-confirmed,
--                       2 human-reviewed. Stored as the gate compared it, so
--                       there is no range check.
--   include_stale       Whether content past its stale_after was served,
--                       flagged.
--   policy_at           The instant staleness was judged at.
--
-- All three are NULL on rows written before this migration, which recorded no
-- policy.

-- schema: prem_config

ALTER TABLE prem_config.retrieval_event
    ADD COLUMN trust_minimum_tier SMALLINT,
    ADD COLUMN include_stale BOOLEAN,
    ADD COLUMN policy_at TIMESTAMPTZ;
