using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using SmartAgri.Api.Authentication;
using SmartAgri.Api.Errors;
using SmartAgri.Domain;
using SmartAgri.Domain.Accounts;

namespace SmartAgri.Api.Authorization;

/// <summary>
/// One authorization policy per <see cref="AccountPermission"/> (M1 plan, Slice 5). Each
/// requires an authenticated caller whose account currently holds the permission, read
/// from the database per request (<see cref="RequestAccountPermissions"/>).
/// </summary>
public static class PermissionPolicies
{
    private const string Prefix = "permission:";

    /// <summary>The policy name for <paramref name="permission"/>, e.g.
    /// <c>permission:manage-assistants</c>.</summary>
    public static string NameFor(AccountPermission permission) => Prefix + WireNames<AccountPermission>.ToWire(permission);

    public static AuthorizationBuilder AddPermissionPolicies(this AuthorizationBuilder builder)
    {
        foreach (var permission in Enum.GetValues<AccountPermission>())
        {
            builder.AddPolicy(NameFor(permission), policy => policy
                .RequireAuthenticatedUser()
                .AddRequirements(new PermissionRequirement(permission)));
        }

        return builder;
    }

    /// <summary>
    /// Protects an endpoint with the policy for <paramref name="permission"/>. A caller
    /// without it gets <see cref="ApiErrors.Forbidden"/> for <paramref name="reason"/> —
    /// the same bytes the endpoint must return for "not found".
    /// </summary>
    public static TBuilder RequirePermission<TBuilder>(this TBuilder builder, AccountPermission permission, ForbiddenReason reason)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(reason);
        return builder
            .RequireAuthorization(NameFor(permission))
            .WithMetadata(new ForbiddenReasonMetadata(reason));
    }
}

/// <summary>The requirement behind each permission policy.</summary>
public sealed class PermissionRequirement : IAuthorizationRequirement
{
    public PermissionRequirement(AccountPermission permission)
    {
        Permission = permission;
    }

    public AccountPermission Permission { get; }
}

/// <summary>Succeeds when the caller's account (<c>sub</c>) currently holds the permission.</summary>
public sealed class PermissionAuthorizationHandler : AuthorizationHandler<PermissionRequirement>
{
    private readonly RequestAccountPermissions _permissions;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public PermissionAuthorizationHandler(RequestAccountPermissions permissions, IHttpContextAccessor httpContextAccessor)
    {
        _permissions = permissions;
        _httpContextAccessor = httpContextAccessor;
    }

    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, PermissionRequirement requirement)
    {
        if (AccountClaims.GetAccountId(context.User) is not { } accountId)
        {
            return;
        }

        var cancellationToken = _httpContextAccessor.HttpContext?.RequestAborted ?? CancellationToken.None;
        var granted = await _permissions.GetAsync(accountId, cancellationToken);
        if (granted.Contains(requirement.Permission))
        {
            context.Succeed(requirement);
        }
    }
}

/// <summary>Endpoint metadata naming the <c>403</c> reason a failed policy check returns.</summary>
public sealed class ForbiddenReasonMetadata
{
    public ForbiddenReasonMetadata(ForbiddenReason reason)
    {
        Reason = reason;
    }

    public ForbiddenReason Reason { get; }
}

/// <summary>
/// Turns the authorization middleware's "forbidden" into the API's <c>403</c>
/// ProblemDetails (<see cref="ApiErrors.Forbidden"/>) instead of the authentication
/// handler's bare <c>403</c>. "Not signed in" still goes to the authentication handler's
/// challenge: <c>401</c> with no body.
/// </summary>
public sealed class ApiAuthorizationResultHandler : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler _default = new();

    public async Task HandleAsync(
        RequestDelegate next,
        HttpContext context,
        AuthorizationPolicy policy,
        PolicyAuthorizationResult authorizeResult)
    {
        if (authorizeResult.Forbidden)
        {
            var reason = context.GetEndpoint()?.Metadata.GetMetadata<ForbiddenReasonMetadata>()?.Reason
                ?? ForbiddenReason.Unspecified;
            await ApiErrors.Forbidden(reason).ExecuteAsync(context);
            return;
        }

        await _default.HandleAsync(next, context, policy, authorizeResult);
    }
}
