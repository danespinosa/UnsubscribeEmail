using UnsubscribeEmail.Models;

namespace UnsubscribeEmail.Services
{
    public interface IEmailManagementBackgroundService
    {
        Task ProcessEmailsAsync(int daysBack, string connectionId, string accessToken);
    }
}
