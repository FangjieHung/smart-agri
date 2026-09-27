using Shouldly;
using SmartAgri.Application.Knowledge.Processing;

namespace SmartAgri.Application.Tests.Knowledge.Processing;

public class KnowledgeProcessingIssuesTests
{
    [Fact]
    public void The_messages_say_what_the_plan_says()
    {
        KnowledgeProcessingIssues.NoReadableText.ShouldBe("找不到可讀文字，可能是掃描檔；目前不支援 OCR");
        KnowledgeProcessingIssues.Encrypted.ShouldContain("請解除密碼後重新上傳");
        KnowledgeProcessingIssues.NotUtf8.ShouldContain("請另存為 UTF-8（目前不支援 Big5）");
        KnowledgeProcessingIssues.For(DocumentExtractionFailure.Encrypted).ShouldBe(KnowledgeProcessingIssues.Encrypted);
        KnowledgeProcessingIssues.For(DocumentExtractionFailure.NotUtf8).ShouldBe(KnowledgeProcessingIssues.NotUtf8);
        KnowledgeProcessingIssues.For(DocumentExtractionFailure.Damaged).ShouldBe(KnowledgeProcessingIssues.Damaged);
        KnowledgeProcessingIssues.EmbeddingUnavailable.ShouldBe("嵌入模型暫時無法使用，請稍後重試");
    }

    [Fact]
    public void A_final_failure_shows_a_file_problem_as_is_and_anything_else_as_a_generic_retry_message()
    {
        KnowledgeProcessingIssues.ForFinalFailure(KnowledgeProcessingIssues.Encrypted).ShouldBe(KnowledgeProcessingIssues.Encrypted);
        KnowledgeProcessingIssues.ForFinalFailure(KnowledgeProcessingIssues.NotUtf8).ShouldBe(KnowledgeProcessingIssues.NotUtf8);
        KnowledgeProcessingIssues.ForFinalFailure(KnowledgeProcessingIssues.Damaged).ShouldBe(KnowledgeProcessingIssues.Damaged);
        KnowledgeProcessingIssues.ForFinalFailure(KnowledgeProcessingIssues.EmbeddingUnavailable).ShouldBe(KnowledgeProcessingIssues.EmbeddingUnavailable);
        KnowledgeProcessingIssues.ForFinalFailure(KnowledgeProcessingIssues.EmbeddingNotConfigured).ShouldBe(KnowledgeProcessingIssues.EmbeddingNotConfigured);

        // Never an internal exception message.
        KnowledgeProcessingIssues.ForFinalFailure("Npgsql.NpgsqlException: connection refused").ShouldBe("處理時發生錯誤，請重試。");
        KnowledgeProcessingIssues.ForFinalFailure(string.Empty).ShouldBe(KnowledgeProcessingIssues.Unexpected);
    }
}
