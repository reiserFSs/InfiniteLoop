using AscNet.Common.MsgPack;

namespace AscNet.Common.Database;

public partial class Inventory
{
    // The client displays item 2 + item 3 as one Black Card balance.
    // Keep legacy constant names for compatibility with existing reward tables.
    public static bool IsBlackCard(int id) => id is PaidGem or FreeGem;

    public long SpendableCount(int id) => Items
        .Where(item => IsBlackCard(id) ? IsBlackCard(item.Id) : item.Id == id)
        .Sum(item => item.Count);

    public List<Item> Spend(int id, int amount)
    {
        if (amount < 0 || SpendableCount(id) < amount)
            throw new InvalidOperationException("Insufficient currency");
        List<Item> changed = new();
        foreach (int source in IsBlackCard(id) ? new[] { PaidGem, FreeGem } : new[] { id })
        {
            foreach (Item stack in Items.Where(item => item.Id == source))
            {
                long debit = Math.Min(amount, stack.Count);
                if (debit <= 0) continue;
                stack.Count -= debit;
                stack.RefreshTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                amount -= checked((int)debit);
                changed.Add(stack);
            }
        }
        return changed;
    }
}
