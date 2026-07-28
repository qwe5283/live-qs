# ADR-0003: Server-Authoritative Cross-Device Events

Status: Accepted

## Context

Local device stores must work offline, while WebUI and AI require one cross-device query authority. Treating both client and server copies as independently authoritative creates unresolved conflicts.

## Decision

MongoDB is authoritative for cross-device events and derived queries. Clients own collection, local raw context where allowed, and durable outboxes until per-event revisions are acknowledged.

## Consequences

WebUI and AI query service data. Clients may retain local copies but do not silently override accepted server facts; changes use higher revisions or explicit invalidation.
