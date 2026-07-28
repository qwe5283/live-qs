# LiveQs Domain Context

## Purpose

LiveQs is a single-Owner quantified-self system. It records structured, privacy-minimized observations from personal devices so the Owner and authorized read-only AI Agents can understand current context and historical patterns.

## Ubiquitous Language

- **Owner**: the only data owner and human administrator in V1.
- **Device**: an authenticated Windows or Android collector with an independent Device Token.
- **Credential**: an independently revocable Owner Session, Device Token, or read-only Query Token.
- **Observation**: a source-specific fact reported by a device or external source. Observations are retained even when they conflict.
- **Event**: the versioned, structured record used to persist an observation. An Event has a stable identifier and a revision.
- **Revision**: a monotonically increasing interpretation of one logical Event. The latest valid revision is the default query result.
- **Current state**: a short-lived device projection produced by heartbeats. It is not historical evidence and never contributes to duration totals.
- **Historical interval**: a finalized or checkpointed time range reported by the originating client and used for historical analysis.
- **Subject**: a stable semantic target, such as a service or approved project alias, that can group activity across applications and platforms.
- **Classification rule**: a versioned, Owner-managed rule executed locally by a device to map raw local context to a Subject or activity category.
- **Source policy**: a versioned rule that chooses a normalized result from retained observations without deleting competing sources.
- **Coverage**: the known completeness, source availability, conflicts, and gaps for a query range.
- **Device minutes**: the sum of qualifying durations across devices; concurrent use can exceed elapsed time.
- **Active minutes**: the union of qualifying non-AFK intervals; concurrent use is counted once.
- **Private observation**: an observation blocked on the client before upload.

## Language to Avoid

- Do not call a heartbeat an Event or use it for historical duration.
- Do not call a source-policy result “raw data”; it is a normalized interpretation.
- Do not call inferred sleep, focus, or anomaly output a fact.
- Do not call a Query Token an Owner credential or management credential.
