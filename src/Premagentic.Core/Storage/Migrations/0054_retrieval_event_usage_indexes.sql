-- Indexes for the manager's usage page, which reads the audit trail by
-- tenant and time, always inside a window of at most a year.
--
-- The tenant and time index from the baseline serves the totals. These serve
-- the two lists a page asks for beside them: each caller's use, and the
-- questions that returned no passage, which are the content gaps.

-- schema: prem_config

CREATE INDEX retrieval_event_tenant_user_created_idx
    ON prem_config.retrieval_event(tenant_id, caller_user_id, created_at)
    WHERE caller_agent_id IS NULL;

CREATE INDEX retrieval_event_tenant_agent_created_idx
    ON prem_config.retrieval_event(tenant_id, caller_agent_id, created_at)
    WHERE caller_agent_id IS NOT NULL;

CREATE INDEX retrieval_event_tenant_no_passage_idx
    ON prem_config.retrieval_event(tenant_id, created_at)
    WHERE kind = 'search' AND jsonb_array_length(passages) = 0;
