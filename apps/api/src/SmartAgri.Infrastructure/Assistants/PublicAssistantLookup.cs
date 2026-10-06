using Microsoft.EntityFrameworkCore;

namespace SmartAgri.Infrastructure.Assistants;

/// <summary>
/// Finds which organization an assistant belongs to, for an anonymous website visitor starting a
/// session (M5a #196, <c>POST /api/v1/public/assistants/{id}/visitor-sessions</c>). Like sign-in
/// (<see cref="Accounts.AccountLookup"/>), nobody is signed in at that point, so the organization
/// filter would hide every assistant; this is the only other place in <c>apps/api/src</c> allowed to
/// switch that filter off (a source-scanning test enforces it).
/// </summary>
/// <remarks>
/// It returns the organization id and nothing else. The caller pins that organization for the
/// request and then reads the assistant, its website channel and everything else under the usual
/// filter and write guard — so nothing of another organization is ever loaded through here.
/// </remarks>
public sealed class PublicAssistantLookup
{
    private readonly AppDbContext _dbContext;

    public PublicAssistantLookup(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    /// <summary>The organization of assistant <paramref name="assistantId"/>, or
    /// <see langword="null"/> when there is no such assistant in any organization.</summary>
    public async Task<Guid?> FindOrganizationIdAsync(Guid assistantId, CancellationToken cancellationToken = default) =>
        await _dbContext.Assistants
            .IgnoreQueryFilters([AppDbContext.OrganizationFilter])
            .AsNoTracking()
            .Where(assistant => assistant.Id == assistantId)
            .Select(assistant => (Guid?)assistant.OrganizationId)
            .SingleOrDefaultAsync(cancellationToken);
}
