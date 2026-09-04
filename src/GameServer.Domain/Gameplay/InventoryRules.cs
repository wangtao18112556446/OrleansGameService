using GameServer.Contracts;

namespace GameServer.Domain.Gameplay;

public static class InventoryRules
{
    public const int DefaultCapacity = 24;
    public static int UsedSlots(IReadOnlyDictionary<string, int> inventory, IReadOnlyDictionary<string, ItemDefinition> items)
        => inventory.Where(x => x.Value > 0).Sum(x => Stacks(x.Value, items.TryGetValue(x.Key, out var item) ? item.MaxStack : 1));
    public static bool CanReceive(IReadOnlyDictionary<string, int> inventory, IReadOnlyList<RewardGrant> rewards, IReadOnlyDictionary<string, ItemDefinition> items, int capacity = DefaultCapacity)
    {
        var combined = new Dictionary<string, int>(inventory, StringComparer.Ordinal);
        foreach (var reward in rewards) combined[reward.ItemId] = combined.GetValueOrDefault(reward.ItemId) + reward.Quantity;
        return UsedSlots(combined, items) <= capacity;
    }
    public static int Stacks(int quantity, int maxStack) => quantity <= 0 ? 0 : (quantity + Math.Max(1, maxStack) - 1) / Math.Max(1, maxStack);
}
