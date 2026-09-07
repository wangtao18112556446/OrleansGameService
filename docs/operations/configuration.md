# 运行配置

配置继续使用 ASP.NET Core 默认提供者：`appsettings.json`、环境专属文件、环境变量和命令行，后加载的来源覆盖前者。环境变量使用双下划线分隔层级，例如 `Jwt__SigningKey`。

| 配置键 | 默认值 | 启动约束 |
| --- | --- | --- |
| `Jwt:Issuer` | 无 | 非空 |
| `Jwt:Audience` | 无 | 非空 |
| `Jwt:SigningKey` | 无 | 至少 32 个 UTF-8 字节；生产环境必须外部注入 |
| `Jwt:AccessTokenLifetime` | `08:00:00` | 大于零 |
| `Jwt:ClockSkew` | `00:01:00` | 不小于零 |
| `Orleans:UseAdoNet` | `true` | 为 true 时必须配置 PostgreSQL 连接 |
| `Orleans:ClusterId` | `orleans-game` | 非空 |
| `Orleans:ServiceId` | `orleans-game` | 非空 |
| `GameServer:Realtime:MaxMessageBytes` | `65536` | 大于零 |
| `GameServer:Realtime:ReceiveBufferBytes` | `8192` | 大于零且不超过消息上限 |
| `GameServer:RateLimit:PermitLimit` | `120` | 大于零 |
| `GameServer:RateLimit:Window` | `00:01:00` | 大于零 |
| `GameServer:Content:BootstrapPath` | `Content/game-content.v1.json` | 非空；相对路径以宿主内容根目录解析 |
| `GameServer:Content:AllowBuiltInFallback` | `true` | Production 默认配置为 false，文件缺失时启动失败 |

`ConnectionStrings:Postgres` 当前同时用于 Identity、内容/账本数据库以及 ADO.NET Orleans 模式，因此始终必需。`ConnectionStrings:Redis` 为空时使用进程内分布式缓存适配器；Redis 目前不阻断就绪。

Options 在宿主启动时统一校验，非法配置不会等到首个玩家请求才失败。`/health` 和 `/health/ready` 的现有行为未因本次配置重构改变；真实依赖就绪检查仍由 OPS-001 跟踪。
