-- The change record: one row per administrator change, append only.
--
-- A change and its row are written in one transaction, so they land together
-- or not at all. The application has no path that updates or deletes a row,
-- and the triggers below refuse both from every role, so history is corrected
-- by adding a row, never by editing one.
--
--   kind           What changed, such as setting.set or source.add.
--   target         The key or the object that changed: a setting key, a
--                  source name.
--   old_value      The value before, or NULL when there was none.
--   new_value      The value after, or NULL when the object was removed.
--   actor_surface  Where the change was made: cli, or later the portal.
--   actor_account  The operating-system account that ran the command, for
--                  the CLI, where nobody signs in.
--   actor_user_id  The signed-in user, when there is one.

-- schema: prem_config

CREATE TABLE prem_config.admin_event(
    id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    tenant_id UUID NOT NULL REFERENCES prem_config.tenant(id),
    occurred_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    kind TEXT NOT NULL,
    target TEXT NOT NULL,
    old_value JSONB,
    new_value JSONB,
    actor_surface TEXT NOT NULL,
    actor_account TEXT,
    actor_user_id UUID,
    CONSTRAINT admin_event_kind_shape CHECK (kind ~ '^[a-z0-9_.]+$'),
    CONSTRAINT admin_event_actor_user FOREIGN KEY (tenant_id, actor_user_id)
        REFERENCES prem_config.app_user(tenant_id, id)
);
CREATE INDEX admin_event_tenant_idx ON prem_config.admin_event(tenant_id, id);

CREATE FUNCTION prem_config.admin_event_is_append_only() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'prem_config.admin_event is append only: a change is recorded by adding a row, never by editing or removing one';
END;
$$;

CREATE TRIGGER admin_event_no_update_or_delete
    BEFORE UPDATE OR DELETE ON prem_config.admin_event
    FOR EACH ROW EXECUTE FUNCTION prem_config.admin_event_is_append_only();

CREATE TRIGGER admin_event_no_truncate
    BEFORE TRUNCATE ON prem_config.admin_event
    FOR EACH STATEMENT EXECUTE FUNCTION prem_config.admin_event_is_append_only();
