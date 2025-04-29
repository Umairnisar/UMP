using Microsoft.AspNetCore.Http;
using UMB.Api.Services.Integrations;
using UMB.Model.Models;
using Microsoft.EntityFrameworkCore;

namespace UMB.Api.Services
{
    public class MessageService : IMessageService
    {
        private readonly IGmailIntegrationService _gmail;
        private readonly ILinkedInIntegrationService _linkedin;
        private readonly IOutlookIntegrationService _outlook;
        private readonly IWhatsAppIntegrationService _whatsapp;
        private readonly AppDbContext _dbContext;
        private readonly ILogger<MessageService> _logger;

        // Configure how frequently to refresh messages from external APIs
        private readonly TimeSpan _refreshThreshold = TimeSpan.FromMinutes(0.5);

        public MessageService(
            IGmailIntegrationService gmail,
            ILinkedInIntegrationService linkedin,
            IOutlookIntegrationService outlook,
            IWhatsAppIntegrationService whatsapp,
            AppDbContext dbContext,
            ILogger<MessageService> logger)
        {
            _gmail = gmail;
            _linkedin = linkedin;
            _outlook = outlook;
            _whatsapp = whatsapp;
            _dbContext = dbContext;
            _logger = logger;
        }

        public async Task<List<MessageMetadata>> GetConsolidatedMessages(
            int userId, bool? unread, string? platform)
        {
            // Get all connected platforms for the user
            var platformAccounts = await _dbContext.PlatformAccounts
                .Where(pa => pa.UserId == userId)
                .ToListAsync();

            // Check for WhatsApp connection separately (if we're using separate tables)
            var whatsAppConnection = await _dbContext.WhatsAppConnections
                .FirstOrDefaultAsync(c => c.UserId == userId && c.IsConnected);

            bool hasWhatsApp = whatsAppConnection != null;

            // Initialize result list
            var allMessages = new List<MessageMetadata>();

            // Track when messages were last synced for each platform
            var lastSyncTimes = await GetLastSyncTimesForUser(userId);

            // Process each platform account
            foreach (var account in platformAccounts)
            {
                // Skip if we're filtering by platform and this isn't the one
                if (platform != null && !account.PlatformType.Equals(platform, StringComparison.OrdinalIgnoreCase))
                    continue;

                // Get messages for this platform
                var platformMessages = await GetMessagesForPlatform(
                    userId,
                    account.PlatformType,
                    lastSyncTimes.GetValueOrDefault(account.PlatformType));

                // Apply unread filter if specified
                if (unread.HasValue)
                {
                    platformMessages = platformMessages.Where(m => m.IsRead != unread.Value).ToList();
                }

                allMessages.AddRange(platformMessages);
            }

            // Sort by received date descending
            return allMessages.OrderByDescending(m => m.ReceivedAt).ToList();
        }

        private async Task<Dictionary<string, DateTime>> GetLastSyncTimesForUser(int userId)
        {
            // Query platform message sync records
            var syncRecords = await _dbContext.PlatformMessageSyncs
                .Where(sync => sync.UserId == userId)
                .ToListAsync();

            // Build dictionary of platform -> lastSyncTime
            var syncTimes = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
            foreach (var record in syncRecords)
            {
                syncTimes[record.PlatformType] = record.LastSyncTime;
            }

            return syncTimes;
        }

        private async Task<List<MessageMetadata>> GetMessagesForPlatform(
            int userId, string platformType, DateTime lastSyncTime)
        {
            // Get messages from database - include attachments
            var dbMessages = await _dbContext.MessageMetadatas
                .Include(m => m.Attachments)
                .Where(m => m.UserId == userId && m.PlatformType == platformType)
                .OrderByDescending(m => m.ReceivedAt)
                .Take(100) // Limit to reasonable number
                .ToListAsync();

            // Check if we need to refresh from API
            bool shouldRefresh = ShouldRefreshMessages(lastSyncTime, platformType);

            if (shouldRefresh)
            {
                _logger.LogInformation("Refreshing messages for user {UserId} from {Platform}", userId, platformType);

                try
                {
                    // Fetch fresh messages from the appropriate service
                    List<MessageMetadata> freshMessages = await FetchMessagesFromService(userId, platformType);

                    if (freshMessages.Any())
                    {
                        // Update last sync time
                        await UpdateLastSyncTime(userId, platformType);

                        // Merge with existing messages, eliminating duplicates
                        return MergeMessageLists(freshMessages, dbMessages);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error refreshing messages for {Platform}", platformType);
                    // Fall back to database messages on error
                }
            }

            // Return database messages if no refresh was needed or if refresh failed
            return dbMessages;
        }

        private bool ShouldRefreshMessages(DateTime lastSyncTime, string platformType)
        {
            // Always refresh if we've never synced before
            if (lastSyncTime == default)
                return true;

            // Don't refresh WhatsApp from API as we get updates via webhook
            if (platformType.Equals("WhatsApp", StringComparison.OrdinalIgnoreCase))
                return false;

            // Check if we've passed the refresh threshold
            return DateTime.UtcNow - lastSyncTime > _refreshThreshold;
        }

        private async Task<List<MessageMetadata>> FetchMessagesFromService(int userId, string platformType)
        {
            // Call the appropriate service based on platform type
            return platformType.ToLower() switch
            {
                "gmail" => await _gmail.FetchMessagesAsync(userId),
                "linkedin" => await _linkedin.FetchMessagesAsync(userId),
                "outlook" => await _outlook.FetchMessagesAsync(userId),
                "whatsapp" => await _whatsapp.FetchMessagesAsync(userId),
                _ => new List<MessageMetadata>()
            };
        }

        public async Task SendMessage(int userId, string platform, string subject, string body, string to, List<IFormFile> attachments = null)
        {
            switch (platform.ToLower())
            {
                case "gmail":
                    await _gmail.SendMessageAsync(userId, subject, body, to, attachments);
                    break;
                case "linkedin":
                    await _linkedin.SendMessageAsync(userId, to, body);
                    break;
                case "outlook":
                    await _outlook.SendMessageAsync(userId, subject, body, to, attachments);
                    break;
                case "whatsapp":
                    await _whatsapp.SendMessageAsync(userId, body, to);
                    break;
                default:
                    throw new Exception("Unsupported platform.");
            }
        }

        private async Task UpdateLastSyncTime(int userId, string platformType)
        {
            var syncRecord = await _dbContext.PlatformMessageSyncs
                .FirstOrDefaultAsync(s => s.UserId == userId && s.PlatformType == platformType);

            if (syncRecord == null)
            {
                syncRecord = new PlatformMessageSync
                {
                    UserId = userId,
                    PlatformType = platformType,
                    LastSyncTime = DateTime.UtcNow
                };
                _dbContext.PlatformMessageSyncs.Add(syncRecord);
            }
            else
            {
                syncRecord.LastSyncTime = DateTime.UtcNow;
            }

            await _dbContext.SaveChangesAsync();
        }

        private List<MessageMetadata> MergeMessageLists(
           List<MessageMetadata> freshMessages,
           List<MessageMetadata> dbMessages)
        {
            // Create a dictionary of existing messages for quick lookup  
            var mergedDict = dbMessages
                .Where(m => m.ExternalMessageId != null) // Filter out null ExternalMessageId  
                .ToDictionary(m => m.ExternalMessageId!); // Use null-forgiving operator  
                                                          // Create a dictionary of existing messages for quick lookup  
            //var mergedDict = dbMessages
            //       .Where(m => m.ExternalMessageId != null) // Filter out null ExternalMessageId  
            //       .GroupBy(m => m.ExternalMessageId!) // Group by ExternalMessageId to handle duplicates  
            //       .ToDictionary(g => g.Key, g => g.First()); // Use the first message in case of duplicates  

            // Add or update with fresh messages  
            foreach (var freshMsg in freshMessages)
            {
                if (freshMsg.ExternalMessageId != null && !mergedDict.ContainsKey(freshMsg.ExternalMessageId))
                {
                    mergedDict[freshMsg.ExternalMessageId] = freshMsg;
                }
                // If needed, update properties from fresh message to db message  
                // For example, if read status changed  
            }

            // Convert back to list and sort  
            return mergedDict.Values
                .OrderByDescending(m => m.ReceivedAt)
                .ToList();
        }

        public async Task<MessageMetadata> GetMessageByExternalId(int userId, string externalMessageId)
        {
            // Get message directly from the database - include attachments
            var message = await _dbContext.MessageMetadatas
                .Include(m => m.Attachments)
                .FirstOrDefaultAsync(m => m.UserId == userId && m.ExternalMessageId == externalMessageId);

            if (message == null)
            {
                throw new KeyNotFoundException($"Message with ID {externalMessageId} not found");
            }

            // Update read status if not already read
            if (!message.IsRead)
            {
                message.IsRead = true;
                await _dbContext.SaveChangesAsync();
            }

            return message;
        }

        public async Task<(byte[] Content, string ContentType, string FileName)> GetAttachmentAsync(int userId, string externalMessageId, string attachmentId)
        {
            // Find the message
            var message = await _dbContext.MessageMetadatas
                .FirstOrDefaultAsync(m => m.UserId == userId && m.ExternalMessageId == externalMessageId);

            if (message == null)
            {
                throw new KeyNotFoundException($"Message with ID {externalMessageId} not found");
            }

            // Find the attachment
            var attachment = await _dbContext.MessageAttachments
                .FirstOrDefaultAsync(a => a.MessageMetadataId == message.Id && a.AttachmentId == attachmentId);

            if (attachment == null)
            {
                throw new KeyNotFoundException($"Attachment with ID {attachmentId} not found");
            }

            // If we have the content stored in the database, return it
            if (attachment.Content != null)
            {
                return (attachment.Content, attachment.ContentType, attachment.FileName);
            }

            // Otherwise, fetch it from the platform
            return message.PlatformType.ToLower() switch
            {
                "gmail" => await _gmail.GetAttachmentAsync(userId, externalMessageId, attachmentId),
                "outlook" => await _outlook.GetAttachmentAsync(userId, externalMessageId, attachmentId),
                _ => throw new NotSupportedException($"Attachment download not supported for platform {message.PlatformType}")
            };
        }
    }
}