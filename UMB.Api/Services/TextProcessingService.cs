using UMB.Api.Services.Integrations;
using UMB.Model.Models;

namespace UMB.Api.Services
{

    public class TextProcessingService : ITextProcessingService
    {
        private readonly IOpenAIService _openAIService;
        private readonly AppDbContext _dbContext;

        public TextProcessingService(IOpenAIService openAIService, AppDbContext dbContext)
        {
            _openAIService = openAIService;
            _dbContext = dbContext;
        }

        public async Task<string> SummarizeTextAsync(int userId, string text)
        {
            // Validate user has access
            var user = await _dbContext.Users.FindAsync(userId);
            if (user == null)
            {
                throw new UnauthorizedAccessException("User not found");
            }

            // Log the request
            await LogTextProcessingActivity(userId, "Summarize", text.Length);

            // Process the text
            var summary = await _openAIService.SummarizeTextAsync(text);
            return summary;
        }

        public async Task<string> TranslateTextAsync(int userId, string text, string targetLanguage)
        {
            // Validate user has access
            var user = await _dbContext.Users.FindAsync(userId);
            if (user == null)
            {
                throw new UnauthorizedAccessException("User not found");
            }

            // Log the request
            await LogTextProcessingActivity(userId, $"Translate to {targetLanguage}", text.Length);

            // Process the text
            var translatedText = await _openAIService.TranslateTextAsync(text, targetLanguage);
            return translatedText;
        }

        private async Task LogTextProcessingActivity(int userId, string activity, int characterCount)
        {
            var log = new TextProcessingLog
            {
                UserId = userId,
                Activity = activity,
                CharacterCount = characterCount,
                ProcessedAt = DateTime.UtcNow
            };

            _dbContext.TextProcessingLogs.Add(log);
            await _dbContext.SaveChangesAsync();
        }
    }
}
