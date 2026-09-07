# 贡献指南

先阅读 [项目目标](README.md)、[架构](docs/ARCHITECTURE.md)、[工程规范](docs/ENGINEERING.md)及 [AGENTS.md](AGENTS.md)。从 [近期待办](docs/BACKLOG.md)选择可独立验收的功能；提出新功能时说明服务于通用框架还是可选玩法，以及如何验证。

## 本地开发

需要 .NET 10 SDK；持久化/端到端验证需要 Docker Compose 和 PowerShell 7。在仓库根目录执行：

```powershell
dotnet restore OrleansGameService.slnx
dotnet build OrleansGameService.slnx --no-restore
dotnet test OrleansGameService.slnx --no-build
# 需要真实依赖时，使用独立测试 Compose 项目：
pwsh -File scripts/Test-Integration.ps1
```

开发模式的 Orleans 内存存储不代表整个应用不需要数据库，Identity 和内容加载仍使用 PostgreSQL。可运行 `docker compose up --build` 验证完整服务，启动及请求示例见 [README](README.md)。

模型变更还需执行：

```powershell
dotnet tool restore
dotnet ef migrations has-pending-model-changes --project src/GameServer.Infrastructure --no-build
```

集成脚本会重启独立测试项目的 Gateway，并保留测试数据卷；详情见 README。不要提交 `bin/`、`obj/`、`.dotnet-cli/`、`TestResults/` 或凭据。

## 提交与评审

分支默认使用 `codex/` 前缀，提交标题使用简短祈使句并限定范围。功能变更、格式整理和依赖升级尽量分开。PR 使用仓库模板说明玩家/API 行为、验证及兼容影响。

涉及跨 Grain 流程、公共协议、持久化状态或模块职责的变更先写简短 ADR。每个 class 添加中文职责注释，复杂实现解释一致性和业务约束。测试结果列出通过、失败、跳过和未运行原因；不要用单元测试通过替代真实存储验证。

许可证和外部贡献授权流程尚待项目负责人确定，正式面向社区接收贡献前需补齐。发现可能涉及凭据或玩家资产的漏洞时不要在公开 Issue 中粘贴密钥或个人数据；专用私密报告渠道将在发行准备阶段公布。
