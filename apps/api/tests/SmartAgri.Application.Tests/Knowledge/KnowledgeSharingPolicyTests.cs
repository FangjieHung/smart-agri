using Shouldly;
using SmartAgri.Application.Knowledge;
using SmartAgri.Domain.Knowledge;

namespace SmartAgri.Application.Tests.Knowledge;

/// <summary>
/// The sharing rules ported from the mock's <c>updateKnowledgeSharing</c>
/// (<c>mock-demo-repository.ts</c>) and <c>getKnowledgeBaseDetail</c>'s <c>shareTargets</c>.
/// </summary>
public class KnowledgeSharingPolicyTests
{
    private static readonly Guid Owner = Guid.Parse("00000000-0000-7000-8000-000000000001");
    private static readonly Guid Internal = Guid.Parse("00000000-0000-7000-8000-000000000002");
    private static readonly Guid Customer = Guid.Parse("00000000-0000-7000-8000-000000000003");

    /// <summary>What the endpoint passes: the organization's accounts minus the owner.</summary>
    private static readonly IReadOnlyList<Guid> Targets = KnowledgeSharingPolicy.ShareTargetIds(Owner, [Owner, Internal, Customer]);

    [Fact]
    public void Share_targets_are_every_organization_account_except_the_owner_in_the_given_order()
    {
        Targets.ShouldBe([Internal, Customer]);
        KnowledgeSharingPolicy.ShareTargetIds(Owner, [Customer, Owner, Internal, Customer]).ShouldBe([Customer, Internal]);
        KnowledgeSharingPolicy.ShareTargetIds(Owner, [Owner]).ShouldBeEmpty();
    }

    [Fact]
    public void Specific_accounts_keeps_only_share_targets_in_target_order_without_duplicates()
    {
        var otherOrganizationsAccount = Guid.NewGuid();

        var result = KnowledgeSharingPolicy.Normalize(
            "specific-accounts",
            [
                Customer.ToString(),
                otherOrganizationsAccount.ToString(), // not in this organization's targets
                Owner.ToString(),                     // yourself
                "account-internal-employee",          // not a GUID at all
                null,
                Internal.ToString("N"),               // any GUID format is fine
                Customer.ToString().ToUpperInvariant(),
            ],
            allowOriginalDownload: false,
            Targets);

        result.IsValid.ShouldBeTrue();
        result.Value.Scope.ShouldBe(KnowledgeSharingScope.SpecificAccounts);
        result.Value.SharedWithAccountIds.ShouldBe([Internal, Customer]);
        result.Value.AllowOriginalDownload.ShouldBeFalse();
    }

    public static TheoryData<string[]> NothingValidRequested { get; } = new()
    {
        { ["00000000-0000-7000-8000-000000000001"] },                   // only yourself
        { ["0a0b0c0d-0000-7000-8000-00000000abcd", "not-a-guid"] },     // unknown or another organization's
        { [] },
    };

    [Theory]
    [MemberData(nameof(NothingValidRequested))]
    public void Specific_accounts_with_no_valid_target_left_fails_with_the_mocks_message(string[] requested)
    {
        var result = KnowledgeSharingPolicy.Normalize("specific-accounts", requested, allowOriginalDownload: false, Targets);

        result.IsValid.ShouldBeFalse();
        var failure = result.Failures.ShouldHaveSingleItem();
        failure.Field.ShouldBe("sharedWithAccountIds");
        failure.Message.ShouldBe("請至少選擇一個帳號或團隊。");
    }

    [Fact]
    public void Specific_accounts_with_a_missing_list_fails_too()
    {
        KnowledgeSharingPolicy.Normalize("specific-accounts", null, allowOriginalDownload: false, Targets)
            .IsValid.ShouldBeFalse();
    }

    [Theory]
    [InlineData("private", KnowledgeSharingScope.Private)]
    [InlineData("public", KnowledgeSharingScope.Public)]
    public void Other_scopes_ignore_the_account_list(string scope, KnowledgeSharingScope expected)
    {
        var result = KnowledgeSharingPolicy.Normalize(scope, [Internal.ToString()], allowOriginalDownload: false, Targets);

        result.IsValid.ShouldBeTrue();
        result.Value.Scope.ShouldBe(expected);
        result.Value.SharedWithAccountIds.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("private", false)]
    [InlineData("specific-accounts", false)]
    [InlineData("public", true)]
    public void Original_download_is_kept_only_for_public(string scope, bool expected)
    {
        var result = KnowledgeSharingPolicy.Normalize(scope, [Internal.ToString()], allowOriginalDownload: true, Targets);

        result.Value.AllowOriginalDownload.ShouldBe(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Public")]
    [InlineData("SpecificAccounts")]
    [InlineData("everyone")]
    public void An_unknown_scope_fails_on_scope(string? scope)
    {
        var result = KnowledgeSharingPolicy.Normalize(scope, [Internal.ToString()], allowOriginalDownload: false, Targets);

        var failure = result.Failures.ShouldHaveSingleItem();
        failure.Field.ShouldBe("scope");
        failure.Message.ShouldBe(KnowledgeSharingPolicy.UnknownScopeMessage);
    }

    [Fact]
    public void Equivalence_ignores_the_order_of_accounts_but_nothing_else()
    {
        var sharing = new KnowledgeSharingSettings(KnowledgeSharingScope.SpecificAccounts, [Internal, Customer], false);

        sharing.IsEquivalentTo(sharing with { SharedWithAccountIds = [Customer, Internal] }).ShouldBeTrue();
        sharing.IsEquivalentTo(sharing with { SharedWithAccountIds = [Customer] }).ShouldBeFalse();
        sharing.IsEquivalentTo(sharing with { Scope = KnowledgeSharingScope.Public }).ShouldBeFalse();
        sharing.IsEquivalentTo(sharing with { AllowOriginalDownload = true }).ShouldBeFalse();
    }
}
