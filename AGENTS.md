# 仓库指南

## 项目结构与模块组织

`OrleansGameService.slnx` 用于组织 .NET 10 项目。跨项目依赖应始终由外向内流动：

- `src/GameServer.Contracts/`：公共 DTO、Grain 接口和带版本的实时通信协议。
- `src/GameServer.Domain/`：确定性的游戏规则和属性计算，不应包含基础设施相关内容。
- `src/GameServer.Grains/`：Orleans 账户、角色、区域和怪物等有状态行为的实现。
- `src/GameServer.Infrastructure/`：EF Core 持久化、Redis 缓存、内容加载和 DI 注册。
- `src/GameServer.Gateway/`：ASP.NET Core HTTP/WebSocket 宿主；其中的 `Content/game-content.v1.json` 是初始内容包。
- `tests/GameServer.Tests/`：xUnit 测试。测试应放在其所验证行为的附近，而不是部署代码附近。

## 构建、测试与开发命令

请在仓库根目录运行：

```powershell
dotnet restore OrleansGameService.slnx        # 还原包依赖
dotnet build OrleansGameService.slnx          # 编译全部项目
dotnet test OrleansGameService.slnx           # 运行 xUnit 测试
dotnet run --project src/GameServer.Gateway   # 使用 Development 配置在本地运行
docker compose up --build                     # 启动 Gateway、PostgreSQL、Redis 和 Orleans 架构初始化
```

验证 PostgreSQL/Redis 或生产风格的 Orleans 持久化时，请使用 Docker Compose。不要提交生成的 `bin/`、`obj/`、`.dotnet-cli/` 或 `TestResults/` 文件。

## 代码风格与命名约定

使用启用了可空引用类型和隐式 using 的 C#。遵循现有四空格缩进、文件范围命名空间，以及以下命名规则：公共类型和成员使用 PascalCase，局部变量和参数使用 camelCase，接口以 `I` 为前缀。尽可能保持领域规则纯粹；将需持久化的 Grain 状态置于 `State/`，并通过 `WriteStateAsync()` 持久化变更。维护协议兼容性：应新增带版本的 MessagePack 契约，避免轻率修改既有线上的数据结构。**每个 class 都必须添加简明、准确的中文注释，说明其功能和职责；核心实现与复杂代码还应优先说明业务约束、状态一致性、幂等性及兼容性等“为什么”，避免为直观语句添加重复性注释。**

## 测试指南

测试使用 xUnit（`[Fact]`）和 Coverlet 收集覆盖率。测试类应以被测单元命名，方法名应描述行为，例如 `Movement_rejects_teleport`。修改游戏校验、序列化或 Grain 端幂等性时，应补充有针对性的测试。创建 PR 前请运行 `dotnet test OrleansGameService.slnx`；仓库未配置全局覆盖率阈值。

## 提交与拉取请求指南

当前检出没有 Git 历史可供推断既有提交格式。请使用简短、祈使、限定范围的主题，例如 `Add quest reward idempotency`。提交应保持聚焦。PR 应说明游戏玩法/API 影响，列出验证命令，关联对应 Issue，并为网关或 UI 可见改动提供请求/响应示例或截图。应明确标注内容包、架构、环境变量或协议版本的变更。

## 安全与配置

不要提交凭据或生产环境 JWT 签名密钥。生产环境应通过环境变量提供 `Jwt__SigningKey` 和连接字符串；Compose 中的配置值仅是本地开发默认值。
