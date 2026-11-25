# Server key 与下游网关 API key 设计

日期：2026-11-11

状态：用户已于 2026-11-11 批准实施；使用 EF Core + SQLite，仅保留最近一次成功鉴权的时间/IP。

## 1. 目的与已确认需求

下游可使用网关签发的 API key 发起请求。网关根据现有模型映射、classifier 和 backend 配置选路，再使用对应 provider key 调用上游；同一个有效网关 key 可跨 provider 使用，切换 provider 时无需下游同步更换 key。网关可签发多个下游 key，不限制某个下游持有或配置的 key 数量，并保留现有 BYOK 方式。

用户已确认：

- 每个 provider 一个 key；本项目中 provider 直接对应现有命名 backend，不引入另一套 provider 注册表。
- 支持 appsettings.json 配置 provider key，生产由 Jenkins 在 Docker Compose 部署时注入。
- 程序不将 provider key 写入数据库或 runtime JSON；admin 可临时替换 provider key，仅保存在内存。
- 下游网关 key 通过 admin 接口签发和撤销，使用 SQLite 保存持久数据，通过 EF Core 操作；这取代早先 PostgreSQL/Npgsql 的建议。
- 数据访问应便于以后更换数据库基础设施，本地测试可以使用内存数据库。
- 网关 key 表包含 last_used_at 和 last_used_ip，仅保留最近一次成功鉴权的时间和 IP；当前不保留逐次历史。
- 当前没有按下游、模型或 backend 的访问控制需求；任何有效网关 key 均可使用所有现有路由。
- 当前单实例部署；dashboard 已按 backend 配置组织。
- 网关 key 必须与 BYOK 明确区分，验证失败不能误入 BYOK 路径。

本文具体接口、错误码及实现选择随完整设计获准实施。用户另确认香港 httpd 已配置客户端 IP 还原；网关负责可信转发头处理，不修改 httpd 配置。

## 2. 方案选择

推荐“provider key 由配置与内存管理，下游 key 由 EF Core + SQLite 管理”。满足临时切换、不持久化上游秘密和下游 key 可撤销的要求，且不需要独立数据库服务。

仅用静态下游 key 的方案更简单，但不符合已经选择的 admin 签发和撤销方式。将两类 key 全部放入数据库的方案便于统一管理，但违背 provider key 不由程序持久化的要求。本期不采用这两种方案，也不配置 PostgreSQL 服务。

这是一个需要书面设计的功能，涉及配置、鉴权、代理、数据库、管理接口及模型发现。实现保持 .NET 9 Minimal API；使用与 net9.0 兼容的 EF Core 9.x、SQLite provider 及开发时迁移工具。项目将不再是零第三方依赖，相关说明需要在实现时更新。

生产使用 SQLite 文件，测试优先使用 SQLite 内存模式，而非 EF Core InMemory provider。两者均在内存中运行，但 SQLite 内存模式能保留生产所用 provider 的事务、约束与 SQL 翻译行为。

通过专用 DbContext 和集中注册的 provider 配置隔离存储细节，代理与鉴权流程不直接依赖 SQLite 连接或手写数据库方言。不构建通用仓储或多数据库运行时切换框架。EF Core 能减少未来切换工作，但不同 provider 的迁移、数据搬迁和查询验证仍需要单独处理，不能承诺只改连接串即可迁移。

## 3. 配置与启用方式

建议的配置结构（只有占位值）：

```json
{
  "GatewayKeys": {
    "Enabled": true
  },
  "ConnectionStrings": {
    "GatewayKeys": "Data Source=/data/gateway-keys.db"
  },
  "Backends": {
    "openrouter": {
      "BaseUrl": "https://openrouter.ai/api",
      "ApiKey": "<injected by deployment>"
    }
  }
}
```

- `GatewayKeys:Enabled` 默认 false，允许既有部署继续仅使用 BYOK，无需数据库。
- `Backends:{name}:ApiKey` 为可选配置。Jenkins 可注入 `Backends__openrouter__ApiKey`；SQLite 文件位置可通过 `ConnectionStrings__GatewayKeys` 覆盖。真实 provider key 不写入提交的 appsettings.json、镜像构建参数或仓库 Compose 示例。
- 上述路径是 Linux 容器部署示例；本地开发显式使用本地可写路径，不依赖容器路径。
- 启用 GatewayKeys 时缺少连接串、使用生产内存库、目录不可写或迁移失败均阻止启动；未启用时不创建数据库文件、不执行迁移。
- 生产挂载整个 `/data` 目录并为容器内的非 root 用户授予写权限，不只挂载单个文件，以容纳数据库、日志/临时辅助文件；数据库文件不放在不可写的镜像 `/app` 目录。
- 某个 backend 未配置 provider key 不妨碍启动或 BYOK。只有网关 key 请求实际选中它时，才返回明确错误。
- 配置按启动时快照使用，不承诺文件热更新。admin 内存覆盖优先于启动时已合并的配置（环境变量高于 JSON）；删除覆盖或重启均恢复部署配置。
- 保留 `sk-aigw-` 命名空间，即使功能关闭，该前缀也返回 401，不转发到上游。识别保留前缀时大小写不敏感；完整 token 验证大小写敏感。

## 4. Provider key 的内存管理

新增独立的 provider credential store，按现有 backend 名称索引，名称大小写规则与 `BackendOptions` 一致。

- 每个 backend 具有可选的配置 key 和可选的内存覆盖 key，有效值为“覆盖优先”。
- 临时修改采用原子快照或等效并发机制；请求在构造上游请求时读取一次有效 key。
- 修改成功后的新请求使用新 key；已经构造的请求、重试之外的在途请求和已建立的 SSE 流不切换 key。
- PUT 只接受非空、非空白且不含控制字符的 key；配置与 PUT 均拒绝网关保留前缀的值，避免将下游网关 key 错配为 provider key。清除覆盖使用 DELETE，不用空字符串表达。
- 删除覆盖是恢复配置值，不是禁用 backend；本期没有临时禁用 provider 的独立功能。
- 未知 backend 返回 404，不因保存凭证自动创建 backend。
- 不复用会落盘的 `RuntimeMappingStore`、`RuntimeBackendTranslationStore` 等持久化逻辑。
- 内存中以及 .NET 配置对象中会存在明文。这里的“不持久化”指应用不额外写盘或写数据库，并不表示 Jenkins、容器环境或用户自行写入的 JSON 不会保有部署秘密。

## 5. 下游网关 key 与 EF Core / SQLite

### 5.1 Token 与生命周期

- 格式建议：`sk-aigw-{id}.{secret}`；id 为公开 UUID，secret 为密码学安全随机生成的至少 32 字节、base64url 编码的随机值。
- SHA-256 摘要绑定完整 token，数据库只保存摘要和元数据。高熵随机 token 不采用密码口令的慢哈希，也不增加必须部署的 pepper。
- 查询 id 对应行后，使用固定时间比较校验摘要，并检查撤销和过期状态。
- admin 签发成功且数据库事务提交后，201 响应仅此一次返回完整 key；列表永远不返回 key、secret 或摘要。
- 响应丢失时不能找回原文；管理员通过列表找到记录并撤销，再重新签发。请求方可以使用有辨识度的 name；名称不是唯一约束，也不承担权限含义。
- 首期支持签发、分页列举和撤销，可选过期时间；不支持恢复、修改 secret 或原文找回。轮换通过“签发新 key，切换下游，再撤销旧 key”完成。

### 5.2 最小数据结构

由 EF Core 实体、Fluent API 和 migrations 管理 `gateway_api_keys` 表，不手写 PostgreSQL 类型或查询：

| 列 | 含义 |
|---|---|
| id | Guid 主键，公开管理标识，SQLite 物理映射由 provider 管理 |
| name | 管理员填写的名称，必填且有长度上限 |
| token_hash | SHA-256 完整摘要，byte[] / SQLite BLOB，固定 32 字节，不在 API 返回 |
| created_at | 应用生成的 UTC DateTime |
| expires_at | 可空 UTC DateTime，签发时必须晚于当前时间 |
| revoked_at | 可空 UTC DateTime，非空表示已撤销 |
| last_used_at | 可空 UTC DateTime，最近一次成功通过网关 key 鉴权的时间；从未使用时为 null |
| last_used_ip | 可空 string，最近一次成功鉴权对应的客户端 IPv4/IPv6 地址，不含端口；从未使用或无法取得地址时为 null |

所有时间通过可注入的 TimeProvider 获取 UTC，存储时统一 UTC DateTime，读取恢复 UTC 语义，API 输出带 `Z` 的 ISO 8601。请求的 expiresAt 允许带偏移，入库前归一化为 UTC。避免对 SQLite 不支持原生比较/排序的 DateTimeOffset 直接执行数据库范围查询。过期与撤销在每次认证时生效，不依赖清理任务。列表的过期状态按查询时刻计算。

last_used_at / last_used_ip 的语义为“最近一次成功通过鉴权的时间及来源”，不是“上游推理成功时间”。本地模型目录与推理请求通过网关 key 鉴权后均更新；上游报错、缺少 provider key 或流式请求后续中断不撤销该记录。无效、过期、已撤销的 key、BYOK 和 admin 管理操作不更新；同一个请求只在共享鉴权入口更新一次。

首期每次成功鉴权后尝试一次同时更新 last_used_at / last_used_ip 的短写入，不增加后台队列或节流缓存。两个字段使用同一条条件更新原子写入，保证对应同一个请求；时间只前进，不被较早请求覆盖，同时避免整行保存覆盖并发撤销结果。这些字段是尽力记录的管理信息：认证查询失败仍返回 503，但已通过认证后的最近使用记录失败只记脱敏告警，不阻断请求，也不影响过期与撤销检查；因此故障时显示值可能滞后。无法获取本次 IP 时将 last_used_ip 写为 null，不沿用上一次 IP 搭配新时间。

客户端 IP 从 HttpContext.Connection.RemoteIpAddress 获取，IPv4-mapped IPv6 规范化为 IPv4。当前程序尚未配置 ForwardedHeaders；默认记录直接连接的对端地址。若部署在反向代理后，需要显式配置可信代理地址并由 ASP.NET Core ForwardedHeaders 中间件处理 X-Forwarded-For，之后再取 RemoteIpAddress；不在业务代码直接读取或信任任意客户端提交的 IP header。未配置可信代理时，记录代理 IP 而不宣称取得真实终端地址。具体可信代理地址由部署注入，不硬编码内网地址或默认信任全部代理。

不增加用户、角色、scope、模型授权、配额或计费。

### 5.3 数据库使用与故障

- 每个操作创建独立 DbContext（例如通过 IDbContextFactory），不把 DbContext 保存在单例或跨请求并发共享。每个网关 key 请求执行一次按主键查询的认证，通过后尝试一次 last_used_at / last_used_ip 条件更新；首期不缓存认证成功结果。
- 用统一 TimeProvider 检查有效期，不依赖数据库专属时钟函数。撤销提交后启动的认证查询必须拒绝该 key；提交之前已通过认证的请求允许完成，SSE 不被中断。认证使用不跟踪查询，不因长生命周期实体跟踪而接受旧状态。
- 数据库故障或表结构缺失时，网关 key 验证以及依赖数据库的 admin 操作返回脱敏的 503；绝不放行或回退 BYOK。仅最近使用时间/IP 记录失败的例外按第 5.2 节处理，不把这一例外用于认证查询或撤销操作。
- BYOK、provider key 内存管理和现有不依赖数据库的 admin 功能不因运行期间的数据库故障而停止。
- 数据库锁等待使用较短的 SQLite command/busy timeout，建议 3 秒；避免长期写事务，认证查询只读，使用时间记录、签发和撤销短写入。尽可能传递取消信号，但不承诺 SQLite 原生调用具备网络数据库等价的异步取消能力。不沿用上游推理的 10 分钟超时。
- 使用提交到仓库的 EF Core migrations 管理 schema。当前单实例和轻量部署采用启用功能时、开始接受请求前执行 MigrateAsync；失败则拒绝启动并给出脱敏诊断。不混用 EnsureCreated，不在每个请求执行迁移，也不自动删除或重建现有数据库。
- 迁移设置启动期限（建议 30 秒）；EF Core 9 SQLite 的迁移锁可能因异常终止残留，需在 README 提供停机检查及恢复步骤，不能让启动无限等待或自动清锁。迁移前备份已有文件；首期需验证空库创建和重复启动。
- 运行期间发生锁超时、磁盘不可写或损坏等故障，相关数据库请求返回 503；BYOK 及其他不依赖库的功能继续可用。故障解除后可恢复的连接可重新使用；不自动修复损坏的库或创建替代内存库。
- SQLite 文件持久化在挂载目录，仅包含网关 key 摘要、元数据及 EF 迁移信息。备份应在停止写入后保存数据库及所需辅助文件，恢复后已撤销状态必须保留。
- `/health` 维持现有 backend 网络可达性定义，不能据此判断网关 key 鉴权是否可用；本期不重新定义该接口。

## 6. 请求认证与代理流程

推荐在 `/v1` 入口共享认证逻辑，向后续处理传递一次认证结果；本地 discovery 和 proxy 共用，不重复查库。

1. 读取 `x-api-key` 或 `Authorization: Bearer`。缺失返回 401。若两者同时携带不同 key，或单个认证头存在多个值，返回 400，避免歧义；两处为同一个 key 时允许。
2. 以保留前缀识别网关 key。格式错误、未知、过期、已撤销均返回统一 401，不透露具体状态，不进入 BYOK；功能关闭时也拒绝。
3. 其他非空凭证为 BYOK，不访问数据库，不尝试换成 provider key；上游负责验证其有效性。
4. 网关 key 验证成功后，按现有模型映射/classifier 逻辑确定最终 backend 和 proxy。鉴权不改变路由，也不把 key 绑定到某个 backend。
5. 网关 key 模式读取最终 backend 的有效 provider key；缺失则返回 503 `provider_key_not_configured`，不向上游发请求，不使用下游 key 或其他 backend 的 key 顶替。
6. BYOK 模式继续使用下游提供的原 key。普通路由与 classifier 都遵循这一模式；BYOK 本身不获得跨 provider 换 key 的能力。
7. 构造上游请求时设置正确的 Authorization，不将网关 key 转发给上游；其余模型改写、协议翻译、SOCKS5、SSE 和非流式行为保持兼容。

数据面错误保持现有 `{"error":{"type":"...","message":"..."}}` 风格。内部异常、连接串及认证凭证不写入响应。上游返回的 401/403 沿用既有处理，不触发凭证重试、跨 provider 重试或 BYOK 回退。

## 7. `/v1/models` 规则

当 GatewayKeys 开启时，本地目录与推理使用相同凭证识别规则：

| 凭证 | 本地目录开启 | 本地目录关闭 |
|---|---|---|
| 有效网关 key | 返回共享目录，不需要 provider key | 用默认 backend 的 provider key 查询上游 |
| 无效/过期/已撤销的网关 key | 401 | 401 |
| BYOK | 返回共享目录，不验证上游有效性 | 原 key 查询默认 backend |
| 缺失 | 401 | 401 |
| 网关 key 查库失败 | 503 | 503 |

本地目录关闭时，GET 没有 model 可用于选路，继续使用现有默认 backend `openrouter`，不聚合多个 provider 的模型目录。

GatewayKeys 关闭时，保留现有本地目录可匿名读取的行为；显式携带保留前缀的 token 仍返回 401，避免功能关闭后网关 key 被误作 BYOK。该开关带来的本地目录鉴权变化需写入升级说明。所有 BYOK 本地目录响应仅说明凭证存在，不证明上游可用。

## 8. Admin API 契约建议

所有接口使用现有 `x-admin-key` 保护；没有 `Admin:ApiKey` 时仍统一 404。网关 key 不能替代 admin key。API 字段使用 camelCase。

### 8.1 Provider credential

| 方法 | 路径 | 行为 |
|---|---|---|
| GET | /admin/backend-credentials | 返回全部已配置 backend 的凭证状态 |
| PUT | /admin/backend-credentials/{name} | `{ "apiKey": "..." }` 写入内存覆盖，200 返回状态 |
| DELETE | /admin/backend-credentials/{name} | 清除内存覆盖，200 返回恢复后的状态；重复执行结果相同 |

同现有 translation API 提供查询参数形式的 PUT/DELETE（`/admin/backend-credentials?name=...`），支持包含斜杠等不适合作为路由段的 backend 名称。

状态仅包含 `{ "name": "openrouter", "configured": true, "source": "configuration" }`，source 为 `configuration`、`runtime`、`none`。GET 返回 `{ "backends": [...] }`。不返回 key、摘要、前后缀或伪装为可回填 key 的掩码。

空白/非法 key、缺失 name 或非法请求体返回 400；未知 backend 返回 404。此管理功能不依赖数据库，也不要求 GatewayKeys 已开启。

### 8.2 下游网关 key

| 方法 | 路径 | 行为 |
|---|---|---|
| POST | /admin/gateway-keys | `{ "name": "desktop", "expiresAt": null }` 签发，201 返回元数据与一次性 apiKey |
| GET | /admin/gateway-keys?limit=50&offset=0 | 分页返回元数据，包含已撤销记录 |
| POST | /admin/gateway-keys/{id}/revoke | 原子设置 revokedAt，204；重复撤销 204；不存在 404 |

元数据为 `id/name/createdAt/expiresAt/revokedAt/lastUsedAt/lastUsedIp`，签发时 lastUsedAt / lastUsedIp 均为 null。列表按 `createdAt DESC, id DESC` 稳定排序，返回 `{ "keys": [...], "limit": 50, "offset": 0 }`。limit 范围 1–100，offset 不得小于 0；无效分页参数和 name/时间/id 输入返回 400。名称建议长度 1–200；具体输入约束可由实现者定稿并写入契约。

GatewayKeys 关闭时，上述网关 key 管理端点返回 404。数据库故障返回 503，不将签发结果保存在本地文件作为替代。包含秘密的签发响应及 credential 管理响应设置 `Cache-Control: no-store`。

### 8.3 Dashboard 对接边界

沿用现有 `/admin/backends` 的名称列表契约，不直接序列化新增 ApiKey 的 BackendConfig。dashboard 按 backend 名称关联 credential 状态。

后端交付对接契约；本期不跨仓库修改 dashboard。未来凭证编辑表单只能提交新值，不能从 GET 读取旧值；网关 key 明文只在签发响应显示。无需增加权限设置页面。

## 9. 日志与信息暴露

- 不记录完整网关 key、provider key、摘要、admin key 或数据库连接串；签发与凭证修改请求体不进入现有 compliance 日志。
- 可记录网关 key id、认证模式、backend 名称及签发/撤销事件，不需要记录每次认证成功，也不增加数据库审计表。
- 审查现有配置序列化和错误日志路径，确保给 BackendConfig 增加 ApiKey 后不会出现在管理返回值、健康响应或异常文本中。
- 不把凭证放入 HttpClient 默认共享 headers；每次请求独立设定 Authorization，避免并发串 key。
- admin 管理流量应走现有受信任部署路径；本期不增加新 SSO 或管理鉴权体系。

## 10. 验收边界

实现阶段应覆盖以下有实际意义的验证，本轮不编写产品代码或测试：

- 模型映射、classifier 和默认 backend 在网关模式下选用各自 provider key；BYOK 仍原样传递，最终上游永远不收到网关 key。
- 网关 key 的未知、格式错误、过期、撤销、禁用及数据库故障不回退 BYOK，不发出上游请求。
- 数据层及接口测试优先使用 SQLite 内存模式，每个测试隔离，并保持底层连接开启直到测试结束，以免 DbContext 释放连接后数据库消失；使用真实 migrations 建表，覆盖签发、摘要存储、事务、过期与撤销，不以 EF Core InMemory 替代关系行为。
- last_used_at / last_used_ip 验证初始为空、成功鉴权后成对更新、失败鉴权不更新、本地目录请求更新、上游失败仍记录、并发时间不倒退且 IP 对应同一请求、更新不覆盖撤销，以及仅使用记录写入失败时不阻断已通过鉴权的请求。
- IP 验证直接连接、可信代理、多层转发中的信任边界、不可信 X-Forwarded-For 不被采用、IPv4/IPv6 和无法取得地址时的 null；不将代理地址误报为已验证的真实终端地址。
- 少量临时文件 SQLite 集成测试验证 migrations、重复启动、跨 DbContext 和重新打开后的有效性/撤销状态，以及锁超时和目录权限/落盘错误；内存测试不能证明持久化与部署可用性。
- provider 临时替换、并发读取、清除覆盖与重启恢复配置；provider key 不进入 SQLite 或 runtime JSON。
- 本地/上游 discovery 在模式开关、网关/BYOK/无凭证/数据库故障组合下符合第 7 节。
- 错误或冲突认证头、admin 鉴权、非法输入、未知 backend、一次性返回和敏感字段不外泄。
- 协议翻译、SOCKS5、流式/非流式和现有完整测试回归；遵循仓库命令显式运行 Tests/AiGateway.Tests.csproj。

## 11. 非目标与实施者自由度

不包含逐次鉴权历史表/查询/保留清理任务、多实例同步、多 provider key 轮询、provider key 数据库存储、细粒度授权、配额计费、JWT、用户系统、推理自动重试、多 provider 模型聚合、dashboard 页面实现或生产部署。

实施者可决定内部类型/文件名、EF Core 9.x 补丁版本、迁移目录和测试夹具组织，但不得改变凭证分类、失败不回退、存储边界和公开接口语义。SQLite 初始化、挂载目录权限、备份、迁移恢复及本地测试方式应写入 README。未来更换数据库需要新的 provider 配置、迁移及数据迁移方案，本期不实现。

批准本文后才进入实现计划及后续实施；生产部署、注入真实凭证和连接生产数据库不属于本次设计与后端实现范围。

## 12. 官方参考

- EF Core SQLite 限制（时间类型比较、迁移与迁移锁）：https://learn.microsoft.com/ef/core/providers/sqlite/limitations
- 测试策略（不推荐 EF Core InMemory provider，SQLite 内存测试保留关系行为）：https://learn.microsoft.com/ef/core/testing/choosing-a-testing-strategy
- SQLite 内存库连接生命周期：https://learn.microsoft.com/ef/core/testing/testing-without-the-database#sqlite-in-memory
- EF Core 迁移应用方式：https://learn.microsoft.com/ef/core/managing-schemas/migrations/applying
- SQLite 异步 I/O 限制与 WAL：https://learn.microsoft.com/dotnet/standard/data/sqlite/async
- SQLite 锁等待与超时：https://learn.microsoft.com/dotnet/standard/data/sqlite/database-errors
