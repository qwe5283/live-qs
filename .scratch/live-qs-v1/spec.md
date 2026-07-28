# LiveQs V1：单用户量化自我平台

Status: ready-for-agent

## Problem Statement

用户希望持续追踪自己在 Windows 和 Android 设备上的当前及历史活动，包括前台应用、屏幕使用、健康指标和消费记录，并通过 WebUI 查看跨设备时间线与摘要，也允许只读 AI Agent 基于这些数据提供有证据的分析和建议。

当前实现由多轮敏捷试验演化而来，缺少统一的 PRD、可执行协议、领域术语和架构决策。各端手写并复制接口模型，服务端同时承担实时状态、历史合并和启发式分析，鉴权依赖共享 Token，事件载荷缺少版本化约束。人工冒烟测试无法稳定验证断网恢复、幂等、时间区间、来源冲突和跨端兼容，已造成多次返工。

V1 需要先证明一条可靠的纵向链路：真实设备能够连续采集，离线时安全排队，恢复联网后无丢失、无重复地上传，Owner 能在 WebUI 查看和修正跨设备事实，并让受限 AI Agent 只读查询。V1 不追求完整推断、公开分享或公网生产化。

## Solution

构建一个单一数据所有者、服务端权威、客户端本地采集的量化自我平台。Windows 和 Android 保留已经验证的平台采集与生命周期代码，但统一迁移到全新的版本化事件协议。Express 服务通过 MongoDB 保存跨设备权威事件，严格执行 OpenAPI 3.1 和 JSON Schema 契约，并提供当前状态、时间线、摘要、诊断、分类、修正和凭据管理能力。

实时心跳与历史事件分离：心跳只表示可过期的设备状态，历史统计只读取客户端生成的版本化区间事件。客户端使用稳定事件标识、递增 revision、持久化 outbox 和逐事件确认协议实现可靠同步。原始窗口标题和通知正文留在设备本地；客户端依据集中管理、版本化的规则生成可解释语义标签。服务端保留不同来源的观测，并依据显式来源优先级生成规范化统计。

Owner 使用纯密码登录 WebUI，并通过安全 Session 管理 Device Token 和只读 Query Token。权限由细粒度 scopes、数据敏感度和有效期共同决定。AI Agent 完全只读，只能访问授权的结构化查询和汇总接口。

V1 在可信局域网内运行，以真实 HTTP API 加真实 MongoDB 为主要自动化测试边界，并通过真实设备冒烟和连续七天验收验证采集可靠性。

## User Stories

1. As the Owner, I want to initialize the system with a password, so that I can control access without managing an unnecessary username.
2. As the Owner, I want to log in through the WebUI, so that I can access all authorized personal data and management functions.
3. As the Owner, I want my browser session to be revocable, so that a lost or shared browser does not retain permanent access.
4. As the Owner, I want to create a separately named Device Token for each collector, so that one compromised device can be revoked without interrupting others.
5. As the Owner, I want a Device Token to upload only its permitted event types, so that a collector cannot query or administer my data.
6. As the Owner, I want to create time-limited Query Tokens with explicit scopes, so that AI Agents receive only the data required for a task.
7. As the Owner, I want to see each credential's scopes, expiry, last use, and revocation state, so that I can understand and control access.
8. As the Owner, I want credential plaintext displayed only once when created, so that long-lived secrets are not recoverable from the database or UI.
9. As the Owner, I want to revoke any device, query, or browser credential immediately, so that access ends without rotating unrelated credentials.
10. As the Owner, I want all credential use and management actions audited, so that I can investigate unexpected access.
11. As the Owner, I want to see all online devices and their latest current activity, so that I know what the system presently observes.
12. As the Owner, I want stale devices to become offline automatically, so that an old heartbeat is never presented as current truth.
13. As the Owner, I want concurrent device states shown independently, so that the system does not invent a single attention focus.
14. As the Owner, I want a cross-device historical timeline with separate device lanes, so that simultaneous activities remain visible.
15. As the Owner, I want device usage minutes to sum every device's activity, so that I can measure total device consumption.
16. As the Owner, I want de-duplicated active minutes to union overlapping intervals, so that I can measure elapsed active time without double counting.
17. As the Owner, I want every metric clearly labelled as device time or de-duplicated time, so that I do not misinterpret totals.
18. As the Owner, I want Windows foreground activity and AFK intervals collected locally, so that application use remains available during server outages.
19. As the Owner, I want Android foreground context and UsageStats collected for different purposes, so that realtime context does not duplicate authoritative daily usage.
20. As the Owner, I want Health Connect steps, heart rate, and sleep intervals ingested with their source metadata, so that health summaries remain traceable.
21. As the Owner, I want payment notifications converted into structured transaction events, so that I can review spending without storing notification bodies.
22. As the Owner, I want unparseable or rejected observations placed in a visible failure queue, so that collection failures are never silently discarded.
23. As the Owner, I want clients to retain pending events through disconnection and process restart, so that intermittent connectivity does not lose history.
24. As the Owner, I want repeated uploads of the same event to remain one logical fact, so that retrying cannot inflate reports.
25. As the Owner, I want older revisions rejected after a newer revision is accepted, so that delayed messages cannot overwrite current truth.
26. As the Owner, I want partial batch success acknowledged per event, so that valid events progress while invalid events can be diagnosed separately.
27. As the Owner, I want the WebUI to show last collection, last upload, queue depth, and recent sync errors per device, so that I can distinguish missing activity from a broken collector.
28. As the Owner, I want browser and IDE titles classified locally into stable services or projects, so that meaningful activities can be compared across platforms without uploading raw titles.
29. As the Owner, I want Edge activity containing a Bilibili rule to share a semantic subject with the Android Bilibili package, so that cross-platform media use can be aggregated.
30. As the Owner, I want Rider activity classified by an approved project alias, so that work time can be separated by project.
31. As the Owner, I want to manage a central semantic dictionary and versioned classification rules in the WebUI, so that devices use consistent categories.
32. As the Owner, I want clients to cache and execute applicable rules locally, so that classification continues offline without revealing raw titles.
33. As the Owner, I want each classification to expose its rule, version, and confidence, so that results are explainable and correctable.
34. As the Owner, I want rule changes to affect future observations by default, so that historical reports do not change unexpectedly.
35. As the Owner, I want to explicitly request historical reclassification within available local retention, so that improved rules can correct past summaries under my control.
36. As the Owner, I want new automatically discovered project names represented by opaque identifiers until I approve an alias, so that sensitive names are not uploaded accidentally.
37. As the Owner, I want `private` observations blocked from upload on the client, so that privacy is enforced before data reaches the network.
38. As the Owner, I want raw window titles, executable paths, and notification text to remain local, so that the service stores only analysis-relevant structured data.
39. As the Owner, I want to correct merchant, category, project, or other interpretations without destroying the collected fact, so that reports improve while provenance remains intact.
40. As the Owner, I want to mark a false observation invalid without immediately erasing it, so that it stops affecting reports but remains auditable.
41. As the Owner, I want affected summaries rebuilt after a correction or reclassification, so that all views agree on the latest valid revision.
42. As the Owner, I want every corrected result to indicate automatic or manual provenance, so that I can assess its trustworthiness.
43. As the Owner, I want all source observations retained when multiple collectors observe the same phenomenon, so that automatic de-duplication cannot silently delete evidence.
44. As the Owner, I want an explicit, versioned source-priority policy for each metric, so that normalized statistics have deterministic authority.
45. As the Owner, I want source conflicts and coverage gaps shown in the WebUI, so that a selected value is not presented without qualification.
46. As the Owner, I want event facts stored as UTC instants with capture timezone context, so that travel and daylight-saving changes do not corrupt history.
47. As the Owner, I want one configured report timezone to define day and week boundaries, so that reports remain stable across browsers and devices.
48. As the Owner, I want changing my report timezone to rebuild derived summaries without rewriting original events, so that the same facts can be viewed under a different calendar.
49. As the Owner, I want source-provided sleep intervals displayed as observations, so that the system does not pretend to medically infer sleep.
50. As the Owner, I want focus shown only when produced by an explicit user rule, so that a software label is not confused with a psychological state.
51. As the Owner, I want usage, health, and spending summaries to include source coverage and missing-data information, so that I can interpret them responsibly.
52. As an AI Agent, I want a scoped, documented read API for current context, timelines, summaries, and coverage, so that I can provide evidence-based analysis.
53. As an AI Agent, I want every response to identify time range, timezone, provenance, and completeness, so that I do not present partial data as certain.
54. As an AI Agent, I want no mutation or execution endpoints available to my credential, so that analysis cannot alter the Owner's records or settings.
55. As the Owner, I want AI recommendations separated from system actions, so that no suggestion is executed without a future explicit approval flow.
56. As a developer, I want OpenAPI and versioned JSON Schema to be the only protocol authority, so that TypeScript, Kotlin, C#, and Web models cannot drift independently.
57. As a developer, I want valid and invalid examples for each event type, so that collectors and the service agree on time, units, privacy, and required fields.
58. As a developer, I want breaking contract changes to create a new version, so that historical data remains interpretable.
59. As a developer, I want all components in one Monorepo with independent component versions, so that cross-platform changes are atomic without coupling release cadence.
60. As a developer, I want component-specific release tags and update manifests, so that Windows and Android can discover only their applicable releases.
61. As a developer, I want to retain proven Windows and Android platform adapters, so that protocol work does not reopen solved operating-system integration risks.
62. As a developer, I want legacy API compatibility removed, so that the new model is not constrained by deprecated routes or payloads.
63. As a developer, I want automated tests to enter through the real HTTP boundary and real MongoDB, so that they verify behavior instead of implementation calls.
64. As a developer, I want sanitized real event shapes used as golden fixtures, so that deterministic tests remain representative of actual collectors.
65. As the Owner, I want the complete system to run for seven days without manual collector restart, so that V1 demonstrates practical reliability.
66. As the Owner, I want a 24-hour simulated outage to recover every queued event, so that the offline-first claim is verified.
67. As the Owner, I want observable latency, accuracy, CPU, and battery results recorded during acceptance, so that V1 quality is evaluated against agreed evidence.

## Implementation Decisions

1. **Product boundary:** V1 has one Owner and no registration or multi-tenant product behavior. Other humans do not receive accounts in V1. Full sharing-link UX is deferred.
2. **Deployment boundary:** V1 runs on a trusted LAN and listens on the configured wildcard host. Dual-stack listening should be used where the operating system supports it. Firewall rules restrict access to trusted networks, and CORS uses an explicit origin allowlist. Public VPS deployment is a later security gate.
3. **Owner authentication:** The Owner uses a password with an implicit fixed identity. Passwords are stored with a memory-hard hash. Successful login produces a revocable, server-managed session in an HttpOnly, SameSite cookie. Production deployment additionally requires Secure cookies and HTTPS.
4. **Credential model:** Device, query, and optional management credentials are separate actor types. Authorization is expressed with capabilities rather than a simple linear access level: actor type, scopes, maximum privacy level, permitted event types, expiry, and revocation. The server stores only token hashes and displays plaintext once. Normal Web administration uses the Owner Session, not a long-lived management token.
5. **Repository model:** The project becomes one Monorepo containing contracts, server, Web, Windows, and Android. Components keep independent semantic versions and component-prefixed release tags. Path-aware CI builds only affected components. Release assets may remain on GitHub Releases; platform-specific update manifests avoid the ambiguous repository-wide “latest release” endpoint.
6. **Contract authority:** OpenAPI 3.1 defines HTTP routes, authentication, errors, and response models. Versioned JSON Schema defines event envelopes and payloads. These language-neutral contracts are authoritative; runtime validation and generated or contract-checked client models derive from them.
7. **Protocol compatibility:** The new API uses a clean version boundary and has no obligation to preserve deprecated API routes, response shapes, tokens, or test data. All active clients migrate together. The deprecated server remains reference material only.
8. **Event envelope:** Every analyzable event contains a stable globally unique event identifier, type, schema version, Owner, source, device, UTC start and optional end, capture timezone and offset, structured payload, privacy level, revision, finalization state, provenance, and deletion or invalidation state where applicable. Unknown event types or schema versions are rejected.
9. **Payload registry:** Each core event type has a registered, versioned schema defining units, time semantics, required fields, privacy defaults, and legal sources. New optional fields may be compatible; removal, renaming, unit changes, or semantic changes require a new schema version.
10. **Data authority:** MongoDB is authoritative for cross-device querying and analysis. Client stores provide local collection, offline experience, short-term raw context where permitted, and durable outbox delivery. WebUI and AI read service results.
11. **Upload protocol:** Clients batch-upsert events with stable identifiers and increasing revisions. The server returns one result per item with accepted, duplicate, stale-revision, or rejected status and a stable error code. A client removes an outbox revision only after its corresponding acknowledgement. Partial success retries only unresolved transient failures; permanent validation failures enter a visible local failure queue.
12. **Concurrency control:** The database uniquely identifies logical events by Owner and event identifier. A lower or equal revision cannot replace a higher revision. Competing revisions are resolved atomically.
13. **Realtime boundary:** Heartbeats update a TTL-backed device projection and never directly contribute to historical totals. Devices become offline after the configured staleness threshold. Current context exposes concurrent device states rather than inferring one global focus.
14. **Historical activity:** Clients create historical activity intervals because they own platform context and offline state. Active intervals use stable identifiers and revision checkpoints; final transitions mark them finalized. A server does not extend an event merely because a device stopped reporting.
15. **Data minimization:** Window-title text, full executable paths, and notification bodies are not uploaded. Windows may keep raw titles locally according to local retention. Any uploaded title fingerprint uses a device-secret HMAC, not an unsalted general hash. Payment ingestion uploads extracted amount, direction, approved merchant label, category, and source metadata. A private event is blocked before upload.
16. **Semantic model:** Device and application identity remain provenance; semantic dimensions such as activity category and subject provide cross-platform grouping. A browser and a native application may map to one service subject, while an IDE may map to an approved project subject. Aggregation never destroys source identity.
17. **Classification control plane:** The service and WebUI manage semantic entities and versioned rules. Clients download, cache, and execute applicable rules locally. Uploaded classification contains the semantic result, rule identifier, rule version, and confidence, never the matched raw title.
18. **Project privacy:** User-approved project aliases may be uploaded. Newly parsed project names remain local and use opaque identifiers until the Owner approves a stable label.
19. **Historical reclassification:** Rule edits affect new observations by default. The Owner may explicitly start historical reclassification. Devices re-evaluate only locally retained raw context and submit higher event revisions. Affected derived summaries are invalidated and rebuilt; the operation is audited.
20. **Corrections:** Collected structured observations retain provenance. Owner corrections create auditable higher revisions and record changed fields, time, actor, and optional reason. False observations become invalid for statistics without immediate physical erasure. Default queries return the latest valid revision and expose correction provenance.
21. **Multiple sources:** Raw or structured observations from different sources remain distinct. Each normalized metric has an explicit, versioned authority policy. Android UsageStats is authoritative for Android daily application totals; accessibility observations support current and contextual activity. Health events retain data origin. Payment records prefer stable source identifiers, and ambiguous candidates are surfaced instead of fuzzily merged.
22. **Time semantics:** Facts are stored as UTC instants with capture IANA timezone and offset. One Owner report timezone defines default day and week boundaries. Rollup cache identity includes timezone. Changing report timezone rebuilds derived summaries, not events. Source-provided sleep intervals retain full bounds and are assigned consistently for reporting.
23. **Time metrics:** Device minutes sum all qualifying device intervals and may exceed elapsed time. Active minutes are the union of qualifying non-AFK intervals and never double count overlap. Timelines preserve parallel device lanes. APIs and UI label each metric explicitly.
24. **WebUI scope:** Core screens cover Owner login, current device state, cross-device timeline, usage/health/spending summaries, data coverage, synchronization diagnostics, classification entities and rules, explicit historical reclassification, corrections, source conflicts, and credential lifecycle management.
25. **AI boundary:** V1 provides one thin Skill over documented read APIs. Query Tokens can access only granted structured resources, have bounded time range, pagination, rate limits, expiry, and revocation. AI cannot create, update, delete, classify, administer, or execute. Responses expose time range, timezone, provenance, and completeness.
26. **Platform reuse:** Windows retains proven Win32 foreground sampling, SQLite, tray lifecycle, and WPF foundations. Android retains UsageStats, Health Connect, notification listening, and permission flows. Both replace legacy DTOs, event boundaries, classification integration, outbox behavior where needed, and API clients.
27. **Core versus experimental reports:** Source-provided health facts can appear in core views. Inferred anomalies, inferred sleep, and inferred focus remain experimental and are excluded from V1 acceptance and the primary dashboard. Focus is presented only when based on an explicit Owner rule.
28. **Operational visibility:** Each collector reports or exposes last collection, last successful upload, pending queue size, permanent failures, and recent transient error. Missing data is never visually indistinguishable from zero activity.
29. **V1 sequencing:** Implement contracts first, followed by service behavior, Windows integration, WebUI, Android integration, and finally the read-only AI Skill. A vertical slice must be runnable before adding another data domain.
30. **Backup deferral:** Automated, encrypted, and offsite backup is not a V1 functional requirement. The accepted consequence is that LAN-stage history may be unrecoverable after database loss. Public deployment must reopen backup, recovery, HTTPS, rate limiting, and credential-hardening decisions.

## Testing Decisions

1. Tests assert externally observable behavior rather than internal calls, Mongoose query shape, component structure, or private method use. Refactoring is safe when the public contract and resulting facts remain unchanged.
2. The primary automated seam is the real HTTP API running against a real MongoDB instance. Tests submit contract-valid or invalid requests, then observe HTTP results and subsequent query behavior.
3. Contract validation covers OpenAPI structure, every event schema, compatible examples, invalid examples, units, time semantics, and unknown-version rejection.
4. Service integration tests cover Owner and credential authorization, scope denial, privacy ceilings, partial batch results, duplicate delivery, stale revisions, competing revisions, current-state expiry, correction, invalidation, source policies, timezone reports, and union versus summed time.
5. Client synchronization tests stop at the transport interface and cover exactly the critical state transitions: acknowledged success, transient network failure, partial acknowledgement, permanent rejection, stale revision, and process restart with a durable outbox.
6. Pure deterministic tests cover interval union, day-boundary calculation, source-priority selection, rule precedence, rule-version output, and classification golden cases.
7. Sanitized events captured from real collectors become contract examples and golden fixtures. Synthetic boundary cases supplement rather than replace real event shapes.
8. No generic coverage percentage is required. The small set of synchronization, authorization, schema, and statistics invariants must have regression tests before related defects are considered fixed.
9. Real-device smoke testing validates Windows foreground sampling and AFK detection, Android UsageStats permission, Health Connect permission and records, notification parsing, local classification, outbox upload, and WebUI visibility.
10. V1 acceptance runs the system continuously for seven days without manual collector restart and records evidence for all agreed quality targets.
11. The outage acceptance test disconnects a device for 24 hours, restores connectivity, and reconciles all locally queued event identifiers with service acknowledgements and queries.
12. Replaying one logical event ten times must produce one latest logical fact. Older revisions must never replace a newer accepted revision.
13. While online, 95% of historical events should become queryable within five minutes; Android system scheduling may extend the maximum to twenty minutes.
14. Current status should be no more than thirty seconds old while reporting, and a device should appear offline within sixty seconds after heartbeats stop.
15. Activity interval boundary error must be no more than one configured sample period. Daily application totals should remain within five percent of the agreed operating-system reference measurement.
16. Health and payment ingestion reconciles source record counts. Unsupported or invalid observations must appear in a failure queue rather than disappear.
17. Acceptance records Windows background average CPU with a target below one percent and Android incremental daily battery usage with a target below three percent, including device and measurement method. A missed target requires an explicit documented decision, not silent acceptance.

## Out of Scope

- User registration, multiple data owners, tenant isolation, organizations, and account switching.
- Complete human sharing-link experience or viewer accounts.
- Any AI mutation, automatic execution, autonomous remediation, or natural-language chat UI.
- Machine-learning classification and server-side processing of raw window or notification text.
- Server-inferred sleep, psychological focus, anomaly diagnosis, or health advice as trusted V1 behavior.
- macOS and iOS collectors.
- Compatibility with deprecated APIs, deprecated response contracts, legacy tokens, or legacy test databases.
- Public VPS production deployment and its required TLS, reverse-proxy, rate-limiting, and internet threat-model work.
- Automated, encrypted, offsite backups and disaster-recovery automation.
- A generalized raw personal-data lake.
- Broad UI unit-test coverage, multi-tenant tests, high-load benchmarks, and arbitrary coverage-percentage targets.
- Guaranteed reclassification beyond the retention window of raw context on the originating device.

## Further Notes

- V1 success means trustworthy capture and explainable data coverage, not the number of charts or inferred insights.
- MongoDB and Express are suitable implementation choices for the agreed event model, but the language-neutral contracts, not Mongoose schemas, define the product protocol.
- Existing code is evidence and reusable platform infrastructure, not an implicit requirements document.
- The deprecated implementations remain read-only references during migration and should not receive new features.
- The first runnable slice should use one activity event type from one real Windows collector, persist it through the new batch protocol, and display it in the WebUI timeline before health, finance, or AI work expands the surface.
- Major decisions from this specification should be recorded as focused ADRs when implementation begins. Product scope belongs in the PRD/specification; protocol details belong in executable contracts; shared terminology belongs in the domain glossary.
