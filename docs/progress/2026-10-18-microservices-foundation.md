# SWE5001 微服务基础骨架进度报告

日期：2026 年 10 月 18 日
分支：`codex/microservices-foundation`

## 本次目标

在不删除或改写现有单体业务的前提下，建立后续五名成员可以共同使用的本地微服务基础，并完成 Member 5 的 Fulfilment and Delivery 迁移；不开始 Azure 部署。

## 已完成内容

- 新增 YARP Gateway，以及 Identity、Catalog、Order、Fulfilment and Delivery 四个独立 ASP.NET Core 8 项目。
- 新增根目录 `JiKeDa.Platform.sln`，五个新项目可以统一 restore 和 build。
- 每个服务提供独立 `/health`；Gateway 提供自身及四个下游服务的 health 路由。
- Gateway 已预留认证、账号、菜单、商家、购物车、结算、订单、履约、配送和骑手路由。
- 新增 `docker-compose.microservices.yml`，与原单体 `docker-compose.yml` 并存，不影响现有系统。
- 本地 PostgreSQL 使用一个容器和四个逻辑数据库：`identity_db`、`catalog_db`、`order_db`、`fulfilment_db`，各自使用独立账号和连接字符串。
- 新增五个 Dockerfile 和 `.dockerignore`，五个镜像均已成功构建。
- 新增基础 smoke test，通过 Gateway 检查 Gateway 和四个服务的健康状态及 `X-Correlation-ID`。
- 更新 `docs/api-contract.md`，记录本地端口、Gateway 路由、数据库归属和第一批跨服务示例。
- 新增 Catalog 库存预留示例和 `OrderConfirmed` 事件示例，供 Member 3、4、5 后续开发时统一字段。
- Member 5 已在 Fulfilment 中实现 `OrderConfirmed` 幂等消费、商家接单/备餐、骑手原子认领及取餐到送达的状态机，并添加 NUnit 流程测试。

## 当前约定

- 现有单体继续保留并可运行；业务采用逐个模块迁移方式，不一次性删除旧代码。
- 新前端和后续迁移页面统一通过 Gateway 访问服务；5101 至 5104 端口只用于本地调试。
- 每个新服务只访问自己的数据库，不共享 DbContext，也不直接查询其他服务的表。
- `contracts/` 只保存 API 和事件示例，不放 EF 实体或共享业务模型。
- 接口字段需要调整时，相关成员在同一个 PR 中同步修改实现、`contracts/` 和 `docs/api-contract.md`，不需要额外审批流程。
- 所有新增的前端文字和用户可见信息使用英语。

## 验证结果

- `dotnet build JiKeDa.Platform.sln --configuration Release --no-restore`：通过，0 warning，0 error。
- `docker compose -f docker-compose.microservices.yml config --quiet`：通过。
- Gateway、Identity、Catalog、Order、Fulfilment 五个容器镜像：全部构建成功。
- 隔离 Compose 测试栈：全部容器达到 healthy。
- Gateway health smoke：Gateway 和四个下游服务全部通过。
- 数据库检查：四个逻辑数据库全部创建成功。
- 测试结束后已删除隔离测试容器、网络和临时数据卷，不影响现有单体环境。

## 尚未包含

- Fulfilment 目前通过 Order 的 HTTP 事件 feed 轮询，不是 Service Bus；后续可替换 transport 而不改变事件格式。
- 其余尚未迁移的业务仍按模块逐步迁移；不共享 DbContext 或数据库。
- 尚未修改现有 Vue 页面，也没有新增用户可见页面。
- 尚未开始 Azure、Bicep、云端 CI/CD 和扩缩容配置。

## 建议下一步

1. Member 2 从现有单体迁移最小登录和 JWT 到 Identity Service。
2. Member 3 同时迁移公开菜单查询和基础库存到 Catalog Service。
3. Identity 和 Catalog 稳定后，Member 4 开始迁移购物车与 checkout，并按示例调用库存预留接口。
4. Member 5 已接入 Fulfilment 消费和履约流程；后续接入地址读取合同或消息总线时，保持现有事件幂等与服务边界。
