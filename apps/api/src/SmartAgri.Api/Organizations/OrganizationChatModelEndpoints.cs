using Microsoft.EntityFrameworkCore;
using SmartAgri.Api.Authentication;
using SmartAgri.Api.Authorization;
using SmartAgri.Api.Errors;
using SmartAgri.Domain.Organizations;
using SmartAgri.Infrastructure;
using SmartAgri.Infrastructure.Ai;

namespace SmartAgri.Api.Organizations;

/// <summary>One chat model the deployment offers, as anyone may see it: never its key, endpoint or
/// provider settings.</summary>
/// <param name="Id">What <c>PUT</c> sends as <c>modelId</c>.</param>
/// <param name="DisplayName">The name people see.</param>
/// <param name="Model">The model name, what each answer's and test run's records name.</param>
public sealed record ChatModelOptionView(string Id, string DisplayName, string Model);

/// <summary>
/// <c>GET /api/v1/organization/chat-model</c>'s response (M6 plan §3 D, issue #239).
/// </summary>
/// <param name="Options">The deployment's chat models, the default first; empty when the deployment
/// has none.</param>
/// <param name="SelectedId">The organization's choice; <see langword="null"/> for the deployment
/// default.</param>
/// <param name="Effective">The model new answers, test runs and reports use now;
/// <see langword="null"/> only when the deployment has no chat model at all.</param>
/// <param name="Source"><c>selected</c>, <c>deployment-default</c> (nothing chosen) or <c>removed</c>
/// (the choice is no longer offered: the default instead).</param>
/// <param name="CanChange">Whether the caller is the organization's manager (may <c>PUT</c>).</param>
/// <param name="LastChange">The last change, or <see langword="null"/> before the first.</param>
/// <param name="Revision">What a <c>PUT</c> sends back (<c>Organization.SettingsRevision</c>).</param>
public sealed record OrganizationChatModelView(
    IReadOnlyList<ChatModelOptionView> Options,
    string? SelectedId,
    ChatModelOptionView? Effective,
    ChatModelSource Source,
    bool CanChange,
    OrganizationSettingChangeView? LastChange,
    int Revision);

/// <summary><c>PUT /api/v1/organization/chat-model</c>: <see cref="ModelId"/> one of the options'
/// ids, or <see langword="null"/> for the deployment default; <see cref="Revision"/> as read.</summary>
public sealed record UpdateOrganizationChatModelRequest(string? ModelId, int Revision);

/// <summary>
/// The organization's chat model (M6 plan §5 Slice 2, issue #239). Anyone in the organization may
/// read it — members can know which model answers them, and the acceptance tab compares its
/// <c>effective.model</c> with each test run's — while only the manager may change it
/// (<see cref="OrganizationAdminPolicy.RequireOrganizationAdmin{TBuilder}"/>, <c>403
/// organization-settings</c>). A change applies to the next answer, test run or report: the
/// resolver reads the choice once per scope.
/// </summary>
public static class OrganizationChatModelEndpoints
{
    public const string Path = "/api/v1/organization/chat-model";

    internal const string UnknownModelMessage = "這個模型不在部署提供的清單中，請重新載入後再選擇。";

    private static readonly OrganizationActivityAction[] ChangeActions = [OrganizationActivityAction.ChatModelChanged];

    public static IEndpointRouteBuilder MapOrganizationChatModelEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(Path, GetAsync)
            .RequireAuthorization()
            .Produces<OrganizationChatModelView>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);

        endpoints.MapPut(Path, UpdateAsync)
            .RequireOrganizationAdmin(ForbiddenReason.OrganizationSettings)
            .Produces<OrganizationChatModelView>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status422UnprocessableEntity);
        return endpoints;
    }

    internal static async Task<IResult> GetAsync(
        HttpContext httpContext,
        AppDbContext dbContext,
        ChatModelCatalog catalog,
        RequestAccountRole roles,
        CancellationToken cancellationToken)
    {
        if (AccountClaims.GetAccountId(httpContext.User) is null
            || await OrganizationSettings.FindAsync(dbContext, tracked: false, cancellationToken) is not { } organization)
        {
            return ApiErrors.Unauthorized();
        }

        return Results.Ok(await ViewAsync(dbContext, catalog, roles, httpContext, organization, cancellationToken));
    }

    /// <summary>
    /// Order of checks after the manager check: the id is offered (else <c>422</c>
    /// <c>errors.modelId</c>), the revision is current (else <c>409</c>); the same value again
    /// answers <c>200</c> without writing anything. A change writes one
    /// <see cref="OrganizationActivityAction.ChatModelChanged"/> row in the same save.
    /// </summary>
    internal static async Task<IResult> UpdateAsync(
        UpdateOrganizationChatModelRequest request,
        HttpContext httpContext,
        AppDbContext dbContext,
        ChatModelCatalog catalog,
        RequestAccountRole roles,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        if (AccountClaims.GetAccountId(httpContext.User) is not { } callerId
            || await OrganizationSettings.FindAsync(dbContext, tracked: true, cancellationToken) is not { } organization)
        {
            return ApiErrors.Unauthorized();
        }

        ChatModelEntry? chosen = null;
        if (!string.IsNullOrWhiteSpace(request.ModelId) && (chosen = catalog.Find(request.ModelId)) is null)
        {
            return ApiErrors.ValidationFailed(
                UnknownModelMessage, new Dictionary<string, string[]> { ["modelId"] = [UnknownModelMessage] });
        }

        var before = Choice(catalog, organization.ChatModelId);
        switch (organization.ChangeChatModel(chosen?.Id, request.Revision))
        {
            case OrganizationSettingsChange.RevisionConflict:
                return OrganizationSettings.RevisionConflict();
            case OrganizationSettingsChange.Changed:
                dbContext.OrganizationActivities.Add(OrganizationActivity.ChatModelChanged(
                    organization.Id, callerId, clock.GetUtcNow(), before, Choice(catalog, organization.ChatModelId)));
                try
                {
                    await dbContext.SaveChangesAsync(cancellationToken);
                }
                catch (DbUpdateConcurrencyException)
                {
                    // Another PUT saved between our read and our write (SettingsRevision is a
                    // concurrency token): nothing of ours was written.
                    return OrganizationSettings.RevisionConflict();
                }

                break;
        }

        return Results.Ok(await ViewAsync(dbContext, catalog, roles, httpContext, organization, cancellationToken));
    }

    private static async Task<OrganizationChatModelView> ViewAsync(
        AppDbContext dbContext,
        ChatModelCatalog catalog,
        RequestAccountRole roles,
        HttpContext httpContext,
        Organization organization,
        CancellationToken cancellationToken)
    {
        // The same rule the scoped resolver applies to every chat call (from this row, so a PUT's
        // response already shows the new choice).
        var resolved = OrganizationChatModelResolver.Resolve(catalog, organization.ChatModelId);
        return new OrganizationChatModelView(
            [.. catalog.Entries.Select(Option)],
            organization.ChatModelId,
            catalog.IsConfigured ? Option(resolved.Entry) : null,
            resolved.Source,
            await roles.IsOrganizationAdminAsync(httpContext.User, cancellationToken),
            await OrganizationSettings.LastChangeAsync(dbContext, ChangeActions, cancellationToken),
            organization.SettingsRevision);
    }

    private static ChatModelOptionView Option(ChatModelEntry entry) => new(entry.Id, entry.DisplayName, entry.Model);

    /// <summary>An activity side: the stored id and the name that id has in the list now
    /// (<see langword="null"/> id: the deployment default's name; an id no longer offered: no name).</summary>
    private static ChatModelChoice Choice(ChatModelCatalog catalog, string? chatModelId) =>
        new(chatModelId, chatModelId is null ? catalog.DeploymentDefault.DisplayName : catalog.Find(chatModelId)?.DisplayName);
}
