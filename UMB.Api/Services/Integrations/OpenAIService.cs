using System.Text;
using System.Text.Json;

namespace UMB.Api.Services.Integrations
{

    public class OpenAIService : IOpenAIService
    {
        private readonly HttpClient _httpClient;
        private readonly string _apiKey;
        private readonly string _apiUrl;
        private readonly string _model;

        public OpenAIService(IConfiguration configuration, HttpClient httpClient)
        {
            _httpClient = httpClient;
            _apiKey = configuration["OpenAI:ApiKey"];
            _apiUrl = configuration["OpenAI:ApiUrl"] ?? "https://api.openai.com/v1/chat/completions";
            _model = configuration["OpenAI:Model"] ?? "gpt-4o";

            if (string.IsNullOrEmpty(_apiKey))
            {
                throw new ArgumentNullException(nameof(_apiKey), "OpenAI API key is not configured");
            }

            _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {_apiKey}");
        }

        public async Task<string> SummarizeTextAsync(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }

            var messages = new[]
            {
                new { role = "system", content = "You are a helpful assistant that summarizes text accurately and concisely." },
                new { role = "user", content = $"Please summarize the following text in a concise manner, preserving the key points and important details:\n\n{text}" }
            };

            return await SendRequestAsync(messages);
        }

        public async Task<string> TranslateTextAsync(string text, string targetLanguage)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }

            var messages = new[]
            {
                new { role = "system", content = $"You are a professional translator. Translate the following text into {targetLanguage}. Maintain the original meaning, tone, and formatting as much as possible." },
                new { role = "user", content = text }
            };

            return await SendRequestAsync(messages);
        }

        private async Task<string> SendRequestAsync(object[] messages)
        {
            var requestData = new
            {
                model = _model,
                messages,
                temperature = 0.3,
                max_tokens = 1000
            };

            var content = new StringContent(
                JsonSerializer.Serialize(requestData),
                Encoding.UTF8,
                "application/json");

            var response = await _httpClient.PostAsync(_apiUrl, content);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                throw new HttpRequestException($"OpenAI API request failed with status {response.StatusCode}: {errorContent}");
            }

            var responseJson = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(responseJson);

            // Extract the response content
            return doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString() ?? string.Empty;
        }
    }
}
