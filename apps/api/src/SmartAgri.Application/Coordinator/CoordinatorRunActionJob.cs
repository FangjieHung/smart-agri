namespace SmartAgri.Application.Coordinator;

/// <summary>
/// The payload of a <see cref="Kind"/> background job (store-coordinator design §5 Slice 4, #375): one action of a
/// confirmed command, run at its due time (<c>RunAfter</c>). Only the id: the job reads the row when it runs and does
/// nothing unless it is still scheduled, so cancelling an action is setting its row cancelled.
/// </summary>
public sealed record CoordinatorRunActionJob(Guid ActionId)
{
    public const string Kind = "coordinator.run-action";
}
