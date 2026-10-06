using Microsoft.EntityFrameworkCore;
using SmartAgri.Infrastructure;

namespace SmartAgri.Api.Accounts;

/// <summary>
/// The one account name lookup (M7 plan §3 C, decision C): accounts are deactivated, never deleted,
/// but a history row may still name one that can no longer be found — then it shows as
/// <see cref="Removed"/>. Replaces the fallback strings that the database access view, the
/// submission views and the settings' 「上次變更」 each kept, and the issue views' lookup.
/// </summary>
public static class AccountNames
{
    /// <summary>What an account that can no longer be found is called on every screen.</summary>
    public const string Removed = "已停用的帳號";

    /// <summary>Reads the display names of <paramref name="accountIds"/> (of the current
    /// organization, under its filter) in one query.</summary>
    public static async Task<AccountNameLookup> LoadAsync(
        AppDbContext dbContext, IEnumerable<Guid?> accountIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(accountIds);
        var ids = accountIds.OfType<Guid>().Distinct().ToList();
        if (ids.Count == 0)
        {
            return AccountNameLookup.Empty;
        }

        return new AccountNameLookup(await dbContext.Accounts.AsNoTracking()
            .Where(account => ids.Contains(account.Id))
            .ToDictionaryAsync(account => account.Id, account => account.DisplayName, cancellationToken));
    }

    /// <inheritdoc cref="LoadAsync(AppDbContext, IEnumerable{Guid?}, CancellationToken)"/>
    public static Task<AccountNameLookup> LoadAsync(
        AppDbContext dbContext, IEnumerable<Guid> accountIds, CancellationToken cancellationToken) =>
        LoadAsync(dbContext, accountIds.Select(id => (Guid?)id), cancellationToken);

    /// <summary>The display name of one account, or <see cref="Removed"/>.</summary>
    public static async Task<string> NameOfAsync(AppDbContext dbContext, Guid accountId, CancellationToken cancellationToken) =>
        (await LoadAsync(dbContext, [accountId], cancellationToken)).NameOf(accountId);
}

/// <summary>Display names read by <see cref="AccountNames.LoadAsync(AppDbContext, IEnumerable{Guid?}, CancellationToken)"/>
/// (or from accounts a query already loaded, <see cref="From"/>).</summary>
public sealed class AccountNameLookup
{
    public static readonly AccountNameLookup Empty = new(new Dictionary<Guid, string>());

    private readonly IReadOnlyDictionary<Guid, string> _names;

    internal AccountNameLookup(IReadOnlyDictionary<Guid, string> names)
    {
        _names = names;
    }

    /// <summary>Wraps names already loaded with the accounts a view needed anyway.</summary>
    public static AccountNameLookup From(IReadOnlyDictionary<Guid, string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        return new AccountNameLookup(names);
    }

    /// <summary>The account's display name, or <see cref="AccountNames.Removed"/> when it can no
    /// longer be found.</summary>
    public string NameOf(Guid accountId) => _names.GetValueOrDefault(accountId) ?? AccountNames.Removed;

    /// <summary><see cref="NameOf(Guid)"/>, or <see langword="null"/> when there is no account.</summary>
    public string? NameOf(Guid? accountId) => accountId is { } id ? NameOf(id) : null;
}
