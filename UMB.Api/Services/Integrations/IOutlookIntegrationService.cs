using Microsoft.AspNetCore.Http;
using UMB.Model.Models;

namespace UMB.Api.Services.Integrations
{
    public interface IOutlookIntegrationService
    {
        /// <summary>
        /// Gets the authorization URL for the user to initiate the OAuth flow
        /// </summary>
        string GetAuthorizationUrl(int userId);

        /// <summary>
        /// Exchanges an authorization code for access and refresh tokens
        /// </summary>
        Task ExchangeCodeForTokenAsync(int userId, string code);

        /// <summary>
        /// Fetches messages from the user's Outlook account
        /// </summary>
        Task<List<MessageMetadata>> FetchMessagesAsync(int userId);

        /// <summary>
        /// Sends an email message from the user's Outlook account
        /// </summary>
        Task SendMessageAsync(int userId, string subject, string body, string toEmail, List<IFormFile> attachments = null);

        /// <summary>
        /// Gets an attachment from an Outlook message
        /// </summary>
        Task<(byte[] Content, string ContentType, string FileName)> GetAttachmentAsync(int userId, string messageId, string attachmentId);

        /// <summary>
        /// Ensures the access token is valid, refreshing it if necessary
        /// </summary>
        /// <returns>True if the token is valid or was successfully refreshed, false otherwise</returns>
        Task<bool> EnsureValidAccessTokenAsync(PlatformAccount account);
    }
}