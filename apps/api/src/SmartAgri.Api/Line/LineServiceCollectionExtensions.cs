using Microsoft.Extensions.Options;
using SmartAgri.Application.Line;
using SmartAgri.Infrastructure.Line;

namespace SmartAgri.Api.Line;

public static class LineServiceCollectionExtensions
{
    /// <summary>
    /// Registers the LINE Messaging API client (M5b plan §3 H): <see cref="LineOptions"/> from the
    /// <c>Line</c> section, validated on start (in Production <c>Line:ApiBaseUrl</c> must be LINE's
    /// own address), and <see cref="ILineMessagingClient"/> as a typed client of the named
    /// <see cref="HttpClient"/> <see cref="LineMessagingClient.HttpClientName"/> — base address
    /// <c>Line:ApiBaseUrl</c>, a 10-second timeout, and none of <c>IHttpClientFactory</c>'s request
    /// loggers (the client logs method, path, status and <c>x-line-request-id</c> itself).
    /// </summary>
    public static IServiceCollection AddLineMessaging(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<LineOptions>()
            .Bind(configuration.GetSection(LineOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<LineOptions>, LineOptionsValidator>();

        services.AddHttpClient<ILineMessagingClient, LineMessagingClient>(LineMessagingClient.HttpClientName, (provider, http) =>
            {
                // Validated on start; the official address stands in only if a host skipped that.
                http.BaseAddress = provider.GetRequiredService<IOptions<LineOptions>>().Value.ResolvedApiBaseUrl
                    ?? new Uri(LineOptions.OfficialApiBaseUrl + "/");
                http.Timeout = LineMessagingClient.Timeout;
            })
            .RemoveAllLoggers();
        return services;
    }

    private sealed class LineOptionsValidator : IValidateOptions<LineOptions>
    {
        private readonly IHostEnvironment _environment;

        public LineOptionsValidator(IHostEnvironment environment)
        {
            _environment = environment;
        }

        public ValidateOptionsResult Validate(string? name, LineOptions options) =>
            options.Validate(_environment.EnvironmentName) is { } error ? ValidateOptionsResult.Fail(error) : ValidateOptionsResult.Success;
    }
}
