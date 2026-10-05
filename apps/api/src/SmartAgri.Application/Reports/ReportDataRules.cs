using SmartAgri.Application.Databases;
using SmartAgri.Domain.Reports;

namespace SmartAgri.Application.Reports;

/// <summary>
/// When a report has enough records to say anything about change (M4 #150). A report always keeps the
/// counts and sums the fixed query returned — they are facts — but a <b>change or trend</b> is only
/// real when both the period and the period before it have records; otherwise the report is saved as
/// <see cref="ReportDataState.InsufficientRecords"/> (紀錄不足), shows no change, chart trend or AI
/// summary, and says so.
/// </summary>
public static class ReportDataRules
{
    public static ReportDataState StateOf(DatabasePeriodSummaryResult statistics)
    {
        ArgumentNullException.ThrowIfNull(statistics);
        return statistics.RecordCount > 0 && statistics.PreviousRecordCount > 0
            ? ReportDataState.Sufficient
            : ReportDataState.InsufficientRecords;
    }

    /// <summary>The sentence a viewer reads in place of the change and the summary; <see langword="null"/>
    /// for a report with enough records.</summary>
    public static string? InsufficientMessage(DatabasePeriodSummaryResult statistics)
    {
        ArgumentNullException.ThrowIfNull(statistics);
        return StateOf(statistics) == ReportDataState.Sufficient
            ? null
            : $"紀錄不足：這一期有 {statistics.RecordCount} 筆、前一期有 {statistics.PreviousRecordCount} 筆紀錄，兩期都有紀錄才會顯示變化、趨勢與 AI 摘要。";
    }

    /// <summary>Said for a period that produced no report.</summary>
    public static string SkipMessage(ReportSkipReason reason) => reason switch
    {
        ReportSkipReason.NotConnected => "這一期沒有產生報表：助理已不再連接這個數據庫。",
        ReportSkipReason.OwnerCannotRead => "這一期沒有產生報表：助理擁有者目前無法讀取這個數據庫的紀錄。",
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "Not a declared reason."),
    };

    public const string SummaryLabel = "AI 摘要";

    public const string SummaryDisclaimer = "由 AI 根據上方已算好的統計數字撰寫，僅供參考；一切數字以統計為準。";

    public const string SummaryFailedNote = "AI 摘要暫時無法產生，統計與圖表不受影響。可以稍後重試。";

    public const string SummaryDiscardedNote = "AI 摘要含有統計結果裡沒有的數字，已捨棄、沒有顯示。統計與圖表不受影響，可以重試。";

    public const string SummaryTooLongNote = "AI 摘要過長，已捨棄、沒有顯示。統計與圖表不受影響，可以重試。";
}
