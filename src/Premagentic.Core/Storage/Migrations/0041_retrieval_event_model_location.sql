-- Where the model that received this answer runs, copied from the agent at the
-- moment of the read rather than read back from the agent later, because an
-- administrator can change an agent's model location and the record has to say
-- what was true when the passages were served.
--
--   model_location  hosted when a hosted-model agent asked, so what came back
--                   left the network; local when a local-model agent asked,
--                   and local for a person reading in the portal, since
--                   nothing leaves then.
--
-- NULL on rows written before this migration. What those answers were served
-- to is not known, and a guess in an audit trail is worse than a gap.

-- schema: prem_config

ALTER TABLE prem_config.retrieval_event
    ADD COLUMN model_location TEXT,
    ADD CONSTRAINT retrieval_event_model_location_known
        CHECK (model_location IS NULL OR model_location IN ('local', 'hosted'));

CREATE INDEX retrieval_event_tenant_hosted_idx
    ON prem_config.retrieval_event(tenant_id, created_at)
    WHERE model_location = 'hosted';
