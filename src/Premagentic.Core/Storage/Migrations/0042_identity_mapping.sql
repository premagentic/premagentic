-- What a group in somebody else's directory means here.
--
-- One row says "this external principal is that Premagentic group". A sign-in
-- adapter reports the groups the system it spoke to saw, in that system's own
-- terms, and a connector can name the principals a file's permissions carry;
-- both become Premagentic groups only through these rows, which an
-- administrator wrote.
--
-- external_principal  The name or id exactly as the outside system gives it:
--                     a directory group's id, a Windows security identifier, a
--                     POSIX group. Compared exactly, never case folded and
--                     never trimmed into shape, because a mapping that guesses
--                     at a principal is a mapping that matches the wrong one.
-- group_id            The Premagentic group it means. A group, never a role and
--                     never a user: a mapping can say what somebody is part of
--                     and nothing about what they may do.
--
-- One principal maps to one group, which the primary key enforces. A principal
-- with no row is not an error and not a guess: it is ignored at sign-in and
-- reaches nobody at ingest. Both are the fail-closed reading, and both are
-- counted where somebody will see them.

-- schema: prem_config

CREATE TABLE prem_config.identity_mapping(
    tenant_id UUID NOT NULL REFERENCES prem_config.tenant(id),
    external_principal TEXT NOT NULL,
    group_id UUID NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    PRIMARY KEY(tenant_id, external_principal),
    FOREIGN KEY(tenant_id, group_id) REFERENCES prem_config.app_group(tenant_id, id),
    CONSTRAINT identity_mapping_principal_trimmed
        CHECK (external_principal <> '' AND external_principal = btrim(external_principal))
);

-- For listing what one group is mapped from, and for the delete that follows a
-- group being removed.
CREATE INDEX identity_mapping_group_idx ON prem_config.identity_mapping(group_id);
