using Orleans;
namespace GameServer.Contracts;

[Alias("GameServer.Contracts.IAccountGrain")]
public interface IAccountGrain : IGrainWithStringKey
{
    [Alias("CreateCharacterAsync")]
    Task<CreateCharacterResult> CreateCharacterAsync(string name, string contentVersion, string? classId = null);
    [Alias("GetCharactersAsync")]
    Task<IReadOnlyList<CharacterSummary>> GetCharactersAsync();
    [Alias("OwnsCharacterAsync")]
    Task<bool> OwnsCharacterAsync(string characterId);
}

[Alias("GameServer.Contracts.ICharacterGrain")]

public interface ICharacterGrain : IGrainWithStringKey
{
    [Alias("InitializeAsync")]
    Task InitializeAsync(string accountId, string name, string contentVersion, string classId);
    [Alias("GetSnapshotAsync")]
    Task<CharacterSnapshot> GetSnapshotAsync();
    [Alias("EnterZoneAsync")]
    Task<CommandResult> EnterZoneAsync(string zoneId, string contentVersion);
    [Alias("MoveAsync")]
    Task<CommandResult> MoveAsync(MoveCommand command, string operationId);
    [Alias("AttackAsync")]
    Task<CommandResult> AttackAsync(AttackCommand command, string operationId);
    [Alias("UseSkillAsync")]
    Task<CommandResult> UseSkillAsync(UseSkillCommand command, string operationId);
    [Alias("EquipAsync")]
    Task<CommandResult> EquipAsync(EquipCommand command, string operationId);
    [Alias("UnequipAsync")]
    Task<CommandResult> UnequipAsync(UnequipCommand command, string operationId);
    [Alias("InteractNpcAsync")]
    Task<CommandResult> InteractNpcAsync(InteractNpcCommand command, string operationId);
    [Alias("AcceptQuestAsync")]
    Task<CommandResult> AcceptQuestAsync(QuestCommand command, string operationId);
    [Alias("CompleteQuestAsync")]
    Task<CommandResult> CompleteQuestAsync(QuestCommand command, string operationId);
}

[Alias("GameServer.Contracts.IZoneGrain")]

public interface IZoneGrain : IGrainWithStringKey
{
    [Alias("JoinAsync")]
    Task<ZoneSnapshot> JoinAsync(string characterId, WorldPosition position, string contentVersion);
    [Alias("LeaveAsync")]
    Task LeaveAsync(string characterId);
    [Alias("MoveAsync")]
    Task<bool> MoveAsync(string characterId, WorldPosition position);
    [Alias("IsNearNpcAsync")]
    Task<bool> IsNearNpcAsync(string characterId, string npcId);
    [Alias("AttackMonsterAsync")]
    Task<AttackResult> AttackMonsterAsync(string characterId, string monsterId, decimal damage, decimal range, string operationId);
    [Alias("GetSnapshotAsync")]
    Task<ZoneSnapshot> GetSnapshotAsync();
}

[Alias("GameServer.Contracts.IMonsterGrain")]

public interface IMonsterGrain : IGrainWithStringKey
{
    [Alias("InitializeAsync")]
    Task InitializeAsync(MonsterDefinition definition);
    [Alias("ApplyDamageAsync")]
    Task<AttackResult> ApplyDamageAsync(decimal damage, string operationId);
    [Alias("GetSnapshotAsync")]
    Task<ZoneEntity> GetSnapshotAsync();
}
