using System.Text.Json;
using Shouldly;
using SmartAgri.Domain.Organizations;

namespace SmartAgri.Domain.Tests;

/// <summary><see cref="Organization.ChatModelId"/>, <see cref="Organization.SettingsRevision"/> and
/// <see cref="OrganizationActivity"/> (M6 plan §3 D, E, issue #239).</summary>
public class OrganizationSettingsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 6, 9, 30, 0, TimeSpan.Zero);

    [Fact]
    public void A_new_organization_uses_the_deployment_default_at_revision_zero()
    {
        var organization = NewOrganization();

        (organization.ChatModelId, organization.SettingsRevision).ShouldBe(((string?)null, 0));
    }

    [Fact]
    public void A_change_bumps_the_revision_and_the_same_value_or_a_stale_revision_changes_nothing()
    {
        var organization = NewOrganization();

        organization.ChangeChatModel(" second ", 0).ShouldBe(OrganizationSettingsChange.Changed);
        (organization.ChatModelId, organization.SettingsRevision).ShouldBe(("second", 1));

        organization.ChangeChatModel("second", 1).ShouldBe(OrganizationSettingsChange.Unchanged);
        organization.SettingsRevision.ShouldBe(1);

        organization.ChangeChatModel(null, 0).ShouldBe(OrganizationSettingsChange.RevisionConflict);
        (organization.ChatModelId, organization.SettingsRevision).ShouldBe(("second", 1));

        organization.ChangeChatModel("  ", 1).ShouldBe(OrganizationSettingsChange.Changed);
        (organization.ChatModelId, organization.SettingsRevision).ShouldBe(((string?)null, 2));
    }

    [Fact]
    public void An_id_longer_than_64_characters_is_refused()
    {
        var organization = NewOrganization();

        Should.Throw<ArgumentException>(() => organization.ChangeChatModel(new string('a', 65), 0));
        organization.ChangeChatModel(new string('a', 64), 0).ShouldBe(OrganizationSettingsChange.Changed);
    }

    [Fact]
    public void A_chat_model_change_records_who_when_and_both_sides_by_id_and_name_only()
    {
        var organizationId = Guid.CreateVersion7();
        var actorId = Guid.CreateVersion7();

        var activity = OrganizationActivity.ChatModelChanged(
            organizationId, actorId, At, new ChatModelChoice(null, "預設模型"), new ChatModelChoice("second", "第二個模型"));

        (activity.OrganizationId, activity.Action, activity.ActorAccountId, activity.At)
            .ShouldBe((organizationId, OrganizationActivityAction.ChatModelChanged, (Guid?)actorId, At));
        activity.Id.ShouldNotBe(Guid.Empty);
        using var detail = JsonDocument.Parse(activity.Detail!);
        detail.RootElement.GetProperty("from").GetProperty("id").ValueKind.ShouldBe(JsonValueKind.Null);
        detail.RootElement.GetProperty("from").GetProperty("displayName").GetString().ShouldBe("預設模型");
        detail.RootElement.GetProperty("to").GetProperty("id").GetString().ShouldBe("second");
        detail.RootElement.GetProperty("to").GetProperty("displayName").GetString().ShouldBe("第二個模型");
    }

    [Fact]
    public void A_system_action_has_no_actor_and_an_empty_organization_or_actor_id_is_refused()
    {
        var organizationId = Guid.CreateVersion7();

        var system = OrganizationActivity.Record(organizationId, OrganizationActivityAction.ChatModelChanged, actorAccountId: null, At);
        (system.ActorAccountId, system.Detail).ShouldBe(((Guid?)null, (string?)null));

        Should.Throw<ArgumentException>(() => OrganizationActivity.Record(Guid.Empty, OrganizationActivityAction.ChatModelChanged, null, At));
        Should.Throw<ArgumentException>(() => OrganizationActivity.Record(organizationId, OrganizationActivityAction.ChatModelChanged, Guid.Empty, At));
    }

    [Fact]
    public void Activity_actions_have_wire_names()
    {
        WireNames<OrganizationActivityAction>.All.ShouldBe(["chat-model-changed"]);
    }

    private static Organization NewOrganization() => new(Guid.CreateVersion7(), "模型商行", "models");
}
