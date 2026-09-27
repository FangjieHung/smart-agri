using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using SmartAgri.Api.Knowledge;
using SmartAgri.Api.Knowledge.Evaluation;
using SmartAgri.Application.Knowledge;
using SmartAgri.Application.Knowledge.Retrieval;
using SmartAgri.Domain.Knowledge;

namespace SmartAgri.Api.Answers.Evaluation;

/// <summary>The two reply kinds an answer evaluation question expects (issue #83): the grounded-
/// answers ADR requires the bank to cover questions that should find nothing, so every question
/// says which it is.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<AnswerEvalExpectedKind>))]
public enum AnswerEvalExpectedKind
{
    [JsonStringEnumMemberName("company-data")]
    CompanyData,

    [JsonStringEnumMemberName("no-result")]
    NoResult,
}

/// <summary>One question of an <see cref="AnswerEvalSet"/>.</summary>
/// <param name="ExpectedCitedDocuments">For <see cref="AnswerEvalExpectedKind.CompanyData"/>: the
/// document names the reply should cite (by their uploaded file name); empty for
/// <see cref="AnswerEvalExpectedKind.NoResult"/>.</param>
/// <param name="FollowUpOf">Another question's <see cref="Id"/>, earlier in the bank: this
/// question is answered with that one's question and reply as conversation history (M3 plan §7
/// decision D — the retrieval query joins the previous question).</param>
public sealed record AnswerEvalQuestion(
    string Id,
    string Question,
    AnswerEvalExpectedKind ExpectedKind,
    IReadOnlyList<string> ExpectedCitedDocuments,
    string? FollowUpOf,
    string? Note);

/// <summary>Why an <see cref="AnswerEvalSet"/> cannot be used: every problem found, one per line.</summary>
public sealed class AnswerEvalSetException : Exception
{
    public AnswerEvalSetException(string directory, IReadOnlyList<string> problems)
        : base($"回答評測題庫「{directory}」無法使用：\n- " + string.Join("\n- ", problems))
    {
        Problems = problems;
    }

    public IReadOnlyList<string> Problems { get; }
}

/// <summary>
/// The answer evaluation set (M3 plan Slice 13; ticket #83): a directory with
/// <c>documents.json</c> (the knowledge bases and their documents, files under the same
/// directory — the same shape as <see cref="RetrievalEvalSet"/>'s), <c>questions.json</c> (the
/// question bank, with each question's expected reply kind and, for a <c>company-data</c>
/// question, the documents it should cite) and the files themselves. The committed demo set is
/// <c>apps/api/eval/answers/</c>, copied next to the Api assembly (<see cref="DefaultDirectory"/>);
/// its README describes the format.
/// </summary>
/// <remarks>
/// A set of its own (not <c>apps/api/eval/retrieval/</c>) so the two evaluations never fight over
/// the same committed bank: eval-retrieval's questions and documents are about the passages a
/// search should find, while this one is about how <c>GroundedAnswerService</c> turns a question
/// into a final reply — reply kind, citations, rejection reasons and, for a real model, token
/// usage. The demo knowledge is the same 安心商行 story, kept deliberately small (plain Markdown,
/// no PDF/DOCX/XLSX generation) since answer evaluation does not need eval-retrieval's exact
/// per-location assertions.
/// </remarks>
public sealed class AnswerEvalSet
{
    public const string DocumentsFileName = "documents.json";
    public const string QuestionsFileName = "questions.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private AnswerEvalSet(string directory, IReadOnlyList<EvalKnowledgeBase> knowledgeBases, IReadOnlyList<AnswerEvalQuestion> questions, string fingerprint)
    {
        Directory = directory;
        KnowledgeBases = knowledgeBases;
        Questions = questions;
        Fingerprint = fingerprint;
    }

    /// <summary>The committed demo set, as copied next to the Api assembly by the build.</summary>
    public static string DefaultDirectory => Path.Combine(AppContext.BaseDirectory, "eval", "answers");

    /// <summary>The full path of the set's directory.</summary>
    public string Directory { get; }

    public IReadOnlyList<EvalKnowledgeBase> KnowledgeBases { get; }

    public IReadOnlyList<AnswerEvalQuestion> Questions { get; }

    /// <summary>SHA-256 (lower-case hex) of both JSON files and every document file, in order:
    /// which set a report was made with.</summary>
    public string Fingerprint { get; }

    public IEnumerable<EvalDocument> Documents => KnowledgeBases.SelectMany(knowledgeBase => knowledgeBase.Documents);

    /// <summary>Reads and checks the set in <paramref name="directory"/>.</summary>
    /// <exception cref="AnswerEvalSetException">Anything is missing, malformed or inconsistent.</exception>
    public static AnswerEvalSet Load(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        var root = Path.GetFullPath(directory);
        var problems = new List<string>();

        var documentsJson = ReadFile(root, DocumentsFileName, problems);
        var questionsJson = ReadFile(root, QuestionsFileName, problems);
        var documentsFile = Parse<DocumentsFile>(documentsJson, DocumentsFileName, problems);
        var questionsFile = Parse<QuestionsFile>(questionsJson, QuestionsFileName, problems);
        if (problems.Count > 0)
        {
            throw new AnswerEvalSetException(root, problems);
        }

        var knowledgeBases = ReadKnowledgeBases(root, documentsFile!, problems);
        var questions = ReadQuestions(questionsFile!, knowledgeBases, problems);
        if (problems.Count > 0)
        {
            throw new AnswerEvalSetException(root, problems);
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(documentsJson!);
        hash.AppendData(questionsJson!);
        foreach (var version in knowledgeBases.SelectMany(knowledgeBase => knowledgeBase.Documents).SelectMany(document => document.Versions))
        {
            hash.AppendData(version.Content);
        }

        return new AnswerEvalSet(root, knowledgeBases, questions, Convert.ToHexStringLower(hash.GetHashAndReset()));
    }

    private static List<EvalKnowledgeBase> ReadKnowledgeBases(string root, DocumentsFile file, List<string> problems)
    {
        var knowledgeBases = new List<EvalKnowledgeBase>();
        if (file.KnowledgeBases is not { Count: > 0 })
        {
            problems.Add($"{DocumentsFileName}：至少要有一個知識庫（knowledgeBases）。");
            return knowledgeBases;
        }

        for (var i = 0; i < file.KnowledgeBases.Count; i++)
        {
            var entry = file.KnowledgeBases[i];
            var where = $"{DocumentsFileName} knowledgeBases[{i}]";
            var name = entry.Name?.Trim() ?? string.Empty;
            if (name.Length is 0 or > KnowledgeBase.NameMaxLength)
            {
                problems.Add($"{where}：name 必須是 1–{KnowledgeBase.NameMaxLength} 個字。");
            }
            else if (knowledgeBases.Any(existing => existing.Name == name))
            {
                problems.Add($"{where}：知識庫名稱「{name}」重複。");
            }

            var purpose = entry.Purpose?.Trim() ?? string.Empty;
            if (purpose.Length > KnowledgeBase.PurposeMaxLength)
            {
                problems.Add($"{where}：purpose 最多 {KnowledgeBase.PurposeMaxLength} 個字。");
            }

            var documents = new List<EvalDocument>();
            if (entry.Documents is not { Count: > 0 })
            {
                problems.Add($"{where}：至少要有一份文件（documents）。");
            }
            else
            {
                for (var j = 0; j < entry.Documents.Count; j++)
                {
                    if (ReadDocument(root, entry.Documents[j], $"{where}.documents[{j}]", problems) is { } document)
                    {
                        documents.Add(document);
                    }
                }
            }

            foreach (var duplicate in documents.GroupBy(document => document.Name).Where(group => group.Count() > 1))
            {
                problems.Add($"{where}：文件名稱「{duplicate.Key}」重複（同一個知識庫不能有同名文件）。");
            }

            foreach (var duplicate in documents.SelectMany(document => document.Versions).GroupBy(version => version.Sha256).Where(group => group.Count() > 1))
            {
                problems.Add($"{where}：{string.Join("、", duplicate.Select(version => version.File))} 內容完全相同（同一個知識庫不能重複上傳相同內容）。");
            }

            knowledgeBases.Add(new EvalKnowledgeBase(name, purpose, documents));
        }

        foreach (var duplicate in knowledgeBases.SelectMany(knowledgeBase => knowledgeBase.Documents).GroupBy(document => document.Name).Where(group => group.Count() > 1))
        {
            problems.Add($"{DocumentsFileName}：文件名稱「{duplicate.Key}」出現在多個知識庫，題目無法指明是哪一份。");
        }

        return knowledgeBases;
    }

    private static EvalDocument? ReadDocument(string root, DocumentEntry entry, string where, List<string> problems)
    {
        if (entry.Versions is not { Count: > 0 })
        {
            problems.Add($"{where}：至少要有一個版本（versions）。");
            return null;
        }

        var versions = new List<EvalDocumentVersion>();
        for (var k = 0; k < entry.Versions.Count; k++)
        {
            var version = entry.Versions[k];
            var at = $"{where}.versions[{k}]";
            if (string.IsNullOrWhiteSpace(version.File) || string.IsNullOrWhiteSpace(version.FileName))
            {
                problems.Add($"{at}：file 與 fileName 都必須填寫。");
                continue;
            }

            var path = Path.GetFullPath(Path.Combine(root, version.File));
            if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                problems.Add($"{at}：file「{version.File}」必須在題庫目錄之內。");
                continue;
            }

            if (!File.Exists(path))
            {
                problems.Add($"{at}：找不到檔案「{version.File}」。");
                continue;
            }

            var content = File.ReadAllBytes(path);
            var inspection = KnowledgeUploadRules.Inspect(version.FileName, content, KnowledgeOptions.DefaultMaxFileBytes);
            if (!inspection.IsAccepted)
            {
                problems.Add($"{at}：上傳時會被拒絕（{inspection.Rejection.Message}）。");
                continue;
            }

            if (inspection.Value.FileName != version.FileName)
            {
                problems.Add($"{at}：fileName「{version.FileName}」上傳後會變成「{inspection.Value.FileName}」，請直接寫後者。");
                continue;
            }

            versions.Add(new EvalDocumentVersion(version.File, version.FileName, content));
        }

        return versions.Count == entry.Versions.Count ? new EvalDocument(versions) : null;
    }

    private static List<AnswerEvalQuestion> ReadQuestions(QuestionsFile file, IReadOnlyList<EvalKnowledgeBase> knowledgeBases, List<string> problems)
    {
        var questions = new List<AnswerEvalQuestion>();
        if (file.Questions is not { Count: > 0 })
        {
            problems.Add($"{QuestionsFileName}：至少要有一個題目（questions）。");
            return questions;
        }

        var documentNames = knowledgeBases.SelectMany(knowledgeBase => knowledgeBase.Documents).Select(document => document.Name).ToHashSet();
        var ids = new HashSet<string>();
        for (var i = 0; i < file.Questions.Count; i++)
        {
            var entry = file.Questions[i];
            var id = entry.Id?.Trim() ?? string.Empty;
            var where = id.Length > 0 ? $"{QuestionsFileName} {id}" : $"{QuestionsFileName} questions[{i}]";
            if (id.Length == 0)
            {
                problems.Add($"{where}：id 必須填寫。");
            }
            else if (!ids.Add(id))
            {
                problems.Add($"{where}：id 重複。");
            }

            var question = entry.Question?.Trim() ?? string.Empty;
            if (question.Length is 0 or > KnowledgeRetrievalRules.QuestionMaxLength)
            {
                problems.Add($"{where}：question 必須是 1–{KnowledgeRetrievalRules.QuestionMaxLength} 個字。");
            }

            if (entry.ExpectedKind is not { } expectedKind)
            {
                problems.Add($"{where}：expectedKind 必須是 company-data 或 no-result。");
                continue;
            }

            var cited = entry.ExpectedCitedDocuments ?? [];
            if (expectedKind == AnswerEvalExpectedKind.CompanyData && cited.Count == 0)
            {
                problems.Add($"{where}：company-data 的題目至少要有一個 expectedCitedDocuments。");
            }
            else if (expectedKind == AnswerEvalExpectedKind.NoResult && cited.Count > 0)
            {
                problems.Add($"{where}：no-result 的題目不能有 expectedCitedDocuments。");
            }

            foreach (var document in cited)
            {
                if (!documentNames.Contains(document))
                {
                    problems.Add($"{where}：expectedCitedDocuments 的「{document}」不在 {DocumentsFileName} 裡。");
                }
            }

            var followUpOf = string.IsNullOrWhiteSpace(entry.FollowUpOf) ? null : entry.FollowUpOf.Trim();
            if (followUpOf is not null && !ids.Contains(followUpOf))
            {
                problems.Add($"{where}：followUpOf「{followUpOf}」必須是題庫裡在它之前的題目 id。");
            }

            questions.Add(new AnswerEvalQuestion(
                id, question, expectedKind, cited, followUpOf, string.IsNullOrWhiteSpace(entry.Note) ? null : entry.Note.Trim()));
        }

        return questions;
    }

    private static byte[]? ReadFile(string root, string name, List<string> problems)
    {
        var path = Path.Combine(root, name);
        if (File.Exists(path))
        {
            return File.ReadAllBytes(path);
        }

        problems.Add($"找不到 {name}。");
        return null;
    }

    private static T? Parse<T>(byte[]? json, string name, List<string> problems)
        where T : class
    {
        if (json is null)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(json, JsonOptions) ?? throw new JsonException("The file is empty (null).");
        }
        catch (JsonException exception)
        {
            problems.Add($"{name} 格式錯誤：{exception.Message}");
            return null;
        }
    }

    private sealed record DocumentsFile(IReadOnlyList<KnowledgeBaseEntry>? KnowledgeBases);

    private sealed record KnowledgeBaseEntry(string? Name, string? Purpose, IReadOnlyList<DocumentEntry>? Documents);

    private sealed record DocumentEntry(IReadOnlyList<VersionEntry>? Versions);

    private sealed record VersionEntry(string? File, string? FileName);

    private sealed record QuestionsFile(IReadOnlyList<QuestionEntry>? Questions);

    private sealed record QuestionEntry(
        string? Id,
        string? Question,
        AnswerEvalExpectedKind? ExpectedKind,
        IReadOnlyList<string>? ExpectedCitedDocuments,
        string? FollowUpOf,
        string? Note);
}
