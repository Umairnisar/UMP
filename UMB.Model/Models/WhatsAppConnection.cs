using System;
using System.ComponentModel.DataAnnotations;

namespace UMB.Model.Models
{
    public class WhatsAppConnection
    {
        [Key]
        public int Id { get; set; }
        public int UserId { get; set; }
        [Required]
        [MaxLength(100)]
        public string PhoneNumberId { get; set; }
        [Required]
        [MaxLength(500)]
        public string AccessToken { get; set; }
        [Required]
        [MaxLength(20)]
        public string PhoneNumber { get; set; } // Acts as AccountIdentifier
        [MaxLength(100)]
        public string BusinessName { get; set; }
        public bool IsConnected { get; set; } = true;
        public bool IsActive { get; set; } = false; // True for the active account
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
        public User User { get; set; }
    }
}