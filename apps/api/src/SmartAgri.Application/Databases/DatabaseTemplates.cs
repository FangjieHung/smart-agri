using SmartAgri.Domain.Databases;

namespace SmartAgri.Application.Databases;

/// <summary>A template to create a database from: its name, what it is for, and the fields of
/// the initial form (version 1).</summary>
public sealed record DatabaseTemplate(
    DatabaseTemplateId Id,
    string Name,
    string Description,
    IReadOnlyList<DatabaseFormField> Fields);

/// <summary>
/// The five templates, in the order the create dialog lists them. Same names, descriptions and
/// fields as the frontend mock (<c>apps/admin/src/app/core/repositories/demo-seed-databases.ts</c>
/// <c>DATABASE_TEMPLATES</c>), so mock and API mode create the same initial form; a test reads
/// that file and compares. Templates are code, not data: a new organization needs no seed to
/// start collecting, and changing a template never changes databases already created from it
/// (they own a copy as form version 1).
/// </summary>
public static class DatabaseTemplates
{
    public static IReadOnlyList<DatabaseTemplate> All { get; } =
    [
        new(
            DatabaseTemplateId.CustomerProfile,
            "客戶基本資料",
            "整理客戶聯絡方式與類型，方便後續服務。",
            [
                Text("field-customer-name", "客戶姓名", required: true),
                Text("field-phone", "聯絡電話"),
                Date("field-first-visit", "首次來店日期"),
                Single("field-customer-type", "客戶類型", ["個人", "企業"], required: true),
            ]),
        new(
            DatabaseTemplateId.PeriodicReport,
            "定期回報",
            "每週或每月收集同一格式的回報，比較每一期的變化。",
            [
                Date("field-report-date", "回報日期", required: true),
                Number("field-completed-count", "本期完成數量", "件", required: true),
                Text("field-issue", "遇到的問題"),
            ]),
        new(
            DatabaseTemplateId.Satisfaction,
            "滿意度調查",
            "收集客戶對服務的評分與建議。",
            [
                Scale("field-overall-satisfaction", "整體滿意度", new DatabaseScaleRange(1, 5, "很不滿意", "非常滿意"), required: true),
                Multiple("field-liked-services", "喜歡的服務", ["商品品質", "客服回應", "配送速度"]),
                Text("field-suggestion", "其他建議"),
            ]),
        new(
            DatabaseTemplateId.Progress,
            "症狀或進度追蹤",
            "為每位追蹤對象建立時間軸，比較本次、上次與首次的變化。",
            [
                Date("field-check-date", "紀錄日期", required: true),
                Scale("field-condition-score", "狀況分數", new DatabaseScaleRange(0, 10, "最差", "最好"), required: true),
                Text("field-progress-note", "備註"),
            ]),
        new(
            DatabaseTemplateId.Blank,
            "空白模板",
            "從一個文字欄位開始，自行設計要收集的欄位。",
            [Text("field-item", "項目名稱", required: true)]),
    ];

    /// <summary>The template with this id; every declared id has one.</summary>
    public static DatabaseTemplate Get(DatabaseTemplateId id) =>
        All.FirstOrDefault(template => template.Id == id)
            ?? throw new ArgumentOutOfRangeException(nameof(id), id, "Not a declared template.");

    private static DatabaseFormField Text(string id, string label, bool required = false) =>
        new(id, label, DatabaseFieldType.Text, required, [], null, string.Empty);

    private static DatabaseFormField Date(string id, string label, bool required = false) =>
        new(id, label, DatabaseFieldType.Date, required, [], null, string.Empty);

    private static DatabaseFormField Number(string id, string label, string unit, bool required = false) =>
        new(id, label, DatabaseFieldType.Number, required, [], null, unit);

    private static DatabaseFormField Single(string id, string label, IReadOnlyList<string> options, bool required = false) =>
        new(id, label, DatabaseFieldType.SingleChoice, required, options, null, string.Empty);

    private static DatabaseFormField Multiple(string id, string label, IReadOnlyList<string> options, bool required = false) =>
        new(id, label, DatabaseFieldType.MultipleChoice, required, options, null, string.Empty);

    private static DatabaseFormField Scale(string id, string label, DatabaseScaleRange range, bool required = false) =>
        new(id, label, DatabaseFieldType.Scale, required, [], range, string.Empty);
}
