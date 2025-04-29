using UMB.Model.Models;

namespace UMB.Api.Services.Integrations
{
    public interface IWhatsAppIntegrationService
    {
        /// <summary>
        /// Validates the provided WhatsApp Business API credentials
        /// </summary>
        Task<bool> ValidateCredentialsAsync(string phoneNumberId, string accessToken, string phoneNumber);

        /// <summary>
        /// Fetches recent messages from the connected WhatsApp account (from local storage)
        /// </summary>
        /// <param name="userId">The user ID whose WhatsApp messages to fetch</param>
        /// <returns>A list of message metadata</returns>
        Task<List<MessageMetadata>> FetchMessagesAsync(int userId);

        /// <summary>
        /// Sends a WhatsApp text message to the specified recipient
        /// </summary>
        /// <param name="userId">The user ID sending the message</param>
        /// <param name="message">The message text content</param>
        /// <param name="recipientPhoneNumber">The recipient's phone number</param>
        /// <returns>The WhatsApp message ID</returns>
        Task<string> SendMessageAsync(int userId, string message, string recipientPhoneNumber);

        /// <summary>
        /// Sends a WhatsApp template message to the specified recipient
        /// </summary>
        /// <param name="userId">The user ID sending the message</param>
        /// <param name="recipientPhoneNumber">The recipient's phone number</param>
        /// <param name="templateName">The name of the template to use</param>
        /// <param name="languageCode">The language code for the template</param>
        /// <returns>The WhatsApp message ID</returns>
        Task<string> SendTemplateMessageAsync(int userId, string recipientPhoneNumber, string templateName, string languageCode);

        /// <summary>
        /// Processes incoming messages received via webhook
        /// </summary>
        /// <param name="payload">The webhook payload containing the message data</param>
        Task ProcessIncomingMessageAsync(WhatsAppFullWebhookPayload payload);

        /// <summary>
        /// Processes a WhatsApp message change from a webhook
        /// </summary>
        /// <param name="change">The change object containing message data</param>
        Task ProcessWhatsAppMessageChangeAsync(WhatsAppChange change);

        /// <summary>
        /// Marks a message as read
        /// </summary>
        /// <param name="userId">The user ID</param>
        /// <param name="messageId">The message ID to mark as read</param>
        Task MarkMessageAsReadAsync(int userId, string messageId);
    }
}