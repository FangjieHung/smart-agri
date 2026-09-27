using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;

namespace SmartAgri.Domain.Knowledge;

/// <summary>
/// The content of one version of an FAQ entry (<see cref="KnowledgeItemKind.Faq"/>, M2 plan
/// Slice 10): a question and its answer, typed by the owner rather than uploaded.
/// </summary>
/// <remarks>
/// <para>
/// Stored exactly like an uploaded file (<see cref="ToContent"/>: UTF-8 JSON
/// <c>{"question":…,"answer":…}</c> in <see cref="KnowledgeFileContent"/>, content type
/// <see cref="ContentType"/>), so an FAQ version has a size and a SHA-256 like every other
/// version and the same database rules hold for it: the knowledge base never has the same
/// content twice, and nothing about versions, approval or retrieval needs a special case.
/// </para>
/// <para>
/// The encoding is canonical — the same question and answer always give the same bytes — so
/// the SHA-256 identifies the content. The Application layer's rules normalize what the owner
/// typed before it gets here; this type only refuses what they would never produce.
/// </para>
/// </remarks>
public sealed class KnowledgeFaqEntry
{
    /// <summary>The content type an FAQ version is stored with; no upload can have it.</summary>
    public const string ContentType = "application/vnd.smartagri.faq+json";

    /// <summary>The longest question, in UTF-16 characters (as long as a retrieval question).</summary>
    public const int QuestionMaxLength = 500;

    /// <summary>The longest answer, in UTF-16 characters.</summary>
    public const int AnswerMaxLength = 4000;

    private const string QuestionProperty = "question";
    private const string AnswerProperty = "answer";

    private static readonly JsonWriterOptions WriterOptions = new()
    {
        // Readable Chinese in the stored bytes; still escapes HTML-sensitive characters.
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
    };

    /// <param name="question">Not blank, not padded, at most <see cref="QuestionMaxLength"/>.</param>
    /// <param name="answer">Not blank, not padded, at most <see cref="AnswerMaxLength"/>.</param>
    public KnowledgeFaqEntry(string question, string answer)
    {
        Require(question, QuestionMaxLength, nameof(question));
        Require(answer, AnswerMaxLength, nameof(answer));
        Question = question;
        Answer = answer;
    }

    public string Question { get; }

    public string Answer { get; }

    /// <summary>The bytes stored for this entry: canonical UTF-8 JSON, no BOM.</summary>
    public byte[] ToContent()
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteString(QuestionProperty, Question);
            writer.WriteString(AnswerProperty, Answer);
            writer.WriteEndObject();
        }

        return buffer.ToArray();
    }

    /// <summary>The entry <see cref="ToContent"/> wrote.</summary>
    /// <exception cref="FormatException">Not such content (the database was changed by hand, or
    /// the bytes are damaged).</exception>
    public static KnowledgeFaqEntry FromContent(byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);
        try
        {
            using var document = JsonDocument.Parse(content);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty(QuestionProperty, out var question) && question.ValueKind == JsonValueKind.String
                && root.TryGetProperty(AnswerProperty, out var answer) && answer.ValueKind == JsonValueKind.String)
            {
                return new KnowledgeFaqEntry(question.GetString()!, answer.GetString()!);
            }
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException)
        {
            throw new FormatException("The content is not a stored FAQ entry.", exception);
        }

        throw new FormatException("The content is not a stored FAQ entry.");
    }

    private static void Require(string value, int maxLength, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (value.Length > maxLength || string.IsNullOrWhiteSpace(value) || value.Trim().Length != value.Length)
        {
            throw new ArgumentException(
                $"An FAQ {parameterName} must be 1-{maxLength} characters, not blank and without surrounding white space.",
                parameterName);
        }
    }
}
