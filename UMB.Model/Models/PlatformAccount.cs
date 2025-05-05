using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace UMB.Model.Models
{
    //public class PlatformAccount
    //{
    //    public int Id { get; set; }
    //    public int UserId { get; set; }
    //    [Required]
    //    [MaxLength(50)]
    //    public string PlatformType { get; set; }
    //    [Required]
    //    [MaxLength(255)]
    //    public string AccountIdentifier { get; set; }
    //    public string AccessToken { get; set; }
    //    public string RefreshToken { get; set; }
    //    public DateTime? TokenExpiresAt { get; set; }
    //    public bool IsActive { get; set; } = false;
    //    public string ExternalAccountId { get; set; }
    //    public string ExternalBusinessId { get; set; }
    //    public User User { get; set; }
    //}

    public class PlatformAccount
    {
        public int Id { get; set; }
        public int UserId { get; set; }
        public string PlatformType { get; set; } // e.g., Gmail, WhatsApp, LinkedIn
        public string AccountIdentifier { get; set; } // Email for Gmail/Outlook, PhoneNumber for WhatsApp, ProfileId/Email for LinkedIn
        public string AccessToken { get; set; }
        public string RefreshToken { get; set; }
        public DateTime? TokenExpiresAt { get; set; }
        public string ExternalAccountId { get; set; }
        public bool IsActive { get; set; } // New: Indicates the active account for the platform
        public DateTime CreatedAt { get; set; }
        public DateTime? UpdatedAt { get; set; }

        public User User { get; set; }
    }

}
