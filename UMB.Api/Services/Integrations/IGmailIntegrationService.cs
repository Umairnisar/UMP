using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using UMB.Model.Models;

namespace UMB.Api.Services.Integrations
{
    public interface IGmailIntegrationService
    {
        string GetAuthorizationUrl(int userId, string email);
        Task ExchangeCodeForTokenAsync(int userId, string code, string email);
        Task<List<MessageMetadata>> FetchMessagesAsync(int userId, string email = null);
        Task<string> SendMessageAsync(int userId, string subject, string body, string toEmail, string accountEmail, List<IFormFile> attachments = null);
        Task<(byte[] Content, string ContentType, string FileName)> GetAttachmentAsync(int userId, string messageId, string attachmentId, string accountEmail);
    }
}