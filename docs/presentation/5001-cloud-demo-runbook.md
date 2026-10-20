# SWE5001 云端汇报与演示手册

适用：Team 3，Member 1 HAO MINGYANG。基于 `feat/presentation-readiness`。最终测试数值、版本及截图以本次交付的验证记录为准，不能拿旧单体的 CD 或压测充当新微服务证据。

## 先用一句话讲清楚

JiKeDa 把顾客、商家、骑手和管理员连接到同一订单流程；5001 重点讲平台边界、服务间协作、独立扩缩容、数据一致性及可重复发布。

## 40 分钟分别讲什么

课程建议时间含每部分问答。Team 3：2026 年 10 月 21 日 10:40–11:20，新加坡时间。

| 部分 | 讲解 + 问答 | 用最简单的话解释要求 | 我们应展示什么 |
|---|---:|---|---|
| Overview | 2 + 1 分钟 | 帮谁解决什么问题、为什么是平台、做多大范围 | 四类用户；统一身份、菜单、订单、履约能力可供应用复用；模拟支付、无 GPS |
| Project Conduct | 1.5 + 0.5 | 现在做到哪里、怎么推进、投入多少 | 真实里程碑、当前遗留问题；个人投入由本人确认，不能从提交次数推算 |
| Logical Architecture | 7 + 2 | 为什么这样拆服务、谁负责什么、怎样协作 | Gateway + Identity/Catalog/Order/Fulfilment；各自数据所有权；REST 与事件；商家是供给方，顾客及菜单应用是消费方 |
| Physical Architecture | 7 + 2 | 真实跑在哪里、网络怎样连、数据怎样存、重点流程怎样实现 | Azure VM、私有容器网络、HTTPS 入口、四逻辑库、Service Bus、ACR、App Insights；以跨服务 checkout 为代表 |
| DevOps | 6 + 1 | 代码到云端怎样自动完成、如何检查、出错怎么办 | PR、CI、CodeQL、镜像门禁、ACR 版本、OIDC、单次迁移、云端 smoke、应用回滚 |
| Demo | 8 + 2 | 当场证明业务及质量属性 | 实际业务、自动扩缩容、k6 性能结果、真实 GitHub 工作流 |

课程用了自动扩缩容作为质量属性演示的例子；团队提案又明确承诺 HTTP 和队列伸缩，因此这次做两种真实触发验证。

## 8 分钟演示顺序

提前打开业务页、Grafana、GitHub 最新成功 CI/CD、Azure 队列和监控。预先完成一次排练并保留原始证据。

1. **0:00–1:30，业务。** 顾客下单；商家接受/备餐/就绪；骑手接单并送达；顾客看 Completed。可由队友在各自窗口操作。说明模拟支付。
2. **1:30–4:00，HTTP 伸缩。** 在 Grafana 显示 Catalog 基线 1、Identity/Order/Fulfilment 各 1；启动 HTTP 压测。真实请求率连续超过阈值后，Catalog 变 2，其余服务不跟着扩。指出请求确实分到两个实例。停止负载后观察回到 1；等待缩容期间继续讲测试结果。
3. **4:00–6:00，消息与队列伸缩。** 运行受控队列演练。短暂停一个消费者制造真实积压，向真实 Service Bus 重放 1000 条带测试标识的现有事件，随后自动增加消费者。展示队列清空、消费者回到 1、业务订单数不增加、死信数 0。明确这是重复投递去重与伸缩实验，不是 1000 笔新订单的吞吐结果。
4. **6:00–7:00，性能。** 打开 k6 原始结果：20 VU，登录/菜单/真实 checkout/重放/跟踪；p95 <2 秒、错误率 <1%，库存精确扣减、订单不重复。讲真实结果，不提前填“通过”。
5. **7:00–8:00，维护与发布。** 打开新微服务的成功 CI/CD，指出版本 SHA、七镜像检查、ACR、云端验证和回滚演练。可现场触发重部署已有版本，并展示预先完成的完整成功记录；构建时长不占满演示。

如果某一步现场网络慢，切到本次真实截图和对应原始日志，明确说明截图来自排练，不冒充实时。

## 操作入口

- 应用：https://jikeda5001-m1-apy24jqzjhch2.eastus2.cloudapp.azure.com/customer/merchants
- Grafana：https://jikeda5001-m1-apy24jqzjhch2.eastus2.cloudapp.azure.com/grafana/d/jikeda-cloud-scaling/
- 审查：https://github.com/Friedriceee/5001/pull/13
- 发布：https://github.com/Friedriceee/5001/actions/workflows/cd.yml
- Azure 资源组：`rg-jikeda5001-m1-demo`

Grafana 用户为 `admin`；密码使用私下交付的演示密码，不放 PPT、报告或 Git。业务账号 customer1、merchant1/2、rider1/2、admin。

在已登录正确 Azure 订阅的操作终端中，从仓库运行：

```sh
python3 scripts/presentation/invoke-demo.py status
python3 scripts/presentation/invoke-demo.py http
python3 scripts/presentation/invoke-demo.py queue
python3 scripts/presentation/invoke-demo.py smoke
python3 scripts/presentation/invoke-demo.py rollback
```

可用 `--az /path/to/az` 指定 CLI。HTTP/queue 会保存完整验证 JSON；不要并行运行伸缩、发布和回滚演练。queue 演练会暂停消费者，应在无其他人下单的排练时段运行。

k6 使用 `scripts/presentation/k6-acceptance.js`。必须私下提供 `PASSWORD`，并显式设 `ALLOW_DEMO_WRITES=true`；会创建带 `accept_` 前缀的合成测试账号、标为 Acceptance 的独立菜品及 20 笔订单，保留测试证据，避免误用真实业务数据。运行时指定 k6 v2.3.0，20 VU、120 秒；API 提交不输出 token。

## 伸缩规则怎样解释

- 每 5 秒采样真实 nginx 请求计数；Catalog 连续 2 次 ≥12 请求/秒，从 1 变 2；连续 6 次 ≤3 请求/秒后缩至 1。
- Fulfilment 观察 Service Bus 实际积压；≥5 条可加到 2，队列空连续 6 次可回到 1。
- 两者都有 60 秒冷却期、最多 2 副本。指标缺失或过期不会当作“空闲”触发缩容。
- 伸缩只调整目标服务，部署与伸缩共享锁。运行时不执行数据库迁移；发布先执行一次迁移。

## 必须讲清的设计取舍

当前是预算内的**单 VM 容器级自动伸缩**，Service Bus/ACR/监控使用 Azure 服务。它证明独立服务的度量驱动伸缩，不会新增 VM，也不能突破该 VM 的 CPU/内存上限。数据库是同一 PostgreSQL 实例的四个逻辑数据库、独立账号。

提案中的 ACA、托管 PostgreSQL、跨主机高可用、完全托管伸缩不是本次部署。不要把单 VM 描述为高可用集群。若老师问生产方案，说明将容器迁入 ACA/Kubernetes，使用托管数据库、异地备份和更细的身份隔离。

应用回滚验证的是旧版本程序仍可在兼容的增量数据库上运行；它不自动撤销已提交订单，也不等同数据库恢复演练。数据库发布前的备份保留在 VM 的私有目录，尚不能代替异机灾备。

## 报告需要同步的地方

现有 `docs/report/project-report.md`、README 和旧幻灯片中“单体/四服务未完成/无云端”的说明已经过时。最终稿应按以下真实状态逐项更新，不能只换一个标题：

- 逻辑视图：四业务服务、YARP、生产者/消费者、REST/事件、各库所有权。
- 物理视图：实际 Azure VM 和网络、ACR、Service Bus、App Insights、Grafana；注明与提案差异。
- 代表用例：先 Catalog 预留库存，再 Order 本地事务与 Outbox，再消息投递和 Inbox 去重；不使用跨库事务。
- 质量属性：记录负载配置、p95/错误率/吞吐、两种副本时间线和缩容、重复投递不增订单、权限/内部路由检查。
- DevOps：放本次微服务 CI/CD 链接、同一版本 SHA 与镜像版本、安全完整扫描和应用回滚记录。
- 项目实施：仅记录 haomingyang / Member 1 的实际工作；时间待本人确认，不编其他成员贡献或工时。
- 限制：单 VM、模拟支付、托管拓扑未落地、是否存在上游未修复漏洞、哪些非核心提案功能未验证。
