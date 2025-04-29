using System;
using System.ComponentModel.DataAnnotations;

namespace UMB.Model.Models
{
    public class PlatformMessageSync
    {
        [Key]
        public int Id { get; set; }

        public int UserId { get; set; }

        [Required]
        [MaxLength(50)]
        public string PlatformType { get; set; }

        public DateTime LastSyncTime { get; set; }

        // Navigation property
        public User User { get; set; }
    }
}