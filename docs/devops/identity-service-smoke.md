# Identity Service — 环境与冒烟清单（member2）

交 member1 汇总到本地 Compose / Azure staging 与 CD。

## 模块概览
- 服务：`services/identity/JiKeDa.Identity.Api`，拥有 `identity_db`。
- 对外路由（经 Gateway）：`/api/auth/*`、`/api/accounts/*`；本地直连 `5101`。
- 迁移：`InitialCreate`（Accounts + UserProfiles + 四个种子账号）。启动时自动 `Migrate()`（连接串非空时）。

## 环境变量
| 变量 | 说明 | 必需 |
|------|------|------|
| `Jwt__Key` | JWT 签名密钥，≥32 字节。**全团队共享同一值**（Identity 签发，其他服务 + Gateway 验证）。未配置或过短时服务启动失败。 | 是 |
| `Jwt__Issuer` / `Jwt__Audience` | 默认 `JiKeDa` / `JiKeDa.Client`，全团队一致。 | 否（有默认） |
| `ConnectionStrings__Default` | `identity_db` 连接串（独立账号 `identity_app`）。为空时跳过迁移（仅用于设计时/单测）。 | 是（运行时） |

## 种子账号（仅 staging/测试）
| 用户名 | 角色 | 密码 |
|--------|------|------|
| admin | Administrator | Passw0rd!123 |
| merchant1 | Merchant | Passw0rd!123 |
| rider1 | Rider | Passw0rd!123 |
| customer1 | Customer | Passw0rd!123 |

## 健康检查
- `GET /health`（服务自身）；经 Gateway 为 `GET /health/identity`。
- 就绪验证：`POST /api/auth/login`（admin / Passw0rd!123）返回 200 且含 `accessToken`。

## 冒烟步骤
1. 登录：`POST /api/auth/login`（admin）→ 200，拿 Administrator token。
2. 账号查询：`GET /api/accounts?role=Customer`（带 admin token）→ 200，分页结构正确。
3. 停用账号：`PUT /api/accounts/{id}/status` `{"isActive":false}` → 200；随后该账号登录 → 401。
4. 重新启用 → 200；账号恢复可登录。
5. 顾客注册：`POST /api/auth/customer/register`（新用户名）→ 201；重复用户名 → 409。

### 负向冒烟（鉴权）
- 匿名访问 `/api/accounts` → 401。
- Customer/Merchant/Rider token 访问 `/api/accounts` → 403。
- 错误密码 / 停用账号登录 → 401。

## 自动化测试（CI 证据）
- `AuthServiceTests`（注册、重复冲突、登录、错误密码、停用拦截）。
- `AccountServiceTests`（分页/角色筛选、启停、角色调整、无效角色、不存在）。
- `IdentityEndpointTests`（登录 happy、注册 201、匿名 401、非管理员 403、管理员列账号）。

本地：`dotnet test services/identity/JiKeDa.Identity.Tests` → 19 通过 / 0 失败（SQLite in-memory，不依赖 PostgreSQL）。
