-- Who removed an agent.
--
-- An agent is removed by setting deleted_at, which every read of an agent
-- already skips and which frees its name (agent_live_name_idx is unique among
-- the agents not removed). The row stays, with its tokens and every audit and
-- usage row that points at it, so the history stays whole. deleted_by is the
-- actor as the change record describes it: the surface, and the account or
-- the signed-in user. A removal must say who made it; the check is NOT VALID
-- so an upgrade never fails on a row removed some other way before now.

-- schema: prem_config

ALTER TABLE prem_config.agent
    ADD COLUMN deleted_by TEXT,
    ADD CONSTRAINT agent_removed_by_whom CHECK (deleted_at IS NULL OR deleted_by IS NOT NULL) NOT VALID;
