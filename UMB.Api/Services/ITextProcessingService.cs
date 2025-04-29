namespace UMB.Api.Services
{
    public interface ITextProcessingService
    {
        Task<string> SummarizeTextAsync(int userId, string text);
        Task<string> TranslateTextAsync(int userId, string text, string targetLanguage);
    }
}
