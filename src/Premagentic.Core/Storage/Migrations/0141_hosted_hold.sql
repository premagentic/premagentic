-- The folders whose documents are never served to agents whose model runs
-- outside the network: the "may be served to hosted models" switch, off.
--
-- Until now the switch was one deny entry for the hosted-model agents group at
-- the top of the folder's own rule. That held only while that rule decided: a
-- longer rule beneath the folder carried no entry, and a rule written at the
-- folder itself replaced the list and dropped it. A hold is kept here instead,
-- beside the rules, where no rule write reaches it. It covers every document
-- of the source at or beneath the prefix, in whole segments, whatever rule or
-- connector decided the document's list.
--
-- source       The connector a folder rule names, 'filesystem' for a folder.
-- path_prefix  The folder, as a folder rule writes it; '' is the whole source.
-- legacy       Made by this migration from a switch turned off before it. The
--              switch's old entry still opens the folder's rule, and goes when
--              this hold is released; no other release touches a rule.

-- schema: prem_config

CREATE TABLE prem_config.hosted_hold(
    tenant_id UUID NOT NULL REFERENCES prem_config.tenant(id),
    source TEXT NOT NULL,
    path_prefix TEXT NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    legacy BOOLEAN NOT NULL DEFAULT false,
    PRIMARY KEY(tenant_id, source, path_prefix)
);

-- Every switch turned off before this migration is a live folder rule
-- ('filesystem') at the prefix of a registered source whose first line denies
-- the hosted-model agents group: the switch wrote exactly there and nowhere
-- else. Each becomes a legacy hold on that folder. The entry stays in its
-- rule, where it changes nothing more, and goes when the hold is released.
-- The same entry anywhere else was written by hand and stays an ordinary
-- entry of an ordinary rule.
INSERT INTO prem_config.hosted_hold(tenant_id, source, path_prefix, legacy)
SELECT r.tenant_id, r.source, r.path_prefix, true
FROM prem_config.folder_rule r
JOIN prem_config.acl_set s ON s.tenant_id = r.tenant_id AND s.id = r.acl_set_id
JOIN prem_config.app_group g ON g.tenant_id = r.tenant_id AND g.system_key = 'hosted_model_agents'
WHERE r.deleted_at IS NULL
  AND r.source = 'filesystem'
  AND EXISTS (SELECT 1 FROM prem_config.source src
              WHERE src.tenant_id = r.tenant_id AND src.path_prefix = r.path_prefix AND src.deleted_at IS NULL)
  AND split_part(s.canonical_text, E'\n', 1) = 'deny group:' || g.id::text;

-- schema: prem_index

-- What a hosted-model agent's read asks for each list it may read: does any
-- document of a held folder's source, under this list, lie at or beneath the
-- folder (HostedHolds.HeldSetIdsAsync). With the path in byte order a folder's
-- documents are one range, so each question is one seek, and the read's cost
-- grows with lists and holds rather than with the documents.
CREATE INDEX document_hold_probe_idx
    ON prem_index.document(tenant_id, source_name, acl_set_id, (path COLLATE "C"));
