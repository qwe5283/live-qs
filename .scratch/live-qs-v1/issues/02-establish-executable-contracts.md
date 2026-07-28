# 02 — 建立可执行契约基础

**What to build:** 提供语言中立、可机器验证的 V1 协议基础，使服务端与所有客户端从同一契约理解请求、响应和版本化事件，而不是继续复制手写 DTO。

**Blocked by:** 01 — 建立 Monorepo 基线.

**Status:** resolved

- [x] OpenAPI 3.1 文档能够通过自动校验，并定义统一认证、错误和分页约定。
- [x] JSON Schema 定义稳定事件信封、版本字段、时间语义、revision、隐私和来源元数据。
- [x] 每个已定义 Schema 至少包含一个合法样例和多个关键非法样例。
- [x] 契约检查能够拒绝未知事件类型、未知 Schema 版本和非法单位或时间范围。
- [x] 兼容性规则明确区分可选字段扩展与必须升版的破坏性变化。
- [x] CI 或等价根级检查能够发现无效契约和未同步的派生客户端模型。

## Answer

在独立的 `contracts/` Node 组件中建立了 OpenAPI 3.1、稳定事件信封、
闭合事件注册表和首个 `activity.interval@1` Schema。合法与非法样例覆盖未知
类型/版本、单位、时间范围、IANA 时区、UTC 偏移和时长一致性。

契约检查会校验 OpenAPI、Schema 注册、样例语义、批量确认约束和四端派生模型
同步；模型由 OpenAPI 与事件联合 Schema 共同生成到 Server、Web、Windows 和
Android。C# 与 Kotlin 序列化测试确认 snake_case 字段和枚举 wire 值与契约一致。
根级 `.\scripts\check.ps1` 已将 contracts 作为首个检查组件。兼容性和升版规则
记录在 `contracts/README.md`。

验证：`.\scripts\check.ps1` 全部通过。
