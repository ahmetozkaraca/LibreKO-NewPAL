using LibreKO.Game.Protocol;

namespace LibreKO.Game.World;

public static class Coins
{
    public static bool CanCredit(int balance, long amount) =>
        amount >= 0 && balance + amount <= ExchangePacketConstants.CoinMax;

    public static int Credit(int balance, long amount) =>
        (int)Math.Clamp(balance + amount, 0L, ExchangePacketConstants.CoinMax);

    public static bool TryCredit(UserSession player, long amount) =>
        player.WithLock(holder =>
        {
            if (!CanCredit(holder.Money, amount))
                return false;

            holder.Money = Credit(holder.Money, amount);
            return true;
        });

    public static bool TryDebit(UserSession player, long amount) =>
        player.WithLock(holder =>
        {
            if (amount < 0 || holder.Money < amount)
                return false;

            holder.Money -= (int)amount;
            return true;
        });
}
