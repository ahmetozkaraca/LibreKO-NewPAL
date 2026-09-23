namespace LibreKO.Game.Protocol;

public sealed class MagicCharge
{
    private readonly Func<Task<bool>>? _payment;

    private MagicCharge(Func<Task<bool>>? payment)
    {
        _payment = payment;
        IsPaid = payment == null;
    }

    public static MagicCharge Prepaid { get; } = new(null);

    public bool IsPaid { get; private set; }

    public static MagicCharge Deferred(Func<Task<bool>> payment) => new(payment);

    public async Task<bool> TryPayAsync()
    {
        if (IsPaid)
            return true;

        IsPaid = await _payment!();
        return IsPaid;
    }
}
