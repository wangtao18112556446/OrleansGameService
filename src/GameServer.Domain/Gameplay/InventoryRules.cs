using GameServer.Contracts;

namespace GameServer.Domain.Gameplay;

public static class InventoryRules
{
    public const int DefaultCapacity = 24;
    public static int UsedSlots(IReadOnlyDictionary<string, int> inventory, IReadOnlyDictionary<string, ItemDefinition> items)
        => inventory.Where(x => x.Value > 0).Sum(x => Stacks(x.Value, items.TryGetValue(x.Key, out var item) ? item.MaxStack : 1));
    public static bool CanReceive(IReadOnlyDictionary<string, int> inventory, IReadOnlyList<RewardGrant> rewards, IReadOnlyDictionary<string, ItemDefinition> items, int capacity = DefaultCapacity)
    {
        // 先合并同一物品再计算槽位：多个奖励分别判断会遗漏它们可共用未满堆叠的情况。
        var combined = new Dictionary<string, int>(inventory, StringComparer.Ordinal);
        foreach (var reward in rewards) combined[reward.ItemId] = combined.GetValueOrDefault(reward.ItemId) + reward.Quantity;
        return UsedSlots(combined, items) <= capacity;
    }
    // 内容配置错误时仍以 1 作为安全下限，避免除零并让物品占用行为保持保守。
    public static int Stacks(int quantity, int maxStack) => quantity <= 0 ? 0 : (quantity + Math.Max(1, maxStack) - 1) / Math.Max(1, maxStack);
}
