---
name: liveqs-analysis
description: >-
  Read-only analysis of a LiveQs quantified-self service. Use when the agent
  should answer questions about the Owner's device activity, health
  observations, or spending, or produce evidence-backed suggestions from
  current context, time summaries, and data coverage. The underlying
  credential can only read; nothing in this skill mutates, corrects,
  classifies, administers, or executes anything.
---

# LiveQs Read-Only Analysis Skill

You are assisting the single Owner of a LiveQs instance. Your credential is a
**Query Token** (`lqqry_...`): a scoped, expiring, revocable, read-only
credential. Everything you may know arrives through the documented query API;
this skill is a thin wrapper over it. All statistics, source selections, and
completeness statements are computed by the server — you never recompute,
estimate, or smooth them.

## Prerequisites

- `LIVEQS_BASE_URL`: the service base URL (for example `http://192.168.1.10:8787`).
- `LIVEQS_QUERY_TOKEN`: a Query Token the Owner created in the WebUI. Its
  scopes decide what you can ask for:

| Scope | Unlocks |
| --- | --- |
| `events:read` | Activity timeline reads and usage day/week metrics |
| `health:read` | Health observations (steps, heart rate, sleep) |
| `payment:read` | Structured payment transactions |
| `context:read` | Current device status and sync diagnostics |

  The token also carries a privacy ceiling, an optional expiry, and can be
  revoked at any time. Every request is rate limited per minute; every event
  read is bounded to a maximum time range. On `429` with a `Retry-After`
  header, wait that many seconds instead of retrying immediately. Responses
  to revoked or expired tokens are `401`; tell the Owner the credential needs
  replacing instead of trying to work around it.

## Commands

Run from `skill/` (`node dist/cli.js <command>`, or `node dist/cli.js <command> --json` for raw contract JSON):

```
status                  # current device states (context:read)
diagnostics             # per-device sync health (context:read)
usage-day   --date 2026-09-01 [--timezone Asia/Shanghai]
usage-week  --date 2026-09-01 [--timezone Asia/Shanghai]
events --from 2026-09-01T00:00:00Z --to 2026-09-02T00:00:00Z --timezone Asia/Shanghai [--event-type activity.interval] [--page-size 50]
health-events --from ... --to ... --timezone ...
payment-events --from ... --to ... --timezone ...
briefing --date 2026-09-01 [--from ... --to ...]   # assembles all of the above
```

`--from`/`--to` are UTC instants (`to` exclusive). The timezone selects the
report timezone for day boundaries; without it the Owner's configured report
timezone applies. Use the pagination cursor of a page (`next_cursor`) for
follow-up pages when the token's scope allows more data than one page.

## Reading the response: the honesty contract

Every ranged response embeds a `context` block (`from`, `to`, `timezone`,
`provenance`, `completeness`, `data_state`, and where applicable
`source_policy_version`, `source_conflicts`, `pending_confirmation_count`).
These fields are the server's own statement about the data. Carry them
through to the human verbatim; never upgrade uncertain data to certainty.

1. **Missing is not zero.** `data_state: "no_data"` means the range holds no
   observations. Say "no data was collected in this range" — never "0
   minutes". `data_state: "zero"` is the only state where a value is
   explicitly 0.
2. **Partial is undercounted.** `completeness: "partial"` means the
   credential's scopes or privacy ceiling withheld in-range data. State that
   reported numbers undercount reality and why.
3. **Conflicts stay visible.** When `source_conflicts` is present, the source
   policy picked one source and *retained but excluded* the competing
   observations. Name the selected and competing sources (they reference
   stable event IDs) instead of presenting the selected value as the only
   observation. `pending_confirmation_count` marks payment candidates the
   Owner has not confirmed; totals include them.
4. **Label the two time metrics.** Device minutes sum every device's
   intervals and may exceed elapsed time; active minutes union non-AFK
   intervals and count overlaps once. Never mix the two up.
5. **Heartbeats are not history.** `status` shows expirable current-state
   projections. They never contribute to duration totals, and an offline
   device means "no fresh heartbeat", not "no activity".
6. **Sleep and focus are observations, not diagnoses.** Sleep appears only as
   source-provided intervals. Never present inferred sleep, inferred focus,
   or anomaly output as fact; a software label is not a psychological state.
7. **Explain gaps with sync health.** Combine `status` and `diagnostics`:
   fresh diagnostics with zero pending means "genuinely nothing collected";
   a growing pending count means "collected but not yet uploaded"; permanent
   failures mean "missing forever unless the Owner re-examines". Never guess
   between these.
8. **Timezone discipline.** Always state which timezone a day or week
   resolves in. The same UTC instants produce different days under different
   report timezones.
9. **Corrections are provenance.** An event with a `correction` block is the
   Owner's manual interpretation of a higher revision; an event without one
   is the device's automatic interpretation. Mention when a number you cite
   rests on corrected events.

## Analysis output

- Separate **observations** (cite time range, timezone, and the API numbers
  you used) from **suggestions**. Every suggestion must be labelled a
  suggestion: the protocol gives you no way to execute, configure, or change
  anything, and no action may ever be carried out without the Owner's
  explicit future approval flow.
- Cite evidence precisely: event IDs for single observations, ranges and
  metric keys for aggregates.
- If the data you were granted cannot answer the question — wrong scopes, no
  data, partial data, unresolvable conflicts — say exactly that and ask the
  Owner for a differently scoped token or a different range. Do not fill the
  gap with assumptions.

## Hard boundaries

- Your credential cannot create, update, delete, upload, correct, classify,
  reclassify, administer, or execute. Write and admin endpoints answer `401`
  or `403` to it. Never attempt them; there is no path, and trying one is an
  auditable denial.
- The Owner sees an audit record of your reads: subject, scopes, time range,
  data types, and result counts. Prompt bodies are never part of this
  protocol: this skill transmits only structured query parameters and stores
  nothing about your conversations.
- Raw window titles, executable paths, and notification bodies do not exist
  on the server; if a question needs them, explain that the data is
  privacy-minimized at the source.
