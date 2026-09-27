namespace SmartAgri.Application.Ai;

/// <summary>
/// A conversation's answer could not be generated (M3 plan, Slice 4; the chat equivalent of
/// <c>SmartAgri.Application.Knowledge.Embeddings.KnowledgeEmbeddingException</c>, the
/// embedding-side original this mirrors). Its message is what the caller is shown, so it reads
/// as one. Thrown directly by the unconfigured chat client when there is no
/// <c>Ai:Chat:Provider</c> at all (<see cref="ProviderNotConfigured"/> = true); the answer
/// pipeline (M3 Slice 5) wraps a failed provider call in this too, with
/// <see cref="ProviderNotConfigured"/> = false and the provider's error as the inner exception.
/// </summary>
/// <remarks>
/// Mapped by the Api to <c>503</c> with reason <c>chat-not-configured</c> or
/// <c>chat-unavailable</c> (<c>SmartAgri.Api.Ai.ChatErrors</c>) — the same shape as the
/// embedding pipeline's <c>embedding-not-configured</c> / <c>embedding-unavailable</c>. This
/// slice has no public conversation endpoint yet, so nothing throws the "unavailable" case in
/// production code; it is exercised directly by tests so Slice 5 and Slice 7 can rely on the
/// mapping being correct before they exist.
/// </remarks>
public sealed class ChatGenerationException : Exception
{
    public ChatGenerationException(bool providerNotConfigured, Exception? innerException = null)
        : base(
            providerNotConfigured
                ? "沒有設定對話模型，請聯絡系統管理員設定 Ai:Chat:Provider 後再試。"
                : "對話模型暫時無法使用，請稍後重試。",
            innerException)
    {
        ProviderNotConfigured = providerNotConfigured;
    }

    /// <summary>Whether the deployment has no chat provider configured at all, as opposed to a
    /// configured provider's call failing.</summary>
    public bool ProviderNotConfigured { get; }
}
