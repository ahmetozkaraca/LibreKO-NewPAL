using LibreKO.Common.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace LibreKO.Game.Tests;

public sealed class SaveChangesProbe : SaveChangesInterceptor
{
    private int _saves;

    public int Saves => Volatile.Read(ref _saves);

    public Func<DbContext, Task>? BeforeSave { get; set; }

    public Func<DbContext, bool>? FailWhen { get; set; }

    public void Register(IServiceCollection services) =>
        services.ConfigureDbContext<AppDbContext>(options => options.AddInterceptors(this));

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _saves);
        var context = eventData.Context!;
        if (BeforeSave is { } before)
            await before(context);
        if (FailWhen?.Invoke(context) == true)
            throw new DbUpdateException("Injected commit failure.");
        return result;
    }
}
