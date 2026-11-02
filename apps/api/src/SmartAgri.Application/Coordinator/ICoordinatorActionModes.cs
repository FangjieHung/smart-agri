using SmartAgri.Domain.Coordinator;

namespace SmartAgri.Application.Coordinator;

/// <summary>
/// How an action of a shop will actually be carried out — 自動 or 提醒 — as the preview card shows it
/// (store-coordinator design §3 Q18, #374). The settings' <see cref="ShopAction.Mode"/> is what the operator
/// wants; whether it can run automatically depends on integrations that come later (LINE 群發 Slice 5, Google
/// Slice 7, FB Slice 8: a token, an approved API, an authorization). Each of those slices replaces this seam.
/// </summary>
public interface ICoordinatorActionModes
{
    /// <summary>The mode <paramref name="action"/> of <paramref name="organizationId"/> runs in now.</summary>
    ValueTask<ShopActionMode> ModeOfAsync(Guid organizationId, ShopAction action, CancellationToken cancellationToken);
}

/// <summary>This slice's <see cref="ICoordinatorActionModes"/>: nothing runs automatically yet, so every action
/// is 提醒 whatever its settings say.</summary>
public sealed class ReminderOnlyActionModes : ICoordinatorActionModes
{
    public ValueTask<ShopActionMode> ModeOfAsync(Guid organizationId, ShopAction action, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        return ValueTask.FromResult(ShopActionMode.Reminder);
    }
}
