using LibreKO.Common.Domain.Entities;

namespace LibreKO.Common.Domain.Services;

public interface IAccountRepository
{
    Task<Account?> GetById(int id);
    Task<Account?> GetByLogin(string login);
    Task CreateAsync(Account account);
    Task<bool> TryCreateAsync(Account account);
    Task UpdateAsync(Account account);
    Task UpdatePasswordAsync(Account account, string passwordHash);
    Task SetOnlineServerAsync(int accountId, int serverId);
    Task ClearOnlineServerAsync(int accountId);
    Task<int> ClearOnlineServerForServerAsync(int serverId);
}
