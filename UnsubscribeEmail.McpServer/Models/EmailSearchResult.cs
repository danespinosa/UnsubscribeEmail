namespace UnsubscribeEmail.McpServer.Models;

public sealed class EmailSearchResult
{
    public string MessageId { get; init; } = string.Empty;
    public string Subject { get; init; } = string.Empty;
    public string ReceivedDateTime { get; init; } = string.Empty;
    public string Sender { get; init; } = string.Empty;
    public bool IsRead { get; init; }
    public bool HasAttachments { get; init; }
}

public sealed class EmailSearchPage
{
    public IReadOnlyList<EmailSearchResult> Messages { get; init; } = [];
    public bool HasMore { get; init; }
}
