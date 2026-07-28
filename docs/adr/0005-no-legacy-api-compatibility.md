# ADR-0005: No Legacy API Compatibility

Status: Accepted

## Context

The legacy API lacks the event, revision, authorization, privacy, and source-policy semantics required by V1. No external compatibility commitment exists.

## Decision

Build a clean V1 protocol and migrate active clients together. Deprecated server and Windows implementations remain behavioral references only.

## Consequences

Legacy routes, tokens, response shapes, and test data may be discarded. New work must not add compatibility behavior unless a future ADR reverses this decision.
