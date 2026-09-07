# 服务器权威移动

## 当前行为

客户端通过 `Move` 命令提交目标二维坐标；坐标是移动意图，不是可信结果。角色 Grain 根据当前位置、版本化移动配置和服务器流逝时间核准实际位移，区域 Grain 只接受角色 Grain 已核准的位置。

示例内容配置如下：

```json
"movement": {
  "speedPerSecond": 6,
  "maximumBudget": "00:00:02"
}
```

服务器使用距离预算：

```text
容量 = speedPerSecond × maximumBudget
核准前余额 = min(容量, 原余额 + speedPerSecond × 服务器流逝秒数)
核准后余额 = 核准前余额 - 本次二维直线距离
```

当前示例容量为 12 个距离单位。它保留升级前单次最多移动 12 单位的合法路径，但连续请求共享同一余额，不能通过拆成多个小步绕过速度限制。内容包省略 `movement` 时使用相同缺省值；速度必须是有限正数，最大预算时长必须为正数，否则内容导入失败且不会激活该版本。

## 时间与生命周期语义

- 首次成功移动以及缺少预算字段的旧角色状态从满容量开始；首次拒绝不会消耗预算。
- 同一激活内使用 `TimeProvider` 的单调时间计算间隔，操作系统 UTC 调整不会改变进程内移动速度。
- 跨重激活使用最近一次已提交预算的 UTC 时间锚点。负时间差按零处理，长停顿最多补满容量。
- 切换区域会先按原内容版本结算已有余额，再按目标内容版本的容量收紧；切区和同区重复进入都不会补满预算。
- 同一操作号和同一载荷在成功后重放时返回已有回执，不再次移动或扣减预算。

## 故障恢复

角色先把目标位置、命令指纹、核准时间和扣减后的预算写入 `PendingMove`，然后更新区域位置。区域提交成功而角色最终提交失败时，重激活会复用原核准结果完成角色位置、预算和操作回执的同一次提交，不会重新获得或再次扣除预算。

区域拒绝不在成员列表中的角色时，角色返回 `not_in_zone` 且不提交核准预算。存储写入结果不确定时仍遵循现有规则：客户端收到 `server_error` 后必须使用相同操作号重试。

## 错误与客户端重试

| 错误码 | 含义 | 客户端处理 |
| --- | --- | --- |
| `invalid_movement` | 坐标不是有限数、距离计算溢出，或单次距离超过最大容量 | 修正目标坐标；不要原样持续重试 |
| `movement_rate_limited` | 目标距离合法，但当前服务器时间预算不足 | 等待后使用同一操作号和相同载荷重试，或使用新操作号提交更近目标 |
| `not_in_zone` | 区域权威成员关系中不存在该角色 | 重新获取角色状态并执行入区/重连流程 |

完整的操作号和协议错误语义见[实时协议错误码](../protocol/errors.md)。

## 代码与验证入口

- 纯规则：`src/GameServer.Domain/Gameplay/MovementRules.cs`
- 权威状态与恢复：`src/GameServer.Grains/CharacterGrain.cs`、`src/GameServer.Grains/State/GrainStates.cs`
- 内容配置与校验：`src/GameServer.Contracts/GameContracts.cs`、`src/GameServer.Infrastructure/Content/ContentServices.cs`
- 领域边界测试：`tests/GameServer.Tests/GameplayRulesTests.cs`
- 重激活及提交点故障测试：`tests/GameServer.Tests/GrainRecoveryTests.cs`

当前移动规则尚不包含地图多边形、障碍物、寻路或区域坐标上下界；这些约束由 `GAME-002` 继续补齐。

## 验证记录

2026-09-07 本地执行 `dotnet build OrleansGameService.slnx --no-restore` 通过；`dotnet test OrleansGameService.slnx --no-restore` 为 33 通过、6 跳过、0 失败。Compose 集成脚本在启动前因环境找不到 `docker` 命令而停止，真实 PostgreSQL、Gateway 和重启路径仍待具备 Docker 的环境验证。
