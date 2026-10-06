namespace SmartAgri.Application.Cases;

/// <summary>
/// 送出後自動開案 (M7 plan §3 I, decision M; issue #255): what a case opened by a database submission
/// says. Only the database's name and fixed text — <b>never anything the member submitted</b>, so a
/// withdrawal really takes the content away (case ADR). The type gives the group and the due time.
/// </summary>
public static class DatabaseAutoCaseRules
{
    /// <summary>The field of <c>PUT /api/v1/databases/{id}/auto-case</c>.</summary>
    public const string CaseTypeField = "caseTypeId";

    /// <summary>The fixed description of every case opened this way.</summary>
    public const string Description = "由數據庫送出自動建立，內容請開啟紀錄查看。";

    /// <summary>「{數據庫名稱}：新紀錄」 (a database name is at most 40 characters, so it always fits the
    /// case title's 120).</summary>
    public static string Title(string databaseName)
    {
        ArgumentNullException.ThrowIfNull(databaseName);
        return $"{databaseName}：新紀錄";
    }
}
