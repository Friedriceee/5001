# 接口契约

## SWE5001 微服务基础骨架

本节只约定第一阶段的服务边界和联调入口，不代表业务接口已经完成。现有单体 API 在迁移期间继续保留；新功能逐步迁入以下服务，不允许新服务直接读取其他服务的数据库。

### 本地地址与 Gateway 路由

| 组件 | 本地地址 | Gateway 路由 |
|---|---|---|
| Gateway | `http://localhost:5000` | 单一外部入口 |
| Identity | `http://localhost:5101` | `/api/auth/*`、`/api/accounts/*` |
| Catalog | `http://localhost:5102` | `/api/catalog/*`、`/api/merchants/*`（`/internal/catalog/*` 不经 Gateway，仅服务间直连） |
| Order | `http://localhost:5103` | `/api/cart/*`、`/api/checkout/*`、`/api/orders/*`、`/api/pricing/*`、`/api/addresses/*`（`/internal/order/*` 不经 Gateway） |
| Fulfilment and Delivery | `http://localhost:5104` | `/api/fulfilment/*`、`/api/deliveries/*`、`/api/riders/*` |

Gateway health 路由为 `/health/identity`、`/health/catalog`、`/health/order` 和 `/health/fulfilment`；Gateway 自身为 `/health`。业务前端完成迁移后只调用 Gateway，直接服务端口仅用于本地调试。

履约追踪补充接口：`GET /api/fulfilment/tracking/orders` 只允许顾客访问，按 JWT
`profile_id` 返回其订单的厨房进度、整体订单状态、配送状态，以及已接单骑手的
`riderId`、`riderName`、`riderPhone` 和各配送时间戳。商家订单接口同时返回相同的
骑手身份/联系方式和 `deliveryStatus`，因此骑手接单后两端可以持续显示该记录直到完成。

### 数据库归属

本地使用一个 PostgreSQL 容器和四个逻辑数据库：`identity_db`、`catalog_db`、`order_db`、`fulfilment_db`。每个服务只使用自己的连接字符串、DbContext 和迁移。现有单体 `takeout` 数据库在渐进迁移期间保持不变。

### 第一批跨服务示例

- Order 调用 Catalog 的库存预留和释放示例见 `contracts/catalog-reservation.example.md`。
- Order 发布给 Fulfilment 的初始事件示例见 `contracts/events/order-confirmed.v1.example.json`。
- Gateway 生成或转发 `X-Correlation-ID`。后续服务调用和事件应继续携带同一个值。
- 示例用于帮助成员并行开发；字段需要调整时，相关成员在同一个 PR 中同步修改示例和实现即可。

## Sprint 3 — Member 3: Menu Factory and inventory

The existing merchant dish endpoints retain their URLs and authorization rules.
POST/PUT now accept `itemType` (`Standard`, `Bundle`, `Promotional`; default `Standard`),
`bundleSize` (Bundle only, integer >= 2), and `promotionStartsAtUtc` / `promotionEndsAtUtc`
(Promotional only, ISO 8601 instants with start < end). Inapplicable settings are rejected
with an English `400` message. Existing dishes migrate to `Standard` without changing stock.
Responses include these fields plus `isAvailable`: stock > 0 and, for promotions,
start <= server UTC time < end. A bundle reserves one unit per complete bundle;
component-level recipe inventory is not implemented in this sprint.

Cart add/update rejects a promotional item outside its availability window with `400`.
Order creation reserves stock atomically inside the existing order transaction;
missing dishes return `404`, unavailable/insufficient stock or invalid quantities return `400`.
All item reservations roll back if any item fails. Repeated identical internal reservation
calls for one order do not deduct again; a changed/released reservation is rejected.
This is inventory-level idempotency, **not** payment or order-creation idempotency.
Pending-order cancellation releases the whole reservation; merchant rejection releases
only that merchant's items. Repeated releases do not restore twice. Pre-migration orders
without a ledger retain the existing restoration path. Orders' existing hard-deletion
behavior on cancellation is unchanged and cascades their reservation rows.

The reservation ledger is internal, not a new public API. Database constraints prohibit
negative dish inventory and non-positive reservation quantities. A dish with retained
reservation history cannot be hard-deleted (`404` with an English explanation);
merchants can set inventory to zero to stop new purchases. This does not introduce a
soft-delisting feature or change other members' fulfilment states or pricing rules.

前后端并行开发的唯一依据。改接口先改这个文件，并在 PR 里说明。

## 通用响应格式

所有接口统一返回：

```jsonc
{
  "success": true,
  "data": { /* 业务数据，失败时为 null */ },
  "message": null            // 失败时为错误提示字符串
}
```

HTTP 状态码：成功 `200`（创建 `201`）、参数错误 `400`、未找到 `404`、服务端异常 `500`。

## Iteration 2 认证与授权

### 顾客注册

`POST /api/auth/customer/register`，请求体：`username`（3–50 字符，全局唯一且忽略大小写）、`password`（8–100 字符）、`displayName`（必填，最多 50 字符）、`phoneNumber`（8–11 位数字）。成功返回 `201`，`data` 包含 `accountId`、`customerId`、`username`、`displayName`；重复用户名返回 `409`。

### 统一登录

`POST /api/auth/login`，请求体：`username`、`password`。顾客和商家使用同一接口。成功返回 `200`，`data` 包含 `accessToken`、`expiresAtUtc`、`accountId`、`role`、`profileId`；凭据错误返回 `401`。JWT 包含 `sub`（Account ID）、`role` 和 `profile_id`，密钥从 `Jwt__Key` 环境变量或部署 Secret 注入，长度至少 32 字节。

### RBAC 与资源归属

受保护接口使用 `Authorization: Bearer <token>`。未携带有效 token 返回 `401`，角色无权访问返回 `403`。商家菜品和满减活动的写接口只允许 `Merchant`；顾客地址和订单接口只允许 `Customer`。服务端以 JWT 的 `profile_id` 为身份依据，请求中的 `merchantId` / `userId` 仅为旧客户端兼容字段，不能改变访问主体。商家菜品与满减列表继续允许匿名浏览。

## Sprint 2：顾客购物车

所有购物车接口均要求 `Customer` JWT。顾客身份只取 JWT 的 `profile_id`；请求体不接受 `userId`、`merchantId`、价格或优惠金额。服务端从菜品、商家和 `SpecialOffer` 实时计算金额，按商家分组，并在每组中自动应用符合门槛的最大满减金额。

### 查询购物车

```
GET /api/cart
```

返回的 `data` 包含 `merchants`（每个商家有 `items`、`subtotal`、`discount` 和 `total`）、以及全车 `totalCount`、`subtotal`、`discount` 和 `total`。每个项目包含 `id`（购物车项 ID）、`dishId`、`dishName`、`unitPrice`、`dishNum` 和 `lineTotal`。

### 添加和修改商品

```
POST /api/cart/items
PUT  /api/cart/items/{cartItemId}
```

POST 请求体为 `{ "dishId": 12, "dishNum": 1 }`。相同顾客和菜品会合并为一项并累加数量。PUT 请求体为 `{ "dishNum": 3 }`，将数量设为该绝对值。数量须为正整数且不得超过菜品当前库存；菜品不存在返回 `404`，库存不足返回 `400`。成功均返回最新的完整购物车快照（POST 为 `201`，PUT 为 `200`）。

### 删除商品或清空

```
DELETE /api/cart/items/{cartItemId}
DELETE /api/cart/merchants/{merchantId}
DELETE /api/cart
```

依次用于删除一项、清空一个商家的购物车项和清空整个购物车；成功返回最新的完整购物车快照。购物车项只允许其所属顾客操作，其他顾客访问同一 ID 返回 `404`。

## US-08 商家注册

`POST /api/merchant/register`，请求体：`username`（3–50 字符，唯一）、`password`（6–100 字符）、`merchantName`（必填）、`merchantAddress`（必填）、`contact`（必填）、`dishType`（可选）、`timeForOpenBusiness` 和 `timeForCloseBusiness`（当天秒数，0–86399）、`walletPassword`（6–100 字符）。

成功返回 `201` 和统一响应，`data` 仅含 `id`、`username`、`merchantName`；重复用户名返回 `409`，无效输入返回 `400`。密码及钱包密码仅以哈希值存储，不在响应中返回。钱包与优惠券类型初始化为 0。现有演示商家没有注册账号字段，仍可用于原有菜品和活动接口。

## 第一周：商家创建菜品

### 创建菜品

```
POST /api/merchant/dishes
Content-Type: application/json
```

请求体：

| 字段 | 类型 | 必填 | 规则 |
|---|---|---|---|
| merchantId | int | 否 | 旧客户端兼容字段；实际商家身份取自 JWT `profile_id` |
| name | string | 是 | 1–50 字符 |
| price | decimal | 是 | > 0，最多两位小数 |
| category | string | 否 | ≤ 20 字符 |
| imageUrl | string | 否 | 合法 URL，留空用默认图 |
| inventory | int | 是 | ≥ 0 |

响应 `201`：

```json
{ "success": true, "data": { "id": 12, "name": "宫保鸡丁", "price": 28.00, "category": "川菜", "imageUrl": "https://...", "inventory": 100 }, "message": null }
```

响应 `400`（校验失败，示例）：

```json
{ "success": false, "data": null, "message": "price 必须大于 0" }
```

响应 `404`（商家不存在）：

```json
{ "success": false, "data": null, "message": "商家不存在" }
```

### 查询某商家的菜品列表（列表页需要）

```
GET /api/merchant/{merchantId}/dishes
```

响应 `200`：`data` 为菜品数组，元素结构同上。

> 鉴权第一周先跳过，`merchantId` 直接从请求里传。后续再引入登录态。

### 编辑菜品（US-03）

```
PUT /api/merchant/dishes/{dishId}
```

请求体同创建但不含 `merchantId`。商家身份由 JWT `profile_id` 确定，用于校验菜品归属。响应结构同创建；`404` 覆盖「菜品不存在」和「菜品不属于当前商家」两种情况。

### 下架/删除菜品（US-04）

```
DELETE /api/merchant/dishes/{dishId}
```

响应 `200`：`{ "success": true, "data": null, "message": null }`。`404` 同上。当前阶段的“下架”使用删除接口实现，后续接入菜品状态字段后可改为软删除。

## 第一周：商家满减活动

> 实体为 `SpecialOffer`：`merchantId`、`minPrice`（满减门槛）、`amountRemission`（减免金额）。以下接口按此契约落地。

### 新增满减活动（US-05）

```
POST /api/merchant/special-offers
```

请求体：`merchantId`（必填，商家须存在）、`minPrice`（必填，> 0，最多两位小数且不超过 `9999999999999999.99`）、`amountRemission`（必填，> 0，最多两位小数且不超过 `9999999999999999.99`，并且 < `minPrice`）。响应结构同菜品创建。

### 编辑 / 删除满减活动（US-06）

```
PUT    /api/merchant/special-offers/{offerId}
DELETE /api/merchant/special-offers/{offerId}
```

PUT 请求体只包含 `minPrice` 和 `amountRemission`，商家身份由 JWT `profile_id` 确定；规则同新增，且更新不能改变活动所属商家。`404` 覆盖不存在 / 不属于当前商家。

### 查看某商家的满减活动列表（US-07）

```
GET /api/merchant/{merchantId}/special-offers
```

响应 `200`：`data` 为活动数组。


## Sprint 4：管理员后端（Administrator）

> 所有 `/api/admin/*` 接口要求 `Authorize(Roles = Administrator)`：匿名返回 `401`，非管理员角色（Customer/Merchant/Rider）返回 `403`。管理员身份与操作者（actor）取自 JWT，不信任请求参数。敏感操作写审计日志（member3 的 Audit Log）。
> 内置管理员账号 `admin` **没有可用的默认密码**（数据库中的密码哈希是不可用值，任何密码都无法登录）。启用方式：非生产环境由 `Seed__Password` 设置（与其它演示账号共用）；其它环境由运维设置环境变量 `Admin__InitialPassword`（至少 12 个字符，只会在账号尚未启用时生效一次，不会覆盖已设置的密码）。

### 账号查询
```
GET /api/admin/accounts?role={role}&page={page}&pageSize={pageSize}
```
`role` 可选，按角色筛选（Customer/Merchant/Rider/Administrator）。分页默认 `page=1`、`pageSize=20`（上限 100）。响应 `200`：`data` 为 `PagedResult`，含 `items`（`id`、`username`、`role`、`profileId`、`isActive`、`createdAtUtc`）、`page`、`pageSize`、`totalCount`、`totalPages`。

### 启用 / 停用账号
```
PUT /api/admin/accounts/{accountId}/status
```
请求体：`{ "isActive": true|false }`。停用后该账号即使凭据正确也无法登录。`404`：账号不存在。

### 受控调整账号角色
```
PUT /api/admin/accounts/{accountId}/role
```
请求体：`{ "role": "Customer|Merchant|Rider|Administrator" }`。角色非法返回 `400`；账号不存在返回 `404`。

### 查询平台优惠券
```
GET /api/admin/coupons
```
响应 `200`：`data` 为优惠券数组（`id`、`code`、`name`、`minimumSpend`、`discountAmount`、`startsAtUtc`、`expiresAtUtc`、`isActive`）。

### 新建优惠券
```
POST /api/admin/coupons
```
请求体：`code`（必填，唯一，≤40）、`name`（必填，≤100）、`minimumSpend`（≥0）、`discountAmount`（> 0）、`startsAtUtc`、`expiresAtUtc`（须晚于 `startsAtUtc`）、`isActive`。响应 `201`。`409`：优惠券码重复；`400`：日期区间非法或金额无效。

### 修改优惠券
```
PUT /api/admin/coupons/{couponId}
```
请求体：`name`、`minimumSpend`、`discountAmount`、`startsAtUtc`、`expiresAtUtc`（不含 `code`，码不可改）。规则同新建。`404`：优惠券不存在。

### 启用 / 停用优惠券
```
PUT /api/admin/coupons/{couponId}/status
```
请求体：`{ "isActive": true|false }`。`404`：优惠券不存在。

### 管理员订单查询
```
GET /api/admin/orders?userId={userId}&status={status}&fromUtc={fromUtc}&toUtc={toUtc}&page={page}&pageSize={pageSize}
```
所有筛选参数可选：按用户、订单状态（Pending/Paid/.../Cancelled）、时间范围过滤。响应 `200`：`data` 为 `PagedResult`，`items` 含 `orderId`、`userId`、`status`、`price`、`orderTimestamp`、`itemCount`。只读查询，不修改订单，不影响已支付订单的价格快照。


## SWE5001 Identity Service（member2）

> 独立 Identity 微服务（`services/identity/JiKeDa.Identity.Api`），拥有 `identity_db`。通过 Gateway 暴露 `/api/auth/*` 与 `/api/accounts/*`。本地直连端口 `5101`。所有服务共享同一 `Jwt__Key` 签发/验证 token；下游服务各自强制 `[Authorize]`，Gateway 的 JWT 验证为纵深防御，不是唯一授权点。

### JWT 契约（全团队对齐）
- 签名：HMAC-SHA256，`Jwt__Key`（≥32 字节，来自环境变量/部署 Secret），`Issuer=JiKeDa`、`Audience=JiKeDa.Client`。
- Claims：`sub`（Account Id）、`nameid`（Account Id）、`name`（用户名）、`role`（Customer/Merchant/Rider/Administrator）、`profile_id`、`jti`。
- 其他服务验证时需 `RoleClaimType = "role"`、`MapInboundClaims = false`，与本服务一致。

### 登录
```
POST /api/auth/login
```
请求体：`username`、`password`。成功 `200`，`data` 含 `accessToken`、`expiresAtUtc`、`accountId`、`role`、`profileId`；凭据错误或**账号被停用**返回 `401`。

### 顾客注册
```
POST /api/auth/customer/register
```
请求体：`username`（3–50，唯一忽略大小写）、`password`（8–100）、`displayName`（≤50）、`phoneNumber`（8–11 位数字）。成功 `201`（`accountId`/`customerId`/`username`/`displayName`）；用户名重复 `409`；必填缺失 `400`。
> 范围说明（member2）：本 Sprint 只实现顾客自助注册；商家/骑手账号由种子或管理员创建，其业务资料由 Catalog/Fulfilment 拥有。Identity 只持有账号 + `ProfileId` 引用，不保存商家/骑手业务资料。

### 账号管理（仅 Administrator）
```
GET /api/accounts?role={role}&page={page}&pageSize={pageSize}
PUT /api/accounts/{accountId}/status      body: { "isActive": true|false }
PUT /api/accounts/{accountId}/role        body: { "role": "Customer|Merchant|Rider|Administrator" }
```
匿名 `401`；非 Administrator（Customer/Merchant/Rider）`403`。查询返回 `PagedResult`（items + page/pageSize/totalCount/totalPages）。停用账号后该账号无法登录。角色调整受 `(Role, ProfileId)` 唯一约束限制。`404`：账号不存在；`400`：角色非法。

### 种子账号（仅 staging/测试，非生产密钥）
| 用户名 | 角色 | 密码 |
|--------|------|------|
| `admin` | Administrator | `Passw0rd!123` |
| `merchant1` | Merchant | `Passw0rd!123` |
| `rider1` | Rider | `Passw0rd!123` |
| `customer1` | Customer | `Passw0rd!123` |

## SWE5001 Catalog Service（member3）

> 独立 Catalog 微服务（`services/catalog/JiKeDa.Catalog.Api`），拥有 `catalog_db`：商家资料、菜品、满减活动和库存预留台账。本地直连端口 `5102`。只验证 Identity 签发的 JWT（共享 `Jwt__Key`），不签发 token。
>
> 商家账号（Identity）的 `profile_id` 即 Catalog 的商家 `id`。种子数据与 Identity 对齐：`merchant1` → 商家 `1`（Demo Merchant），另有商家 `2`；菜品 `1`–`6`、满减 `1`–`2`。新建记录的 id 从 `1000` 开始，不会与种子冲突。

### 公开查询（匿名，经 Gateway）
```
GET /api/merchants                              商家列表
GET /api/merchants/{merchantId}                 商家详情
GET /api/merchants/{merchantId}/dishes          商家菜单（含 isAvailable）
GET /api/merchants/{merchantId}/special-offers  满减活动（只读）
GET /api/catalog/dishes/{dishId}                单个菜品（购物车取价格用）
```
商家或菜品不存在返回 `404`。菜品字段与单体一致：`id`、`merchantId`、`name`、`price`、`category`、`imageUrl`、`inventory`、`itemType`（Standard/Bundle/Promotional）、`bundleSize`、`promotionStartsAtUtc`、`promotionEndsAtUtc`、`isAvailable`（有库存且在促销时间窗内）。

### 商家菜品管理（仅 Merchant，经 Gateway）
```
GET    /api/catalog/merchant/dishes             本商家全部菜品
POST   /api/catalog/merchant/dishes             新建，201
PUT    /api/catalog/merchant/dishes/{dishId}    修改
DELETE /api/catalog/merchant/dishes/{dishId}    删除
```
请求体与单体相同，但**不再接受 `merchantId`**：商家身份只取 JWT `profile_id`。匿名 `401`，非 Merchant `403`；菜品不存在或属于其他商家 `404`；校验失败 `400`（英文 message，菜单类型规则沿用 Menu Factory）；有预留记录的菜品不能删除，返回 `409`（可把库存改为 0 停售）。

### 库存预留与释放（服务间内部接口，不经 Gateway）
```
POST /internal/catalog/reservations
POST /internal/catalog/reservations/{reservationId}/release[?merchantId=]
```
- 调用方（Order）在 Compose 网络内访问 `http://catalog:8080`，使用自己签发的短期服务 token（`role=Service`，共享 `Jwt__Key`）；无 token 返回 `401`。Gateway 没有 `/internal/*` 路由，外部请求返回 `404`。
- `reservationId` 由调用方生成（建议用 checkout 的幂等键或订单号），1–64 位字母、数字或 `. _ : -`。
- 一次预留内所有菜品要么全部成功，要么全部回滚。同一菜品多行会合并数量。
- 成功 `201`；同一 `reservationId` 和相同菜品数量重放返回 `200` 且 `replayed: true`，不会重复扣减；同一 id 但菜品不同或已释放返回 `409`（`RESERVATION_CONFLICT`）。
- 菜品不存在 `404`（`DISH_NOT_FOUND`）；库存不足 `400`（`INSUFFICIENT_STOCK`）；促销菜品不在时间窗内 `400`（`UNAVAILABLE`）。
- 响应返回每行的 `merchantId`、`dishName`、`unitPrice`（预留时的价格快照）、`lineTotal` 和 `totalAmount`，Order 可直接用于建单和 `OrderConfirmed` 事件。
- 释放：恢复所有未释放行的库存；带 `merchantId` 时只释放该商家的行（商家拒单）。重复释放不会重复恢复，返回 `200` 且 `alreadyReleased: true`；不存在返回 `404`。
- 并发保证（PostgreSQL 测试覆盖）：库存扣减与检查在同一条 `UPDATE` 中完成，30 个并发请求抢 20 份库存恰好成功 20 个；同一 `reservationId` 并发重放只扣一次；数据库 `CHECK` 约束禁止负库存。

### 暂不包含
满减活动的增删改（仍在单体）、商家注册、菜品图片上传、把单体 `takeout` 库的历史数据迁入 `catalog_db`。

## SWE5001 Order Service（member4）

> 独立 Order 微服务（`services/order/JiKeDa.Order.Api`），拥有 `order_db`：购物车、收货地址、平台优惠券与顾客配额、checkout 尝试、订单、支付和 outbox。本地直连端口 `5103`。菜品名称、价格、商家和满减全部来自 Catalog，客户端不能传价格。
>
> 顾客 token 的 `profile_id` 即顾客 id。种子与 Identity 对齐：`customer1` → 顾客 `1`，拥有地址 `1`、`WELCOME5`（3 次，满 20 减 5）和 `SAVE12`（1 次，满 80 减 12）。

### 顾客接口（仅 Customer，经 Gateway）
```
GET    /api/cart                         购物车（按商家分组，含实时价格和满减）
POST   /api/cart/items                   { dishId, quantity }，同一菜品累加，201
PUT    /api/cart/items/{cartItemId}      { quantity }
DELETE /api/cart/items/{cartItemId}
DELETE /api/cart/merchants/{merchantId}
DELETE /api/cart                         清空
GET    /api/pricing/coupons              我可用的优惠券
POST   /api/pricing/quote                { couponCode? } 预览金额
GET    /api/addresses                    我的收货地址
POST   /api/addresses                    { address, houseNumber?, contactName, phoneNumber }，201
POST   /api/checkout                     Idempotency-Key 头 + { addressId, couponCode?, needUtensils, paymentMethod }
GET    /api/orders                       我的订单
GET    /api/orders/{orderId}             他人的订单返回 404
```
匿名 `401`，非 Customer `403`。Catalog 不可用时返回 `503`（`code: ServiceUnavailable`），可重试。

### 下单（跨服务，替代单体的单一事务）
1. 用 `(顾客, Idempotency-Key)` 插入 checkout 尝试记录（唯一索引），它就是幂等锁：只有插入成功的请求继续，并发重复请求返回 `409 CheckoutInProgress` 或已完成订单。
2. 向 Catalog 预留库存，`reservationId = chk-{尝试 id}`。
3. 用预留返回的价格快照计价：先按商家套满减，再在减后金额上用一张平台券，最后加配送费 5。
4. 本地一个事务：原子领取购物车行、原子扣减优惠券次数、模拟支付、建订单和订单行（价格快照）、写 `OrderConfirmed` 到 outbox、把尝试标为完成。

- 成功 `201`；同一 key 同一请求重放返回 `200` 且 `replayed: true`（不再预留、不再扣款）；同一 key 不同请求 `409 IdempotencyKeyReused`。
- 第 3、4 步任何失败（拒付 `402`、优惠券不可用 `400`、购物车被改 `409`）都会**释放 Catalog 预留**并删除尝试记录，顾客可以用同一个 key 重试；购物车和优惠券保持原样。
- 库存不足 `409 InsufficientStock`；菜品不可用 `409 ItemUnavailable`；地址不属于本人 `404`。
- Catalog 不可达：返回 `503`，并在 outbox 里记一条防御性的释放（预留请求可能已经到达 Catalog）。后台 worker 每 10 秒重试待释放项；卡住超过 5 分钟的尝试（例如进程中途重启）会被放弃并释放库存。释放在 Catalog 端是幂等的，多副本同时运行也安全。

### 事件（给 Fulfilment，member5）
```
GET /internal/order/events?after={lastId}&limit={1..200}     （不经 Gateway，需要任意有效 JWT）
```
按 id 递增返回 `OrderConfirmed` 事件，字段见 `contracts/events/order-confirmed.v1.example.json`（含 `addressId`、`needUtensils`、商品明细、`deliveryFee`、`totalAmount`、`correlationId`）。Fulfilment 记住处理过的最后一个 id 轮询即可；以后接入消息队列时，outbox 改为由 worker 发布，事件格式不变。

### Fulfilment and Delivery（member5）

Fulfilment 使用自己的 `fulfilment_db` 保存 `OrderConfirmed` 的只读投影、每个商家的履约状态、骑手资料和配送状态；绝不读取 Order、Catalog 或 Identity 的数据库。启动后的 worker 使用服务 JWT 轮询上面的内部事件 feed，并在同一事务内保存投影及最后处理的 event id，因此重试不会重复建单。

| 路由（均经 Gateway） | 角色 | 说明 |
|---|---|---|
| `GET /api/fulfilment/orders` | Merchant | 当前商家的待处理/历史订单 |
| `PUT /api/fulfilment/orders/{orderId}/status` | Merchant | `Accept` → `StartPreparing` → `MarkReady`；`Reject` 需要 `reason` |
| `GET /api/riders/available` | Rider | 所有商家已备餐且尚未被认领的订单 |
| `GET /api/riders/deliveries` | Rider | 当前骑手的配送任务 |
| `POST /api/deliveries/{orderId}/accept` | Rider | 原子认领任务；重复认领返回 `409` |
| `PUT /api/deliveries/{orderId}/status` | Rider | `PickUp` → `StartDelivering` → `Complete` |

订单只有在全部商家 `MarkReady` 后才能被认领。骑手身份来自 JWT `profile_id`，服务只接受本人任务的状态操作。当前 `OrderConfirmed` 合同只提供 `addressId`，本地演示使用 `ZoneA`；在引入地址读取合同前，Fulfilment 不推断或复制住址详情。

### 测试
SQLite 上 18 个接口测试（使用与 Catalog 规则一致的内存 Catalog 替身，可模拟宕机和释放失败），PostgreSQL 上 3 个并发测试：10 个相同 key 并发只产生 1 个订单、只预留 1 次；8 个不同 key 抢同一购物车和一次性优惠券，只有 1 单成功，其余预留全部释放，优惠券只扣一次。

### 暂不包含
管理员优惠券管理与发放（仍在单体）、订单取消与退款、商家接单和骑手配送（Fulfilment，member5）。


## SWE5001 cloud presentation revision (2026-10-20)

- Public business routes and JSON bodies remain compatible with `main`.
- Every API response includes `X-Service-Instance` and `X-Trace-ID`; Catalog responses through its router also expose `X-Catalog-Instance` for replica evidence. Correlation IDs continue across services.
- Gateway ingress applies a configurable request budget (default 500 requests/second/source). Health probes are exempt. A rejected request returns HTTP 429. On the demo VM the edge proxy shares a source budget; this is not a per-end-user quota.
- With `Messaging__FullyQualifiedNamespace` configured, Order publishes `OrderConfirmed` from its transactional outbox to the private Service Bus queue `order-confirmed`. Fulfilment acknowledges only after its projection and per-event inbox receipt commit. Duplicates and out-of-order delivery do not create additional business orders.
- `GET /internal/messaging/status` exists only on the private Order port, is not routed by Gateway, and returns active count, dead-letter count and observation timestamp for the local controller. No cloud credentials are returned.
- `QueueReplayCount=1..1000` is an operator-only container invocation for explicitly labelled duplicate replay tests; it is not a public endpoint.
- Runtime replicas use `Database__MigrateOnStartup=false`; the release procedure runs one `MigrateOnly=true` job per service first. Local default migration behaviour remains compatible.
