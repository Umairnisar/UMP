using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using UMB.Model.Models;

namespace UMB.Api.Services.Integrations
{
    public interface IOutlookIntegrationService
    {
        string GetAuthorizationUrl(int userId, string accountIdentifier);
        Task ExchangeCodeForTokenAsync(int userId, string code, string accountIdentifier);
        Task<List<MessageMetadata>> FetchMessagesAsync(int userId, string accountIdentifier = null);
        Task SendMessageAsync(int userId, string subject, string body, string toEmail, string accountIdentifier, List<IFormFile> attachments = null);
        Task<(byte[] Content, string ContentType, string FileName)> GetAttachmentAsync(int userId, string messageId, string attachmentId, string accountIdentifier);
    }
}