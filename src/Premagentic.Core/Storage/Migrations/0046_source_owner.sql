-- The person answerable for a source's documents.
--
-- A source is a folder somebody in the organization owns: the person who
-- knows whether a document in it is still true, and who the review queue and
-- the sources pages name. It is nullable because most deployments start
-- without one and naming the wrong person is worse than naming nobody.
--
-- Nothing is enforced against this column. It decides no access and gates
-- nothing: the folder rules do that, and giving an owner privileges here
-- would be a second, quieter access path beside them.
--
--   owner_user_id  A user of this tenant, or NULL. Set with
--                  prem sources set <name> --owner <sign-in name>, and
--                  cleared with --owner "".
--
-- Whether a source may be served to a hosted model is deliberately NOT a
-- column here. That is one thing only: a deny entry for the reserved
-- hosted-model agents group at the top of the source's folder rule. Keeping
-- it in the rule means the gate that already decides every read decides this
-- too, rather than a flag that some code path could forget to consult.

-- schema: prem_config

ALTER TABLE prem_config.source
    ADD COLUMN owner_user_id UUID NULL,
    ADD CONSTRAINT source_owner_user FOREIGN KEY (tenant_id, owner_user_id)
        REFERENCES prem_config.app_user(tenant_id, id);
