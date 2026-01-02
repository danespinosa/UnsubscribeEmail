using UnsubscribeEmail.Models;

namespace UnsubscribeEmail.Services;

public interface IUnsubscribeBackgroundService
{
    string StartProcessing(string userId, string accessToken, int daysBack = 365);
    Task<ProcessingStatus?> GetStatusAsync(string jobId);
}

public class ProcessingStatus
{
    public string JobId { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public string AccessToken { get; set; } = string.Empty;
    public int DaysBack { get; set; } = 365;
    public string CurrentStep { get; set; } = "Initializing";
    public int TotalEmails { get; set; }
    public int ProcessedEmails { get; set; }
    public int TotalSenders { get; set; }
    public int ProcessedSenders { get; set; }
    public List<SenderUnsubscribeInfo> Results { get; set; } = new();
    public bool IsComplete { get; set; }
    public string? Error { get; set; }
    public DateTime StartedAt { get; set; } = DateTime.Now;
    public DateTime? CompletedAt { get; set; }
}
