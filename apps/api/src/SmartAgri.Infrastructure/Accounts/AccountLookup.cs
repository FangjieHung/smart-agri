using Microsoft.EntityFrameworkCore;
using SmartAgri.Domain.Organizations;

namespace SmartAgri.Infrastructure.Accounts;

/// <summary>
/// Finds the account a sign-in attempt refers to. At that point nobody is signed in, so
/// there is no current organization and the organization filter would hide every account;
/// this is therefore the one place in <c>apps/api/src</c> allowed to switch that filter
/// off. A source-scanning test fails if the switch appears anywhere else.
/// </summary>
/// <remarks>
/// The organization is pinned explicitly instead: the result is only ever an account of
/// the organization whose code was given. Results are not tracked, so nothing loaded here
/// can be written back without first establishing that organization as the current one.
/// </remarks>
public sealed class AccountLookup
{
    private readonly AppDbContext _dbContext;

    public AccountLookup(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    /// <summary>
    /// The account named <paramref name="loginName"/> in the organization whose code is
    /// <paramref name="organizationCode"/>, or <see langword="null"/> when either does not
    /// exist (callers must not reveal which).
    /// </summary>
    public async Task<Account?> FindForSignInAsync(
        string organizationCode,
        string loginName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(organizationCode);
        ArgumentNullException.ThrowIfNull(loginName);

        string code;
        try
        {
            code = Organization.NormalizeCode(organizationCode);
        }
        catch (ArgumentException)
        {
            return null;
        }

        var normalizedLoginName = Account.NormalizeLoginName(loginName);
        if (normalizedLoginName.Length == 0)
        {
            return null;
        }

        return await _dbContext.Accounts
            .IgnoreQueryFilters([AppDbContext.OrganizationFilter])
            .AsNoTracking()
            .Where(account => account.NormalizedLoginName == normalizedLoginName
                && _dbContext.Organizations.Any(organization =>
                    organization.Id == account.OrganizationId && organization.Code == code))
            .SingleOrDefaultAsync(cancellationToken);
    }
}
