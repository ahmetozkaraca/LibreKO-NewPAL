using LibreKO.Game.Protocol.Writers;
using LibreKO.Game.World;

namespace LibreKO.Game.Protocol;

public static class RewardNotices
{
    public const string InventoryLocked = "Finish your trade or close your shop before collecting rewards.";
    public const string InventoryFull = "Make room in your inventory before collecting this reward.";
    public const string TooHeavy = "You are carrying too much to collect this reward.";
    public const string PurseFull = "Your purse cannot hold that much gold. Store some coins first.";
    public const string Unavailable = "Rewards are unavailable right now. Please try again shortly.";

    public static Task SendAsync(UserSession session, RewardOutcome outcome)
    {
        var message = outcome switch
        {
            RewardOutcome.InventoryLocked => InventoryLocked,
            RewardOutcome.InventoryFull => InventoryFull,
            RewardOutcome.TooHeavy => TooHeavy,
            RewardOutcome.PurseFull => PurseFull,
            RewardOutcome.Unavailable => Unavailable,
            _ => null,
        };

        return message == null
            ? Task.CompletedTask
            : session.Client.SendPacket(ChatPacketWriter.SystemNotice((byte)session.Nation, message));
    }
}
