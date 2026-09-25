using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace SmartAgri.Infrastructure;

/// <summary>
/// Lets <c>dotnet ef migrations add</c> build the model without a running database or
/// the full Api host: the connection string here is never opened at design time, EF
/// Core only needs it to pick the Npgsql provider.
/// </summary>
public class AppDbContextDesignTimeFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<AppDbContext>();
        optionsBuilder.UseNpgsql("Host=localhost;Port=5432;Database=smartagri;Username=smartagri;Password=smartagri");

        return new AppDbContext(optionsBuilder.Options);
    }
}
