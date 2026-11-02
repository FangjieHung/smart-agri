using SmartAgri.Domain.Coordinator;

namespace SmartAgri.Application.Coordinator;

/// <summary>
/// How an action of a shop will actually be carried out — 自動 or 提醒 — as the preview card shows it and a confirmed
/// command records it (store-coordinator design §3 Q18, #374, #375). The settings' <see cref="ShopAction.Mode"/> is
/// what the operator wants; whether it can run automatically depends on an integration being available
/// (<see cref="ICoordinatorActionExecutor.IsAvailableAsync"/>).
/// </summary>
public interface ICoordinatorActionModes
{
    /// <summary>The mode <paramref name="action"/> of <paramref name="organizationId"/> runs in now.</summary>
    ValueTask<ShopActionMode> ModeOfAsync(Guid organizationId, ShopAction action, CancellationToken cancellationToken);
}

/// <summary>
/// The registered <see cref="ICoordinatorActionExecutor"/>s by kind (the last one registered for a kind wins).
/// </summary>
public sealed class CoordinatorActionExecutors
{
    private readonly Dictionary<ShopActionKind, ICoordinatorActionExecutor> _byKind = [];

    public CoordinatorActionExecutors(IEnumerable<ICoordinatorActionExecutor> executors)
    {
        ArgumentNullException.ThrowIfNull(executors);
        foreach (var executor in executors)
        {
            if (executor.Kind == ShopActionKind.Ordering)
            {
                throw new InvalidOperationException("Ordering systems are always reminders (Q15): no executor may run them.");
            }

            _byKind[executor.Kind] = executor;
        }
    }

    /// <summary>The executor of <paramref name="kind"/>, or <see langword="null"/> when none is registered.</summary>
    public ICoordinatorActionExecutor? For(ShopActionKind kind) => _byKind.GetValueOrDefault(kind);
}

/// <summary>
/// The effective mode, shared by the preview card and the execution (#375): <see cref="ShopActionMode.Auto"/> exactly
/// when the settings want it automatic, it is not an ordering system, and its kind's executor is available for the
/// shop. With no executor registered (Slice 4 alone) every action is 提醒.
/// </summary>
public sealed class ExecutorActionModes : ICoordinatorActionModes
{
    private readonly CoordinatorActionExecutors _executors;

    public ExecutorActionModes(CoordinatorActionExecutors executors)
    {
        _executors = executors;
    }

    public async ValueTask<ShopActionMode> ModeOfAsync(Guid organizationId, ShopAction action, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (action.Mode != ShopActionMode.Auto || action.Kind == ShopActionKind.Ordering || _executors.For(action.Kind) is not { } executor)
        {
            return ShopActionMode.Reminder;
        }

        return await executor.IsAvailableAsync(organizationId, cancellationToken) ? ShopActionMode.Auto : ShopActionMode.Reminder;
    }
}
