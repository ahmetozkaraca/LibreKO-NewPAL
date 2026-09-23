using LibreKO.Game.Protocol;

namespace LibreKO.Game.World;

public static class Coins
{
    public static bool CanCredit(int balance, long amount) =>
        amount >= 0 && balance + amount <= ExchangePacketConstants.CoinMax;

    public static int Credit(int balance, long amount) =>
        (int)Math.Clamp(balance + amount, 0L, ExchangePacketConstants.CoinMax);
}
