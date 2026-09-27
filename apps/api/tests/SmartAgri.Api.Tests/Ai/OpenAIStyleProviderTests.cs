using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using SmartAgri.Application.Ai;
using SmartAgri.Domain.Ai;
using SmartAgri.Infrastructure.Ai;
using SmartAgri.Infrastructure.Tenancy;

namespace SmartAgri.Api.Tests.Ai;

/// <summary>
/// The OpenAI-style providers for real — the official OpenAI client through
/// <c>Microsoft.Extensions.AI.OpenAI</c>, wrapped in the recording middleware — against a local
/// server that answers like OpenAI's <c>/embeddings</c>: where requests go, what they carry (the
/// key, the model, the prefixed inputs, and nothing of ours), and that the reported usage is
/// recorded. No network beyond loopback, no API key.
/// </summary>
public sealed class OpenAIStyleProviderTests : IAsyncLifetime
{
    private readonly ConcurrentQueue<CapturedRequest> _requests = new();
    private WebApplication? _server;
    private Uri _address = null!;

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        _server = builder.Build();
        _server.MapPost("/{**path}", async (HttpContext context) =>
        {
            using var reader = new StreamReader(context.Request.Body);
            var body = await reader.ReadToEndAsync(context.RequestAborted);
            _requests.Enqueue(new CapturedRequest(context.Request.Path, context.Request.Headers.Authorization.ToString(), body));

            var inputs = JsonNode.Parse(body)!["input"]!.AsArray();
            var data = new JsonArray([.. inputs.Select((_, index) => (JsonNode)new JsonObject
            {
                ["object"] = "embedding",
                ["index"] = index,
                ["embedding"] = new JsonArray(0.6, 0.8 * (index + 1), 0.0),
            })]);
            return Results.Json(new JsonObject
            {
                ["object"] = "list",
                ["data"] = data,
                ["model"] = "served-model",
                ["usage"] = new JsonObject { ["prompt_tokens"] = 11, ["total_tokens"] = 11 },
            });
        });
        await _server.StartAsync(CancellationToken);
        _address = new Uri(_server.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
    }

    public async ValueTask DisposeAsync()
    {
        if (_server is not null)
        {
            await _server.DisposeAsync();
        }
    }

    [Fact]
    public async Task An_openai_compatible_server_gets_the_model_and_inputs_and_its_usage_is_recorded()
    {
        var recorded = new List<ModelInvocation>();
        var options = new EmbeddingOptions { Provider = "OpenAICompatible", Endpoint = $"{_address}v1", Model = "intfloat/multilingual-e5-large", ApiKey = "local-token" };

        var embeddings = await EmbedAsync(options, recorded, ["passage: 退貨須知", "passage: 運費"]);

        embeddings.Select(embedding => embedding.Vector.ToArray()).ShouldBe([[0.6f, 0.8f, 0f], [0.6f, 1.6f, 0f]]);
        var request = _requests.ShouldHaveSingleItem();
        request.Path.ShouldBe("/v1/embeddings");
        request.Authorization.ShouldBe("Bearer local-token");
        using var body = JsonDocument.Parse(request.Body);
        body.RootElement.GetProperty("model").GetString().ShouldBe("intfloat/multilingual-e5-large");
        body.RootElement.GetProperty("input").EnumerateArray().Select(input => input.GetString()).ShouldBe(["passage: 退貨須知", "passage: 運費"]);
        request.Body.ShouldNotContain("smartagri", Case.Insensitive);

        var row = recorded.ShouldHaveSingleItem();
        (row.Provider, row.Model, row.InputTokens, row.Succeeded).ShouldBe(("openai-compatible", "intfloat/multilingual-e5-large", (long?)11, true));
    }

    [Fact]
    public async Task A_server_without_a_key_gets_a_placeholder_the_client_accepts()
    {
        await EmbedAsync(new EmbeddingOptions { Provider = "OpenAICompatible", Endpoint = $"{_address}v1/", Model = "e5" }, [], ["文字"]);

        var request = _requests.ShouldHaveSingleItem();
        request.Path.ShouldBe("/v1/embeddings");
        request.Authorization.ShouldBe("Bearer no-key");
    }

    [Fact]
    public async Task Azure_openai_is_the_same_client_on_the_resources_v1_endpoint_with_the_deployment_as_model()
    {
        var recorded = new List<ModelInvocation>();
        var options = new EmbeddingOptions { Provider = "AzureOpenAI", Endpoint = $"{_address}openai/v1/", Model = "embedding-deployment", ApiKey = "azure-key" };
        options.Validate("Production").ShouldBeNull();

        await EmbedAsync(options, recorded, ["文字"]);

        var request = _requests.ShouldHaveSingleItem();
        request.Path.ShouldBe("/openai/v1/embeddings");
        request.Authorization.ShouldBe("Bearer azure-key");
        JsonDocument.Parse(request.Body).RootElement.GetProperty("model").GetString().ShouldBe("embedding-deployment");
        recorded.ShouldHaveSingleItem().Provider.ShouldBe("azure-openai");
    }

    private static async Task<Microsoft.Extensions.AI.GeneratedEmbeddings<Microsoft.Extensions.AI.Embedding<float>>> EmbedAsync(
        EmbeddingOptions options,
        List<ModelInvocation> recorded,
        string[] inputs)
    {
        using var provider = EmbeddingProvider.Create(options);
        using var generator = new ModelInvocationRecordingEmbeddingGenerator(
            provider,
            new ListRecorder(recorded),
            new FixedOrganizationContext(Guid.CreateVersion7()),
            TimeProvider.System,
            metrics: null,
            NullLogger<ModelInvocationRecordingEmbeddingGenerator>.Instance);
        return await generator.GenerateAsync(
            inputs,
            new ModelInvocationAttribution(ModelInvocationPurpose.EmbedDocument, null, null).ToEmbeddingOptions(),
            CancellationToken);
    }

    private sealed record CapturedRequest(string Path, string Authorization, string Body);

    private sealed class ListRecorder(List<ModelInvocation> rows) : IModelInvocationRecorder
    {
        public Task RecordAsync(ModelInvocation invocation, CancellationToken cancellationToken)
        {
            rows.Add(invocation);
            return Task.CompletedTask;
        }
    }
}
