using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using SmartAgri.Api.Jobs;
using SmartAgri.Application.Notifications;
using SmartAgri.Infrastructure.Notifications;

namespace SmartAgri.Api.Notifications;

public static class NotificationServiceCollectionExtensions
{
    /// <summary>
    /// Registers 轉真人 notifications (#355): <see cref="SmtpOptions"/> from the <c>Smtp</c> section
    /// (validated on start, nothing required), the SMTP <see cref="IEmailSender"/>, the per-conversation
    /// <see cref="VisitorHandoffThrottle"/>, the <see cref="VisitorHandoffNotifier"/> the visitor channels
    /// call, and the <see cref="SendHandoffEmailJob.Kind"/> job handler. Requires <c>AddBackgroundJobs</c>.
    /// </summary>
    public static IServiceCollection AddVisitorHandoffNotifications(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<SmtpOptions>()
            .Bind(configuration.GetSection(SmtpOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<SmtpOptions>, SmtpOptionsValidator>();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IEmailSender, SmtpEmailSender>();
        services.AddSingleton<VisitorHandoffThrottle>();
        services.AddScoped<VisitorHandoffNotifier>();
        services.AddJobHandler<SendHandoffEmailHandler>(SendHandoffEmailJob.Kind);
        return services;
    }

    private sealed class SmtpOptionsValidator : IValidateOptions<SmtpOptions>
    {
        private readonly IHostEnvironment _environment;

        public SmtpOptionsValidator(IHostEnvironment environment)
        {
            _environment = environment;
        }

        public ValidateOptionsResult Validate(string? name, SmtpOptions options) =>
            options.Validate(_environment.EnvironmentName) is { } error ? ValidateOptionsResult.Fail(error) : ValidateOptionsResult.Success;
    }
}
