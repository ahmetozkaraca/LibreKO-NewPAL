namespace LibreKO.Game.World;

public enum RewardOutcome : byte
{
    Succeeded,
    Refused,
    Unavailable,
    InventoryLocked,
    InventoryFull,
    TooHeavy,
    PurseFull,
}

internal readonly record struct RewardVerdict(RewardOutcome Outcome, ViolationKind? Violation)
{
    public static readonly RewardVerdict Succeeded = new(RewardOutcome.Succeeded, null);

    public static RewardVerdict Violated(ViolationKind kind) => new(RewardOutcome.Refused, kind);

    public static RewardVerdict Blocked(RewardGrantStatus status) => new(status switch
    {
        RewardGrantStatus.InventoryLocked => RewardOutcome.InventoryLocked,
        RewardGrantStatus.InventoryFull => RewardOutcome.InventoryFull,
        RewardGrantStatus.TooHeavy => RewardOutcome.TooHeavy,
        RewardGrantStatus.PurseFull => RewardOutcome.PurseFull,
        RewardGrantStatus.MissingItems => RewardOutcome.Refused,
        _ => RewardOutcome.Unavailable,
    }, null);
}
