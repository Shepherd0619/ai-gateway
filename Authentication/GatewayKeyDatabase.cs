using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AiGateway.Authentication;

internal sealed record GatewayKeyOptions
{
    public bool Enabled { get; init; }
}

internal static class GatewayKeyDatabase
{
    public static IServiceCollection AddGatewayKeys(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<GatewayKeyOptions>(config.GetSection("GatewayKeys"));
        services.TryAddSingleton(TimeProvider.System);
        if (!config.GetValue<bool>("GatewayKeys:Enabled")) return services;

        string connection;
        try
        {
            var value = config.GetConnectionString("GatewayKeys");
            if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException();
            var parsed = new SqliteConnectionStringBuilder(value);
            if (string.IsNullOrWhiteSpace(parsed.DataSource) || parsed.DataSource.Contains(":memory:", StringComparison.OrdinalIgnoreCase) ||
                parsed.DataSource.Contains("mode=memory", StringComparison.OrdinalIgnoreCase) || parsed.Mode == SqliteOpenMode.Memory)
                throw new ArgumentException();
            parsed.DefaultTimeout = 3;
            connection = parsed.ToString();
        }
        catch (ArgumentException)
        {
            throw new InvalidOperationException("GatewayKeys requires a valid file-backed SQLite ConnectionStrings:GatewayKeys configuration.");
        }

        services.AddDbContextFactory<GatewayKeyDbContext>(options => options.UseSqlite(connection, sqlite => sqlite.CommandTimeout(3)));
        services.AddSingleton<GatewayKeyStore>();
        return services;
    }

    public static async Task InitializeAsync(IServiceProvider services, CancellationToken ct = default)
    {
        if (services.GetService<IDbContextFactory<GatewayKeyDbContext>>() is not { } factory) return;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            // SQLite ADO.NET may run synchronously: keep startup's deadline observable even during a lock wait.
            await Task.Run(async () =>
            {
                await using var db = await factory.CreateDbContextAsync(deadline.Token);
                await db.Database.MigrateAsync(deadline.Token);
            }, deadline.Token).WaitAsync(TimeSpan.FromSeconds(30), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            deadline.Cancel();
            throw new InvalidOperationException("Gateway key database initialization failed. Check the writable volume, migrations, and migration lock recovery instructions.");
        }
    }
}

// Tooling only; creates no connection or database while scaffolding migrations.
internal sealed class GatewayKeyDesignFactory : IDesignTimeDbContextFactory<GatewayKeyDbContext>
{
    public GatewayKeyDbContext CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<GatewayKeyDbContext>()
        .UseSqlite("Data Source=gateway-keys.db").Options);
}
