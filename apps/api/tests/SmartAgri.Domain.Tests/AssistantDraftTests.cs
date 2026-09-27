using Shouldly;
using SmartAgri.Domain.Assistants;

namespace SmartAgri.Domain.Tests;

public class AssistantDraftTests
{
    private static readonly Guid Organization = Guid.CreateVersion7();
    private static readonly Guid Owner = Guid.CreateVersion7();
    private static readonly DateTimeOffset Created = new(2026, 9, 27, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_new_draft_starts_at_revision_1_and_is_owned_by_its_creator()
    {
        var draft = new AssistantDraft(Organization, Owner, """{"name":"客服助理"}""", schemaVersion: 1, Created);

        draft.Id.ShouldNotBe(Guid.Empty);
        draft.OrganizationId.ShouldBe(Organization);
        draft.OwnerAccountId.ShouldBe(Owner);
        draft.Payload.ShouldBe("""{"name":"客服助理"}""");
        draft.SchemaVersion.ShouldBe(1);
        draft.Revision.ShouldBe(1);
        draft.SavedAt.ShouldBe(Created);
        draft.CreatedAt.ShouldBe(Created);
    }

    [Fact]
    public void TrySave_with_the_current_revision_saves_and_increments_it()
    {
        var draft = new AssistantDraft(Organization, Owner, """{"name":"A"}""", schemaVersion: 1, Created);
        var savedAt = Created.AddMinutes(5);

        var saved = draft.TrySave("""{"name":"B"}""", schemaVersion: 2, expectedRevision: 1, savedAt);

        saved.ShouldBeTrue();
        draft.Payload.ShouldBe("""{"name":"B"}""");
        draft.SchemaVersion.ShouldBe(2);
        draft.Revision.ShouldBe(2);
        draft.SavedAt.ShouldBe(savedAt);
    }

    [Fact]
    public void TrySave_with_a_stale_revision_refuses_and_writes_nothing()
    {
        var draft = new AssistantDraft(Organization, Owner, """{"name":"A"}""", schemaVersion: 1, Created);
        draft.TrySave("""{"name":"B"}""", schemaVersion: 1, expectedRevision: 1, Created.AddMinutes(1));

        // The caller still thinks the revision is 1 (e.g. a second browser tab that never
        // saw the first tab's save); the second save must be refused.
        var saved = draft.TrySave("""{"name":"C"}""", schemaVersion: 1, expectedRevision: 1, Created.AddMinutes(2));

        saved.ShouldBeFalse();
        draft.Payload.ShouldBe("""{"name":"B"}""", "a refused save must not overwrite the winning one");
        draft.Revision.ShouldBe(2);
    }

    [Fact]
    public void A_payload_over_the_byte_limit_is_refused()
    {
        var oversized = $$"""{"name":"{{new string('字', AssistantDraft.PayloadMaxBytes)}}"}""";

        Should.Throw<ArgumentException>(() => new AssistantDraft(Organization, Owner, oversized, 1, Created));
    }

    [Fact]
    public void An_empty_organization_or_owner_id_is_refused()
    {
        Should.Throw<ArgumentException>(() => new AssistantDraft(Guid.Empty, Owner, "{}", 1, Created));
        Should.Throw<ArgumentException>(() => new AssistantDraft(Organization, Guid.Empty, "{}", 1, Created));
    }
}
