using System.Net;
using LibreKO.Common.Infrastructure.Network;
using LibreKO.Login.Configuration;
using Microsoft.Extensions.Options;

namespace LibreKO.Login;

public sealed class AccountCreationThrottle(IOptions<LoginServerSettings> settings, TimeProvider time)
{
    private readonly AttemptCounter<IPAddress> _creations = new(
        settings.Value.Account.MaxCreatedPerIp,
        TimeSpan.FromSeconds(Math.Max(0, settings.Value.Account.CreationWindowSeconds)),
        time);

    public bool TryReserve(IPAddress? address) => _creations.TryRecord(address ?? IPAddress.None);
}
