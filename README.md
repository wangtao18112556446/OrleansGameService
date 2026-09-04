# Orleans MMO Game Service

`net10.0` MMO 服务端 MVP。HTTP 提供本地账号和角色接口，WebSocket 使用 MessagePack 发送服务器权威的实时指令；Orleans 管理账号、角色、区域和怪物 Grain。

## Run

安装 Docker Desktop 后执行 `docker compose up --build`，Gateway 在 `http://localhost:8080` 提供服务。Compose 会从固定的 Orleans `v10.2.1` 标签取得官方 PostgreSQL 集群和 Grain 持久化脚本；数据库首次启动时由应用创建 Identity、配置版本、流水和背包投影表。

开发时先注册 `POST /api/auth/register`，再登录 `POST /api/auth/login` 获取 Bearer JWT；创建角色可提交 `{ "name": "Rin", "classId": "adventurer" }`（当内容包只有一个职业时 `classId` 可省略），再调用 `/api/characters/{id}/enter`（`{ "zoneId": "starter-plains" }`）进入当前内容版本的区域实例。WebSocket 连接为 `/ws?characterId={id}`，需携带 Bearer JWT，二进制帧为 `RealtimeEnvelope`。

协议 V1 保留移动、普攻和任务命令。协议 V2 新增 `UseSkill`、`Equip`、`Unequip` 与 `InteractNpc` 消息，并返回包含职业、资源、装备、Buff、已学技能和 NPC 的 V2 快照。内容包只可使用服务器内置的伤害、资源和 Buff 效果类型；导入后的内容版本不可覆盖，已运行区域会固定使用其创建时的版本。

`Content/game-content.v1.json` 是版本化的首个内容包。新内容通过管理员 `POST /api/admin/content/import` 导入；已运行区域固定其内容版本。

开发环境显式使用 Orleans 内存存储；Production/Compose 使用 PostgreSQL ADO.NET 集群和 Grain 存储。生产环境必须通过环境变量替换 `Jwt__SigningKey`。
