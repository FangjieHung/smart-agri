var builder = WebApplication.CreateBuilder(args);

var app = builder.Build();

app.MapGet("/health/live", () => Results.Ok());

app.Run();

namespace SmartAgri.Api
{
    /// <summary>
    /// Exposes the generated <c>Program</c> class so integration tests can
    /// bootstrap the API with <see cref="Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory{TEntryPoint}"/>.
    /// </summary>
    public partial class Program
    {
    }
}
