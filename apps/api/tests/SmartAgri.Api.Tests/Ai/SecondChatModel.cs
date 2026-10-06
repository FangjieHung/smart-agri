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

    /// <summary><paramref name="factory"/>'s host, with the second model configured and chosen.</summary>
    public static WebApplicationFactory<Program> Host(WebApplicationFactory<Program> factory) =>
        factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Ai:Chat:Models:0:Provider", "Fake");
            builder.UseSetting("Ai:Chat:Models:0:Model", Model);
            builder.UseSetting("Ai:Chat:Models:0:Id", Id);
            builder.ConfigureTestServices(Choose);
        });

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
