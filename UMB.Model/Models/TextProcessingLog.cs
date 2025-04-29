using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace UMB.Model.Models
{
    /// <summary>
    /// Represents a log entry for text processing activities
    /// </summary>
    public class TextProcessingLog
    {
        public int Id { get; set; }
        public int UserId { get; set; }
        public string Activity { get; set; }
        public int CharacterCount { get; set; }
        public DateTime ProcessedAt { get; set; }

        // Navigation property
        public User User { get; set; }
    }
}
