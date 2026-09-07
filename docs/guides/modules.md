# 编译期玩法模块开发指南

## 接入方式

模块项目只需引用 `GameServer.Abstractions`；内容 DTO、效果定义等 Contracts 类型由该引用传递。模块包公开一个命名明确的扩展方法，宿主显式调用它：

```csharp
builder.Services
    .AddGameGateway(builder.Configuration)
    .AddSampleVampirism();
```

`AddGameServer` 已注册 `core.gameplay`。模块通过 `GameModuleDescriptor` 声明稳定、区分大小写的 ID 和依赖，再在同一个 `GameModuleBuilder` 中贡献效果、内容节和校验器：

```csharp
public static GameServerBuilder AddMyCombat(this GameServerBuilder builder)
    => builder.AddModule(
        new GameModuleDescriptor("studio.my-combat", ["core.gameplay"]),
        module => module
            .AddEffect<MyEffect>()
            .AddContentSection<MyCombatContent>(required: false, () => new())
            .AddContentValidator<MyCombatValidator>());
```

模块内部依赖使用 `module.Services` 注册。后台生命周期使用标准 `IHostedService`，不要另建模块启停 Interface。

## 效果 Interface

`IGameEffect.TypeId` 必须是稳定且全局唯一的字符串，建议使用反向域名或组织前缀。`Validate` 检查该效果定义专属约束，`Resolve` 只根据显式 `EffectDefinition`、属性和当前 `GameContentVersion` 返回 `EffectResolution`，不得读取可变全局内容或直接修改 Grain 状态。

旧内容继续使用 `EffectKind`；新模块在效果定义中设置 `typeId`：

```json
"vampiric-strike-damage": {
  "id": "vampiric-strike-damage",
  "kind": 0,
  "typeId": "sample.vampirism.damage",
  "amount": 2
}
```

`kind` 仅作为旧结构兼容字段；存在 `typeId` 时不参与实现选择。

## 类型化内容节

每个模块最多注册一个根内容类型。JSON 使用完全相同的模块 ID：

```json
"modules": {
  "sample.vampirism": {
    "healingResourceId": "health",
    "healingRatio": 0.25
  }
}
```

效果通过 `context.ContentVersion.GetModuleContent<T>(moduleId)` 获取同版本配置。可选节必须提供安全默认值，确保已存储的旧内容仍可加载；必需节缺失、类型错误、未知模块节或校验失败都会阻止导入/启动，并报告模块 ID 与 JSON 路径。删除模块前必须先发布不再引用其效果和内容节的新版本。

## 当前限制

模块由宿主显式引用并随进程启动，不能扫描、热加载或卸载。本轮不允许模块注册新的 WebSocket 消息、修改现有消息号或拥有自定义 Grain 持久化状态；这些能力涉及授权、幂等回执和状态迁移，需要独立 ADR。
