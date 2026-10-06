using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace FHSMS.Infrastructure.Persistence;

/// <summary>
/// Used ONLY by `dotnet ef` tooling (migrations add / database update). It lets
/// EF build the DbContext directly instead of booting the whole API - which
/// would otherwise run the startup schema-creation + seeding block and need a
/// live database just to generate a migration.
/// Reads ConnectionStrings__DefaultConnection from the environment if set.
/// </summary>
public class ApplicationDbContextFactory : IDesignTimeDbContextFactory<ApplicationDbContext>
{
    public ApplicationDbContext CreateDbContext(string[] args)
    {
        var connectionString =
            Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection")
            ?? "Host=localhost;Port=5432;Database=fhsms;Username=fhsms_user;Password=change_me";

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(connectionString, b => b.MigrationsAssembly(typeof(ApplicationDbContext).Assembly.FullName))
            .Options;

        return new ApplicationDbContext(options);
    }
}
