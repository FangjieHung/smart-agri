using Microsoft.EntityFrameworkCore;
using SmartAgri.Api.Authentication;
using SmartAgri.Api.Authorization;
using SmartAgri.Api.Chat;
using SmartAgri.Api.Errors;
using SmartAgri.Application.Chat;
using SmartAgri.Domain.Accounts;
using SmartAgri.Domain.Assistants;
using SmartAgri.Domain.Chat;
using SmartAgri.Infrastructure;

namespace SmartAgri.Api.Assistants;

/// <summary>The member must explicitly confirm the one exchange to share. Text fields are
/// used only when the assistant does not save conversations.</summary>
public sealed record CreateAssistantHandoffRequest(
    Guid? ThreadId, Guid? QuestionMessageId, Guid? AnswerMessageId,
    string? SharedQuestion, string? SharedAnswer, bool Confirmed);

public static class AssistantHandoffEndpoints
{
    public static IEndpointRouteBuilder MapAssistantHandoffEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/v1/assistants/{id:guid}/chat/handoffs", CreateAsync)
            .RequireAuthorization()
            .Produces<AssistantIssueView>(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status422UnprocessableEntity);
        return endpoints;
    }

    internal static async Task<IResult> CreateAsync(
        Guid id, CreateAssistantHandoffRequest request, HttpContext httpContext,
        AppDbContext dbContext, RequestAccountPermissions permissions, TimeProvider clock,
        CancellationToken cancellationToken)
    {
        if (AccountClaims.GetAccountId(httpContext.User) is not { } callerId)
            return ApiErrors.Unauthorized();
        var assistant = await ChatEndpoints.FindUsableAsync(dbContext, permissions, id, callerId, cancellationToken);
        if (assistant is null)
            return ApiErrors.NotFound(ForbiddenReason.AssistantUse);
        if (!request.Confirmed)
            return Invalid("confirmed", "請先確認要分享這一則問題與回覆。");

        string question;
        string answer;
        if (assistant.KeepConversations)
        {
            if (request.ThreadId is not { } threadId || request.QuestionMessageId is not { } questionId
                || request.AnswerMessageId is not { } answerId)
                return ApiErrors.NotFound(ForbiddenReason.ChatThread);
            var owned = await dbContext.ChatThreads.AsNoTracking()
                .Where(ChatThreadAccess.OwnedBy(callerId, id))
                .AnyAsync(thread => thread.Id == threadId, cancellationToken);
            if (!owned)
                return ApiErrors.NotFound(ForbiddenReason.ChatThread);
            var pair = await dbContext.ChatMessages.AsNoTracking()
                .Where(message => message.ThreadId == threadId && (message.Id == questionId || message.Id == answerId))
                .ToListAsync(cancellationToken);
            var asked = pair.SingleOrDefault(message => message.Id == questionId && message.Author == ChatMessageAuthor.Account);
            var replied = pair.SingleOrDefault(message => message.Id == answerId && message.Author == ChatMessageAuthor.Assistant);
            // A form request or receipt (M4 #148) is not an answer to hand off: the submitted
            // answers reach their readers only through the consented record, never through an issue.
            if (asked is null || replied is null || replied.Sequence != asked.Sequence + 1
                || replied.ReplyKind is ChatReplyKind.FormRequest or ChatReplyKind.SubmissionReceipt)
                return ApiErrors.NotFound(ForbiddenReason.ChatThread);
            question = asked.Text;
            answer = replied.Text;
        }
        else
        {
            if (request.ThreadId is not null || request.QuestionMessageId is not null || request.AnswerMessageId is not null)
                return Invalid("threadId", "不保存對話的助理不能指定已保存的訊息。");
            question = request.SharedQuestion?.Trim() ?? "";
            answer = request.SharedAnswer?.Trim() ?? "";
            if (question.Length is 0 or > ChatRunRules.QuestionMaxLength)
                return Invalid("sharedQuestion", "請提供本次問題，最多 2000 字。");
            if (answer.Length is 0 or > 20000)
                return Invalid("sharedAnswer", "請提供本次回覆，最多 20000 字。");
        }

        // The owner is the default handler only while eligible. Otherwise it remains unassigned
        // for an eligible handler to claim or be assigned later (product decision pending).
        var ownerEligible = await dbContext.AccountPermissions.AsNoTracking().AnyAsync(
            grant => grant.AccountId == assistant.OwnerAccountId
                && grant.Permission == AccountPermission.HandleAssistantIssues, cancellationToken);
        var assigneeId = ownerEligible ? assistant.OwnerAccountId : (Guid?)null;
        var (issue, created) = AssistantIssue.OpenFromHandoff(
            assistant, callerId, question, answer, !assistant.KeepConversations, assigneeId, clock.GetUtcNow());
        dbContext.AssistantIssues.Add(issue);
        dbContext.AssistantIssueEvents.Add(created);
        await dbContext.SaveChangesAsync(cancellationToken);
        return Results.Created($"/api/v1/issues/{issue.Id}", new AssistantIssueView(
            issue.Id, assistant.Id, assistant.Name, issue.Source, issue.Status, issue.Title,
            issue.AssigneeAccountId, null, issue.ReporterAccountId, null, issue.DueAt,
            issue.TestRunId, issue.TestResultId, issue.TestFailureReason, issue.QuestionSnapshot,
            issue.AnswerSnapshot, issue.ResolutionNote, issue.CreatedAt, issue.UpdatedAt,
            issue.ResolvedAt, false, false, issue.HandoffUnverified));
    }

    private static IResult Invalid(string field, string message) =>
        ApiErrors.ValidationFailed(message, new Dictionary<string, string[]> { [field] = [message] });
}
