using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Shouldly;
using SmartAgri.Api.Ai;
using SmartAgri.Api.Tests.Errors;
using SmartAgri.Application.Ai;

namespace SmartAgri.Api.Tests.Ai;

/// <summary>
/// <c>ChatErrors</c>' <c>503</c> mapping (M3 plan, Slice 4; the chat-side twin of the embedding
/// pipeline's <c>embedding-not-configured</c> / <c>embedding-unavailable</c>). No public
/// conversation endpoint exists yet, so this is exercised directly.
/// </summary>
public class ChatErrorsTests
{
    [Fact]
    public async Task Provider_not_configured_maps_to_chat_not_configured()
    {
        var response = await ApiErrorsTests.ExecuteAsync(ChatErrors.ToApiResult(new ChatGenerationException(providerNotConfigured: true)));

        response.StatusCode.ShouldBe(StatusCodes.Status503ServiceUnavailable);
        response.ContentType.ShouldBe("application/problem+json");

        using var json = JsonDocument.Parse(response.Body);
        json.RootElement.GetProperty("reason").GetString().ShouldBe("chat-not-configured");
        json.RootElement.GetProperty("message").GetString().ShouldNotBeNull().ShouldContain("Ai:Chat:Provider");
    }

    [Fact]
    public async Task A_failed_provider_call_maps_to_chat_unavailable()
    {
        var response = await ApiErrorsTests.ExecuteAsync(
            ChatErrors.ToApiResult(new ChatGenerationException(providerNotConfigured: false, new InvalidOperationException("timeout"))));

        response.StatusCode.ShouldBe(StatusCodes.Status503ServiceUnavailable);

        using var json = JsonDocument.Parse(response.Body);
        json.RootElement.GetProperty("reason").GetString().ShouldBe("chat-unavailable");
        json.RootElement.GetProperty("message").GetString().ShouldBe("對話模型暫時無法使用，請稍後重試。");
    }
}
