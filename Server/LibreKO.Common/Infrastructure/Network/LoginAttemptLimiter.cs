using System.Net;
using LibreKO.Common.Domain.Services;
using Microsoft.Extensions.Options;

namespace LibreKO.Common.Infrastructure.Network;

public sealed class LoginAdmission : IDisposable
{
    public static LoginAdmission Unthrottled { get; } = new(null);

    private Action? _release;

    public LoginAdmission(Action? release) => _release = release;

    public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
}

public sealed class LoginAttemptLimiter(IOptions<ConnectionLimitsSettings> options, TimeProvider time)
{
    private readonly ConnectionLimitsSettings _limits = options.Value;

    private readonly AttemptCounter<IPAddress> _failuresByAddress =
        new(options.Value.MaxLoginFailuresPerIp, options.Value.LoginFailureWindow, time);

    private readonly AttemptCounter<(string Account, IPAddress Address)> _failuresByAccountAndAddress =
        new(options.Value.MaxLoginFailuresPerAccountAndIp, options.Value.LoginFailureWindow, time);

    private readonly AttemptCounter<string> _failuresByAccount =
        new(options.Value.AccountSlowdownFailures, options.Value.LoginFailureWindow, time);

    private readonly Lock _gateSync = new();
    private readonly Dictionary<string, AccountGate> _gates = [];

    public async Task<LoginAdmission?> AdmitAsync(IPAddress? address, string login, CancellationToken ct = default)
    {
        if (IsLockedOut(address, login))
            return null;

        var account = AccountKey(login);
        if (!_failuresByAccount.IsExhausted(account))
            return LoginAdmission.Unthrottled;

        var gate = EnterGate(account);
        try
        {
            await gate.Turn.WaitAsync(ct);
        }
        catch
        {
            LeaveGate(account, gate);
            throw;
        }

        var admission = new LoginAdmission(() =>
        {
            gate.Turn.Release();
            LeaveGate(account, gate);
        });

        try
        {
            if (_limits.AccountSlowdown > TimeSpan.Zero)
                await Task.Delay(_limits.AccountSlowdown, time, ct);
        }
        catch
        {
            admission.Dispose();
            throw;
        }

        return admission;
    }

    public bool IsLockedOut(IPAddress? address, string login)
        => _failuresByAddress.IsExhausted(AddressKey(address))
           || _failuresByAccountAndAddress.IsExhausted((AccountKey(login), AddressKey(address)));

    public void RecordFailure(IPAddress? address, string login)
    {
        _failuresByAddress.Record(AddressKey(address));
        _failuresByAccountAndAddress.Record((AccountKey(login), AddressKey(address)));
        _failuresByAccount.Record(AccountKey(login));
    }

    public void RecordSuccess(IPAddress? address, string login)
    {
        _failuresByAccountAndAddress.Reset((AccountKey(login), AddressKey(address)));
        _failuresByAccount.Reset(AccountKey(login));
    }

    private AccountGate EnterGate(string account)
    {
        using var scope = _gateSync.EnterScope();
        if (!_gates.TryGetValue(account, out var gate))
            _gates[account] = gate = new AccountGate();

        gate.Holders++;
        return gate;
    }

    private void LeaveGate(string account, AccountGate gate)
    {
        using var scope = _gateSync.EnterScope();
        if (--gate.Holders == 0)
            _gates.Remove(account);
    }

    private static IPAddress AddressKey(IPAddress? address) => address ?? IPAddress.None;

    private static string AccountKey(string login)
    {
        var key = login.Length > AccountCredentialRules.MaxLoginLength
            ? login[..AccountCredentialRules.MaxLoginLength]
            : login;
        return key.ToUpperInvariant();
    }

    private sealed class AccountGate
    {
        private const int OneAttemptAtATime = 1;

        public readonly SemaphoreSlim Turn = new(OneAttemptAtATime, OneAttemptAtATime);
        public int Holders;
    }
}
