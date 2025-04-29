namespace UMB.Api.Dtos
{
    /// <summary>
    /// Request DTO for text processing operations
    /// </summary>
    public class TextProcessingRequest
    {
        public string Text { get; set; }
    }

    /// <summary>
    /// Request DTO for translation operations
    /// </summary>
    public class TranslateRequest : TextProcessingRequest
    {
        public string TargetLanguage { get; set; }
    }

    /// <summary>
    /// Response DTO for text processing operations
    /// </summary>
    public class TextProcessingResponse
    {
        public string ProcessedText { get; set; }
    }
}
