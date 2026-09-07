using GameServer.Abstractions;
using GameServer.Contracts;
using GameServer.Grains.State;
using Orleans.Runtime;

namespace GameServer.Grains;

/// <summary>维护账号拥有的角色清单，并依据活动内容创建合法的初始角色。</summary>
public sealed class AccountGrain(
    [PersistentState("account", "gameStore")] IPersistentState<AccountState> state,
    IGameContentCatalog contentCatalog) : Grain, IAccountGrain
{
    public async Task<CreateCharacterResult> CreateCharacterAsync(string name, string contentVersion, string? classId = null)
    {
        name = name.Trim();
        if (name.Length is < 3 or > 20 || !name.All(char.IsLetterOrDigit)) return new CreateCharacterResult(false, "invalid_character_name", null);
        if (state.State.Characters.Any(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase))) return new CreateCharacterResult(false, "character_name_taken", null);
        var content = contentCatalog.GetVersion(contentVersion).Content;
        classId ??= content.Classes.Count == 1 ? content.Classes.Keys.Single() : null;
        if (classId is null || !content.Classes.ContainsKey(classId)) return new CreateCharacterResult(false, "invalid_character_class", null);
        var character = new CharacterSummary(Guid.NewGuid().ToString("N"), name, 1);
        await GrainFactory.GetGrain<ICharacterGrain>(character.CharacterId).InitializeAsync(this.GetPrimaryKeyString(), name, contentVersion, classId);
        state.State.Characters.Add(character);
        await state.WriteStateAsync();
        return new CreateCharacterResult(true, null, character);
    }
    public Task<IReadOnlyList<CharacterSummary>> GetCharactersAsync() => Task.FromResult<IReadOnlyList<CharacterSummary>>(state.State.Characters.ToArray());
    public Task<bool> OwnsCharacterAsync(string characterId) => Task.FromResult(state.State.Characters.Any(x => x.CharacterId == characterId));
}
