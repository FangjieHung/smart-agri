using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using SmartAgri.Infrastructure.Ai;

namespace SmartAgri.Api.Tests.Ai;

/// <summary>
/// A host whose deployment offers a second chat model (<c>Ai:Chat:Models:0</c>, <c>Fake</c>) and
/// whose resolver gives every organization that model (M6 plan §5 Slice 1: "替身解析器選到第二個
/// 項目"), so a test can see that a purpose really goes through the resolver.
/// </summary>
internal static class SecondChatModel
{
    public const string Id = "second";

    public const string Model = "fake-chat-second";

    public const string DisplayName = "第二個模型";

    /// <summary>The key and endpoint the second model is configured with, which no response may
    /// ever contain (M6-2).</summary>
    public const string ApiKey = "sk-second-model-secret-key";

    public const string Endpoint = "http://second-model.internal:8001/v1";

    /// <summary><paramref name="factory"/>'s host, with the second model configured and chosen.</summary>
    public static WebApplicationFactory<Program> Host(WebApplicationFactory<Program> factory) =>
        factory.WithWebHostBuilder(builder =>
        {
            Configure(builder);
            builder.ConfigureTestServices(Choose);
        });

    /// <summary>
    /// <paramref name="factory"/>'s host, with the second model offered but the real resolver: an
    /// organization uses it once its manager chooses it (<c>PUT /api/v1/organization/chat-model</c>,
    /// M6-2). Same database, so a choice made through either host is seen by both — the original
    /// host, which does not offer it, then reports it <c>removed</c>.
    /// </summary>
    public static WebApplicationFactory<Program> Offered(WebApplicationFactory<Program> factory) =>
        factory.WithWebHostBuilder(Configure);

    private static void Configure(IWebHostBuilder builder)
    {
        builder.UseSetting("Ai:Chat:Models:0:Provider", "Fake");
        builder.UseSetting("Ai:Chat:Models:0:Model", Model);
        builder.UseSetting("Ai:Chat:Models:0:Id", Id);
        builder.UseSetting("Ai:Chat:Models:0:DisplayName", DisplayName);
        builder.UseSetting("Ai:Chat:Models:0:ApiKey", ApiKey);
        builder.UseSetting("Ai:Chat:Models:0:Endpoint", Endpoint);
    }

    /// <summary>Replaces the resolver with one that always picks the catalog's <see cref="Id"/>.</summary>
    public static void Choose(IServiceCollection services) =>
        services.AddScoped<IOrganizationChatModelResolver, Resolver>();

    private sealed class Resolver(ChatModelCatalog catalog) : IOrganizationChatModelResolver
    {
        public ValueTask<ResolvedChatModel> ResolveAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(new ResolvedChatModel(
                catalog.Find(Id) ?? throw new InvalidOperationException($"The catalog has no '{Id}' model."),
                ChatModelSource.Selected));
    }
}
