-- The kind of assistant a person connected, on an agent they made for
-- themself on the connect page.
--
-- assistant_kind  Free text naming the assistant, such as a desktop chat
--                 client or a coding tool, shown back to the person beside
--                 the agent. NULL on an agent an administrator registered,
--                 which is how a self-made agent is told apart: the bound on
--                 how many a person may make counts only these.

-- schema: prem_config

ALTER TABLE prem_config.agent
    ADD COLUMN assistant_kind TEXT,
    ADD CONSTRAINT agent_assistant_kind_trimmed
        CHECK (assistant_kind IS NULL OR (assistant_kind <> '' AND assistant_kind = btrim(assistant_kind)));

CREATE INDEX agent_self_made_owner_idx
    ON prem_config.agent(tenant_id, owner_user_id)
    WHERE assistant_kind IS NOT NULL AND deleted_at IS NULL;
