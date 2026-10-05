using SmartAgri.Application.Validation;
using SmartAgri.Domain;
using SmartAgri.Domain.Reports;

namespace SmartAgri.Application.Reports;

/// <summary>What an assistant's report setting should be after an update: <see langword="null"/>
/// <see cref="Frequency"/> is "off" (no schedule row); otherwise the database it reports on.</summary>
public sealed record ReportScheduleChoice(ReportFrequency? Frequency, Guid? DatabaseId)
{
    public static readonly ReportScheduleChoice Off = new(null, null);

    /// <summary>The wire form shown on the settings (<c>rules.periodicReport</c>).</summary>
    public static string Wire(ReportFrequency? frequency) => frequency is { } value ? WireNames<ReportFrequency>.ToWire(value) : ReportScheduleRules.OffName;
}

/// <summary>
/// The assistant's <c>rules.periodicReport</c> setting (M4 #150, the mock's 「定期回報」: <c>off</c>,
/// <c>weekly</c> or <c>monthly</c>) and what it means: <b>the report is about the database the assistant
/// collects forms into</b> (<c>rules.dataWriteDatabaseId</c>, a database connected to the assistant that
/// its owner may use), as the mock's trend tab shows it. Turning it on needs that database; changing the
/// target moves the report with it; clearing the target turns the report off.
/// </summary>
public static class ReportScheduleRules
{
    public const string Field = "periodicReport";

    public const string OffName = "off";

    public const string InvalidMessage = "定期回報設定不正確。";

    public const string NeedsTargetMessage = "請先指定要寫入的資料庫，才能設定定期回報。";

    public const string TargetNotUsableMessage = "只能為已連接到這個助理、而且你仍可使用的資料庫設定定期回報。";

    /// <param name="requested"><see langword="null"/> keeps the current frequency; <c>off</c>, <c>weekly</c>
    /// or <c>monthly</c> sets it.</param>
    /// <param name="current">The frequency of the existing schedule, if any.</param>
    /// <param name="targetDatabaseId">The assistant's form target after this update, if any.</param>
    /// <param name="usableConnectedIds">The connected databases the owner may use right now.</param>
    public static ValidationResult<ReportScheduleChoice> ForUpdate(
        string? requested,
        ReportFrequency? current,
        Guid? targetDatabaseId,
        IReadOnlyCollection<Guid> usableConnectedIds)
    {
        ArgumentNullException.ThrowIfNull(usableConnectedIds);

        var frequency = current;
        if (requested is not null)
        {
            if (requested == OffName)
            {
                frequency = null;
            }
            else if (WireNames<ReportFrequency>.All.Contains(requested))
            {
                frequency = WireNames<ReportFrequency>.Parse(requested);
            }
            else
            {
                return ValidationResult<ReportScheduleChoice>.Invalid(Field, InvalidMessage);
            }
        }

        if (frequency is null)
        {
            return ValidationResult<ReportScheduleChoice>.Valid(ReportScheduleChoice.Off);
        }

        if (targetDatabaseId is not { } target)
        {
            return requested is null
                ? ValidationResult<ReportScheduleChoice>.Valid(ReportScheduleChoice.Off)
                : ValidationResult<ReportScheduleChoice>.Invalid(Field, NeedsTargetMessage);
        }

        // Only a newly chosen setting is held to "usable now"; a schedule that merely stays is
        // re-checked by every period's job (and skipped, with the reason, when it fails).
        var isNew = requested is not null && requested != OffName;
        if (isNew && !usableConnectedIds.Contains(target))
        {
            return ValidationResult<ReportScheduleChoice>.Invalid(Field, TargetNotUsableMessage);
        }

        return ValidationResult<ReportScheduleChoice>.Valid(new ReportScheduleChoice(frequency, target));
    }

    /// <summary>Whether <paramref name="choice"/> differs from the existing schedule.</summary>
    public static bool Differs(ReportFrequency? currentFrequency, Guid? currentDatabaseId, ReportScheduleChoice choice)
    {
        ArgumentNullException.ThrowIfNull(choice);
        return currentFrequency != choice.Frequency || currentDatabaseId != choice.DatabaseId;
    }

    /// <summary>
    /// Whether an update re-enables an auto-disabled schedule (#179): the request names a frequency
    /// (<c>weekly</c>/<c>monthly</c>, the same one or another) and the result keeps a schedule. Such a request
    /// went through <see cref="ForUpdate"/> as a new setting, so the target was re-checked as usable. A request
    /// that does not name <c>periodicReport</c> never re-enables (changing only the collection purpose leaves
    /// a disabled schedule disabled).
    /// </summary>
    public static bool Resumes(bool currentIsAutoDisabled, string? requested, ReportScheduleChoice choice)
    {
        ArgumentNullException.ThrowIfNull(choice);
        return currentIsAutoDisabled && requested is not null && requested != OffName && choice.Frequency is not null;
    }

    /// <summary>
    /// The schedule's skip counter after a period (#179): a skipped period (<paramref name="skipReason"/> not
    /// <see langword="null"/>) adds one, a generated one resets it to zero. The period that brings it to
    /// <see cref="ReportSchedule.AutoDisableAfterSkips"/> disables the schedule with that period's reason.
    /// </summary>
    public static ReportScheduleProgress AfterPeriod(int consecutiveSkips, ReportSkipReason? skipReason)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(consecutiveSkips);
        if (skipReason is not { } reason)
        {
            return new ReportScheduleProgress(0, null);
        }

        var skips = consecutiveSkips + 1;
        return new ReportScheduleProgress(skips, skips >= ReportSchedule.AutoDisableAfterSkips ? reason : null);
    }

    /// <summary>
    /// The schedule's counter after a period that came due while its database was archived (#180): the
    /// schedule is paused, so the period is neither generated nor skipped — the counter stays as it was and
    /// the schedule is never disabled by it.
    /// </summary>
    public static ReportScheduleProgress WhileArchived(int consecutiveSkips)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(consecutiveSkips);
        return new ReportScheduleProgress(consecutiveSkips, null);
    }

    /// <summary>What the owner's settings say about an auto-disabled schedule.</summary>
    public static string AutoDisabledMessage(ReportSkipReason reason) => reason switch
    {
        ReportSkipReason.NotConnected =>
            $"已自動停用：連續 {ReportSchedule.AutoDisableAfterSkips} 期沒有產生報表，最近一期是因為助理已不再連接這個數據庫。重新連接後可以重新啟用。",
        ReportSkipReason.OwnerCannotRead =>
            $"已自動停用：連續 {ReportSchedule.AutoDisableAfterSkips} 期沒有產生報表，最近一期是因為助理擁有者無法讀取這個數據庫的紀錄。恢復權限後可以重新啟用。",
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "Not a declared reason."),
    };
}

/// <summary>A schedule's counter after a period; <see cref="DisabledReason"/> is set when that period disables it.</summary>
public sealed record ReportScheduleProgress(int ConsecutiveSkips, ReportSkipReason? DisabledReason)
{
    public bool Disables => DisabledReason is not null;
}
