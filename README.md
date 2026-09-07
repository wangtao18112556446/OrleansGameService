# Orleans MMO Game Service

`net10.0` MMO 服务端 MVP。HTTP 提供本地账号和角色接口，WebSocket 使用 MessagePack 发送服务器权威的实时指令；Orleans 管理账号、角色、区域和怪物 Grain。

## Run

安装 Docker Desktop 后执行 `docker compose up --build`，Gateway 在 `http://localhost:8080` 提供服务。Compose 会从固定的 Orleans `v10.2.1` 标签取得官方 PostgreSQL 集群和 Grain 持久化脚本；数据库首次启动时由应用执行 EF Core 迁移，创建 Identity、配置版本、流水和累计奖励投影表。

开发时先注册 `POST /api/auth/register`，再登录 `POST /api/auth/login` 获取 Bearer JWT；创建角色可提交 `{ "name": "Rin", "classId": "adventurer" }`（当内容包只有一个职业时 `classId` 可省略），再调用 `/api/characters/{id}/enter`（`{ "zoneId": "starter-plains" }`）进入当前内容版本的区域实例。WebSocket 连接为 `/ws?characterId={id}`，需携带 Bearer JWT，二进制帧为 `RealtimeEnvelope`。

协议 V1 保留移动、普攻和任务命令。协议 V2 新增 `UseSkill`、`Equip`、`Unequip` 与 `InteractNpc` 消息，并返回包含职业、资源、装备、Buff、已学技能和 NPC 的 V2 快照。内容包只可使用服务器内置的伤害、资源和 Buff 效果类型；导入后的内容版本不可覆盖，已运行区域会固定使用其创建时的版本。

`Content/game-content.v1.json` 是版本化的首个内容包。新内容通过管理员 `POST /api/admin/content/import` 导入；已运行区域固定其内容版本。

开发环境显式使用 Orleans 内存存储；Production/Compose 使用 PostgreSQL ADO.NET 集群和 Grain 存储。生产环境必须通过环境变量替换 `Jwt__SigningKey`。

## 第一轮：持久化与故障恢复

角色 Grain 状态是背包、任务、装备和技能资源的权威来源。击杀会先保存攻击意图，再调用怪物；怪物将生命值与攻击回执一次保存。角色拿到结果后，将奖励、任务进度、资源消耗、操作回执和待投递奖励一起保存，最后幂等同步 PostgreSQL 账本。账本失败时保留待投递记录，下一次角色请求或重激活时继续恢复。`PlayerItemProjections` 表示累计发奖量，不包含初始物品或装备转移，不能作为当前背包查询来源。

操作号必须非空且不超过 128 个字符。相同角色重复提交相同命令返回原业务结果与当前快照；同号不同命令返回 `operation_conflict`，非法操作号返回 `invalid_operation_id`。旧状态中仅有操作号、没有指纹的请求返回 `operation_expired`，客户端应刷新状态而非改号盲目重发。服务端临时故障返回 `server_error`，客户端必须使用相同操作号重试。不同角色可安全使用相同操作号。MessagePack V1/V2 字段编号保持不变，新增的序列化标记用于 Orleans 内部通信。

区域切换先持久化意图并加入目标，再提交角色归属和清理原成员关系。容量不足时保留原区域；跨步骤故障会在重激活或下一次请求时恢复。切换过程中成员列表可能短暂包含两个区域，角色命令在恢复完成后才继续执行。

本轮紧凑操作回执不再按 512 条任意淘汰，避免旧请求再次扣血或发奖。上线大规模运行前仍需引入有服务端期限的操作协议及回执归档，控制长期存储增长。升级前已发生的旧版漏奖缺少恢复意图，需单独审计账本与角色状态，不能由新发件箱自动修复。

## 数据库迁移

`InitialGameSchema` 基线支持空数据库、仅有 Orleans 系统表的数据库，以及当前仓库旧 MVP 业务表；创建缺失对象并保留现有数据。任意人工修改过的表结构不在自动接管范围。基线不能自动向下回滚，以免误删接管的玩家数据。后续修改模型使用常规迁移，不再调用 `EnsureCreatedAsync()`。

```powershell
dotnet tool restore
dotnet ef migrations has-pending-model-changes --project src/GameServer.Infrastructure
dotnet ef migrations add <MigrationName> --project src/GameServer.Infrastructure --output-dir Persistence/Migrations
```

迁移工具从 `ConnectionStrings__Postgres` 读取连接。Compose 初始化使用 libpq 的 `PGHOST` 等变量；SQL 下载失败或执行失败会阻止 Gateway 启动，完整的已有 Orleans 架构可重复启动。

## 验证

```powershell
dotnet build OrleansGameService.slnx
dotnet test OrleansGameService.slnx
# 需要 Docker Desktop 和 PowerShell 7：
pwsh -File scripts/Test-Integration.ps1
```

普通测试包含真实 Orleans 测试集群、存储提交前/后故障注入、奖励重投、区域切换恢复和 V1/V2 固定报文兼容检查。真实 PostgreSQL 与 HTTP/WebSocket 测试在未配置依赖时明确显示为跳过。

集成脚本启动独立 `orleans-game-tests` Compose 项目，使用 PostgreSQL `15432`、Redis `16379` 和 Gateway `18080` 端口。它验证迁移及账本并发，执行注册、登录、创建角色、入区、接任务、击杀、领奖，再重启 Gateway 验证账号、角色状态和操作回执，最后重跑 Orleans 初始化。测试账号暂存在被 Git 忽略的 `TestResults/smoke-state.json`，CI 不上传该文件。完成后可用 `docker compose -p orleans-game-tests down` 停止测试实例，保留测试数据卷。

GitHub Actions 执行构建、单元/Grain 测试和完整 Compose 验证，并保存测试报告与容器日志。后续迭代顺序为：按服务端时间校验移动及补齐交互约束 → 会话、断线恢复和多人广播 → 怪物 AI、成长、内容多节点同步与容量验证。
