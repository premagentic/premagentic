-- Who made an agent.
--
-- created_by  cli:<account> for one registered at the command line,
--             portal:<user id> for one an administrator registered in the
--             portal, self:<user id> for one a person made for themself on
--             the connect page, profile:<name> for one a profile made, and
--             unknown for one made before this column existed.
--
-- An agent a person made for themself was told apart by assistant_kind until
-- now, a column only that path wrote: a convention. It is backfilled as
-- self:<owner> here, and from now on the column is written by every path that
-- makes an agent, with no default, so a path that forgets it fails instead of
-- writing a guess.

-- schema: prem_config

ALTER TABLE prem_config.agent
    ADD COLUMN created_by TEXT NOT NULL DEFAULT 'unknown';

UPDATE prem_config.agent
SET created_by = 'self:' || owner_user_id::text
WHERE assistant_kind IS NOT NULL;

ALTER TABLE prem_config.agent
    ALTER COLUMN created_by DROP DEFAULT,
    ADD CONSTRAINT agent_created_by_shape
        CHECK (created_by = 'unknown' OR created_by ~ '^(cli|portal|self|profile):.+$');

CREATE INDEX agent_self_made_by_idx
    ON prem_config.agent(tenant_id, created_by)
    WHERE created_by LIKE 'self:%' AND deleted_at IS NULL;
