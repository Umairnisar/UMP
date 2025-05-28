using UMB.Model.Models;

namespace UMB.Api.Services.Integrations
{
    public interface ITwitterIntegrationService
    {
        string GetAuthorizationUrl(int userId, string accountIdentifier);
        Task ExchangeCodeForTokenAsync(int userId, string code, string accountIdentifier);
        Task<List<MessageMetadata>> FetchMessagesAsync(int userId, string accountIdentifier = null);
        Task<string> SendMessageAsync(int userId, string recipientId, string message, string accountIdentifier);
    }
}