namespace UMB.Api.Services.Integrations
{
    public interface IOpenAIService
    {
        Task<string> SummarizeTextAsync(string text);
        Task<string> TranslateTextAsync(string text, string targetLanguage);
    }
}
