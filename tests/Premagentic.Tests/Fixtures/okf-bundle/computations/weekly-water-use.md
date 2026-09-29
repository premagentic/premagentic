---
type: Attested Computation
title: Weekly water use
description: Liters of water used across all benches in one week.
status: stable
runtime: postgres
parameters:
  - { name: week_start, type: date, required: true }
executor:
  resource: references/skills/run-on-postgres.md
  receipt: [query_id, executed_sql, result]
attester:
  resource: references/attesters/water-use-check.py
generated: { by: drafting_agent/1.4, at: 2026-05-20T12:00:00Z }
verified: { by: human:avery, at: 2026-05-21T08:00:00Z }
---

# Computation

    SELECT SUM(liters) AS water_used
    FROM irrigation.readings
    WHERE read_at >= @week_start
      AND read_at < @week_start + INTERVAL '7 days'

The executor and attester named above are not part of this bundle.
