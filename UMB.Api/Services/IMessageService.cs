using Microsoft.AspNetCore.Http;
using System.Collections.Generic;
using System.Threading.Tasks;
using UMB.Model.Models;

namespace UMB.Api.Services
{
    public interface IMessageService
    {
        Task<List<MessageMetadata>> GetConsolidatedMessages(int userId, bool? unread = null, string platform = null);
        Task SendMessage(int userId, string platform, string subject, string body, string to, string accountIdentifier, List<IFormFile> attachments = null);
        Task<MessageMetadata> GetMessageByExternalId(int userId, string externalMessageId, string accountIdentifier);
        Task<(byte[] Content, string ContentType, string FileName)> GetAttachmentAsync(int userId, string messageId, string attachmentId);
    }
}