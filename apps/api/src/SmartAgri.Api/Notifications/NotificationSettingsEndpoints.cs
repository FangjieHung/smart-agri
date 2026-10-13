using Microsoft.EntityFrameworkCore;
using SmartAgri.Api.Authentication;
using SmartAgri.Api.Errors;
using SmartAgri.Application.Notifications;
using SmartAgri.Application.Validation;
using SmartAgri.Domain.Notifications;
using SmartAgri.Infrastructure;

namespace SmartAgri.Api.Notifications;

/// <summary>The signed-in account's own notification settings (#355). <see cref="EmailDeliveryConfigured"/>
/// says whether this deployment can send Email at all (<c>Smtp:Host</c>), so the admin can explain why
/// nothing would arrive.</summary>
public sealed record NotificationSettingsView(string? Email, bool HandoffEmails, bool EmailDeliveryConfigured);

/// <summary><c>email</c> blank or <see langword="null"/> clears the address.</summary>
public sealed record UpdateNotificationSettingsRequest(string? Email, bool HandoffEmails);

/// <summary>
/// <c>GET</c>/<c>PUT /api/v1/me/notification-settings</c> (#355): each account sets its own address for
/// notification Emails and may turn the 轉真人 Email off. Only ever the caller's own row — there is no
/// id in the path to point at someone else's. Without a row: no address, Emails on.
/// </summary>
public static class NotificationSettingsEndpoints
{
    public const string Path = "/api/v1/me/notification-settings";

    public static IEndpointRouteBuilder MapNotificationSettingsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(Path, GetAsync)
            .RequireAuthorization()
            .Produces<NotificationSettingsView>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized);

        endpoints.MapPut(Path, UpdateAsync)
            .RequireAuthorization()
            .Produces<NotificationSettingsView>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status422UnprocessableEntity);
        return endpoints;
    }

    internal static async Task<IResult> GetAsync(
        HttpContext httpContext, AppDbContext dbContext, IEmailSender email, CancellationToken cancellationToken)
    {
        if (await CallerAsync(httpContext, dbContext, cancellationToken) is not { } callerId)
        {
            return ApiErrors.Unauthorized();
        }

        var settings = await dbContext.AccountNotificationSettings.AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.AccountId == callerId, cancellationToken);
        return Results.Ok(new NotificationSettingsView(settings?.Email, settings?.HandoffEmails ?? true, email.IsConfigured));
    }

    internal static async Task<IResult> UpdateAsync(
        UpdateNotificationSettingsRequest request,
        HttpContext httpContext,
        AppDbContext dbContext,
        IEmailSender email,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        if (await CallerAsync(httpContext, dbContext, cancellationToken) is not { } callerId)
        {
            return ApiErrors.Unauthorized();
        }

        if (!AccountNotificationSettings.TryNormalizeEmail(request.Email, out _))
        {
            return ApiErrors.ValidationFailed([new ValidationFailure("email", AccountNotificationSettings.EmailInvalidMessage)]);
        }

        var now = clock.GetUtcNow();
        var settings = await dbContext.AccountNotificationSettings
            .SingleOrDefaultAsync(candidate => candidate.AccountId == callerId, cancellationToken);
        if (settings is null)
        {
            settings = AccountNotificationSettings.CreateDefault(
                dbContext.OrganizationContext.OrganizationId ?? throw new InvalidOperationException("A signed-in request acts for an organization."),
                callerId,
                now);
            dbContext.AccountNotificationSettings.Add(settings);
        }

        settings.Update(request.Email, request.HandoffEmails, now);
        await dbContext.SaveChangesAsync(cancellationToken);
        return Results.Ok(new NotificationSettingsView(settings.Email, settings.HandoffEmails, email.IsConfigured));
    }

    /// <summary>The caller, when it is an account of the token's organization.</summary>
    private static async Task<Guid?> CallerAsync(HttpContext httpContext, AppDbContext dbContext, CancellationToken cancellationToken)
    {
        if (AccountClaims.GetAccountId(httpContext.User) is not { } callerId)
        {
            return null;
        }

        return await dbContext.Accounts.AsNoTracking().AnyAsync(account => account.Id == callerId, cancellationToken) ? callerId : null;
    }
}
