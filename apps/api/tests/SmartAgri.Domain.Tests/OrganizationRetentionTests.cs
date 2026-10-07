using System.Text.Json;
using Shouldly;
using SmartAgri.Domain.Organizations;

namespace SmartAgri.Domain.Tests;

/// <summary><see cref="Organization.ChangeRetention"/>, <see cref="Organization.ApplyDueRetention"/> and
/// the retention activities (M6 plan §3 F, issue #241).</summary>
public class OrganizationRetentionTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 6, 9, 30, 0, TimeSpan.Zero);

    [Fact]
    public void A_new_organization_keeps_conversations_forever_with_nothing_pending_and_no_chain()
    {
        var organization = NewOrganization();

        (organization.RetentionDays, organization.PendingRetentionDays, organization.PendingRetentionEffectiveAt, organization.RetentionCleanupNextRunAt)
            .ShouldBe(((int?)null, (int?)null, (DateTimeOffset?)null, (DateTimeOffset?)null));
        organization.HasRetentionLimit.ShouldBeFalse();
        OrganizationRetention.Options.ShouldBe([30, 90, 180, 365]);
        OrganizationRetention.BufferPeriod.ShouldBe(TimeSpan.FromDays(7));
    }

    [Fact]
    public void Shortening_is_pending_for_seven_days_and_the_current_value_keeps_applying()
    {
        var organization = NewOrganization();

        organization.ChangeRetention(90, 0, Now).ShouldBe(OrganizationSettingsChange.Changed);

        organization.RetentionDays.ShouldBeNull();
        (organization.PendingRetentionDays, organization.PendingRetentionEffectiveAt).ShouldBe(((int?)90, (DateTimeOffset?)Now.AddDays(7)));
        organization.SettingsRevision.ShouldBe(1);
        organization.HasRetentionLimit.ShouldBeTrue();

        // The pending value again does not restart the buffer.
        organization.ChangeRetention(90, 1, Now.AddDays(3)).ShouldBe(OrganizationSettingsChange.Unchanged);
        organization.PendingRetentionEffectiveAt.ShouldBe(Now.AddDays(7));
        organization.SettingsRevision.ShouldBe(1);

        // A still shorter value restarts it.
        organization.ChangeRetention(30, 1, Now.AddDays(3)).ShouldBe(OrganizationSettingsChange.Changed);
        (organization.PendingRetentionDays, organization.PendingRetentionEffectiveAt).ShouldBe(((int?)30, (DateTimeOffset?)Now.AddDays(10)));
    }

    [Fact]
    public void Sending_the_current_value_while_something_is_pending_changes_it_back()
    {
        var organization = NewOrganization();
        organization.ChangeRetention(30, 0, Now);

        organization.ChangeRetention(null, 1, Now.AddDays(1)).ShouldBe(OrganizationSettingsChange.Changed);

        (organization.RetentionDays, organization.PendingRetentionDays, organization.PendingRetentionEffectiveAt)
            .ShouldBe(((int?)null, (int?)null, (DateTimeOffset?)null));
        organization.SettingsRevision.ShouldBe(2);
        organization.ChangeRetention(null, 2, Now).ShouldBe(OrganizationSettingsChange.Unchanged);
    }

    [Fact]
    public void Lengthening_applies_at_once_and_drops_a_pending_value()
    {
        var organization = NewOrganization();
        organization.ChangeRetention(90, 0, Now);
        organization.ApplyDueRetention(Now.AddDays(7)).ShouldNotBeNull();
        organization.ChangeRetention(30, 2, Now.AddDays(8));

        organization.ChangeRetention(180, 3, Now.AddDays(9)).ShouldBe(OrganizationSettingsChange.Changed);
        (organization.RetentionDays, organization.PendingRetentionDays).ShouldBe(((int?)180, (int?)null));

        organization.ChangeRetention(null, 4, Now.AddDays(9)).ShouldBe(OrganizationSettingsChange.Changed);
        (organization.RetentionDays, organization.PendingRetentionDays, organization.SettingsRevision).ShouldBe(((int?)null, (int?)null, 5));
    }

    [Fact]
    public void A_stale_revision_changes_nothing_and_a_value_outside_the_options_is_refused()
    {
        var organization = NewOrganization();

        organization.ChangeRetention(30, 4, Now).ShouldBe(OrganizationSettingsChange.RevisionConflict);
        (organization.PendingRetentionDays, organization.SettingsRevision).ShouldBe(((int?)null, 0));

        foreach (var days in new[] { 0, 7, 60, 366, -30 })
        {
            Should.Throw<ArgumentOutOfRangeException>(() => organization.ChangeRetention(days, 0, Now), days.ToString());
        }
    }

    [Fact]
    public void A_pending_value_applies_only_once_its_buffer_is_over()
    {
        var organization = NewOrganization();
        organization.ChangeRetention(30, 0, Now);

        organization.ApplyDueRetention(Now.AddDays(7).AddTicks(-10)).ShouldBeNull();
        organization.PendingRetentionDays.ShouldBe(30);
        organization.SettingsRevision.ShouldBe(1);

        organization.ApplyDueRetention(Now.AddDays(7)).ShouldBe(new RetentionSwitch(null, 30));
        (organization.RetentionDays, organization.PendingRetentionDays, organization.PendingRetentionEffectiveAt, organization.SettingsRevision)
            .ShouldBe(((int?)30, (int?)null, (DateTimeOffset?)null, 2));
        organization.ApplyDueRetention(Now.AddDays(30)).ShouldBeNull();
    }

    [Fact]
    public void Retention_activities_carry_days_and_times_only()
    {
        var organizationId = Guid.CreateVersion7();
        var actorId = Guid.CreateVersion7();

        var changed = OrganizationActivity.RetentionChanged(organizationId, actorId, Now, null, 30, Now.AddDays(7));
        changed.Action.ShouldBe(OrganizationActivityAction.RetentionChanged);
        Keys(changed).ShouldBe(["from", "to", "effectiveAt"]);
        using (var detail = JsonDocument.Parse(changed.Detail!))
        {
            detail.RootElement.GetProperty("from").ValueKind.ShouldBe(JsonValueKind.Null);
            detail.RootElement.GetProperty("to").GetInt32().ShouldBe(30);
            detail.RootElement.GetProperty("effectiveAt").GetDateTimeOffset().ShouldBe(Now.AddDays(7));
        }

        var cancelled = OrganizationActivity.RetentionChangeCancelled(organizationId, actorId, Now, 90, 30, Now.AddDays(7));
        (cancelled.Action, cancelled.ActorAccountId).ShouldBe((OrganizationActivityAction.RetentionChangeCancelled, (Guid?)actorId));
        Keys(cancelled).ShouldBe(["days", "cancelledDays", "cancelledEffectiveAt"]);

        var tookEffect = OrganizationActivity.RetentionTookEffect(organizationId, Now, new RetentionSwitch(null, 30));
        (tookEffect.Action, tookEffect.ActorAccountId).ShouldBe((OrganizationActivityAction.RetentionTookEffect, (Guid?)null));
        Keys(tookEffect).ShouldBe(["from", "to"]);

        var cleanup = OrganizationActivity.RetentionCleanup(organizationId, Now, 30, Now.AddDays(-30), 3, 0);
        (cleanup.Action, cleanup.ActorAccountId).ShouldBe((OrganizationActivityAction.RetentionCleanup, (Guid?)null));
        Keys(cleanup).ShouldBe(["days", "cutoff", "threadCount", "answerOutcomeCount"]);

        // A cleanup that deleted nothing is never recorded.
        Should.Throw<ArgumentOutOfRangeException>(() => OrganizationActivity.RetentionCleanup(organizationId, Now, 30, Now, 0, 0));
    }

    [Fact]
    public void A_purge_records_the_assistant_and_the_count_and_nothing_anyone_wrote()
    {
        var organizationId = Guid.CreateVersion7();
        var actorId = Guid.CreateVersion7();
        var assistantId = Guid.CreateVersion7();

        var purged = OrganizationActivity.ConversationsPurged(organizationId, actorId, Now, assistantId, "退貨助理", 12);

        (purged.Action, purged.ActorAccountId, purged.At).ShouldBe((OrganizationActivityAction.ConversationsPurged, (Guid?)actorId, Now));
        Keys(purged).ShouldBe(["assistantId", "assistantName", "threadCount"]);
        using (var detail = JsonDocument.Parse(purged.Detail!))
        {
            detail.RootElement.GetProperty("assistantId").GetGuid().ShouldBe(assistantId);
            detail.RootElement.GetProperty("assistantName").GetString().ShouldBe("退貨助理");
            detail.RootElement.GetProperty("threadCount").GetInt32().ShouldBe(12);
        }

        // A purge that found nothing is still the manager's action, recorded with 0.
        Keys(OrganizationActivity.ConversationsPurged(organizationId, actorId, Now, assistantId, "退貨助理", 0)).Count.ShouldBe(3);
        Should.Throw<ArgumentOutOfRangeException>(() => OrganizationActivity.ConversationsPurged(organizationId, actorId, Now, assistantId, "退貨助理", -1));
    }

    private static List<string> Keys(OrganizationActivity activity)
    {
        using var detail = JsonDocument.Parse(activity.Detail!);
        return [.. detail.RootElement.EnumerateObject().Select(property => property.Name)];
    }

    private static Organization NewOrganization() => new(Guid.CreateVersion7(), "保存商行", "keep");
}
