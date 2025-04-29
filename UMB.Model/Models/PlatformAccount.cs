using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace UMB.Model.Models
{
    public class PlatformAccount
    {
        public int Id { get; set; }
        public int UserId { get; set; }
        public string? PlatformType { get; set; } // e.g., "Gmail", "LinkedIn", "Outlook"
        public string? AccessToken { get; set; }
        public string? RefreshToken { get; set; }
        public DateTime? TokenExpiresAt { get; set; }
        public User User { get; set; }
        public string? ExternalAccountId { get; set; }
        public string? ExternalBusinessId { get; set; }
    }
}
