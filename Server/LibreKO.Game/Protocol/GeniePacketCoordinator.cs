using LibreKO.Common.Enums;
using LibreKO.Common.Infrastructure.Network;
using LibreKO.Game.Protocol.Writers;
using LibreKO.Game.World;

namespace LibreKO.Game.Protocol;

public interface IGeniePacketCoordinator
{
    Task HandleAsync(IClient client, Packet packet);
}

public class GeniePacketCoordinator(
    SessionManager sessionManager,
    IRewardDrawService rewardDraws) : IGeniePacketCoordinator
{
    private const byte GenieSubStatus = 1;
    private const byte GenieSubClaim = 2;

    private static readonly (int MinLevel, string Tip)[] GenieTips =
    {
        (1,  "Welcome! Talk to town NPCs to pick up your first quests."),
        (10, "Slot skills onto your hotkey bar (1-8) and press R to auto-attack."),
        (20, "Repair your gear at a blacksmith before it breaks in the field."),
        (30, "Join a party (P) to share experience and clear tougher monsters."),
        (40, "Visit the merchant to sell loot and stock up on potions."),
        (50, "High-grade armor and enchanted weapons make a real difference now."),
        (60, "Claim your daily genie reward every day - the gold adds up!"),
        (70, "You're elite - chase rare drops and help your nation in the field."),
    };

    public async Task HandleAsync(IClient client, Packet packet)
    {
        var session = sessionManager.GetByClientId(client.Id);
        if (session == null || packet.RemainingBytes < 1)
            return;

        var sub = packet.ReadByte();
        switch (sub)
        {
            case GenieSubStatus:
                await session.Client.SendPacket(GeniePacketWriter.Status(
                    GenieSubStatus,
                    PickGenieTip(session.Level),
                    await rewardDraws.IsDailyRewardAvailableAsync(session, PrizePool.Genie)));
                break;
            case GenieSubClaim:
                await HandleGenieClaimAsync(session);
                break;
        }
    }

    private async Task HandleGenieClaimAsync(UserSession session)
    {
        var result = await rewardDraws.ClaimDailyRewardAsync(session, PrizePool.Genie);
        var succeeded = result.Outcome == RewardOutcome.Succeeded;
        await session.Client.SendPacket(GeniePacketWriter.ClaimResult(
            GenieSubClaim,
            succeeded ? GeniePacketWriter.Succeeded : GeniePacketWriter.Failed,
            result.PrizeGold));
        if (!succeeded)
            await RewardNotices.SendAsync(session, result.Outcome);
    }

    private static string PickGenieTip(byte level)
    {
        string tip = GenieTips[0].Tip;
        foreach (var (minLevel, text) in GenieTips)
        {
            if (level >= minLevel) tip = text;
            else break;
        }
        return tip;
    }
}
