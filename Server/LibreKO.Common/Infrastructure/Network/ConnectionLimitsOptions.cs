using Microsoft.Extensions.Options;

namespace LibreKO.Common.Infrastructure.Network;

public sealed class ConnectionLimitsOptions<TSettings>(IOptions<TSettings> server) : IOptions<ConnectionLimitsSettings>
    where TSettings : class, IConnectionLimitsOwner
{
    public ConnectionLimitsSettings Value => server.Value.Connections;
}
