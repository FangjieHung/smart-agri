using System.Text.Json.Serialization;

namespace SmartAgri.Domain.Ai;

/// <summary>What a model was called for (<see cref="ModelInvocation.Purpose"/>, M2 plan §4).
/// Stored by wire name. M3 adds chat purposes.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ModelInvocationPurpose>))]
public enum ModelInvocationPurpose
{
    /// <summary>Embedding document chunks: processing an uploaded version, or <c>reindex</c>.</summary>
    [JsonStringEnumMemberName("embed-document")]
    EmbedDocument,

    /// <summary>Embedding a question to search with (retrieval, Slice 9).</summary>
    [JsonStringEnumMemberName("embed-query")]
    EmbedQuery,

    /// <summary>Generating a grounded answer for an assistant conversation (M3, Slice 4).</summary>
    [JsonStringEnumMemberName("generate-answer")]
    GenerateAnswer,

    /// <summary>Generating a grounded answer for a wizard trial question, before any assistant
    /// exists (M3, Slice 8): same pipeline as <see cref="GenerateAnswer"/>, distinguished so
    /// trial calls never mix into an assistant's own usage.</summary>
    [JsonStringEnumMemberName("trial-answer")]
    TrialAnswer,

    /// <summary>Generating a grounded answer for one question of an assistant's test-set rerun
    /// (M3.5 plan §3, Slice 2, #124): same pipeline as <see cref="TrialAnswer"/>, distinguished
    /// so test-run usage can be counted on its own.</summary>
    [JsonStringEnumMemberName("assistant-test")]
    AssistantTest,

    /// <summary>Choosing a fixed statistics query and its parameters for a conversation question
    /// (M4 #149): the model only selects a tool; it never sees the result.</summary>
    [JsonStringEnumMemberName("database-query")]
    DatabaseQuery,

    /// <summary>Writing the AI summary of a periodic report from its already-computed statistics
    /// (M4 #150): the model is given numbers, never records, and its text is checked against them.</summary>
    [JsonStringEnumMemberName("generate-report-summary")]
    GenerateReportSummary,

    /// <summary>Deciding whether a conversation question should get the assistant's form
    /// (<c>request_database_form</c>, M4 #164, only with <c>Chat:FormRequests:Trigger = Model</c>):
    /// the model only chooses among the forms the server offers; the server builds the form.</summary>
    [JsonStringEnumMemberName("form-request")]
    FormRequest,

    /// <summary>Generating a grounded answer for an anonymous website visitor (M5a #196,
    /// <c>POST /api/v1/public/assistants/{id}/chat/runs</c>): same pipeline as
    /// <see cref="GenerateAnswer"/>, recorded without an account and counted toward the monthly
    /// token limit like every chat-model call.</summary>
    [JsonStringEnumMemberName("public-answer")]
    PublicAnswer,

    /// <summary>Generating a grounded answer for a LINE user (M5b #232, the LINE webhook's background
    /// processor): same pipeline as <see cref="PublicAnswer"/>, recorded without an account (and without
    /// any LINE id) and counted toward the monthly token limit like every chat-model call.</summary>
    [JsonStringEnumMemberName("line-answer")]
    LineAnswer,

    /// <summary>Deciding whether a conversation question should get a case proposal, and drafting its
    /// title and description (<c>propose_case</c>, M7-9 #254, only with <c>Chat:FormRequests:Trigger = Model</c>,
    /// after the form decision said no): the model only chooses among the case types the server offers;
    /// the server re-checks the type and truncates the draft. Counted toward the monthly token limit.</summary>
    [JsonStringEnumMemberName("case-proposal")]
    CaseProposal,
}
