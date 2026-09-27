using System.Linq.Expressions;
using SmartAgri.Domain.Chat;

namespace SmartAgri.Application.Chat;

/// <summary>
/// Who may see, rename, delete or read a <see cref="ChatThread"/>: only the account it belongs
/// to, for the assistant given — never the assistant's owner, unless they are also the one
/// chatting (mapping §5.4: "助理擁有者不但讀不到別人的對話內容，連別人有幾段對話、叫什麼名字都看不到").
/// Organization isolation is the persistence layer's query filter, as elsewhere; this only adds
/// the <c>(AccountId, AssistantId)</c> narrowing. A <c>threadId</c> that does not match — wrong
/// owner or simply missing — must look identical to the caller (#76 acceptance), so every
/// endpoint here always resolves through this expression rather than loading by id first.
/// </summary>
public static class ChatThreadAccess
{
    public static Expression<Func<ChatThread, bool>> OwnedBy(Guid accountId, Guid assistantId) =>
        thread => thread.AccountId == accountId && thread.AssistantId == assistantId;
}
