using UMB.Api.Services.Integrations;
using UMB.Model.Models;

public interface IWhatsAppIntegrationService
{
    Task<bool> ValidateCredentialsAsync(string phoneNumberId, string accessToken, string phoneNumber);
    Task<List<MessageMetadata>> FetchMessagesAsync(int userId, string phoneNumber = null);
    Task SendMessageAsync(int userId, string recipientPhoneNumber, string message, string phoneNumber, List<IFormFile> attachments = null);
    Task<string> SendTemplateMessageAsync(int userId, string recipientPhoneNumber, string templateName, string languageCode, string phoneNumber);
    Task ProcessIncomingMessageAsync(WhatsAppFullWebhookPayload payload);
    Task MarkMessageAsReadAsync(int userId, string messageId, string phoneNumber);
    Task<(byte[] Content, string ContentType, string FileName)> GetAttachmentAsync(int userId, string messageId, string attachmentId, string phoneNumber);
}