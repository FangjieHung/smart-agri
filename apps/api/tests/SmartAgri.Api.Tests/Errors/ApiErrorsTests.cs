using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using SmartAgri.Api.Errors;

namespace SmartAgri.Api.Tests.Errors;

/// <summary>Error shapes (M1 plan, Slice 5). No database needed.</summary>
public class ApiErrorsTests
{
    [Fact]
    public async Task Not_found_and_forbidden_are_byte_identical()
    {
        var forbidden = await ExecuteAsync(ApiErrors.Forbidden(ForbiddenReason.Team));
        var notFound = await ExecuteAsync(ApiErrors.NotFound(ForbiddenReason.Team));

        notFound.StatusCode.ShouldBe(forbidden.StatusCode);
        notFound.ContentType.ShouldBe(forbidden.ContentType);
        notFound.ContentLength.ShouldBe(forbidden.ContentLength);
        notFound.Body.ShouldBe(forbidden.Body);
        notFound.Body.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task Repeated_forbidden_responses_are_byte_identical()
    {
        var first = await ExecuteAsync(ApiErrors.Forbidden(ForbiddenReason.Team));
        var second = await ExecuteAsync(ApiErrors.Forbidden(ForbiddenReason.Team));

        second.Body.ShouldBe(first.Body);
    }

    [Fact]
    public async Task Forbidden_is_problem_details_with_reason_and_message()
    {
        var response = await ExecuteAsync(ApiErrors.Forbidden(ForbiddenReason.Team));

        response.StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
        response.ContentType.ShouldBe("application/problem+json");
        response.ContentLength.ShouldBe(response.Body.Length);

        using var json = JsonDocument.Parse(response.Body);
        var root = json.RootElement;
        root.GetProperty("status").GetInt32().ShouldBe(403);
        root.GetProperty("title").GetString().ShouldBe("Forbidden");
        root.GetProperty("type").GetString().ShouldNotBeNullOrEmpty();
        root.GetProperty("reason").GetString().ShouldBe("team");
        root.GetProperty("message").GetString().ShouldBe("只有可管理助理與團隊的帳號可以查看或變更團隊成員權限。");

        // Nothing request-specific (traceId, instance) that could make two 403s differ.
        root.EnumerateObject().Select(property => property.Name)
            .ShouldBe(["type", "title", "status", "reason", "message"]);
    }

    [Fact]
    public async Task Validation_failure_is_422_with_message_and_errors()
    {
        var response = await ExecuteAsync(ApiErrors.ValidationFailed(
            "權限設定無法儲存。",
            new Dictionary<string, string[]> { ["permissions"] = ["不能移除自己的管理權限。"] }));

        response.StatusCode.ShouldBe(StatusCodes.Status422UnprocessableEntity);
        response.ContentType.ShouldBe("application/problem+json");

        using var json = JsonDocument.Parse(response.Body);
        var root = json.RootElement;
        root.GetProperty("status").GetInt32().ShouldBe(422);
        root.GetProperty("message").GetString().ShouldBe("權限設定無法儲存。");
        root.GetProperty("errors").GetProperty("permissions")[0].GetString().ShouldBe("不能移除自己的管理權限。");
    }

    [Fact]
    public async Task A_422_with_reason_has_reason_message_extensions_and_the_field_error()
    {
        var response = await ExecuteAsync(ApiErrors.WithReason(
            StatusCodes.Status422UnprocessableEntity,
            "duplicate-content",
            "內容相同。",
            "file",
            [new("existingDocumentName", "退貨政策.pdf")]));

        response.StatusCode.ShouldBe(StatusCodes.Status422UnprocessableEntity);
        response.ContentType.ShouldBe("application/problem+json");
        using var json = JsonDocument.Parse(response.Body);
        var root = json.RootElement;
        root.EnumerateObject().Select(property => property.Name)
            .ShouldBe(["type", "title", "status", "reason", "message", "existingDocumentName", "errors"]);
        root.GetProperty("title").GetString().ShouldBe("Unprocessable Content");
        root.GetProperty("reason").GetString().ShouldBe("duplicate-content");
        root.GetProperty("existingDocumentName").GetString().ShouldBe("退貨政策.pdf");
        root.GetProperty("errors").GetProperty("file")[0].GetString().ShouldBe("內容相同。");
    }

    [Theory]
    [InlineData(StatusCodes.Status409Conflict, "Conflict")]
    [InlineData(StatusCodes.Status413PayloadTooLarge, "Content Too Large")]
    [InlineData(StatusCodes.Status415UnsupportedMediaType, "Unsupported Media Type")]
    [InlineData(StatusCodes.Status503ServiceUnavailable, "Service Unavailable")]
    public async Task Other_reasoned_errors_are_problem_details_with_reason_and_message_only(int status, string title)
    {
        var response = await ExecuteAsync(ApiErrors.WithReason(status, "some-reason", "訊息。"));

        response.StatusCode.ShouldBe(status);
        using var json = JsonDocument.Parse(response.Body);
        var root = json.RootElement;
        root.EnumerateObject().Select(property => property.Name).ShouldBe(["type", "title", "status", "reason", "message"]);
        root.GetProperty("title").GetString().ShouldBe(title);
        root.GetProperty("status").GetInt32().ShouldBe(status);
    }

    [Fact]
    public void Reasoned_errors_refuse_statuses_and_fields_they_do_not_describe()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => ApiErrors.WithReason(StatusCodes.Status400BadRequest, "x", "y"));
        Should.Throw<ArgumentException>(() => ApiErrors.WithReason(StatusCodes.Status422UnprocessableEntity, "x", "y"));
        Should.Throw<ArgumentException>(() => ApiErrors.WithReason(StatusCodes.Status409Conflict, "x", "y", "file"));
    }

    [Fact]
    public async Task A_refusal_about_several_fields_is_a_reasoned_422_grouping_every_message_by_field()
    {
        var response = await ExecuteAsync(ApiErrors.Refused(
            "versions-not-approvable",
            "有 2 個版本不能確認生效。",
            [new("versionIds[1]", "處理失敗。"), new("versionIds[3]", "已經確認過了。"), new("versionIds[1]", "另一個原因。")]));

        response.StatusCode.ShouldBe(StatusCodes.Status422UnprocessableEntity);
        response.ContentType.ShouldBe("application/problem+json");
        using var json = JsonDocument.Parse(response.Body);
        var root = json.RootElement;
        root.EnumerateObject().Select(property => property.Name).ShouldBe(["type", "title", "status", "reason", "message", "errors"]);
        root.GetProperty("reason").GetString().ShouldBe("versions-not-approvable");
        root.GetProperty("message").GetString().ShouldBe("有 2 個版本不能確認生效。");
        root.GetProperty("errors").EnumerateObject().Select(error => (error.Name, error.Value.GetArrayLength()))
            .ShouldBe([("versionIds[1]", 2), ("versionIds[3]", 1)]);
        Should.Throw<ArgumentException>(() => ApiErrors.Refused("x", "y", []));
    }

    [Fact]
    public async Task Unauthorized_has_no_body()
    {
        var response = await ExecuteAsync(ApiErrors.Unauthorized());

        response.StatusCode.ShouldBe(StatusCodes.Status401Unauthorized);
        response.Body.ShouldBeEmpty();
    }

    internal static async Task<CapturedResponse> ExecuteAsync(IResult result)
    {
        var httpContext = CreateHttpContext();
        await result.ExecuteAsync(httpContext);
        return CapturedResponse.From(httpContext);
    }

    internal static DefaultHttpContext CreateHttpContext()
    {
        var services = new ServiceCollection()
            .AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance)
            .AddLogging()
            .BuildServiceProvider();
        return new DefaultHttpContext
        {
            RequestServices = services,
            Response = { Body = new MemoryStream() },
        };
    }

    internal sealed record CapturedResponse(int StatusCode, string? ContentType, long? ContentLength, byte[] Body)
    {
        public static CapturedResponse From(HttpContext httpContext) =>
            new(
                httpContext.Response.StatusCode,
                httpContext.Response.ContentType,
                httpContext.Response.ContentLength,
                ((MemoryStream)httpContext.Response.Body).ToArray());
    }
}
