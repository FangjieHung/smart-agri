using System.Text.Json;
using Shouldly;
using SmartAgri.Domain.Coordinator;

namespace SmartAgri.Domain.Tests;

/// <summary>
/// 統籌助理 binding codes and members (store-coordinator design §5 Slice 1, #372): how codes are made,
/// read back from what people type, hashed for storage, and the rules of the two entities.
/// </summary>
public class CoordinatorBindingCodeTests
{
    private static readonly Guid Organization = Guid.Parse("01a10194-0000-7000-8000-0000000000c1");
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void The_alphabet_has_no_characters_people_mistake_for_one_another()
    {
        BindingCodes.Alphabet.ShouldBe("23456789ABCDEFGHJKMNPQRSTUVWXYZ");
        foreach (var confusable in "01OIL")
        {
            BindingCodes.Alphabet.ShouldNotContain(confusable);
        }

        BindingCodes.Alphabet.Distinct().Count().ShouldBe(BindingCodes.Alphabet.Length);
        BindingCodes.Length.ShouldBe(8);
    }

    [Fact]
    public void Generated_codes_are_eight_alphabet_characters_and_differ()
    {
        var codes = Enumerable.Range(0, 2000).Select(_ => BindingCodes.Generate()).ToList();

        codes.ShouldAllBe(code => BindingCodes.IsCode(code));
        codes.Distinct().Count().ShouldBe(codes.Count);

        // Every character of the alphabet turns up (a CSPRNG over all of it, not a subset).
        codes.SelectMany(code => code).Distinct().Count().ShouldBe(BindingCodes.Alphabet.Length);
    }

    [Fact]
    public void A_code_is_shown_as_two_groups_of_four()
    {
        BindingCodes.Format("ABCD2345").ShouldBe("ABCD-2345");
        Should.Throw<ArgumentException>(() => BindingCodes.Format("ABCD-2345"));
        Should.Throw<ArgumentException>(() => BindingCodes.Format("ABCD234O"));
    }

    [Theory]
    [InlineData("ABCD-2345", "ABCD2345")]
    [InlineData("abcd-2345", "ABCD2345")]
    [InlineData("  ABCD 2345  ", "ABCD2345")]
    [InlineData("ABCD2345", "ABCD2345")]
    [InlineData("ＡＢＣＤ－２３４５", "ABCD2345")]
    [InlineData("ａｂｃｄ　２３４５", "ABCD2345")]
    [InlineData("AB-CD-23-45", "ABCD2345")]
    [InlineData("ABCD\u20102345", "ABCD2345")]
    [InlineData("\nABCD-2345\n", "ABCD2345")]
    public void What_people_type_is_read_as_the_code(string typed, string expected)
    {
        BindingCodes.TryNormalize(typed, out var code).ShouldBeTrue(typed);
        code.ShouldBe(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ABCD-234")]
    [InlineData("ABCD-23456")]
    [InlineData("ABCD-2340")]
    [InlineData("ABCD-234O")]
    [InlineData("ABCD-2341")]
    [InlineData("ABCD-234I")]
    [InlineData("ABCD-234L")]
    [InlineData("ABCD_2345")]
    [InlineData("產生店長綁定碼")]
    [InlineData("今天臨時店休")]
    [InlineData("ABCD.2345")]
    public void Anything_else_does_not_look_like_a_code(string? typed)
    {
        BindingCodes.TryNormalize(typed, out var code).ShouldBeFalse();
        code.ShouldBeEmpty();
    }

    [Fact]
    public void Text_that_is_not_valid_unicode_is_not_a_code()
    {
        BindingCodes.TryNormalize("ABCD2345\uD800", out _).ShouldBeFalse();
    }

    [Fact]
    public void The_hash_is_the_sha256_of_the_normalized_code_in_lower_case_hex()
    {
        // printf 'ABCD2345' | shasum -a 256
        BindingCodes.Hash("ABCD2345").ShouldBe("a00d76646eba91b057841554d5c8334f498dc592ed744bce404f21fe271cd36e");
        BindingCodes.TryNormalize("ａｂｃｄ－２３４５", out var code).ShouldBeTrue();
        BindingCodes.Hash(code).ShouldBe(BindingCodes.Hash("ABCD2345"));
        BindingCodes.Hash("ABCD2346").ShouldNotBe(BindingCodes.Hash("ABCD2345"));
        Should.Throw<ArgumentException>(() => BindingCodes.Hash("abcd2345"));
    }

    [Fact]
    public void Roles_have_wire_names_and_display_names()
    {
        WireNames<CoordinatorRole>.All.ShouldBe(["owner", "manager"]);
        JsonSerializer.Serialize(CoordinatorRole.Manager).ShouldBe("\"manager\"");
        CoordinatorRoleNames.Display(CoordinatorRole.Owner).ShouldBe("老闆");
        CoordinatorRoleNames.Display(CoordinatorRole.Manager).ShouldBe("店長");
    }

    [Fact]
    public void A_code_is_issued_unused_with_its_expiry_and_validity_limits()
    {
        var hash = BindingCodes.Hash("ABCD2345");

        var code = CoordinatorBindingCode.Issue(Organization, hash, CoordinatorRole.Owner, Now, CoordinatorBindingCode.OwnerDefaultValidity, null);

        code.OrganizationId.ShouldBe(Organization);
        code.CodeHash.ShouldBe(hash);
        code.Role.ShouldBe(CoordinatorRole.Owner);
        code.CreatedAt.ShouldBe(Now);
        code.ExpiresAt.ShouldBe(Now.AddHours(168));
        code.UsedAt.ShouldBeNull();
        code.UsedByLineUserId.ShouldBeNull();
        code.IssuedByLineUserId.ShouldBeNull();
        code.IsUsable(Now).ShouldBeTrue();
        code.IsUsable(Now.AddHours(168).AddTicks(-1)).ShouldBeTrue();
        code.IsUsable(Now.AddHours(168)).ShouldBeFalse();

        CoordinatorBindingCode.OwnerDefaultValidity.ShouldBe(TimeSpan.FromHours(168));
        CoordinatorBindingCode.MaxValidity.ShouldBe(TimeSpan.FromHours(720));
        CoordinatorBindingCode.ManagerValidity.ShouldBe(TimeSpan.FromHours(24));
        CoordinatorBindingCode.Issue(Organization, hash, CoordinatorRole.Owner, Now, TimeSpan.FromHours(720), null).ExpiresAt
            .ShouldBe(Now.AddHours(720));
        Should.Throw<ArgumentOutOfRangeException>(() =>
            CoordinatorBindingCode.Issue(Organization, hash, CoordinatorRole.Owner, Now, TimeSpan.FromHours(721), null));
        Should.Throw<ArgumentOutOfRangeException>(() =>
            CoordinatorBindingCode.Issue(Organization, hash, CoordinatorRole.Owner, Now, TimeSpan.Zero, null));
        Should.Throw<ArgumentException>(() =>
            CoordinatorBindingCode.Issue(Guid.Empty, hash, CoordinatorRole.Owner, Now, TimeSpan.FromHours(1), null));
        Should.Throw<ArgumentException>(() =>
            CoordinatorBindingCode.Issue(Organization, "ABCD2345", CoordinatorRole.Owner, Now, TimeSpan.FromHours(1), null));
        Should.Throw<ArgumentException>(() =>
            CoordinatorBindingCode.Issue(Organization, hash, CoordinatorRole.Manager, Now, TimeSpan.FromHours(1), "has space"));
    }

    [Fact]
    public void A_member_is_bound_with_a_role_and_a_line_user_id()
    {
        var lineUserId = "U" + new string('a', 32);

        var member = CoordinatorMember.Bind(Organization, lineUserId, CoordinatorRole.Manager, Now);

        member.OrganizationId.ShouldBe(Organization);
        member.LineUserId.ShouldBe(lineUserId);
        member.Role.ShouldBe(CoordinatorRole.Manager);
        member.BoundAt.ShouldBe(Now);
        member.Id.ShouldNotBe(Guid.Empty);
        Should.Throw<ArgumentException>(() => CoordinatorMember.Bind(Guid.Empty, lineUserId, CoordinatorRole.Owner, Now));
        Should.Throw<ArgumentException>(() => CoordinatorMember.Bind(Organization, "", CoordinatorRole.Owner, Now));
        Should.Throw<ArgumentException>(() => CoordinatorMember.Bind(Organization, new string('U', 65), CoordinatorRole.Owner, Now));
        Should.Throw<ArgumentOutOfRangeException>(() => CoordinatorMember.Bind(Organization, lineUserId, (CoordinatorRole)9, Now));
    }
}
