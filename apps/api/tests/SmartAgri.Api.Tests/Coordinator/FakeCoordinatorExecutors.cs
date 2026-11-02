using System.Collections.Concurrent;
using Shouldly;
using SmartAgri.Application.Coordinator;
using SmartAgri.Domain.Coordinator;

namespace SmartAgri.Api.Tests.Coordinator;

/// <summary>
/// Executors for LINE 群發, Google and FB that stand in for Slices 5, 7 and 8 (#375): available only for the shops a
/// test <see cref="Enroll"/>s for a kind — every other shop sees no executor, so the other tests sharing the host still
/// get reminders — answering each call with the next scripted outcome (a success when the script is used up), and
/// recording every call. A kind with a real executor (LINE 群發 since #376, FB 貼文 since #379) hands every shop not
/// enrolled to it.
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

    /// <summary>The executor of <paramref name="kind"/>: the scripts for enrolled shops, <paramref name="real"/> (if any)
    /// for the others.</summary>
    public ICoordinatorActionExecutor For(ShopActionKind kind, ICoordinatorActionExecutor? real = null) => new Executor(this, kind, real);

    public static Func<CoordinatorActionRequest, CoordinatorActionResult> Succeed(string? externalRef = null) =>
        _ => CoordinatorActionResult.Succeeded(externalRef);

    public static Func<CoordinatorActionRequest, CoordinatorActionResult> Fail(string code) => _ => CoordinatorActionResult.Failed(code);

    public static Func<CoordinatorActionRequest, CoordinatorActionResult> Unavailable() => _ => CoordinatorActionResult.Unavailable;

    public static Func<CoordinatorActionRequest, CoordinatorActionResult> Throw() =>
        _ => throw new InvalidOperationException("The fake integration broke.");

    private sealed class Executor(FakeCoordinatorExecutors fakes, ShopActionKind kind, ICoordinatorActionExecutor? real) : ICoordinatorActionExecutor
    {
        public ShopActionKind Kind => kind;

        public ValueTask<bool> IsAvailableAsync(Guid organizationId, CancellationToken cancellationToken) =>
            Enrolled(organizationId) || real is null
                ? ValueTask.FromResult(Enrolled(organizationId))
                : real.IsAvailableAsync(organizationId, cancellationToken);

        public Task<CoordinatorActionResult> ExecuteAsync(CoordinatorActionRequest request, CancellationToken cancellationToken) =>
            Enrolled(request.OrganizationId) || real is null ? RunAsync(request) : real.ExecuteAsync(request, cancellationToken);

        public Task<CoordinatorActionResult> UndoAsync(CoordinatorActionRequest request, CancellationToken cancellationToken) =>
            Enrolled(request.OrganizationId) || real is null ? RunAsync(request) : real.UndoAsync(request, cancellationToken);

        private bool Enrolled(Guid organizationId) => fakes._scripts.ContainsKey((organizationId, kind));

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
