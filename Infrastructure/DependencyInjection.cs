using Application.Common.Abstractions;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services, string connectionString, bool enableSensitiveDataLogging = false)
    {
        services.AddDbContext<SchoolDbContext>(options => options
            .UseSqlite(connectionString)
            .EnableSensitiveDataLogging(enableSensitiveDataLogging));
        services.AddScoped<IApplicationDbContext>(provider => provider.GetRequiredService<SchoolDbContext>());
        services.AddHealthChecks().AddDbContextCheck<SchoolDbContext>("database",
            customTestQuery: async (db, cancellationToken) =>
            {
                await db.Courses.AnyAsync(cancellationToken);
                return true;
            });

        return services;
    }

    public static async Task MigrateDatabaseAsync(
        this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SchoolDbContext>();
        await db.Database.MigrateAsync(cancellationToken);
    }
}
