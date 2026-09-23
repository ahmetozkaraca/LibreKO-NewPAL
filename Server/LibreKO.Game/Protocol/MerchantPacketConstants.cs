namespace LibreKO.Game.Protocol;

internal static class MerchantPacketConstants
{
    public const int StallSlots = 12;
    public const int StallDisplaySlots = 4;
    public const int StallDisplaySlotsPremium = 8;
    public const int MaxAdvertLength = 48;
    public const int MinimumBuyingMerchantLevel = 35;
    public const byte PremiumStallFlag = 1;

    public const long MaxPrice = ExchangePacketConstants.CoinMax;

    public static bool IsSanePrice(long price) => price is > 0 and <= MaxPrice;
}
