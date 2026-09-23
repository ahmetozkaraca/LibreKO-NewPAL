using LibreKO.Common.Domain.Entities;
using LibreKO.Common.Domain.Services;
using Microsoft.EntityFrameworkCore;

namespace LibreKO.Common.Infrastructure.Persistence;

public class AccountRepository(AppDbContext context) : IAccountRepository
{
    public async Task<Account?> GetById(int id)
    {
        return await context.Accounts.FindAsync(id);
    }

    public async Task<Account?> GetByLogin(string login)
    {
        return await context.Accounts.OrderBy(a => a.Id).FirstOrDefaultAsync(a => a.Login == login);
    }

    public async Task CreateAsync(Account account)
    {
        context.Accounts.Add(account);
        await context.SaveChangesAsync();
    }

    public async Task<bool> TryCreateAsync(Account account)
    {
        context.Accounts.Add(account);
        try
        {
            await context.SaveChangesAsync();
            return true;
        }
        catch (DbUpdateException)
        {
            context.Entry(account).State = EntityState.Detached;
            return false;
        }
    }

    public async Task UpdateAsync(Account account)
    {
        context.Accounts.Update(account);
        await context.SaveChangesAsync();
    }

    public async Task UpdatePasswordAsync(Account account, string passwordHash)
    {
        var entry = context.Entry(account);
        if (entry.State == EntityState.Detached)
            context.Accounts.Attach(account);

        account.Password = passwordHash;
        entry.Property(a => a.Password).IsModified = true;
        await context.SaveChangesAsync();
    }

    public async Task SetOnlineServerAsync(int accountId, int serverId)
    {
        var account = await context.Accounts.FindAsync(accountId);
        if (account == null)
            return;

        account.OnlineServerId = serverId;
        account.OnlineSince = DateTime.UtcNow;
        await context.SaveChangesAsync();
    }

    public async Task ClearOnlineServerAsync(int accountId)
    {
        var account = await context.Accounts.FindAsync(accountId);
        if (account?.OnlineServerId == null)
            return;

        account.OnlineServerId = null;
        account.OnlineSince = null;
        await context.SaveChangesAsync();
    }

    public async Task<int> ClearOnlineServerForServerAsync(int serverId)
    {
        var stale = await context.Accounts.Where(a => a.OnlineServerId == serverId).ToListAsync();
        if (stale.Count == 0)
            return 0;

        foreach (var account in stale)
        {
            account.OnlineServerId = null;
            account.OnlineSince = null;
        }

        await context.SaveChangesAsync();
        return stale.Count;
    }
}
