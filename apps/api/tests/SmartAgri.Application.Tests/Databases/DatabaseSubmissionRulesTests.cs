using Shouldly;
using SmartAgri.Application.Databases;
using SmartAgri.Domain.Databases;

namespace SmartAgri.Application.Tests.Databases;

public class DatabaseSubmissionRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 3, 1, 2, 3, TimeSpan.Zero);

    private static readonly IReadOnlyList<DatabaseFormField> Fields =
    [
        new("field-name", "姓名", DatabaseFieldType.Text, true, [], null, string.Empty),
        new("field-amount", "金額", DatabaseFieldType.Number, false, [], null, "元"),
        new("field-topics", "主題", DatabaseFieldType.MultipleChoice, false, ["品質", "客服", "速度"], null, string.Empty),
    ];

    private static IReadOnlyList<DatabaseAnswerEntry> Validate(string name, string amount, params string[] topics) =>
        DatabaseAnswerRules.Validate(Fields, new Dictionary<string, DatabaseAnswerInput>
        {
            ["field-name"] = DatabaseAnswerInput.FromText(name),
            ["field-amount"] = DatabaseAnswerInput.FromText(amount),
            ["field-topics"] = DatabaseAnswerInput.FromChoices(topics),
        }).Value;

    private static IReadOnlyList<DatabaseSubmissionEntry> Stored(IReadOnlyList<DatabaseAnswerEntry> entries)
    {
        var database = Database.Create(Guid.NewGuid(), Guid.NewGuid(), "資料庫", "用途", DatabaseTemplateId.Blank, Now);
        var version = DatabaseFormVersion.Create(database, 1, Fields, database.OwnerAccountId, Now);
        var submission = DatabaseSubmission.Create(
            database, version, Guid.NewGuid(), Guid.NewGuid(), DatabaseSubmissionSource.FormLink,
            DatabaseSubmissionRules.TermsFor("安心商行", "資料庫", "用途", ["管理者"]), Now);
        return [.. entries.Select((entry, position) => DatabaseSubmissionEntry.Create(
            submission, position, entry.Field, entry.Display, entry.Text, entry.Number, entry.Choices))];
    }

    [Fact]
    public void A_request_needs_a_key_and_a_form_version()
    {
        DatabaseSubmissionRules.ValidateRequest(null, null).Failures.Select(failure => failure.Field)
            .ShouldBe(["submissionId", "formVersionNumber"]);
        DatabaseSubmissionRules.ValidateRequest(Guid.Empty, 0).IsValid.ShouldBeFalse();

        var key = Guid.NewGuid();
        DatabaseSubmissionRules.ValidateRequest(key, 3).Value.ShouldBe((key, 3));
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(null, false)]
    public void Only_an_explicit_true_is_consent(bool? consent, bool expected) =>
        DatabaseSubmissionRules.HasConsented(consent).ShouldBe(expected);

    [Fact]
    public void Terms_name_the_organization_and_database_as_the_recipient()
    {
        var terms = DatabaseSubmissionRules.TermsFor("安心商行", "客戶資料庫", "整理客戶聯絡方式。", ["甲", "乙"]);

        terms.Recipient.ShouldBe("安心商行（客戶資料庫）");
        terms.Viewers.ShouldBe(["甲", "乙"]);
        terms.SensitiveNotice.ShouldBe(DatabaseSubmissionRules.SensitiveNotice);
    }

    [Fact]
    public void A_retry_is_the_same_content_only_when_every_typed_value_matches()
    {
        var stored = Stored(Validate("王小明", "1200", "速度", "品質"));

        // Whitespace, number spelling and choice order normalize to the same typed values.
        DatabaseSubmissionRules.IsSameContent(stored, Validate(" 王小明 ", "1200.0", "品質", "速度")).ShouldBeTrue();

        DatabaseSubmissionRules.IsSameContent(stored, Validate("李大華", "1200", "速度", "品質")).ShouldBeFalse();
        DatabaseSubmissionRules.IsSameContent(stored, Validate("王小明", "1201", "速度", "品質")).ShouldBeFalse();
        DatabaseSubmissionRules.IsSameContent(stored, Validate("王小明", "1200", "速度")).ShouldBeFalse();
        DatabaseSubmissionRules.IsSameContent(stored, Validate("王小明", string.Empty, "速度", "品質")).ShouldBeFalse();
        DatabaseSubmissionRules.IsSameContent([], Validate("王小明", "1200")).ShouldBeFalse("withdrawn content matches nothing");
    }

    [Fact]
    public void A_submission_keeps_a_receipt_number_and_its_own_copy_of_the_terms()
    {
        var viewers = new List<string> { "管理者" };
        var database = Database.Create(Guid.NewGuid(), Guid.NewGuid(), "資料庫", "用途", DatabaseTemplateId.Blank, Now);
        var version = DatabaseFormVersion.Create(database, 1, Fields, database.OwnerAccountId, Now);

        var submission = DatabaseSubmission.Create(
            database, version, Guid.NewGuid(), Guid.NewGuid(), DatabaseSubmissionSource.FormLink,
            DatabaseSubmissionRules.TermsFor("安心商行", "資料庫", "用途", viewers), Now);
        viewers.Add("後來加的人");

        submission.ReceiptNumber.ShouldMatch(@"^R-20261003-[0-9A-F]{10}$");
        submission.ReceiptNumber.ShouldEndWith(submission.Id.ToString("N")[^10..].ToUpperInvariant());
        submission.ConsentTerms.Viewers.ShouldBe(["管理者"]);
        submission.FormVersionNumber.ShouldBe(1);
        submission.WithdrawnAt.ShouldBeNull();

        var otherDatabase = Database.Create(database.OrganizationId, Guid.NewGuid(), "別的", "用途", DatabaseTemplateId.Blank, Now);
        Should.Throw<ArgumentException>(() => DatabaseSubmission.Create(
            otherDatabase, version, Guid.NewGuid(), Guid.NewGuid(), DatabaseSubmissionSource.FormLink, submission.ConsentTerms, Now));
        Should.Throw<ArgumentException>(() => DatabaseSubmission.Create(
            database, version, Guid.NewGuid(), Guid.Empty, DatabaseSubmissionSource.FormLink, submission.ConsentTerms, Now));
    }
}
