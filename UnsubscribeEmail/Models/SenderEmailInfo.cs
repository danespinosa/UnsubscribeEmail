namespace UnsubscribeEmail.Models
{
    public class SenderEmailInfo
    {
        public string SenderName { get; set; } = string.Empty;
        public string SenderEmail { get; set; } = string.Empty;
        public int EmailCount { get; set; }
        public List<string> EmailIds { get; set; } = new();
        public DateTime? LastEmailDate { get; set; }
    }
}
