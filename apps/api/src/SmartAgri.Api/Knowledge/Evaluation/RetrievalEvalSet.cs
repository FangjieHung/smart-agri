using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using SmartAgri.Application.Knowledge;
using SmartAgri.Application.Knowledge.Retrieval;
using SmartAgri.Domain.Knowledge;

namespace SmartAgri.Api.Knowledge.Evaluation;

/// <summary>A knowledge base of a <see cref="RetrievalEvalSet"/>, as it is created.</summary>
public sealed record EvalKnowledgeBase(string Name, string Purpose, IReadOnlyList<EvalDocument> Documents);

/// <summary>A document of a <see cref="RetrievalEvalSet"/>: its versions, oldest first. Every
/// version is uploaded and approved in this order, so the last one is in effect and the others
/// are archived.</summary>
public sealed record EvalDocument(IReadOnlyList<EvalDocumentVersion> Versions)
{
    /// <summary>What the document is called: the name of its first file, as an upload names it.</summary>
    public string Name => Versions[0].FileName;

    /// <summary>The version in effect once the set is imported: the last.</summary>
    public int EffectiveVersion => Versions.Count;
}

/// <summary>One version's file: <paramref name="File"/> is its path in the set's directory,
/// <paramref name="FileName"/> the name it is uploaded under (already normalized as an upload
/// would), <paramref name="Content"/> its bytes.</summary>
public sealed record EvalDocumentVersion(string File, string FileName, byte[] Content)
{
    public string Sha256 { get; } = KnowledgeUploadRules.Sha256(Content);
}

/// <summary>A passage that answers a question: the document (by name), the version number, a
/// text its location label must contain (a page 「第 2 頁」, a heading path, 「工作表『運費』」) and,
/// optionally, a phrase its text must contain.</summary>
public sealed record EvalExpectedPassage(string Document, int Version, string Location, string? Evidence);

/// <summary>One question of the bank. <paramref name="Expected"/> lists the passages that
/// answer it — any one of them in the top five is a hit — and is empty for a question the
/// organization's data should not answer at all (category <see cref="RetrievalEvalSet.Unanswerable"/>).</summary>
public sealed record EvalQuestion(string Id, string Category, string Question, IReadOnlyList<EvalExpectedPassage> Expected, string? Note)
{
    public bool ExpectsNothing => Expected.Count == 0;
}

/// <summary>Why a <see cref="RetrievalEvalSet"/> cannot be used: every problem found, one per line.</summary>
public sealed class RetrievalEvalSetException : Exception
{
    public RetrievalEvalSetException(string directory, IReadOnlyList<string> problems)
        : base($"檢索評測題庫「{directory}」無法使用：\n- " + string.Join("\n- ", problems))
    {
        Problems = problems;
    }

    public IReadOnlyList<string> Problems { get; }
}

/// <summary>
/// A retrieval evaluation set (M2 plan Slice 16; ticket #50): a directory with
/// <c>documents.json</c> (the knowledge bases and their documents' versions, files under the same
/// directory), <c>questions.json</c> (the question bank) and the files. The committed demo set is
/// <c>apps/api/eval/retrieval/</c>, copied next to the Api assembly (<see cref="DefaultDirectory"/>);
/// its README describes the format.
/// </summary>
/// <remarks>
/// <see cref="Load"/> checks everything that can be checked without a database, and reports
/// every problem at once: the files exist and would be accepted by an upload, names and contents
/// are unique where uploads require it, question ids are unique, and every expected passage names
/// a document of the set and its <b>effective</b> (last) version — an archived version is never
/// retrieved, so expecting one is a mistake. So the bank cannot silently rot when the documents
/// change; the tests also check each expected location and phrase against the documents as
/// processing reads them.
/// </remarks>
public sealed class RetrievalEvalSet
{
    public const string DocumentsFileName = "documents.json";
    public const string QuestionsFileName = "questions.json";

    /// <summary>The category of questions that should find nothing.</summary>
    public const string Unanswerable = "unanswerable";

    /// <summary>The categories a question may have, and what the report calls them.</summary>
    public static readonly IReadOnlyDictionary<string, string> Categories = new Dictionary<string, string>
    {
        ["returns"] = "退換貨",
        ["product"] = "商品",
        ["delivery"] = "配送",
        ["faq"] = "常見問題",
        [Unanswerable] = "應查無結果",
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private RetrievalEvalSet(string directory, IReadOnlyList<EvalKnowledgeBase> knowledgeBases, IReadOnlyList<EvalQuestion> questions, string fingerprint)
    {
        Directory = directory;
        KnowledgeBases = knowledgeBases;
        Questions = questions;
        Fingerprint = fingerprint;
    }

    /// <summary>The committed demo set, as copied next to the Api assembly by the build.</summary>
    public static string DefaultDirectory => Path.Combine(AppContext.BaseDirectory, "eval", "retrieval");

    /// <summary>The full path of the set's directory.</summary>
    public string Directory { get; }

    public IReadOnlyList<EvalKnowledgeBase> KnowledgeBases { get; }

    public IReadOnlyList<EvalQuestion> Questions { get; }

    /// <summary>SHA-256 (lower-case hex) of both JSON files and every document file, in order:
    /// which set a report was made with.</summary>
    public string Fingerprint { get; }

    public IEnumerable<EvalDocument> Documents => KnowledgeBases.SelectMany(knowledgeBase => knowledgeBase.Documents);

    /// <summary>Reads and checks the set in <paramref name="directory"/>.</summary>
    /// <exception cref="RetrievalEvalSetException">Anything is missing, malformed or inconsistent.</exception>
    public static RetrievalEvalSet Load(string directory)
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
            throw new RetrievalEvalSetException(root, problems);
        }

        var knowledgeBases = ReadKnowledgeBases(root, documentsFile!, problems);
        var questions = ReadQuestions(questionsFile!, knowledgeBases, problems);
        if (problems.Count > 0)
        {
            throw new RetrievalEvalSetException(root, problems);
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(documentsJson!);
        hash.AppendData(questionsJson!);
        foreach (var version in knowledgeBases.SelectMany(knowledgeBase => knowledgeBase.Documents).SelectMany(document => document.Versions))
        {
            hash.AppendData(version.Content);
        }

        return new RetrievalEvalSet(root, knowledgeBases, questions, Convert.ToHexStringLower(hash.GetHashAndReset()));
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

            // The upload rules: document names, and file contents, are unique per knowledge base.
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

    private static List<EvalQuestion> ReadQuestions(QuestionsFile file, IReadOnlyList<EvalKnowledgeBase> knowledgeBases, List<string> problems)
    {
        var questions = new List<EvalQuestion>();
        if (file.Questions is not { Count: > 0 })
        {
            problems.Add($"{QuestionsFileName}：至少要有一個題目（questions）。");
            return questions;
        }

        var documents = knowledgeBases.SelectMany(knowledgeBase => knowledgeBase.Documents)
            .GroupBy(document => document.Name)
            .ToDictionary(group => group.Key, group => group.First());
        for (var i = 0; i < file.Questions.Count; i++)
        {
            var entry = file.Questions[i];
            var id = entry.Id?.Trim() ?? string.Empty;
            var where = id.Length > 0 ? $"{QuestionsFileName} {id}" : $"{QuestionsFileName} questions[{i}]";
            if (id.Length == 0)
            {
                problems.Add($"{where}：id 必須填寫。");
            }
            else if (questions.Any(existing => existing.Id == id))
            {
                problems.Add($"{where}：id 重複。");
            }

            var question = entry.Question?.Trim() ?? string.Empty;
            if (question.Length is 0 or > KnowledgeRetrievalRules.QuestionMaxLength)
            {
                problems.Add($"{where}：question 必須是 1–{KnowledgeRetrievalRules.QuestionMaxLength} 個字。");
            }

            var category = entry.Category?.Trim() ?? string.Empty;
            if (!Categories.ContainsKey(category))
            {
                problems.Add($"{where}：category 必須是 {string.Join("、", Categories.Keys)} 之一。");
            }

            var expected = new List<EvalExpectedPassage>();
            foreach (var passage in entry.Expected ?? [])
            {
                var document = passage.Document?.Trim() ?? string.Empty;
                var location = passage.Location?.Trim() ?? string.Empty;
                var evidence = string.IsNullOrWhiteSpace(passage.Evidence) ? null : passage.Evidence.Trim();
                if (!documents.TryGetValue(document, out var known))
                {
                    problems.Add($"{where}：預期的文件「{document}」不在 {DocumentsFileName} 裡。");
                }
                else if (passage.Version is not { } number || number < 1 || number > known.Versions.Count)
                {
                    problems.Add($"{where}：「{document}」沒有第 {passage.Version?.ToString(CultureInfo.InvariantCulture) ?? "?"} 版（共 {known.Versions.Count} 版）。");
                }
                else if (number != known.EffectiveVersion)
                {
                    problems.Add($"{where}：「{document}」的第 {number} 版匯入後會封存、永遠不會被檢索到；預期的應該是目前有效的第 {known.EffectiveVersion} 版。");
                }

                if (location.Length == 0)
                {
                    problems.Add($"{where}：預期段落的 location 必須填寫。");
                }

                expected.Add(new EvalExpectedPassage(document, passage.Version ?? 0, location, evidence));
            }

            if (category == Unanswerable && expected.Count > 0)
            {
                problems.Add($"{where}：{Unanswerable} 的題目不能有預期段落（expected 要是空的）。");
            }
            else if (category != Unanswerable && Categories.ContainsKey(category) && expected.Count == 0)
            {
                problems.Add($"{where}：有答案的題目至少要有一個預期段落；應該查無結果的題目請用 category「{Unanswerable}」。");
            }

            questions.Add(new EvalQuestion(id, category, question, expected, string.IsNullOrWhiteSpace(entry.Note) ? null : entry.Note.Trim()));
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

    private sealed record QuestionEntry(string? Id, string? Category, string? Question, IReadOnlyList<ExpectedEntry>? Expected, string? Note);

    private sealed record ExpectedEntry(string? Document, int? Version, string? Location, string? Evidence);
}
