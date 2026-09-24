namespace UnsubscribeEmail.McpServer.Models;

public sealed class EmailAttachment
{
    public string MessageId { get; init; } = string.Empty;
    public string AttachmentId { get; init; } = string.Empty;
    public string? Name { get; init; }
    public string? ContentType { get; init; }
    public long? Size { get; init; }
    public bool? IsInline { get; init; }
    public string? ContentId { get; init; }
    public string? LastModifiedDateTime { get; init; }
    public string AttachmentType { get; init; } = "unknown";
    public bool DownloadSupported { get; init; }
    public string? SourceUrl { get; init; }
    public string? ProviderType { get; init; }
    public string? Permission { get; init; }
    public bool? IsFolder { get; init; }
    public string? ItemId { get; init; }
    public string? ItemType { get; init; }
    public string? ItemSubject { get; init; }
}

public sealed class EmailAttachmentListResult
{
    public string MessageId { get; init; } = string.Empty;
    public int TotalAttachments { get; init; }
    public bool HasMore { get; init; }
    public IReadOnlyList<EmailAttachment> Attachments { get; init; } = [];
}

public sealed class EmailAttachmentDownload
{
    public EmailAttachment Attachment { get; init; } = new();
    public string? ContentType { get; init; }
    public long DownloadedSize { get; init; }
    public string Base64Content { get; init; } = string.Empty;
}
