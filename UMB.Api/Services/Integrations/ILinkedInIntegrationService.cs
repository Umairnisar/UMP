using UMB.Model.Models;

namespace UMB.Api.Services.Integrations
{
    public interface ILinkedInIntegrationService
    {
        string GetAuthorizationUrl(int userId);
        Task ExchangeCodeForTokenAsync(int userId, string code);
        Task<List<MessageMetadata>> FetchMessagesAsync(int userId);
        Task SendMessageAsync(int userId, string recipientId, string messageText);
        Task EnsureValidAccessTokenAsync(PlatformAccount account);
    }
}
