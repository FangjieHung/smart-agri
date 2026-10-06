using System.Text.Json;
using System.Text.Json.Serialization;
using SmartAgri.Domain.Cases;

namespace SmartAgri.Domain.Chat;

/// <summary>Where a case proposal (M7-9, issue #254) is: waiting for the asker, confirmed (a case
/// exists) or dismissed (「不用了」, nothing created). Only <see cref="Proposed"/> may change.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ChatCaseProposalStatus>))]
public enum ChatCaseProposalStatus
{
    [JsonStringEnumMemberName("proposed")]
    Proposed,

    [JsonStringEnumMemberName("confirmed")]
    Confirmed,

    [JsonStringEnumMemberName("dismissed")]
    Dismissed,
}

/// <summary>
/// The snapshot a <see cref="ChatReplyKind.CaseProposal"/> message keeps (M7 plan §3 H, §4; issue
/// #254): the proposed type's id, the title and description (the assistant's draft until the asker
/// confirms, then exactly what the asker confirmed) and the status. The type's name, group and handling
/// time are read again, and the type re-checked, whenever the message is shown — never stored here.
/// No conversation text beyond the asker's own question's first characters (the keyword draft) or the
/// model's draft of it.
/// </summary>
public sealed record ChatCaseProposalSnapshot(Guid TypeId, string Title, string Description, ChatCaseProposalStatus Status)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>A new proposal: trimmed, title 1–<see cref="Case.TitleMaxLength"/> and description at
    /// most <see cref="Case.DescriptionMaxLength"/> characters (the server already truncated a model's draft).</summary>
    public static ChatCaseProposalSnapshot Propose(Guid typeId, string title, string description)
    {
        if (typeId == Guid.Empty)
        {
            throw new ArgumentException("A case proposal needs its type.", nameof(typeId));
        }

        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(description);
        if (title.Length == 0 || title.Length > Case.TitleMaxLength || title.Trim().Length != title.Length)
        {
            throw new ArgumentException($"A proposed title is trimmed and 1–{Case.TitleMaxLength} characters.", nameof(title));
        }

        if (description.Length > Case.DescriptionMaxLength || description.Trim().Length != description.Length)
        {
            throw new ArgumentException($"A proposed description is trimmed and at most {Case.DescriptionMaxLength} characters.", nameof(description));
        }

        return new ChatCaseProposalSnapshot(typeId, title, description, ChatCaseProposalStatus.Proposed);
    }

    public string Serialize() => JsonSerializer.Serialize(this, Json);

    /// <summary>The saved snapshot, or <see langword="null"/> when it cannot be read.</summary>
    public static ChatCaseProposalSnapshot? Deserialize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        try
        {
            return JsonSerializer.Deserialize<ChatCaseProposalSnapshot>(json, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
