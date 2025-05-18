namespace UMB.Model.Models
{
    public class MessageMetadata
    {
        public int Id { get; set; }
        public int UserId { get; set; }
        public string PlatformType { get; set; }  // e.g. "Gmail", "LinkedIn", "Outlook"
        public string? ExternalMessageId { get; set; }
        public string AccountIdentifier { get; set; } // New: Email for Gmail, PhoneNumber for WhatsApp

        public string? Subject { get; set; }
        public string? Snippet { get; set; }
        public DateTime ReceivedAt { get; set; }
        public string? Body { get; set; }        // Plain text body
        public string? HtmlBody { get; set; }    // HTML formatted body
        public string? From { get; set; }
        public string? To { get; set; }
        public string? FromEmail { get; set; }
        public string? fromNumber { get; set; }
        public bool IsRead { get; set; } = false;
        public bool HasAttachments { get; set; } = false;
        public bool IsNew { get; set; } // Added for new messages
        public bool IsAutoReplied { get; set; } // Added to track auto-replies

        // Navigation properties
        public User User { get; set; }
        public ICollection<MessageAttachment> Attachments { get; set; } = new List<MessageAttachment>();
    }
}

