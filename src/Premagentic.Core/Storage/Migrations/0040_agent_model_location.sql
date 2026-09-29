-- Where an agent's model runs, and the reserved group of the agents whose
-- model runs somewhere else.
--
-- model_location  local, the model runs inside the company's network, so
--                 nothing served to this agent leaves it; or hosted, the
--                 model runs on someone else's machines, so everything served
--                 to this agent does. Existing rows are set to hosted, not
--                 local: nothing is known about an agent registered before
--                 this migration, and hosted is the assumption that keeps it
--                 away from a folder marked never-leaves until an
--                 administrator says otherwise. The default is dropped
--                 afterwards, because a new agent states where its model runs
--                 and there is no safe guess to fall back on.
-- model_vendor    Who runs a hosted model, for the record to name. Free text,
--                 never matched on, and NULL for a local model.
--
-- The group 'hosted-model agents' is a system group. The agent commands hold
-- its membership and an administrator cannot delete or rename it, so a folder
-- rule that denies it keeps meaning what it meant when it was written: this
-- folder never leaves the network.
--
-- It is keyed by system_key rather than by one fixed id, because app_group.id
-- is the primary key of the whole table: one constant could not serve two
-- tenants, and a tenant created after this migration would have no group at
-- all. Each tenant's group gets its id here, or at the moment its tenant is
-- created, and keeps it; rules name that id, as they name every other group's.

-- schema: prem_config

ALTER TABLE prem_config.agent
    ADD COLUMN model_location TEXT NOT NULL DEFAULT 'hosted',
    ADD COLUMN model_vendor TEXT,
    ADD CONSTRAINT agent_model_location_known CHECK (model_location IN ('local', 'hosted')),
    ADD CONSTRAINT agent_model_vendor_trimmed
        CHECK (model_vendor IS NULL OR (model_vendor <> '' AND model_vendor = btrim(model_vendor)));

ALTER TABLE prem_config.agent ALTER COLUMN model_location DROP DEFAULT;

ALTER TABLE prem_config.app_group
    ADD COLUMN system_key TEXT,
    ADD CONSTRAINT app_group_system_key_shape
        CHECK (system_key IS NULL OR system_key ~ '^[a-z][a-z0-9_]*$');
CREATE UNIQUE INDEX app_group_live_system_key_idx
    ON prem_config.app_group(tenant_id, system_key) WHERE deleted_at IS NULL;

-- The name is taken from the same pool administrators name their own groups
-- from, so a deployment that already has one under this name is stopped here
-- and told what to do, rather than having the migration fail on an index it
-- would take a schema reader to understand.
DO $$
DECLARE taken TEXT;
BEGIN
    SELECT g.name INTO taken FROM prem_config.app_group g
    WHERE g.deleted_at IS NULL AND lower(g.name) = 'hosted-model agents' LIMIT 1;

    IF taken IS NOT NULL THEN
        RAISE EXCEPTION
            'A group named ''%'' exists already, and this migration needs that name for the system group of hosted-model agents. Rename that group, then migrate again.',
            taken;
    END IF;

    INSERT INTO prem_config.app_group(tenant_id, name, system_key)
    SELECT t.id, 'hosted-model agents', 'hosted_model_agents' FROM prem_config.tenant t;
END $$;

-- The agents that were already there were just marked hosted, and the rule is
-- that an agent marked hosted is in the group. The membership is written here
-- rather than left to the next time somebody edits each agent, because until
-- it is written a folder rule that denies the group would not hold those
-- agents back, which is the whole point of marking them.
INSERT INTO prem_config.agent_group_grant(tenant_id, agent_id, group_id)
SELECT a.tenant_id, a.id, g.id
FROM prem_config.agent a
JOIN prem_config.app_group g ON g.tenant_id = a.tenant_id AND g.system_key = 'hosted_model_agents'
WHERE a.model_location = 'hosted' AND a.deleted_at IS NULL
ON CONFLICT DO NOTHING;

-- The second line under the group commands. A system group is not deleted or
-- renamed by anything: not by a command that forgets to check, not by a hand
-- at the psql prompt. Its membership is another table and is not touched here.
CREATE FUNCTION prem_config.refuse_system_group_change() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'DELETE' THEN
        RAISE EXCEPTION 'Group ''%'' is a system group and cannot be removed.', OLD.name;
    END IF;
    IF NEW.name <> OLD.name THEN
        RAISE EXCEPTION 'Group ''%'' is a system group and cannot be renamed.', OLD.name;
    END IF;
    IF NEW.deleted_at IS NOT NULL AND OLD.deleted_at IS NULL THEN
        RAISE EXCEPTION 'Group ''%'' is a system group and cannot be deleted.', OLD.name;
    END IF;
    IF NEW.system_key IS DISTINCT FROM OLD.system_key THEN
        RAISE EXCEPTION 'Group ''%'' is a system group and cannot stop being one.', OLD.name;
    END IF;
    RETURN NEW;
END $$;

CREATE TRIGGER app_group_system_immutable
    BEFORE UPDATE OR DELETE ON prem_config.app_group
    FOR EACH ROW WHEN (OLD.system_key IS NOT NULL)
    EXECUTE FUNCTION prem_config.refuse_system_group_change();
