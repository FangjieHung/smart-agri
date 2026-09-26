namespace SmartAgri.Application.Validation;

/// <summary>
/// One rule a request broke, which the user can fix on the same screen (a "recoverable"
/// error, <c>docs/handoff/mock-to-api-mapping.md</c> §1.2). The API turns failures into a
/// <c>422</c>: <see cref="Field"/> becomes a key of <c>errors</c>, and the first failure's
/// <see cref="Message"/> becomes <c>message</c>.
/// </summary>
/// <param name="Field">The request field, spelled as on the wire (camelCase).</param>
/// <param name="Message">Shown to the user as is.</param>
public sealed record ValidationFailure(string Field, string Message);

/// <summary>
/// Either a valid, normalized <typeparamref name="T"/> or the rules it broke. Application
/// rules return this instead of throwing, so callers cannot forget the failure case and
/// nothing is written when validation fails.
/// </summary>
public sealed class ValidationResult<T>
{
    private readonly T? _value;

    private ValidationResult(T? value, IReadOnlyList<ValidationFailure> failures)
    {
        _value = value;
        Failures = failures;
    }

    /// <summary>Empty when valid.</summary>
    public IReadOnlyList<ValidationFailure> Failures { get; }

    public bool IsValid => Failures.Count == 0;

    /// <summary>The normalized value; throws when <see cref="IsValid"/> is false.</summary>
    public T Value => IsValid
        ? _value!
        : throw new InvalidOperationException("An invalid result has no value; check IsValid first.");

    public static ValidationResult<T> Valid(T value) => new(value, []);

    public static ValidationResult<T> Invalid(IReadOnlyList<ValidationFailure> failures)
    {
        ArgumentNullException.ThrowIfNull(failures);
        if (failures.Count == 0)
        {
            throw new ArgumentException("An invalid result needs at least one failure.", nameof(failures));
        }

        return new(default, failures);
    }

    public static ValidationResult<T> Invalid(string field, string message) => Invalid([new ValidationFailure(field, message)]);
}
