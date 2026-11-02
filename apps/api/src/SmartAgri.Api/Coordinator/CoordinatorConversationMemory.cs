using System.Collections.Concurrent;

namespace SmartAgri.Api.Coordinator;

/// <summary>
/// What the 統籌助理 keeps <b>in memory only</b> between two events of a conversation (#374), never in the
/// database and never logged:
/// <list type="bullet">
/// <item>the command of a member of several shops while they choose which shop (「哪一家？」), one per LINE user,
/// for <see cref="HeldMessageLifetime"/>;</item>
/// <item>for a command that still waits for its date or time, until that command expires: the reason given with
/// it — so the card can carry it once the details arrive (the reason is stored nowhere else; it ends up only in
/// the announcement) — and the question asked, which the model is shown with the answer (「下週要休哪一天？」 makes
/// 「週四」 next week's).</item>
/// </list>
/// Single instance, like the webhook queue: a restart forgets them (the member types the command again; the
/// card is shown without the reason line, which 「改成：」 can add; the answer is read without the question).
/// </summary>
public sealed class CoordinatorConversationMemory
{
    /// <summary>How long a held command waits for the shop choice.</summary>
    public static readonly TimeSpan HeldMessageLifetime = TimeSpan.FromMinutes(10);

    private readonly TimeProvider _clock;
    private readonly ConcurrentDictionary<string, Entry> _held = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Guid, Notes> _pending = new();

    public CoordinatorConversationMemory(TimeProvider clock)
    {
        _clock = clock;
    }

    /// <summary>Keeps <paramref name="text"/> for <paramref name="lineUserId"/>'s shop choice, replacing an earlier one.</summary>
    public void Hold(string lineUserId, string text)
    {
        var now = _clock.GetUtcNow();
        Prune(_held, now);
        _held[lineUserId] = new Entry(text, now + HeldMessageLifetime);
    }

    /// <summary>The command <paramref name="lineUserId"/> is choosing a shop for, removed; <see langword="null"/>
    /// when there is none or it is older than <see cref="HeldMessageLifetime"/>.</summary>
    public string? Take(string lineUserId) =>
        _held.TryRemove(lineUserId, out var entry) && entry.ExpiresAt > _clock.GetUtcNow() ? entry.Text : null;

    /// <summary>Keeps the reason and the question of waiting command <paramref name="commandId"/> until
    /// <paramref name="until"/>, replacing what was kept.</summary>
    public void RememberPending(Guid commandId, string? reason, string? question, DateTimeOffset until)
    {
        var now = _clock.GetUtcNow();
        foreach (var (key, notes) in _pending)
        {
            if (notes.ExpiresAt <= now)
            {
                _pending.TryRemove(key, out _);
            }
        }

        _pending[commandId] = new Notes(
            string.IsNullOrWhiteSpace(reason) ? null : reason, string.IsNullOrWhiteSpace(question) ? null : question, until);
    }

    /// <summary>The reason kept for command <paramref name="commandId"/>, if any.</summary>
    public string? ReasonOf(Guid commandId) => NotesOf(commandId)?.Reason;

    /// <summary>The question last asked about command <paramref name="commandId"/>, if kept.</summary>
    public string? QuestionOf(Guid commandId) => NotesOf(commandId)?.Question;

    public void Forget(Guid commandId) => _pending.TryRemove(commandId, out _);

    private Notes? NotesOf(Guid commandId) =>
        _pending.TryGetValue(commandId, out var notes) && notes.ExpiresAt > _clock.GetUtcNow() ? notes : null;

    private static void Prune(ConcurrentDictionary<string, Entry> entries, DateTimeOffset now)
    {
        foreach (var (key, entry) in entries)
        {
            if (entry.ExpiresAt <= now)
            {
                entries.TryRemove(key, out _);
            }
        }
    }

    /// <summary>No text in <c>ToString</c>: it is what a person wrote.</summary>
    private sealed record Entry(string Text, DateTimeOffset ExpiresAt)
    {
        public override string ToString() => $"{nameof(Entry)} {{ ExpiresAt = {ExpiresAt:O} }}";
    }

    /// <summary>No text in <c>ToString</c>: the reason is what a person wrote, the question may quote it.</summary>
    private sealed record Notes(string? Reason, string? Question, DateTimeOffset ExpiresAt)
    {
        public override string ToString() => $"{nameof(Notes)} {{ ExpiresAt = {ExpiresAt:O} }}";
    }
}
