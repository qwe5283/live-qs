# ADR-0004: OpenAPI and Versioned JSON Schema Define the Protocol

Status: Accepted

## Context

Handwritten TypeScript, Kotlin, C#, and Web data-transfer models have drifted. Event payloads need durable semantics for historical analysis.

## Decision

OpenAPI 3.1 is authoritative for HTTP behavior. Versioned JSON Schema is authoritative for event envelopes and payloads. Runtime validation, generated models, and contract tests derive from those documents.

## Consequences

Unknown event types and versions are rejected. Compatible additions are limited to optional fields; semantic or breaking changes require a new version.
