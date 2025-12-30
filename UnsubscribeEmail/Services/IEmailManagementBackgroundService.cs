using UnsubscribeEmail.Models;
using static UnsubscribeEmail.Services.EmailManagementBackgroundService;

namespace UnsubscribeEmail.Services
{
    public interface IEmailManagementBackgroundService
    {
        Task ProcessEmailsAsync(int daysBack, string connectionId, string accessToken);
        Task<EmailActionResult> MarkEmailsAsReadAsync(string senderEmail, int daysBack, string accessToken);

        Task<List<EmailMessage>> FetchEmailsAsync(int daysBack, string connectionId, string accessToken);
    }

    public class EmailActionResult
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
        public int Count { get; set; }
    }
}
