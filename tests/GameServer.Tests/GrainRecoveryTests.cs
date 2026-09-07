using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using GameServer.Abstractions;
using GameServer.Contracts;
using GameServer.Domain.Gameplay;
using GameServer.Grains.State;
using GameServer.Infrastructure.Content;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Orleans;
using Orleans.Runtime;
using Orleans.Storage;
using Orleans.TestingHost;

namespace GameServer.Tests;

/// <summary>通过真实 Orleans 调度和可注入失败的存储验证跨 Grain 恢复及操作幂等。</summary>
public sealed class GrainRecoveryTests : IAsyncLifetime
{
    private readonly FaultStorage storage = new();
    private readonly TestLedger ledger = new();
    private readonly ManualTimeProvider timeProvider = new(new DateTimeOffset(2026, 9, 7, 0, 0, 0, TimeSpan.Zero));
    private InProcessTestCluster cluster = null!;

    public async Task InitializeAsync()
    {
        var content = GameContentDefaults.Create() with
        {
            Zones = new Dictionary<string, ZoneDefinition>
            {
                ["starter-plains"] = new("starter-plains", "Plains", 100),
                ["small"] = new("small", "Small", 1)
            }
        };
        var catalog = new Mock<IGameContentCatalog>();
        var contentVersion = new GameContentVersion(content);
        catalog.Setup(x => x.GetVersion(It.IsAny<string>())).Returns(contentVersion);
        catalog.Setup(x => x.GetActive()).Returns(contentVersion);
        var builder = new InProcessTestClusterBuilder(1);
        builder.ConfigureSilo((_, silo) => silo.ConfigureServices(services =>
        {
            services.AddSingleton(catalog.Object);
            services.AddSingleton<IGameplayFeatureCatalog>(new GameplayFeatureCatalog(
            [
                new DamageMonsterEffect(),
                new RestoreResourceEffect(),
                new ConsumeResourceEffect(),
                new ApplyBuffEffect()
            ]));
            services.AddSingleton<IRewardLedger>(ledger);
            services.AddSingleton<TimeProvider>(timeProvider);
            services.AddKeyedSingleton<IGrainStorage>("gameStore", storage);
            services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Error));
        }));
        cluster = builder.Build();
        await cluster.DeployAsync();
    }

    public async Task DisposeAsync() => await cluster.DisposeAsync();

    private async Task<ICharacterGrain> CreateCharacterAsync(string id = "hero")
    {
        var character = cluster.Client.GetGrain<ICharacterGrain>(id);
        await character.InitializeAsync("account", "Hero", "v1", "adventurer");
        Assert.True((await character.EnterZoneAsync("starter-plains", "v1")).Succeeded);
        Assert.True((await character.MoveAsync(new MoveCommand { X = 2, Y = 0 }, "move")).Succeeded);
        Assert.True((await character.AcceptQuestAsync(new QuestCommand { QuestId = "slime-hunt" }, "accept")).Succeeded);
        return character;
    }

    private async Task PrepareKillAsync(ICharacterGrain character)
    {
        Assert.True((await character.AttackAsync(new AttackCommand { TargetMonsterId = "green-slime" }, "first-hit")).Succeeded);
        // 普攻与技能冷却独立，第二次用初始技能完成击杀，无需测试依赖实际等待。
    }

    private static Task<CommandResult> KillAsync(ICharacterGrain character) => character.UseSkillAsync(new UseSkillCommand { SkillId = "basic-attack", TargetMonsterId = "green-slime" }, "kill");

    [Fact]
    public async Task Kill_recovers_after_monster_commit_before_character_commit()
    {
        var character = await CreateCharacterAsync();
        await PrepareKillAsync(character);
        storage.FailNext = value => value is CharacterState s && s.OperationReceipts.ContainsKey("kill");
        await Assert.ThrowsAnyAsync<Exception>(() => KillAsync(character));
        await cluster.DeactivateAsync(character);
        var result = await KillAsync(character);
        Assert.True(result.Succeeded);
        Assert.True(result.Attack!.TargetDefeated);
        Assert.Equal(1, result.Snapshot!.Inventory.Single(x => x.ItemId == "slime-gel").Quantity);
        Assert.Equal(1, result.Snapshot.Quests.Single().Progress);
        Assert.Single(ledger.Operations);
    }

    [Fact]
    public async Task Reward_retries_after_ledger_commit_and_lost_response()
    {
        var character = await CreateCharacterAsync();
        await PrepareKillAsync(character);
        ledger.FailAfterCommit = true;
        await Assert.ThrowsAnyAsync<Exception>(() => KillAsync(character));
        await cluster.DeactivateAsync(character);
        var result = await KillAsync(character);
        Assert.True(result.Succeeded);
        Assert.Equal(1, result.Snapshot!.Inventory.Single().Quantity);
        Assert.Single(ledger.Operations);
    }

    [Fact]
    public async Task Quest_reward_and_completion_survive_uncertain_state_commit()
    {
        var character = await CreateCharacterAsync();
        await PrepareKillAsync(character);
        Assert.True((await KillAsync(character)).Succeeded);
        storage.FailAfterCommit = true;
        storage.FailNext = value => value is CharacterState s && s.OperationReceipts.ContainsKey("complete");
        var command = new QuestCommand { QuestId = "slime-hunt" };
        await Assert.ThrowsAnyAsync<Exception>(() => character.CompleteQuestAsync(command, "complete"));
        // 同一激活必须重新加载不确定状态，不能重复修改内存背包。
        var result = await character.CompleteQuestAsync(command, "complete");
        Assert.True(result.Succeeded);
        Assert.True(result.Snapshot!.Quests.Single().IsCompleted);
        Assert.Equal(1, result.Snapshot.Inventory.Single(x => x.ItemId == "traveler-token").Quantity);
        Assert.Equal(2, ledger.Operations.Count);
        await cluster.DeactivateAsync(character);
        Assert.Equal(1, (await character.GetSnapshotAsync()).Inventory.Single(x => x.ItemId == "traveler-token").Quantity);
    }

    [Fact]
    public async Task Operation_id_reuse_with_different_payload_is_rejected()
    {
        var character = await CreateCharacterAsync();
        var result = await character.MoveAsync(new MoveCommand { X = 3, Y = 0 }, "move");
        Assert.Equal("operation_conflict", result.ErrorCode);
        Assert.Equal(2, result.Snapshot!.Position.X);
        Assert.Equal("invalid_operation_id", (await character.MoveAsync(new MoveCommand { X = 2, Y = 0 }, " ")).ErrorCode);
    }

    [Fact]
    public async Task Different_characters_can_use_the_same_attack_operation_id()
    {
        var first = await CreateCharacterAsync("first");
        var second = await CreateCharacterAsync("second");
        var command = new AttackCommand { TargetMonsterId = "green-slime" };
        Assert.False((await first.AttackAsync(command, "shared")).Attack!.TargetDefeated);
        Assert.True((await second.AttackAsync(command, "shared")).Attack!.TargetDefeated);
    }

    [Fact]
    public async Task Monster_replays_kill_during_respawn_and_after_reactivation()
    {
        var monster = cluster.Client.GetGrain<IMonsterGrain>("isolated-monster");
        await monster.InitializeAsync(GameContentDefaults.Create().Monsters["green-slime"]);
        var first = await monster.ApplyDamageAsync(50, "kill");
        Assert.True(first.TargetDefeated);
        await cluster.DeactivateAsync(monster);
        var replay = await monster.ApplyDamageAsync(50, "kill");
        Assert.Equal(first.TargetDefeated, replay.TargetDefeated);
        Assert.Equal(first.TargetHealth, replay.TargetHealth);
        Assert.Equal(first.RewardItemId, replay.RewardItemId);
        Assert.Equal(first.RewardCount, replay.RewardCount);
    }

    [Fact]
    public async Task Monster_write_failure_does_not_leave_uncommitted_damage_in_memory()
    {
        var monster = cluster.Client.GetGrain<IMonsterGrain>("isolated-monster");
        await monster.InitializeAsync(GameContentDefaults.Create().Monsters["green-slime"]);
        storage.FailNext = value => value is MonsterState;
        await Assert.ThrowsAnyAsync<Exception>(() => monster.ApplyDamageAsync(25, "hit"));
        Assert.Equal(25, (await monster.ApplyDamageAsync(25, "hit")).TargetHealth);
    }

    [Fact]
    public async Task Move_recovers_after_zone_position_was_committed()
    {
        var character = await CreateCharacterAsync();
        storage.FailNext = value => value is CharacterState s && s.OperationReceipts.ContainsKey("next-move");
        await Assert.ThrowsAnyAsync<Exception>(() => character.MoveAsync(new MoveCommand { X = 3, Y = 0 }, "next-move"));
        await cluster.DeactivateAsync(character);
        var snapshot = await character.GetSnapshotAsync();
        var zone = await cluster.Client.GetGrain<IZoneGrain>(snapshot.ZoneId).GetSnapshotAsync();
        Assert.Equal(new WorldPosition(3, 0), snapshot.Position);
        Assert.Equal(snapshot.Position, zone.Entities.Single(x => x.EntityId == "hero").Position);
        for (var x = 4; x <= 12; x++) Assert.True((await character.MoveAsync(new MoveCommand { X = x, Y = 0 }, $"after-recovery-{x}")).Succeeded);
        Assert.Equal("movement_rate_limited", (await character.MoveAsync(new MoveCommand { X = 13, Y = 0 }, "after-recovery-limited")).ErrorCode);
    }

    [Fact]
    public async Task Rapid_small_moves_cannot_exceed_server_time_budget()
    {
        var character = await CreateCharacterAsync();
        for (var x = 3; x <= 12; x++) Assert.True((await character.MoveAsync(new MoveCommand { X = x, Y = 0 }, $"burst-{x}")).Succeeded);
        Assert.Equal("movement_rate_limited", (await character.MoveAsync(new MoveCommand { X = 13, Y = 0 }, "burst-limited")).ErrorCode);

        timeProvider.Advance(TimeSpan.FromSeconds(1));
        Assert.True((await character.MoveAsync(new MoveCommand { X = 13, Y = 0 }, "burst-limited")).Succeeded);
    }

    [Fact]
    public async Task Reactivation_and_clock_rollback_do_not_refill_movement_budget()
    {
        var character = await CreateCharacterAsync();
        for (var x = 3; x <= 12; x++) Assert.True((await character.MoveAsync(new MoveCommand { X = x, Y = 0 }, $"consume-{x}")).Succeeded);
        timeProvider.RewindUtc(TimeSpan.FromHours(1));
        await cluster.DeactivateAsync(character);

        Assert.Equal("movement_rate_limited", (await character.MoveAsync(new MoveCommand { X = 13, Y = 0 }, "after-rollback")).ErrorCode);
    }

    [Fact]
    public async Task Zone_transfer_and_reenter_do_not_grant_a_new_movement_budget()
    {
        var character = await CreateCharacterAsync();
        Assert.True((await character.EnterZoneAsync("starter-plains", "v1")).Succeeded);
        Assert.Equal(new WorldPosition(2, 0), (await character.GetSnapshotAsync()).Position);
        for (var x = 3; x <= 12; x++) Assert.True((await character.MoveAsync(new MoveCommand { X = x, Y = 0 }, $"transfer-{x}")).Succeeded);

        Assert.True((await character.EnterZoneAsync("small", "v1")).Succeeded);
        Assert.Equal("movement_rate_limited", (await character.MoveAsync(new MoveCommand { X = 1, Y = 0 }, "after-transfer")).ErrorCode);
    }

    [Fact]
    public async Task Legacy_state_without_movement_budget_receives_the_defined_initial_allowance()
    {
        var character = await CreateCharacterAsync();
        await cluster.DeactivateAsync(character);
        storage.RemoveProperty("character", character.GetGrainId(), nameof(CharacterState.MovementBudget));

        var result = await character.MoveAsync(new MoveCommand { X = 14, Y = 0 }, "legacy-budget");
        Assert.True(result.Succeeded);
        Assert.Equal(new WorldPosition(14, 0), result.Snapshot!.Position);
    }

    [Fact]
    public async Task Legacy_pending_move_without_approved_budget_is_recovered_once()
    {
        var character = await CreateCharacterAsync();
        storage.FailAfterCommit = true;
        storage.FailNext = value => value is CharacterState { PendingMove.OperationId: "legacy-pending" };
        await Assert.ThrowsAnyAsync<Exception>(() => character.MoveAsync(new MoveCommand { X = 3, Y = 0 }, "legacy-pending"));
        await cluster.DeactivateAsync(character);
        storage.RemoveProperty("character", character.GetGrainId(), nameof(CharacterState.MovementBudget));
        storage.RemoveNestedProperty("character", character.GetGrainId(), nameof(CharacterState.PendingMove), nameof(PendingMove.ApprovedBudget));

        Assert.Equal(new WorldPosition(3, 0), (await character.GetSnapshotAsync()).Position);
        Assert.True((await character.MoveAsync(new MoveCommand { X = 14, Y = 0 }, "legacy-remaining")).Succeeded);
        Assert.Equal("movement_rate_limited", (await character.MoveAsync(new MoveCommand { X = 15, Y = 0 }, "legacy-limited")).ErrorCode);
    }

    [Fact]
    public async Task Full_zone_does_not_remove_character_from_previous_zone()
    {
        var first = await CreateCharacterAsync("first");
        var second = await CreateCharacterAsync("second");
        Assert.True((await first.EnterZoneAsync("small", "v1")).Succeeded);
        var rejected = await second.EnterZoneAsync("small", "v1");
        Assert.Equal("zone_unavailable", rejected.ErrorCode);
        Assert.Equal("starter-plains:v1", rejected.Snapshot!.ZoneId);
        var originalZone = await cluster.Client.GetGrain<IZoneGrain>("starter-plains:v1").GetSnapshotAsync();
        Assert.Contains(originalZone.Entities, x => x.EntityId == "second");
        Assert.True((await second.MoveAsync(new MoveCommand { X = 3, Y = 0 }, "still-here")).Succeeded);
    }

    [Fact]
    public async Task Transfer_recovers_after_target_join_before_character_commit()
    {
        var character = await CreateCharacterAsync();
        storage.FailNext = value => value is CharacterState { PendingZoneTransfer.Joined: true };
        await Assert.ThrowsAnyAsync<Exception>(() => character.EnterZoneAsync("small", "v1"));
        await cluster.DeactivateAsync(character);
        Assert.Equal("small:v1", (await character.GetSnapshotAsync()).ZoneId);
        var originalZone = await cluster.Client.GetGrain<IZoneGrain>("starter-plains:v1").GetSnapshotAsync();
        Assert.DoesNotContain(originalZone.Entities, x => x.EntityId == "hero");
        var destination = await cluster.Client.GetGrain<IZoneGrain>("small:v1").GetSnapshotAsync();
        Assert.Single(destination.Entities, x => x.EntityId == "hero");
    }

    [Fact]
    public async Task Outbox_retries_if_acknowledgement_state_cannot_be_saved()
    {
        var character = await CreateCharacterAsync();
        await PrepareKillAsync(character);
        storage.FailNext = value => value is CharacterState s && s.OperationReceipts.ContainsKey("kill") && s.PendingRewards.Count == 0;
        await Assert.ThrowsAnyAsync<Exception>(() => KillAsync(character));
        await cluster.DeactivateAsync(character);
        Assert.Equal(1, (await character.GetSnapshotAsync()).Inventory.Single().Quantity);
        Assert.Single(ledger.Operations);
    }

    /// <summary>使用独立 JSON 副本模拟持久化，并在提交前或提交后注入一次失败。</summary>
    private sealed class FaultStorage : IGrainStorage
    {
        private readonly ConcurrentDictionary<(string, GrainId), string> states = new();
        public Func<object, bool>? FailNext { get; set; }
        public bool FailAfterCommit { get; set; }
        public void RemoveProperty(string stateName, GrainId grainId, string propertyName)
        {
            var key = (stateName, grainId);
            var root = JsonNode.Parse(states[key])!.AsObject();
            if (!root.Remove(propertyName)) throw new InvalidOperationException($"State property '{propertyName}' was not found.");
            states[key] = root.ToJsonString();
        }
        public void RemoveNestedProperty(string stateName, GrainId grainId, string objectPropertyName, string propertyName)
        {
            var key = (stateName, grainId);
            var root = JsonNode.Parse(states[key])!.AsObject();
            var nested = root[objectPropertyName]?.AsObject() ?? throw new InvalidOperationException($"State object '{objectPropertyName}' was not found.");
            if (!nested.Remove(propertyName)) throw new InvalidOperationException($"State property '{objectPropertyName}.{propertyName}' was not found.");
            states[key] = root.ToJsonString();
        }
        public Task ReadStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
        {
            grainState.RecordExists = states.TryGetValue((stateName, grainId), out var json);
            grainState.State = json is null ? Activator.CreateInstance<T>() : JsonSerializer.Deserialize<T>(json)!;
            return Task.CompletedTask;
        }
        public Task WriteStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
        {
            var fail = FailNext?.Invoke(grainState.State!) == true;
            if (fail) FailNext = null;
            if (!fail || FailAfterCommit) states[(stateName, grainId)] = JsonSerializer.Serialize(grainState.State);
            if (fail) { FailAfterCommit = false; throw new IOException("Injected storage failure."); }
            grainState.RecordExists = true;
            return Task.CompletedTask;
        }
        public Task ClearStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
        {
            states.TryRemove((stateName, grainId), out _);
            grainState.RecordExists = false;
            return Task.CompletedTask;
        }
    }

    /// <summary>模拟账本已提交但响应丢失，按角色操作键去重。</summary>
    private sealed class TestLedger : IRewardLedger
    {
        public HashSet<string> Operations { get; } = [];
        public bool FailAfterCommit { get; set; }
        public Task<bool> TryRecordAsync(string characterId, string operationId, string kind, IReadOnlyList<RewardGrant> rewards, CancellationToken cancellationToken = default)
        {
            var added = Operations.Add(OperationIdentity.Scope(characterId, operationId));
            if (FailAfterCommit) { FailAfterCommit = false; throw new IOException("Injected ledger response loss."); }
            return Task.FromResult(added);
        }
    }
}
