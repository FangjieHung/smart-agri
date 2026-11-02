using System.Collections.Concurrent;
using Shouldly;
using SmartAgri.Application.Coordinator;
using SmartAgri.Domain.Coordinator;

namespace SmartAgri.Api.Tests.Coordinator;

/// <summary>
/// Executors for LINE 群發, Google and FB that stand in for Slices 5, 7 and 8 (#375): available only for the shops a
/// test <see cref="Enroll"/>s for a kind — every other shop sees no executor, so the other tests sharing the host still
/// get reminders — answering each call with the next scripted outcome (a success when the script is used up), and
/// recording every call.
/// </summary>
public sealed class FakeCoordinatorExecutors
{
    public static readonly ShopActionKind[] Kinds = [ShopActionKind.LineBroadcast, ShopActionKind.GoogleSpecialHours, ShopActionKind.FacebookPost];

    private readonly ConcurrentDictionary<(Guid Organization, ShopActionKind Kind), ConcurrentQueue<Func<CoordinatorActionRequest, CoordinatorActionResult>>> _scripts = new();
    private readonly ConcurrentQueue<CoordinatorActionRequest> _calls = new();

    /// <summary>Makes <paramref name="kind"/> available for <paramref name="organizationId"/>, answering with
    /// <paramref name="outcomes"/> in turn.</summary>
    public void Enroll(Guid organizationId, ShopActionKind kind, params Func<CoordinatorActionRequest, CoordinatorActionResult>[] outcomes)
    {
        var script = _scripts.GetOrAdd((organizationId, kind), _ => new ConcurrentQueue<Func<CoordinatorActionRequest, CoordinatorActionResult>>());
        foreach (var outcome in outcomes)
        {
            script.Enqueue(outcome);
        }
    }

    /// <summary>The calls made for <paramref name="organizationId"/>, in order.</summary>
    public IReadOnlyList<CoordinatorActionRequest> Calls(Guid organizationId) => [.. _calls.Where(call => call.OrganizationId == organizationId)];

    public ICoordinatorActionExecutor For(ShopActionKind kind) => new Executor(this, kind);

    public static Func<CoordinatorActionRequest, CoordinatorActionResult> Succeed(string? externalRef = null) =>
        _ => CoordinatorActionResult.Succeeded(externalRef);

    public static Func<CoordinatorActionRequest, CoordinatorActionResult> Fail(string code) => _ => CoordinatorActionResult.Failed(code);

    public static Func<CoordinatorActionRequest, CoordinatorActionResult> Unavailable() => _ => CoordinatorActionResult.Unavailable;

    public static Func<CoordinatorActionRequest, CoordinatorActionResult> Throw() =>
        _ => throw new InvalidOperationException("The fake integration broke.");

    private sealed class Executor(FakeCoordinatorExecutors fakes, ShopActionKind kind) : ICoordinatorActionExecutor
    {
        public ShopActionKind Kind => kind;

        public ValueTask<bool> IsAvailableAsync(Guid organizationId, CancellationToken cancellationToken) =>
            ValueTask.FromResult(fakes._scripts.ContainsKey((organizationId, kind)));

        public Task<CoordinatorActionResult> ExecuteAsync(CoordinatorActionRequest request, CancellationToken cancellationToken) => RunAsync(request);

        public Task<CoordinatorActionResult> UndoAsync(CoordinatorActionRequest request, CancellationToken cancellationToken) => RunAsync(request);

        private Task<CoordinatorActionResult> RunAsync(CoordinatorActionRequest request)
        {
            request.Kind.ShouldBe(kind);
            fakes._calls.Enqueue(request);
            var outcome = fakes._scripts.TryGetValue((request.OrganizationId, kind), out var script) && script.TryDequeue(out var next)
                ? next
                : Succeed();
            return Task.FromResult(outcome(request));
        }
    }
}
