namespace SmartAgri.Domain.Knowledge;

/// <summary>
/// The file formats a knowledge base accepts (M2 plan, Slice 5): PDF with a text layer,
/// Word and Excel (Office Open XML only), plain text and Markdown. Anything else — legacy
/// <c>.doc</c>/<c>.xls</c>, PPTX, images, CSV — is out of M2 (plan §8).
/// </summary>
public enum KnowledgeFileFormat
{
    Pdf,
    Docx,
    Xlsx,
    Text,
    Markdown,
}

/// <summary>
/// Each <see cref="KnowledgeFileFormat"/>'s file extension and the content type stored for
/// it. The stored content type is always this canonical one, never what the uploading
/// browser claimed (browsers disagree, e.g. on Markdown), so later slices can tell the
/// format of a stored version from <see cref="KnowledgeDocumentVersion.ContentType"/> alone
/// (<see cref="TryFromContentType"/>).
/// </summary>
public static class KnowledgeFileFormats
{
    private static readonly (KnowledgeFileFormat Format, string Extension, string ContentType)[] Table =
    [
        (KnowledgeFileFormat.Pdf, ".pdf", "application/pdf"),
        (KnowledgeFileFormat.Docx, ".docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document"),
        (KnowledgeFileFormat.Xlsx, ".xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"),
        (KnowledgeFileFormat.Text, ".txt", "text/plain"),
        (KnowledgeFileFormat.Markdown, ".md", "text/markdown"),
    ];

    /// <summary>Every accepted extension, lower case with the dot, in declaration order.</summary>
    public static IReadOnlyList<string> Extensions { get; } = [.. Table.Select(row => row.Extension)];

    public static string Extension(KnowledgeFileFormat format) => Row(format).Extension;

    public static string ContentType(KnowledgeFileFormat format) => Row(format).ContentType;

    /// <summary>The format named by <paramref name="fileName"/>'s extension, in any letter
    /// case (<c>報價.PDF</c> is a PDF); false for any other extension or none.</summary>
    public static bool TryFromFileName(string fileName, out KnowledgeFileFormat format)
    {
        ArgumentNullException.ThrowIfNull(fileName);

        var dot = fileName.LastIndexOf('.');
        var extension = dot < 0 ? string.Empty : fileName[dot..];
        foreach (var row in Table)
        {
            if (string.Equals(row.Extension, extension, StringComparison.OrdinalIgnoreCase))
            {
                format = row.Format;
                return true;
            }
        }

        format = default;
        return false;
    }

    /// <summary>The format whose canonical <see cref="ContentType"/> this is.</summary>
    public static bool TryFromContentType(string contentType, out KnowledgeFileFormat format)
    {
        ArgumentNullException.ThrowIfNull(contentType);

        foreach (var row in Table)
        {
            if (string.Equals(row.ContentType, contentType, StringComparison.Ordinal))
            {
                format = row.Format;
                return true;
            }
        }

        format = default;
        return false;
    }

    private static (KnowledgeFileFormat Format, string Extension, string ContentType) Row(KnowledgeFileFormat format)
    {
        foreach (var row in Table)
        {
            if (row.Format == format)
            {
                return row;
            }
        }

        throw new ArgumentOutOfRangeException(nameof(format), format, "Not a declared knowledge file format.");
    }
}
