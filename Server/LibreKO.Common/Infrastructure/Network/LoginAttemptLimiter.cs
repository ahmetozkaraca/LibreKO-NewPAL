using System.Net;
using LibreKO.Common.Domain.Services;

namespace LibreKO.Common.Infrastructure.Network;

public sealed class LoginAttemptLimiter(ConnectionLimitsSettings limits, TimeProvider time)
{
    private readonly AttemptCounter<IPAddress> _failuresByAddress =
        new(limits.MaxLoginFailuresPerIp, limits.LoginFailureWindow, time);

    private readonly AttemptCounter<string> _failuresByAccount =
        new(limits.MaxLoginFailuresPerAccount, limits.LoginFailureWindow, time);

    public bool IsLockedOut(IPAddress? address, string login)
        => _failuresByAddress.IsExhausted(AddressKey(address)) || _failuresByAccount.IsExhausted(AccountKey(login));

    public void RecordFailure(IPAddress? address, string login)
    {
        _failuresByAddress.Record(AddressKey(address));
        _failuresByAccount.Record(AccountKey(login));
    }

    public void RecordSuccess(string login) => _failuresByAccount.Reset(AccountKey(login));

    private static IPAddress AddressKey(IPAddress? address) => address ?? IPAddress.None;

    private static string AccountKey(string login)
    {
        var key = login.Length > AccountCredentialRules.MaxLoginLength
            ? login[..AccountCredentialRules.MaxLoginLength]
            : login;
        return key.ToUpperInvariant();
    }
}
