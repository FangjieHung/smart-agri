using Shouldly;
using SmartAgri.Domain.Chat;

namespace SmartAgri.Domain.Tests;

public class ChatFormDismissalTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_dismissal_holds_the_event_assistant_form_and_time_and_no_content()
    {
        var organization = Guid.CreateVersion7();
        var assistant = Guid.CreateVersion7();
        var database = Guid.CreateVersion7();

        var dismissal = ChatFormDismissal.Record(organization, assistant, database, Now);

        dismissal.Id.ShouldNotBe(Guid.Empty);
        (dismissal.OrganizationId, dismissal.AssistantId, dismissal.DatabaseId, dismissal.At)
            .ShouldBe((organization, assistant, database, Now));

        // #171: no account, conversation or anything typed — not even a string property to put it in.
        typeof(ChatFormDismissal).GetProperties().Where(property => property.PropertyType == typeof(string)).ShouldBeEmpty();
        typeof(ChatFormDismissal).GetProperties().Select(property => property.Name)
            .ShouldBe(["Id", "OrganizationId", "AssistantId", "DatabaseId", "At"], ignoreOrder: true);
    }

    [Fact]
    public void Every_id_is_required()
    {
        Should.Throw<ArgumentException>(() => ChatFormDismissal.Record(Guid.Empty, Guid.CreateVersion7(), Guid.CreateVersion7(), Now));
        Should.Throw<ArgumentException>(() => ChatFormDismissal.Record(Guid.CreateVersion7(), Guid.Empty, Guid.CreateVersion7(), Now));
        Should.Throw<ArgumentException>(() => ChatFormDismissal.Record(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.Empty, Now));
    }
}
