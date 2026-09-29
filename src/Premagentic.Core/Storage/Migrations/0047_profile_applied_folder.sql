-- Where a profile was applied from, so drift can be shown against it.
--
-- The health page answers "what is this deployment configured from, and does
-- it still match". The second half needs the profile itself, and a profile is
-- a folder of files rather than something the database holds, so the path is
-- recorded beside the name and version.
--
-- It is advisory. The folder may be gone, on a removable drive, or on another
-- machine entirely, and the page says so rather than pretending the
-- deployment matches. What the deployment was configured FROM stays answerable
-- either way, because that is the name and the version, which are here.
--
-- Nullable, because the rows written before this migration did not record it.

-- schema: prem_config

ALTER TABLE prem_config.profile_applied ADD COLUMN applied_from TEXT NULL;
