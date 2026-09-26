using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using SmartAgri.Api.Knowledge.Evaluation;
using SmartAgri.Application.Knowledge.Processing;
using SmartAgri.Domain.Knowledge;
using SmartAgri.Infrastructure.Knowledge.Extraction;

namespace SmartAgri.Api.Tests.Knowledge.Evaluation;

/// <summary>
/// The committed retrieval evaluation set (<c>apps/api/eval/retrieval/</c>, M2 plan Slice 16;
/// ticket #50) can be loaded, and its questions keep pointing at what the documents say: every
/// expected document and version exists (<see cref="RetrievalEvalSet.Load"/>), and every expected
/// location and phrase is in a chunk exactly as processing cuts the file — so the bank cannot
/// silently rot when a document changes. Also: the version the bank expects really differs from
/// the archived one, and the set stays consistent with the frontend Demo. No database needed.
/// </summary>
public sealed partial class RetrievalEvalSetTests
{
    private static readonly RetrievalEvalSet Set = RetrievalEvalSet.Load(RetrievalEvalSet.DefaultDirectory);

    private static readonly IDocumentTextExtractor[] Extractors =
    [
        new PdfTextExtractor(NullLogger<PdfTextExtractor>.Instance),
        new DocxTextExtractor(),
        new XlsxTextExtractor(),
        new PlainTextExtractor(),
    ];

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public void The_committed_set_has_about_thirty_questions_with_at_least_five_that_should_find_nothing()
    {
        Set.Questions.Count.ShouldBeInRange(30, 40);
        Set.Questions.Count(question => question.ExpectsNothing).ShouldBeGreaterThanOrEqualTo(5);
        Set.Questions.Select(question => question.Category).Distinct().ShouldBe(RetrievalEvalSet.Categories.Keys, ignoreOrder: true, "every category has questions");
        Set.Fingerprint.ShouldMatch("^[0-9a-f]{64}$");
    }

    [Fact]
    public void The_set_has_a_product_guide_a_return_policy_in_two_versions_a_delivery_timetable_and_a_faq()
    {
        Set.KnowledgeBases.Select(knowledgeBase => knowledgeBase.Name).ShouldBe(["商品使用指南", "退換貨政策", "配送常見問題"]);
        Set.Documents.Select(document => (document.Name, document.Versions.Count)).ShouldBe(
        [
            ("商品使用指南.docx", 1),
            ("退換貨辦法.pdf", 2),
            ("運費與配送時間表.xlsx", 1),
            ("常見問題.md", 1),
        ]);
    }

    [Fact]
    public void Every_version_processes_to_ready_as_the_pipeline_would()
    {
        foreach (var version in Set.Documents.SelectMany(document => document.Versions))
        {
            var processed = Process(version);
            processed.Status.ShouldBe(KnowledgeDocumentStatus.Ready, $"{version.File}: {processed.Issue}");
            processed.Units.SelectMany(unit => unit.Chunks).ShouldNotBeEmpty(version.File);
        }
    }

    [Fact]
    public void Every_expected_location_and_phrase_is_in_a_chunk_of_the_expected_version()
    {
        var documents = Set.Documents.ToDictionary(document => document.Name);
        foreach (var question in Set.Questions)
        {
            foreach (var expected in question.Expected)
            {
                var chunks = Chunks(documents[expected.Document].Versions[expected.Version - 1]);
                chunks.ShouldContain(
                    chunk => chunk.LocationLabel.Contains(expected.Location, StringComparison.Ordinal)
                        && (expected.Evidence == null || chunk.Text.Contains(expected.Evidence, StringComparison.Ordinal)),
                    $"{question.Id}: 「{expected.Evidence}」 at 「{expected.Location}」 in {expected.Document} v{expected.Version}; the chunks are at "
                        + string.Join("、", chunks.Select(chunk => chunk.LocationLabel).Distinct()));
            }
        }
    }

    [Fact]
    public void What_a_question_expects_of_a_newer_version_is_not_in_the_archived_one()
    {
        // Otherwise a leak of the archived version could not be told from a hit.
        var documents = Set.Documents.ToDictionary(document => document.Name);
        var checkedPassages = 0;
        foreach (var question in Set.Questions)
        {
            foreach (var expected in question.Expected.Where(expected => documents[expected.Document].Versions.Count > 1))
            {
                expected.Evidence.ShouldNotBeNull($"{question.Id} expects a document with several versions: give the phrase that tells them apart");
                foreach (var older in documents[expected.Document].Versions.Take(expected.Version - 1))
                {
                    Chunks(older).ShouldNotContain(
                        chunk => chunk.Text.Contains(expected.Evidence!, StringComparison.Ordinal),
                        $"{question.Id}: 「{expected.Evidence}」 is in {older.File} too");
                }

                checkedPassages++;
            }
        }

        checkedPassages.ShouldBeGreaterThanOrEqualTo(5, "the bank tests version choice");
    }

    [Fact]
    public void The_frontend_demo_trial_questions_and_knowledge_bases_are_in_the_set()
    {
        var source = File.ReadAllText(FindRepositoryFile("apps", "admin", "src", "app", "core", "repositories", "demo-seed.ts"));
        var knowledgeBases = KnowledgeBaseObject().Matches(Between(source, "knowledgeBases: [", "knowledgeDocuments:"))
            .ToDictionary(match => match.Groups["id"].Value, match => match.Groups["name"].Value);
        Set.KnowledgeBases.Select(knowledgeBase => knowledgeBase.Name).ShouldBeSubsetOf(knowledgeBases.Values);

        var trials = TrialQuestion().Matches(Between(source, "trialQuestions: [", "chatProfiles:")).ToList();
        trials.Count.ShouldBeGreaterThanOrEqualTo(3);
        foreach (var trial in trials)
        {
            var text = trial.Groups["text"].Value;
            var question = Set.Questions.SingleOrDefault(candidate => candidate.Question == text);
            question.ShouldNotBeNull($"the Demo's trial question 「{text}」 is in the bank");
            if (trial.Groups["source"].Success)
            {
                // The Demo answers it from this knowledge base: the bank expects a document of it.
                var knowledgeBase = Set.KnowledgeBases.Single(candidate => candidate.Name == knowledgeBases[trial.Groups["source"].Value]);
                question.Expected.ShouldNotBeEmpty(text);
                question.Expected.ShouldAllBe(expected => knowledgeBase.Documents.Any(document => document.Name == expected.Document), text);
            }
            else
            {
                question.ExpectsNothing.ShouldBeTrue($"「{text}」 has no company answer in the Demo");
            }
        }
    }

    [Fact]
    public void A_bank_that_names_a_missing_document_version_or_category_is_refused_with_every_problem()
    {
        var directory = CopySet();
        try
        {
            File.WriteAllText(
                Path.Combine(directory, RetrievalEvalSet.QuestionsFileName),
                """
                {
                  "questions": [
                    { "id": "a", "category": "returns", "question": "退貨？", "expected": [ { "document": "不存在.pdf", "version": 1, "location": "第 1 頁" } ] },
                    { "id": "a", "category": "returns", "question": "退貨？", "expected": [ { "document": "退換貨辦法.pdf", "version": 3, "location": "第 1 頁" } ] },
                    { "id": "b", "category": "returns", "question": "退貨？", "expected": [ { "document": "退換貨辦法.pdf", "version": 1, "location": "第 2 頁" } ] },
                    { "id": "c", "category": "weather", "question": "天氣？", "expected": [] },
                    { "id": "d", "category": "unanswerable", "question": "機票？", "expected": [ { "document": "常見問題.md", "version": 1, "location": " " } ] },
                    { "id": "e", "category": "faq", "question": "付款？", "expected": [] }
                  ]
                }
                """);

            var problems = Should.Throw<RetrievalEvalSetException>(() => RetrievalEvalSet.Load(directory)).Problems;

            problems.ShouldContain(problem => problem.Contains("「不存在.pdf」不在 documents.json 裡", StringComparison.Ordinal));
            problems.ShouldContain(problem => problem.Contains("id 重複", StringComparison.Ordinal));
            problems.ShouldContain(problem => problem.Contains("沒有第 3 版（共 2 版）", StringComparison.Ordinal));
            problems.ShouldContain(problem => problem.Contains("第 1 版匯入後會封存", StringComparison.Ordinal));
            problems.ShouldContain(problem => problem.StartsWith("questions.json c：category", StringComparison.Ordinal));
            problems.ShouldContain(problem => problem.StartsWith("questions.json d：預期段落的 location", StringComparison.Ordinal));
            problems.ShouldContain(problem => problem.StartsWith("questions.json d：unanswerable 的題目不能有預期段落", StringComparison.Ordinal));
            problems.ShouldContain(problem => problem.StartsWith("questions.json e：有答案的題目至少要有一個預期段落", StringComparison.Ordinal));
            problems.Count.ShouldBe(8);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void A_set_whose_files_are_missing_unknown_fields_or_would_be_refused_on_upload_is_refused()
    {
        var directory = CopySet();
        try
        {
            File.Delete(Path.Combine(directory, "files", "faq.md"));
            File.WriteAllText(Path.Combine(directory, "files", "fake.pdf"), "not a pdf");
            var documents = File.ReadAllText(Path.Combine(directory, RetrievalEvalSet.DocumentsFileName))
                .Replace("\"files/return-policy-v1.pdf\"", "\"files/fake.pdf\"", StringComparison.Ordinal)
                .Replace("\"fileName\": \"商品使用指南.docx\"", "\"fileName\": \"商品使用指南.docx\", \"effective\": true", StringComparison.Ordinal);
            File.WriteAllText(Path.Combine(directory, RetrievalEvalSet.DocumentsFileName), documents);

            var problems = Should.Throw<RetrievalEvalSetException>(() => RetrievalEvalSet.Load(directory)).Problems;

            problems.ShouldHaveSingleItem().ShouldContain("documents.json 格式錯誤", Case.Sensitive, "an unknown field is a typo, not ignored");

            File.WriteAllText(
                Path.Combine(directory, RetrievalEvalSet.DocumentsFileName),
                documents.Replace(", \"effective\": true", string.Empty, StringComparison.Ordinal));
            problems = Should.Throw<RetrievalEvalSetException>(() => RetrievalEvalSet.Load(directory)).Problems;

            problems.ShouldContain(problem => problem.Contains("找不到檔案「files/faq.md」", StringComparison.Ordinal));
            problems.ShouldContain(problem => problem.Contains("上傳時會被拒絕", StringComparison.Ordinal) && problem.Contains("versions[0]", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static ProcessedVersion Process(EvalDocumentVersion version)
    {
        KnowledgeFileFormats.TryFromFileName(version.FileName, out var format).ShouldBeTrue(version.FileName);
        var extracted = Extractors.Single(extractor => extractor.CanExtract(format)).Extract(format, version.Content, ExtractionLimits.Default, CancellationToken);
        return KnowledgeVersionProcessing.Process(extracted, ExtractionLimits.Default, ChunkingOptions.Default);
    }

    private static List<KnowledgeTextChunk> Chunks(EvalDocumentVersion version) => [.. Process(version).Units.SelectMany(unit => unit.Chunks)];

    /// <summary>A scratch copy of the committed set, to break.</summary>
    private static string CopySet()
    {
        var directory = Path.Combine(Path.GetTempPath(), "smartagri-eval-set-" + Guid.NewGuid().ToString("N"));
        foreach (var file in Directory.EnumerateFiles(Set.Directory, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(directory, Path.GetRelativePath(Set.Directory, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }

        return directory;
    }

    private static string Between(string source, string start, string end)
    {
        var from = source.IndexOf(start, StringComparison.Ordinal);
        from.ShouldBeGreaterThanOrEqualTo(0, $"`{start}` not found in demo-seed.ts");
        var to = source.IndexOf(end, from, StringComparison.Ordinal);
        to.ShouldBeGreaterThan(from, $"`{end}` not found after `{start}` in demo-seed.ts");
        return source[from..to];
    }

    private static string FindRepositoryFile(params string[] path)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine([directory.FullName, .. path]);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException($"{string.Join('/', path)} not found above the test output directory.");
    }

    [GeneratedRegex(@"id:\s*'(?<id>knowledge-[^']+)',[^}]*?name:\s*'(?<name>[^']+)'")]
    private static partial Regex KnowledgeBaseObject();

    [GeneratedRegex(@"text:\s*'(?<text>[^']+)',\s*companyAnswer:\s*(?:null|\{\s*sourceId:\s*'(?<source>[^']+)')")]
    private static partial Regex TrialQuestion();
}
