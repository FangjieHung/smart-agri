using System.Collections.Concurrent;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAI;
using Shouldly;
using SmartAgri.Application.Ai;
using SmartAgri.Domain.Ai;
using SmartAgri.Infrastructure.Ai;
using SmartAgri.Infrastructure.Tenancy;

namespace SmartAgri.Api.Tests.Ai;

/// <summary>
/// The deployment's list of chat models (M6 plan §3 A, B; issue #238): <c>Ai:Chat</c> is the
/// default, <c>Ai:Chat:Models</c> adds more, each with its own settings; validation; and the
/// scoped client that sends every call to the model the organization resolves to. No host, no
/// database (the startup side is <see cref="ChatModelCatalogStartupTests"/>).
/// </summary>
public sealed class ChatModelCatalogTests
{
    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    // --- Validation -----------------------------------------------------------------------------

    [Fact]
    public void More_models_without_a_default_are_refused_and_say_why()
    {
        var options = new ChatModelOptions { Models = [Entry("OpenAICompatible", "second", id: "second")] };

        options.Validate("Production").ShouldNotBeNull()
            .ShouldContain("Ai:Chat:Provider is required when Ai:Chat:Models lists more chat models");
    }

    [Fact]
    public void Two_models_with_the_same_id_are_refused_and_say_which()
    {
        var options = Default(id: "main", models: [Entry("OpenAICompatible", "other", id: "MAIN")]);

        options.Validate("Production").ShouldNotBeNull()
            .ShouldContain("Ai:Chat:Models:0:Id 'MAIN' is already the id of Ai:Chat");
    }

    [Fact]
    public void An_id_defaults_to_the_model_name_so_two_entries_of_one_model_need_their_own_ids()
    {
        var options = Default(models: [Entry("OpenAICompatible", "vllm-chat")]);

        options.Validate("Production").ShouldNotBeNull().ShouldContain("Ai:Chat:Models:0:Id 'vllm-chat' is already the id of Ai:Chat");
        options.Models[0].Id = "vllm-chat-fast";
        options.Validate("Production").ShouldBeNull();
    }

    [Fact]
    public void A_fake_entry_outside_development_and_testing_is_refused_like_a_fake_default()
    {
        var options = Default(models: [Entry("Fake", "fake-second", id: "second")]);

        options.Validate("Production").ShouldNotBeNull()
            .ShouldContain("Ai:Chat:Models:0:Provider=Fake is only allowed in the Development and Testing environments, not in 'Production'");
        options.Validate("Testing").ShouldBeNull();
    }

    [Fact]
    public void Every_entry_gets_the_same_rules_as_the_default_with_its_own_path()
    {
        Default(models: [new ChatModelEntryOptions { Provider = "OpenAI", Model = "gpt-second" }]).Validate("Production")
            .ShouldNotBeNull().ShouldContain("Ai:Chat:Models:0:ApiKey is required for OpenAI (set it as the environment variable Ai__Chat__Models__0__ApiKey)");
        Default(models: [Entry("OpenAICompatible", "m2", id: "m2", configure: entry => entry.MaxOutputTokens = 0)]).Validate("Production")
            .ShouldNotBeNull().ShouldContain("Ai:Chat:Models:0:MaxOutputTokens");
        Default(models: [Entry("OpenAICompatible", "m2", id: "m2", configure: entry => entry.ReasoningEffort = "minimal")]).Validate("Production")
            .ShouldNotBeNull().ShouldContain("Ai:Chat:Models:0:ReasoningEffort");
        Default(models: [new ChatModelEntryOptions { Provider = "OpenAICompatible", Model = "m2", Id = "m2" }]).Validate("Production")
            .ShouldNotBeNull().ShouldContain("Ai:Chat:Models:0:Endpoint is required for OpenAICompatible");
    }

    [Theory]
    [InlineData("has space")]
    [InlineData("a/b")]
    [InlineData("中文")]
    public void An_id_may_only_use_letters_digits_dashes_underscores_and_dots(string id)
    {
        Default(id: id).Validate("Production").ShouldNotBeNull().ShouldContain("Ai:Chat:Id");
        Default(models: [Entry("OpenAICompatible", "m2", id: id)]).Validate("Production").ShouldNotBeNull().ShouldContain("Ai:Chat:Models:0:Id");
        Default(id: "gpt-6.luna_A").Validate("Production").ShouldBeNull();
        Default(id: new string('a', ChatModelEntryOptions.IdMaxLength + 1)).Validate("Production").ShouldNotBeNull();
    }

    [Fact]
    public void A_single_default_keeps_any_model_name_as_its_id_but_a_list_needs_usable_ids()
    {
        // A deployment with only Ai:Chat changes nothing — even a model name that is no usable id.
        var single = new ChatModelOptions { Provider = "OpenAICompatible", Model = "meta/llama", Endpoint = "http://vllm:8000/v1" };
        single.Validate("Production").ShouldBeNull();

        single.Models = [Entry("OpenAICompatible", "m2", id: "m2")];
        single.Validate("Production").ShouldNotBeNull().ShouldContain("Ai:Chat:Id is required when the model name 'meta/llama' is not a usable id");
        single.Id = "llama";
        single.Validate("Production").ShouldBeNull();
    }

    [Fact]
    public void An_entry_without_a_provider_is_a_blank_placeholder_and_is_skipped()
    {
        // As a compose file reserving a second model binds it: every value blank.
        var blank = new ChatModelEntryOptions { Provider = "", Model = "", ApiKey = "", Id = "", DisplayName = "" };
        var options = Default(models: [blank]);

        options.Validate("Production").ShouldBeNull();
        using var catalog = Catalog(options);
        catalog.Entries.ShouldHaveSingleItem().IsDeploymentDefault.ShouldBeTrue();

        // Blank placeholders do not need a default either (a deployment without any chat model).
        new ChatModelOptions { Models = [blank] }.Validate("Production").ShouldBeNull();
    }

    // --- The catalog ------------------------------------------------------------------------------

    [Fact]
    public void A_deployment_with_only_ai_chat_offers_one_model_its_default()
    {
        var options = new ChatModelOptions { Provider = "Fake", Model = "fake-chat-dev" };
        using var catalog = Catalog(options, environment: "Development");

        catalog.IsConfigured.ShouldBeTrue();
        var only = catalog.Entries.ShouldHaveSingleItem();
        (only.Id, only.DisplayName, only.Model, only.IsDeploymentDefault).ShouldBe(("fake-chat-dev", "fake-chat-dev", "fake-chat-dev", true));
        only.ShouldBeSameAs(catalog.DeploymentDefault);
        catalog.Find("FAKE-chat-dev").ShouldBeSameAs(only);
        catalog.Find("other").ShouldBeNull();
        catalog.Find(null).ShouldBeNull();
    }

    [Fact]
    public void Without_a_provider_the_catalog_offers_nothing_and_its_default_refuses_calls()
    {
        using var catalog = Catalog(new ChatModelOptions());

        catalog.IsConfigured.ShouldBeFalse();
        catalog.Entries.ShouldBeEmpty();
        catalog.DeploymentDefault.Provider.IsConfigured.ShouldBeFalse();
    }

    [Fact]
    public void The_default_is_the_registered_provider_itself()
    {
        var registered = ChatClientProvider.Create(new ChatModelOptions { Provider = "Fake", Model = "fake-a" });
        using var catalog = ChatModelCatalog.Create(new ChatModelOptions { Provider = "Fake", Model = "fake-a", DisplayName = "  預設模型 " }, registered);

        catalog.DeploymentDefault.Provider.ShouldBeSameAs(registered);
        (catalog.DeploymentDefault.Id, catalog.DeploymentDefault.DisplayName).ShouldBe(("fake-a", "預設模型"));
    }

    [Fact]
    public async Task With_two_models_each_ones_output_cap_reasoning_effort_and_timeout_apply_to_its_own_calls()
    {
        var underlying = new ConcurrentDictionary<string, CapturingChatClient>();
        var timeouts = new ConcurrentDictionary<string, TimeSpan?>();
        IChatClient Fake(OpenAIClientOptions clientOptions, string apiKey, string model)
        {
            timeouts[model] = clientOptions.NetworkTimeout;
            return underlying.GetOrAdd(model, _ => new CapturingChatClient(model));
        }

        var options = new ChatModelOptions
        {
            Provider = "OpenAICompatible",
            Endpoint = "http://vllm:8000/v1",
            Model = "model-a",
            MaxOutputTokens = 256,
            ReasoningEffort = "None",
            TimeoutSeconds = 30,
            Models =
            [
                new ChatModelEntryOptions
                {
                    Provider = "OpenAICompatible", Endpoint = "http://vllm:8001/v1", Model = "model-b", Id = "b", DisplayName = "模型 B",
                    MaxOutputTokens = 1024, ReasoningEffort = "High", TimeoutSeconds = 90,
                },
            ],
        };
        options.Validate("Production").ShouldBeNull();
        using var catalog = ChatModelCatalog.Create(
            options, ChatClientProvider.Create(options, Fake), entry => ChatClientProvider.Create(entry, Fake));

        catalog.Entries.Select(entry => (entry.Id, entry.DisplayName, entry.Model, entry.IsDeploymentDefault))
            .ShouldBe([("model-a", "model-a", "model-a", true), ("b", "模型 B", "model-b", false)]);

        foreach (var entry in catalog.Entries)
        {
            await entry.Provider.Client.GetResponseAsync([new ChatMessage(ChatRole.User, "問題")], options: null, CancellationToken);
        }

        var a = underlying["model-a"].Options.ShouldHaveSingleItem().ShouldNotBeNull();
        var b = underlying["model-b"].Options.ShouldHaveSingleItem().ShouldNotBeNull();
        (a.MaxOutputTokens, a.Reasoning?.Effort).ShouldBe(((int?)256, (ReasoningEffort?)ReasoningEffort.None));
        (b.MaxOutputTokens, b.Reasoning?.Effort).ShouldBe(((int?)1024, (ReasoningEffort?)ReasoningEffort.High));
        timeouts["model-a"].ShouldBe(TimeSpan.FromSeconds(30));
        timeouts["model-b"].ShouldBe(TimeSpan.FromSeconds(90));
        catalog.Entries[1].Provider.Endpoint.ShouldBe(new Uri("http://vllm:8001/v1"));

        // A call's own options still win over the entry's defaults.
        await catalog.Entries[1].Provider.Client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "問題")], new ChatOptions { MaxOutputTokens = 10 }, CancellationToken);
        underlying["model-b"].Options[^1]!.MaxOutputTokens.ShouldBe(10);
    }

    // --- Resolving the organization's model --------------------------------------------------------

    [Fact]
    public void An_organization_gets_its_choice_while_offered_and_the_default_otherwise()
    {
        using var catalog = Catalog(Default(models: [Entry("Fake", "fake-second", id: "second")]), environment: "Testing");

        OrganizationChatModelResolver.Resolve(catalog, selectedId: null)
            .ShouldBe(new ResolvedChatModel(catalog.DeploymentDefault, ChatModelSource.DeploymentDefault));
        OrganizationChatModelResolver.Resolve(catalog, "second")
            .ShouldBe(new ResolvedChatModel(catalog.Entries[1], ChatModelSource.Selected));
        OrganizationChatModelResolver.Resolve(catalog, "withdrawn")
            .ShouldBe(new ResolvedChatModel(catalog.DeploymentDefault, ChatModelSource.Removed));
    }

    [Fact]
    public async Task Until_organizations_can_choose_every_one_gets_the_deployment_default()
    {
        using var catalog = Catalog(Default(models: [Entry("Fake", "fake-second", id: "second")]), environment: "Testing");
        var resolver = new OrganizationChatModelResolver(catalog);

        var resolved = await resolver.ResolveAsync(CancellationToken);

        resolved.ShouldBe(new ResolvedChatModel(catalog.DeploymentDefault, ChatModelSource.DeploymentDefault));
        (await resolver.ResolveAsync(CancellationToken)).ShouldBeSameAs(resolved, "one answer for the whole scope");
    }

    [Fact]
    public async Task The_scoped_client_sends_and_records_every_call_to_the_resolved_model_resolving_once()
    {
        var first = new CapturingChatClient("model-a");
        var second = new CapturingChatClient("model-b");
        var defaultEntry = new ChatModelEntry("a", "A", new ChatClientProvider(first, "openai", "openai", "model-a", null), isDeploymentDefault: true);
        var secondEntry = new ChatModelEntry("b", "B", new ChatClientProvider(second, "openai-compatible", "openai", "model-b", null), isDeploymentDefault: false);
        using var catalog = new ChatModelCatalog(defaultEntry, [secondEntry]);
        var resolver = new CountingResolver(new ResolvedChatModel(secondEntry, ChatModelSource.Selected));
        var recorder = new MemoryRecorder();
        var organization = new FixedOrganizationContext(Guid.CreateVersion7());
        using var client = new OrganizationChatClient(resolver, entry => new ModelInvocationRecordingChatClient(
            entry.Provider, recorder, organization, TimeProvider.System, metrics: null, NullLogger<ModelInvocationRecordingChatClient>.Instance));
        var attribution = new ModelInvocationAttribution(ModelInvocationPurpose.AssistantTest, null, null).ToChatOptions();

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "問題")], attribution, CancellationToken);
        await foreach (var _ in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "問題")], attribution, CancellationToken))
        {
        }

        first.Options.ShouldBeEmpty();
        second.Options.Count.ShouldBe(2);
        resolver.Calls.ShouldBe(1);
        recorder.Rows.Select(row => (row.Provider, row.Model, row.Purpose, row.OrganizationId)).ShouldBe(
        [
            ("openai-compatible", "model-b", ModelInvocationPurpose.AssistantTest, organization.OrganizationId!.Value),
            ("openai-compatible", "model-b", ModelInvocationPurpose.AssistantTest, organization.OrganizationId!.Value),
        ]);
        client.GetService<ChatClientMetadata>().ShouldNotBeNull().DefaultModelId.ShouldBe("model-b");
    }

    // --- Helpers ---------------------------------------------------------------------------------------

    private static ChatModelOptions Default(string? id = null, List<ChatModelEntryOptions>? models = null) =>
        new() { Provider = "OpenAICompatible", Endpoint = "http://vllm:8000/v1", Model = "vllm-chat", Id = id, Models = models ?? [] };

    private static ChatModelEntryOptions Entry(string provider, string model, string? id = null, Action<ChatModelEntryOptions>? configure = null)
    {
        var entry = new ChatModelEntryOptions { Provider = provider, Model = model, Id = id, Endpoint = provider == "Fake" ? null : "http://vllm:8001/v1" };
        configure?.Invoke(entry);
        return entry;
    }

    private static ChatModelCatalog Catalog(ChatModelOptions options, string environment = "Production")
    {
        options.Validate(environment).ShouldBeNull();
        return ChatModelCatalog.Create(options, ChatClientProvider.Create(options));
    }

    private sealed class CountingResolver(ResolvedChatModel resolved) : IOrganizationChatModelResolver
    {
        public int Calls { get; private set; }

        public ValueTask<ResolvedChatModel> ResolveAsync(CancellationToken cancellationToken)
        {
            Calls++;
            return ValueTask.FromResult(resolved);
        }
    }

    private sealed class MemoryRecorder : IModelInvocationRecorder
    {
        public ConcurrentQueue<ModelInvocation> Rows { get; } = new();

        public Task RecordAsync(ModelInvocation invocation, CancellationToken cancellationToken)
        {
            Rows.Enqueue(invocation);
            return Task.CompletedTask;
        }
    }

    /// <summary>An underlying model client that answers at once and keeps the options of every call.</summary>
    private sealed class CapturingChatClient(string model) : IChatClient
    {
        public List<ChatOptions?> Options { get; } = [];

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Options.Add(options?.Clone());
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "答案")) { ModelId = model });
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Options.Add(options?.Clone());
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, "答案") { ModelId = model };
        }

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

        public void Dispose()
        {
        }
    }
}
