using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace SmartAgri.Api.Tests.Infrastructure;

/// <summary>
/// Adds <c>GET /test/protected</c> to a test host: an endpoint that declares nothing, so
/// it gets the Api's fallback policy (signed-in caller) and the "must change password"
/// gate, like any real endpoint added later. M1 has no such product endpoint yet besides
/// the gate's own exemptions (<c>/me</c>, change-password), and the tests need one.
/// </summary>
/// <remarks>
/// Minimal hosting gives tests no hook into <c>Program.cs</c>'s route mapping, so a
/// startup filter serves the path from its own branch with routing, authentication and
/// authorization middleware. Everything that decides the outcome — authentication
/// schemes, the fallback policy, <c>ApiAuthorizationResultHandler</c> and its gate, the
/// flag source — is resolved from the Api's own service container, exactly as for the
/// Api's endpoints.
/// </remarks>
public static class ProtectedProbeEndpoint
{
    public const string Path = "/test/protected";

    public static IServiceCollection AddProtectedProbeEndpoint(this IServiceCollection services) =>
        services.AddSingleton<IStartupFilter, Filter>();

    private sealed class Filter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.MapWhen(
                context => context.Request.Path.Equals(Path, StringComparison.Ordinal),
                branch =>
                {
                    branch.UseRouting();
                    branch.UseAuthentication();
                    branch.UseAuthorization();
                    branch.UseEndpoints(endpoints => endpoints.MapGet(Path, () => Results.Text("ok")));
                });
            next(app);
        };
    }
}
