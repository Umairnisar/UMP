using UMB.Model.Models;

namespace UMB.Api.Services
{
    public interface IMessageService
    {
        Task<List<MessageMetadata>> GetConsolidatedMessages(int userId, bool? unread, string? platform);
        Task SendMessage(int userId, string platform, string subject, string body, string to, List<IFormFile> attachments = null);
        Task<MessageMetadata> GetMessageByExternalId(int userId, string externalMessageId);
        Task<(byte[] Content, string ContentType, string FileName)> GetAttachmentAsync(int userId, string externalMessageId, string attachmentId);
    }
}
