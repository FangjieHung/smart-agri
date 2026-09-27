using System.Collections.Concurrent;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using SmartAgri.Application.Validation;

namespace SmartAgri.Api.Errors;

/// <summary>
/// The API's error responses (M1 plan, Slice 5; <c>docs/handoff/mock-to-api-mapping.md</c>
/// §1.5 and <c>tasks-6-10-backend-handoff.md</c> §1.6):
/// <list type="table">
/// <item><term><c>401</c></term><description>no body — not signed in or the token
/// expired; the frontend sends the user back to <c>/login</c>.</description></item>
/// <item><term><c>403</c></term><description>ProblemDetails + <c>reason</c> +
/// <c>message</c>. "Not found" and "forbidden" produce <b>byte-identical</b>
/// responses, so nobody can probe whether another organization's resource
/// exists.</description></item>
/// <item><term><c>422</c></term><description>ProblemDetails + <c>message</c> +
/// <c>errors</c>; plus a <c>reason</c> when the frontend has to tell refusals of the same
/// field apart (<see cref="WithReason"/>).</description></item>
/// <item><term><c>409</c>, <c>413</c>, <c>415</c></term><description>ProblemDetails +
/// <c>reason</c> + <c>message</c> (<see cref="WithReason"/>).</description></item>
/// </list>
/// </summary>
/// <remarks>
/// Bodies are written from fixed bytes rather than through <c>IProblemDetailsService</c>
/// on purpose: that service adds per-request fields (<c>traceId</c>, <c>instance</c>),
/// which would make two <c>403</c>s differ.
/// </remarks>
public static class ApiErrors
{
    public const string ProblemJsonContentType = "application/problem+json";

    private static readonly JsonWriterOptions WriterOptions = new()
    {
        // Readable Chinese messages; still escapes HTML-sensitive characters.
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
    };

    private static readonly ConcurrentDictionary<ForbiddenReason, byte[]> ForbiddenBodies = new();

    /// <summary>The ProblemDetails <c>type</c> and <c>title</c> of each status
    /// <see cref="WithReason"/> writes (RFC 9110's names).</summary>
    private static readonly Dictionary<int, (string Type, string Title)> ReasonStatuses = new()
    {
        [StatusCodes.Status409Conflict] = ("https://tools.ietf.org/html/rfc9110#section-15.5.10", "Conflict"),
        [StatusCodes.Status413PayloadTooLarge] = ("https://tools.ietf.org/html/rfc9110#section-15.5.14", "Content Too Large"),
        [StatusCodes.Status415UnsupportedMediaType] = ("https://tools.ietf.org/html/rfc9110#section-15.5.16", "Unsupported Media Type"),
        [StatusCodes.Status422UnprocessableEntity] = ("https://tools.ietf.org/html/rfc9110#section-15.5.21", "Unprocessable Content"),
    };

    /// <summary><c>401</c> with no body.</summary>
    public static IResult Unauthorized() => Results.Unauthorized();

    /// <summary>
    /// <c>403</c> for "the caller may not do this". Always identical, byte for byte, to
    /// <see cref="NotFound"/> with the same <paramref name="reason"/>.
    /// </summary>
    public static IResult Forbidden(ForbiddenReason reason)
    {
        ArgumentNullException.ThrowIfNull(reason);
        return new FixedBodyResult(StatusCodes.Status403Forbidden, ForbiddenBodies.GetOrAdd(reason, BuildForbiddenBody));
    }

    /// <summary>
    /// The response for "this resource does not exist (in the caller's organization)". It
    /// is deliberately the very same <c>403</c> as <see cref="Forbidden"/>: never
    /// <c>404</c>, never a message naming the resource.
    /// </summary>
    public static IResult NotFound(ForbiddenReason reason) => Forbidden(reason);

    /// <summary><c>422</c> with a summary <paramref name="message"/> and per-field
    /// <paramref name="errors"/>.</summary>
    public static IResult ValidationFailed(string message, IReadOnlyDictionary<string, string[]> errors)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(errors);
        return new FixedBodyResult(StatusCodes.Status422UnprocessableEntity, BuildValidationBody(message, errors));
    }

    /// <summary>
    /// <c>422</c> for failures from an Application rule: <c>message</c> is the first
    /// failure's, and <c>errors</c> groups every failure's message by field.
    /// </summary>
    public static IResult ValidationFailed(IReadOnlyList<ValidationFailure> failures)
    {
        ArgumentNullException.ThrowIfNull(failures);
        if (failures.Count == 0)
        {
            throw new ArgumentException("A validation failure needs at least one failure.", nameof(failures));
        }

        var errors = failures
            .GroupBy(failure => failure.Field, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Select(failure => failure.Message).ToArray(), StringComparer.Ordinal);
        return ValidationFailed(failures[0].Message, errors);
    }

    /// <summary>
    /// A refusal the caller can understand and act on: <paramref name="statusCode"/> (409,
    /// 413, 415 or 422) with a machine-readable <paramref name="reason"/> (kebab-case, for
    /// the frontend to switch on) and a <paramref name="message"/> to show as is. A
    /// <c>422</c> also names <paramref name="field"/> in <c>errors</c>, like
    /// <see cref="ValidationFailed(string, IReadOnlyDictionary{string, string[]})"/>, so a
    /// generic form handler still works. <paramref name="extensions"/> are extra string
    /// members, e.g. <c>existingDocumentName</c>.
    /// </summary>
    public static IResult WithReason(
        int statusCode,
        string reason,
        string message,
        string? field = null,
        IReadOnlyList<KeyValuePair<string, string>>? extensions = null)
    {
        ArgumentNullException.ThrowIfNull(reason);
        ArgumentNullException.ThrowIfNull(message);
        if (!ReasonStatuses.TryGetValue(statusCode, out var problem))
        {
            throw new ArgumentOutOfRangeException(nameof(statusCode), statusCode, "Not a status WithReason describes.");
        }

        if ((field is not null) != (statusCode == StatusCodes.Status422UnprocessableEntity))
        {
            throw new ArgumentException("A 422 names the field it is about; other statuses do not.", nameof(field));
        }

        return new FixedBodyResult(statusCode, Write(writer =>
        {
            WriteProblemHeader(writer, problem.Type, problem.Title, statusCode);
            writer.WriteString("reason", reason);
            writer.WriteString("message", message);
            foreach (var (name, value) in extensions ?? [])
            {
                writer.WriteString(name, value);
            }

            if (field is not null)
            {
                writer.WriteStartObject("errors");
                writer.WriteStartArray(field);
                writer.WriteStringValue(message);
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
        }));
    }

    /// <summary>
    /// A <c>422</c> <see cref="WithReason"/> about several fields at once: a machine-readable
    /// <paramref name="reason"/>, a summary <paramref name="message"/>, and <c>errors</c>
    /// grouping every failure's message by field — e.g. a refused batch naming each refused
    /// entry as <c>versionIds[2]</c>.
    /// </summary>
    public static IResult Refused(string reason, string message, IReadOnlyList<ValidationFailure> failures)
    {
        ArgumentNullException.ThrowIfNull(reason);
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(failures);
        if (failures.Count == 0)
        {
            throw new ArgumentException("A refusal needs at least one failure.", nameof(failures));
        }

        var problem = ReasonStatuses[StatusCodes.Status422UnprocessableEntity];
        return new FixedBodyResult(StatusCodes.Status422UnprocessableEntity, Write(writer =>
        {
            WriteProblemHeader(writer, problem.Type, problem.Title, StatusCodes.Status422UnprocessableEntity);
            writer.WriteString("reason", reason);
            writer.WriteString("message", message);
            writer.WriteStartObject("errors");
            foreach (var group in failures.GroupBy(failure => failure.Field, StringComparer.Ordinal))
            {
                writer.WriteStartArray(group.Key);
                foreach (var failure in group)
                {
                    writer.WriteStringValue(failure.Message);
                }

                writer.WriteEndArray();
            }

            writer.WriteEndObject();
        }));
    }

    private static byte[] BuildForbiddenBody(ForbiddenReason reason) =>
        Write(writer =>
        {
            WriteProblemHeader(writer, "https://tools.ietf.org/html/rfc9110#section-15.5.4", "Forbidden", StatusCodes.Status403Forbidden);
            writer.WriteString("reason", reason.WireName);
            writer.WriteString("message", reason.Message);
        });

    private static byte[] BuildValidationBody(string message, IReadOnlyDictionary<string, string[]> errors) =>
        Write(writer =>
        {
            WriteProblemHeader(writer, "https://tools.ietf.org/html/rfc9110#section-15.5.21", "Unprocessable Content", StatusCodes.Status422UnprocessableEntity);
            writer.WriteString("message", message);
            writer.WriteStartObject("errors");
            foreach (var (field, fieldErrors) in errors.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                writer.WriteStartArray(field);
                foreach (var error in fieldErrors)
                {
                    writer.WriteStringValue(error);
                }

                writer.WriteEndArray();
            }

            writer.WriteEndObject();
        });

    private static void WriteProblemHeader(Utf8JsonWriter writer, string type, string title, int status)
    {
        writer.WriteString("type", type);
        writer.WriteString("title", title);
        writer.WriteNumber("status", status);
    }

    private static byte[] Write(Action<Utf8JsonWriter> writeProperties)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            writer.WriteStartObject();
            writeProperties(writer);
            writer.WriteEndObject();
        }

        return buffer.ToArray();
    }

    private sealed class FixedBodyResult : IResult, IStatusCodeHttpResult, IContentTypeHttpResult
    {
        private readonly byte[] _body;

        public FixedBodyResult(int statusCode, byte[] body)
        {
            StatusCode = statusCode;
            _body = body;
        }

        public int? StatusCode { get; }

        public string ContentType => ProblemJsonContentType;

        public async Task ExecuteAsync(HttpContext httpContext)
        {
            ArgumentNullException.ThrowIfNull(httpContext);

            var response = httpContext.Response;
            response.StatusCode = StatusCode!.Value;
            response.ContentType = ContentType;
            response.ContentLength = _body.Length;
            await response.Body.WriteAsync(_body, httpContext.RequestAborted);
        }
    }
}
