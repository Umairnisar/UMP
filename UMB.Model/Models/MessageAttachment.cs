using System;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace UMB.Model.Models
{
    public class MessageAttachment
    {
        [Key]
        public int Id { get; set; }

        public int MessageMetadataId { get; set; }

        [Required]
        [MaxLength(255)]
        public string FileName { get; set; }

        [MaxLength(100)]
        public string ContentType { get; set; }

        public long Size { get; set; }

        [Required]
        [MaxLength(500)]
        public string AttachmentId { get; set; } // External ID from the platform (Gmail, Outlook, etc.)

        // This is stored in DB only for small attachments (<= 1MB)
        // For larger attachments, it will be null and content will be fetched on demand
        public byte[]? Content { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        [JsonIgnore]
        public MessageMetadata Message { get; set; }
    }
}