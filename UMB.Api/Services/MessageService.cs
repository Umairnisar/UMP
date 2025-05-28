using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using UMB.Api.Services.Integrations;
using UMB.Model.Models;

namespace UMB.Api.Services
{
    public class MessageService : IMessageService
    {
        private readonly AppDbContext _dbContext;
        private readonly IGmailIntegrationService _gmailIntegration;
        private readonly ILinkedInIntegrationService _linkedinIntegration;
        private readonly IOutlookIntegrationService _outlookIntegration;
        private readonly IWhatsAppIntegrationService _whatsAppIntegration;
        private readonly ITwitterIntegrationService _twitterIntegration;
        private readonly ILogger<MessageService> _logger;

        public MessageService(
            AppDbContext dbContext,
            IGmailIntegrationService gmailIntegration,
            ILinkedInIntegrationService linkedinIntegration,
            IOutlookIntegrationService outlookIntegration,
            IWhatsAppIntegrationService whatsAppIntegration,
            ITwitterIntegrationService twitterIntegration,
            ILogger<MessageService> logger)
        {
            _dbContext = dbContext;
            _gmailIntegration = gmailIntegration;
            _linkedinIntegration = linkedinIntegration;
            _outlookIntegration = outlookIntegration;
            _whatsAppIntegration = whatsAppIntegration;
            _twitterIntegration = twitterIntegration;
            _logger = logger;
        }

        public async Task<List<MessageMetadata>> GetConsolidatedMessages(int userId, bool? unread = null, string platform = null)
        {
            var messages = new List<MessageMetadata>();

            // Fetch messages from database
            var query = _dbContext.MessageMetadatas
                .Where(m => m.UserId == userId);

            if (!string.IsNullOrEmpty(platform))
            {
                query = query.Where(m => m.PlatformType == platform);
            }

            if (unread.HasValue)
            {
                query = query.Where(m => m.IsRead == !unread.Value);
            }

            var dbMessages = await query
                .Include(m => m.Attachments)
                .OrderByDescending(m => m.ReceivedAt)
                .ToListAsync();

            messages.AddRange(dbMessages);

            // Fetch new messages from platforms
            var platforms = await _dbContext.PlatformAccounts
                .Where(pa => pa.UserId == userId)
                .Select(pa => pa.PlatformType)
                .Distinct()
                .ToListAsync();

            if (string.IsNullOrEmpty(platform))
            {
                platforms.Add("WhatsApp"); // Include WhatsApp if no specific platform is specified
                platforms.Add("Twitter"); // Include Twitter if no specific platform is specified
            }
            else if (platform.Equals("WhatsApp", StringComparison.OrdinalIgnoreCase))
            {
                platforms = new List<string> { "WhatsApp" };
            }
            else if (platform.Equals("Twitter", StringComparison.OrdinalIgnoreCase))
            {
                platforms = new List<string> { "Twitter" };
            }
            else
            {
                platforms = new List<string> { platform };
            }

            foreach (var plat in platforms)
            {
                try
                {
                    switch (plat.ToLower())
                    {
                        case "gmail":
                            messages.AddRange(await _gmailIntegration.FetchMessagesAsync(userId));
                            break;
                        case "linkedin":
                            messages.AddRange(await _linkedinIntegration.FetchMessagesAsync(userId));
                            break;
                        case "outlook":
                            messages.AddRange(await _outlookIntegration.FetchMessagesAsync(userId));
                            break;
                        case "whatsapp":
                            messages.AddRange(await _whatsAppIntegration.FetchMessagesAsync(userId));
                            break;
                        case "twitter":
                            messages.AddRange(await _twitterIntegration.FetchMessagesAsync(userId));
                            break;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error fetching messages for {Platform} for user {UserId}", plat, userId);
                }
            }

            // Remove duplicates based on ExternalMessageId and keep the latest
            messages = messages
                .GroupBy(m => new { m.ExternalMessageId, m.AccountIdentifier })
                .Select(g => g.OrderByDescending(m => m.ReceivedAt).First())
                .OrderByDescending(m => m.ReceivedAt)
                .ToList();

            return messages;
        }

        public async Task SendMessage(int userId, string platform, string subject, string body, string to, string accountIdentifier, List<IFormFile> attachments = null)
        {
            try
            {
                foreach (var recipient in to)
                {
                    switch (platform.ToLower())
                    {
                        case "gmail":
                            await _gmailIntegration.SendMessageAsync(userId, subject, body, to, accountIdentifier, attachments);
                            break;
                        case "linkedin":
                            if (attachments != null && attachments.Any())
                                throw new InvalidOperationException("LinkedIn does not support attachments.");
                            await _linkedinIntegration.SendMessageAsync(userId, to, body, accountIdentifier);
                            break;
                        case "outlook":
                            await _outlookIntegration.SendMessageAsync(userId, subject, body, to, accountIdentifier, attachments);

                            break;
                        case "whatsapp":
                            await _whatsAppIntegration.SendMessageAsync(userId, to, body, accountIdentifier, attachments);
                            break;
                        case "twitter":
                            if (attachments != null && attachments.Any())
                                throw new InvalidOperationException("Twitter does not support attachments.");
                            await _twitterIntegration.SendMessageAsync(userId, to, body, accountIdentifier);
                            break;
                        default:
                            throw new ArgumentException($"Unsupported platform: {platform}");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending message for user {UserId} on platform {Platform}", userId, platform);
                throw;
            }
        }

        public async Task<MessageMetadata> GetMessageByExternalId(int userId, string externalMessageId, string accountIdentifier)
        {
            var message = await _dbContext.MessageMetadatas
                .Include(m => m.Attachments)
                .FirstOrDefaultAsync(m => m.UserId == userId && m.ExternalMessageId == externalMessageId && m.AccountIdentifier == accountIdentifier);

            if (message == null)
            {
                throw new KeyNotFoundException($"Message with Id {externalMessageId} not found for account {accountIdentifier}.");
            }

            message.IsRead = true;
            await _dbContext.SaveChangesAsync();
            return message;
        }
        public async Task<(byte[] Content, string ContentType, string FileName)> GetAttachmentAsync(int userId, string messageId, string attachmentId)

        {
            var message = await _dbContext.MessageMetadatas
                .FirstOrDefaultAsync(m => m.UserId == userId && m.ExternalMessageId == messageId);

            if (message == null)
            {
                throw new KeyNotFoundException($"Message with Id {messageId} not found.");
            }

            var platform = message.PlatformType;
            var accountIdentifier = message.AccountIdentifier;

            switch (platform.ToLower())
            {
                case "gmail":
                    return await _gmailIntegration.GetAttachmentAsync(userId, messageId, attachmentId, accountIdentifier);
                case "outlook":
                    return await _outlookIntegration.GetAttachmentAsync(userId, messageId, attachmentId, accountIdentifier);
                case "whatsapp":
                    return await _whatsAppIntegration.GetAttachmentAsync(userId, messageId, attachmentId, accountIdentifier);
                case "twitter":
                    throw new NotSupportedException("Attachments not supported for Twitter.");
                default:
                    throw new NotSupportedException($"Attachments not supported for platform {platform}.");
            }
        }
    }
}