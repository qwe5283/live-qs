# ADR-0001: Single Owner With Scoped Credentials

Status: Accepted

## Context

V1 serves one data owner but must support independent device collectors and read-only AI analysis without sharing a universal bearer token.

## Decision

Use one implicit Owner identity authenticated by password and revocable browser sessions. Create independently revocable Device Tokens and Query Tokens with scopes, privacy ceilings, expiry, and audit records.

## Consequences

V1 does not implement registration, organizations, or viewer accounts. New actor types require an explicit authorization and audit design.
