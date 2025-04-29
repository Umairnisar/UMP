using Microsoft.AspNetCore.Http;
using UMB.Model.Models;

namespace UMB.Api.Services.Integrations
{
    public interface IGmailIntegrationService
    {
        // 1) For the initial redirect
        string GetAuthorizationUrl(int userId);

        // 2) For handling the callback and getting tokens
        Task ExchangeCodeForTokenAsync(int userId, string code);

        // 3) Fetch messages
        Task<List<MessageMetadata>> FetchMessagesAsync(int userId);

        // 4) Send message
        Task<string> SendMessageAsync(int userId, string subject, string body, string toEmail, List<IFormFile> attachments = null);

        // 5) Get attachment 
        Task<(byte[] Content, string ContentType, string FileName)> GetAttachmentAsync(int userId, string messageId, string attachmentId);

        // 6) Refresh tokens if needed
        Task EnsureValidAccessTokenAsync(PlatformAccount account);
    }
}