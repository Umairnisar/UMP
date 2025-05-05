using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using UMB.Model.Models;

namespace UMB.Api.Services.Integrations
{
    public class WhatsAppIntegrationService : IWhatsAppIntegrationService
    {
        private readonly IConfiguration _config;
        private readonly AppDbContext _dbContext;
        private readonly HttpClient _httpClient;
        private readonly ILogger<WhatsAppIntegrationService> _logger;
        private readonly string _apiUrl;

        public WhatsAppIntegrationService(
            IConfiguration config,
            AppDbContext dbContext,
            IHttpClientFactory httpClientFactory,
            ILogger<WhatsAppIntegrationService> logger)
        {
            _config = config;
            _dbContext = dbContext;
            _httpClient = httpClientFactory.CreateClient("WhatsAppClient");
            _logger = logger;
            _apiUrl = _config["WhatsAppSettings:ApiUrl"] ?? "https://graph.facebook.com/v18.0";
        }

        public async Task<bool> ValidateCredentialsAsync(string phoneNumberId, string accessToken, string phoneNumber)
        {
            try
            {
                using var tempClient = new HttpClient();
                tempClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                var response = await tempClient.GetAsync($"{_apiUrl}/{phoneNumberId}/whatsapp_business_profile");
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error validating WhatsApp credentials for {PhoneNumber}", phoneNumber);
                return false;
            }
        }

        public async Task<List<MessageMetadata>> FetchMessagesAsync(int userId, string phoneNumber = null)
        {
            try
            {
                var query = _dbContext.WhatsAppConnections
                    .Where(c => c.UserId == userId && c.IsConnected);
                if (!string.IsNullOrEmpty(phoneNumber))
                {
                    query = query.Where(c => c.PhoneNumber == phoneNumber);
                }

                var connections = await query.ToListAsync();
                if (!connections.Any())
                    return new List<MessageMetadata>();

                var messages = await _dbContext.MessageMetadatas
                    .Where(m => m.UserId == userId && m.PlatformType == "WhatsApp" &&
                        connections.Select(c => c.PhoneNumber).Contains(m.AccountIdentifier))
                    .Include(m => m.Attachments)
                    .OrderByDescending(m => m.ReceivedAt)
                    .Take(50)
                    .ToListAsync();

                return messages;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching WhatsApp messages for user {UserId}", userId);
                return new List<MessageMetadata>();
            }
        }

        public async Task SendMessageAsync(int userId, string recipientPhoneNumber, string message, string phoneNumber, List<IFormFile> attachments = null)
        {
            var connection = await _dbContext.WhatsAppConnections
                .FirstOrDefaultAsync(c => c.UserId == userId && c.IsConnected && c.PhoneNumber == phoneNumber);

            if (connection == null)
                throw new Exception($"No WhatsApp account connected for phone number {phoneNumber}.");

            recipientPhoneNumber = FormatPhoneNumber(recipientPhoneNumber);

            string messageId;
            if (attachments != null && attachments.Any())
            {
                foreach (var attachment in attachments)
                {
                    using var content = new MultipartFormDataContent();
                    using var stream = attachment.OpenReadStream();
                    var fileContent = new StreamContent(stream);
                    fileContent.Headers.ContentType = new MediaTypeHeaderValue(attachment.ContentType);
                    content.Add(fileContent, "file", attachment.FileName);

                    _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", connection.AccessToken);
                    var uploadResponse = await _httpClient.PostAsync($"{_apiUrl}/{connection.PhoneNumberId}/media", content);

                    if (!uploadResponse.IsSuccessStatusCode)
                    {
                        var errorContent = await uploadResponse.Content.ReadAsStringAsync();
                        throw new Exception($"Failed to upload WhatsApp media: {errorContent}");
                    }

                    var uploadResult = JsonSerializer.Deserialize<WhatsAppMediaUploadResponse>(await uploadResponse.Content.ReadAsStringAsync());
                    var mediaId = uploadResult.Id;

                    //var mediaPayload = new
                    //{
                    //    messaging_product = "whatsapp",
                    //    recipient_type = "individual",
                    //    to = recipientPhoneNumber,
                    //    type = GetMediaType(attachment.ContentType),
                    //    [GetMediaType(attachment.ContentType)] = new Dictionary<string, string> { ["id"] = mediaId }
                    //   // [GetMediaType(attachment.ContentType)] = new { id = mediaId }
                    //};

                    var mediaPayload = new Dictionary<string, object>
                    {
                        ["messaging_product"] = "whatsapp",
                        ["recipient_type"] = "individual",
                        ["to"] = recipientPhoneNumber,
                        ["type"] = GetMediaType(attachment.ContentType),
                        [GetMediaType(attachment.ContentType)] = new Dictionary<string, string> { ["id"] = mediaId }
                    };
                    var mediaContent = new StringContent(
                        JsonSerializer.Serialize(mediaPayload),
                        Encoding.UTF8,
                        "application/json");

                    var mediaResponse = await _httpClient.PostAsync($"{_apiUrl}/{connection.PhoneNumberId}/messages", mediaContent);

                    if (!mediaResponse.IsSuccessStatusCode)
                    {
                        var errorContent = await mediaResponse.Content.ReadAsStringAsync();
                        throw new Exception($"Failed to send WhatsApp media message: {errorContent}");
                    }

                    var responseContent = await mediaResponse.Content.ReadAsStringAsync();
                    var responseObj = JsonSerializer.Deserialize<WhatsAppMessageResponse>(responseContent);
                    messageId = responseObj?.Messages?.FirstOrDefault()?.Id ?? Guid.NewGuid().ToString();

                    var attachmentMetadata = new MessageAttachment
                    {
                        FileName = attachment.FileName,
                        ContentType = attachment.ContentType,
                        Size = attachment.Length,
                        AttachmentId = mediaId,
                        Content = attachment.Length < 1024 * 1024 ? await ReadStreamToBytesAsync(attachment.OpenReadStream()) : null,
                        CreatedAt = DateTime.UtcNow
                    };

                    var messageMetadata = new MessageMetadata
                    {
                        UserId = userId,
                        PlatformType = "WhatsApp",
                        ExternalMessageId = messageId,
                        AccountIdentifier = connection.PhoneNumber,
                        Subject = "WhatsApp Media Message",
                        Snippet = $"[Media: {attachment.FileName}]",
                        Body = $"[Media: {attachment.FileName}]",
                        From = "You",
                        ReceivedAt = DateTime.UtcNow,
                        IsRead = true,
                        HasAttachments = true,
                        Attachments = new List<MessageAttachment> { attachmentMetadata }
                    };

                    _dbContext.MessageMetadatas.Add(messageMetadata);
                }
            }
            else
            {
                var payload = new
                {
                    messaging_product = "whatsapp",
                    recipient_type = "individual",
                    to = recipientPhoneNumber,
                    type = "text",
                    text = new { body = message }
                };

                var content = new StringContent(
                    JsonSerializer.Serialize(payload),
                    Encoding.UTF8,
                    "application/json");

                _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", connection.AccessToken);
                var response = await _httpClient.PostAsync($"{_apiUrl}/{connection.PhoneNumberId}/messages", content);

                if (!response.IsSuccessStatusCode)
                {
                    var errorContent = await response.Content.ReadAsStringAsync();
                    throw new Exception($"Failed to send WhatsApp message: {errorContent}");
                }

                var responseContent = await response.Content.ReadAsStringAsync();
                var responseObj = JsonSerializer.Deserialize<WhatsAppMessageResponse>(responseContent);
                messageId = responseObj?.Messages?.FirstOrDefault()?.Id ?? Guid.NewGuid().ToString();

                var messageMetadata = new MessageMetadata
                {
                    UserId = userId,
                    PlatformType = "WhatsApp",
                    ExternalMessageId = messageId,
                    AccountIdentifier = connection.PhoneNumber,
                    Subject = "WhatsApp Message",
                    Snippet = message.Length > 100 ? message.Substring(0, 97) + "..." : message,
                    Body = message,
                    From = "You",
                    ReceivedAt = DateTime.UtcNow,
                    IsRead = true
                };

                _dbContext.MessageMetadatas.Add(messageMetadata);
            }

            await _dbContext.SaveChangesAsync();
        }

        public async Task<string> SendTemplateMessageAsync(int userId, string recipientPhoneNumber, string templateName, string languageCode, string phoneNumber)
        {
            var connection = await _dbContext.WhatsAppConnections
                .FirstOrDefaultAsync(c => c.UserId == userId && c.IsConnected && c.PhoneNumber == phoneNumber);

            if (connection == null)
                throw new Exception($"No WhatsApp account connected for phone number {phoneNumber}.");

            recipientPhoneNumber = FormatPhoneNumber(recipientPhoneNumber);
            var payload = new
            {
                messaging_product = "whatsapp",
                to = recipientPhoneNumber,
                type = "template",
                template = new
                {
                    name = templateName,
                    language = new { code = languageCode }
                }
            };

            var content = new StringContent(
                JsonSerializer.Serialize(payload),
                Encoding.UTF8,
                "application/json");

            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", connection.AccessToken);
            var response = await _httpClient.PostAsync($"{_apiUrl}/{connection.PhoneNumberId}/messages", content);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                throw new Exception($"Failed to send WhatsApp template message: {errorContent}");
            }

            var responseContent = await response.Content.ReadAsStringAsync();
            var responseObj = JsonSerializer.Deserialize<WhatsAppMessageResponse>(responseContent);
            var messageId = responseObj?.Messages?.FirstOrDefault()?.Id ?? Guid.NewGuid().ToString();

            var messageMetadata = new MessageMetadata
            {
                UserId = userId,
                PlatformType = "WhatsApp",
                ExternalMessageId = messageId,
                AccountIdentifier = connection.PhoneNumber,
                Subject = "WhatsApp Template Message",
                Snippet = $"Template: {templateName}",
                Body = $"Template: {templateName}",
                From = "You",
                ReceivedAt = DateTime.UtcNow,
                IsRead = true
            };

            _dbContext.MessageMetadatas.Add(messageMetadata);
            await _dbContext.SaveChangesAsync();
            return messageId;
        }

        public async Task ProcessIncomingMessageAsync(WhatsAppFullWebhookPayload payload)
        {
            try
            {
                foreach (var entry in payload.Entry)
                {
                    foreach (var change in entry.Changes)
                    {
                        await ProcessWhatsAppMessageChangeAsync(change);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing incoming WhatsApp message");
            }
        }

        private async Task ProcessWhatsAppMessageChangeAsync(WhatsAppChange change)
        {
            if (change?.Field != "messages" || change.Value?.Messages == null)
                return;

            var phoneNumberId = change.Value.Metadata?.PhoneNumberId;
            if (string.IsNullOrEmpty(phoneNumberId))
                return;

            var connection = await _dbContext.WhatsAppConnections
                .FirstOrDefaultAsync(c => c.PhoneNumberId == phoneNumberId && c.IsConnected);

            if (connection == null)
            {
                _logger.LogWarning("No user found for WhatsApp phone number ID: {PhoneNumberId}", phoneNumberId);
                return;
            }

            foreach (var message in change.Value.Messages)
            {
                var existingMessage = await _dbContext.MessageMetadatas
                    .AnyAsync(m => m.ExternalMessageId == message.Id);

                if (existingMessage)
                {
                    _logger.LogInformation("Skipping already processed message: {MessageId}", message.Id);
                    continue;
                }

                var contact = change.Value.Contacts?.FirstOrDefault();
                var contactName = contact?.Profile?.Name ?? message.From;

                var messageMetadata = new MessageMetadata
                {
                    UserId = connection.UserId,
                    PlatformType = "WhatsApp",
                    ExternalMessageId = message.Id,
                    AccountIdentifier = connection.PhoneNumber,
                    Subject = "WhatsApp Message",
                    Snippet = message.Text?.Body ?? "[Media Message]",
                    Body = message.Text?.Body ?? "[Media Message]",
                    From = contactName,
                    //FromNumber = message.From,
                    ReceivedAt = DateTimeOffset.FromUnixTimeSeconds(long.Parse(message.Timestamp)).DateTime,
                    IsRead = false
                };

                if (message.Type == "image" || message.Type == "video" || message.Type == "audio" || message.Type == "document")
                {
                    messageMetadata.HasAttachments = true;
                    var attachment = new MessageAttachment
                    {
                        AttachmentId = message.Image?.Id ?? message.Video?.Id ?? message.Audio?.Id ?? message.Document?.Id,
                        FileName = message.Document?.Filename ?? $"media_{message.Id}",
                        ContentType = message.Document?.MimeType ?? GetMimeType(message.Type),
                        Size = 0, // Size not provided in webhook
                        CreatedAt = DateTime.UtcNow
                    };
                    messageMetadata.Attachments = new List<MessageAttachment> { attachment };
                }

                _dbContext.MessageMetadatas.Add(messageMetadata);
            }

            await _dbContext.SaveChangesAsync();
        }

        public async Task MarkMessageAsReadAsync(int userId, string messageId, string phoneNumber)
        {
            var message = await _dbContext.MessageMetadatas
                .FirstOrDefaultAsync(m => m.UserId == userId && m.ExternalMessageId == messageId && m.AccountIdentifier == phoneNumber);

            if (message == null)
                throw new Exception($"Message with ID {messageId} not found");

            message.IsRead = true;
            await _dbContext.SaveChangesAsync();

            var connection = await _dbContext.WhatsAppConnections
                .FirstOrDefaultAsync(c => c.UserId == userId && c.IsConnected && c.PhoneNumber == phoneNumber);

            if (connection == null)
                return;

            var markReadRequest = new
            {
                messaging_product = "whatsapp",
                status = "read",
                message_id = messageId
            };

            var content = new StringContent(
                JsonSerializer.Serialize(markReadRequest),
                Encoding.UTF8,
                "application/json");

            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", connection.AccessToken);
            await _httpClient.PostAsync($"{_apiUrl}/{connection.PhoneNumberId}/messages", content);
        }

        public async Task<(byte[] Content, string ContentType, string FileName)> GetAttachmentAsync(int userId, string messageId, string attachmentId, string phoneNumber)
        {
            var message = await _dbContext.MessageMetadatas
                .Include(m => m.Attachments)
                .FirstOrDefaultAsync(m => m.UserId == userId && m.ExternalMessageId == messageId && m.AccountIdentifier == phoneNumber);

            if (message == null)
                throw new Exception($"Message with ID {messageId} not found");

            var attachment = message.Attachments?.FirstOrDefault(a => a.AttachmentId == attachmentId);
            if (attachment == null)
                throw new Exception($"Attachment with ID {attachmentId} not found");

            if (attachment.Content != null)
            {
                return (attachment.Content, attachment.ContentType, attachment.FileName);
            }

            var connection = await _dbContext.WhatsAppConnections
                .FirstOrDefaultAsync(c => c.UserId == userId && c.IsConnected && c.PhoneNumber == phoneNumber);

            if (connection == null)
                throw new Exception($"No WhatsApp account connected for phone number {phoneNumber}.");

            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", connection.AccessToken);
            var mediaResponse = await _httpClient.GetAsync($"{_apiUrl}/{attachmentId}");

            if (!mediaResponse.IsSuccessStatusCode)
            {
                var errorContent = await mediaResponse.Content.ReadAsStringAsync();
                throw new Exception($"Failed to retrieve WhatsApp media: {errorContent}");
            }

            var mediaInfo = JsonSerializer.Deserialize<WhatsAppMediaResponse>(await mediaResponse.Content.ReadAsStringAsync());
            var mediaUrl = mediaInfo.Url;

            var contentResponse = await _httpClient.GetAsync(mediaUrl);
            if (!contentResponse.IsSuccessStatusCode)
            {
                var errorContent = await contentResponse.Content.ReadAsStringAsync();
                throw new Exception($"Failed to download WhatsApp media: {errorContent}");
            }

            var content = await contentResponse.Content.ReadAsByteArrayAsync();
            if (content.Length < 1024 * 1024)
            {
                attachment.Content = content;
                await _dbContext.SaveChangesAsync();
            }

            return (content, attachment.ContentType, attachment.FileName);
        }

        private string FormatPhoneNumber(string phoneNumber)
        {
            var digitsOnly = new string(phoneNumber.Where(char.IsDigit).ToArray());
            if (!digitsOnly.StartsWith("1") && digitsOnly.Length == 10)
            {
                return $"+1{digitsOnly}";
            }
            if (!digitsOnly.StartsWith("+"))
            {
                return $"+{digitsOnly}";
            }
            return digitsOnly;
        }

        private string GetMediaType(string contentType)
        {
            if (contentType.StartsWith("image")) return "image";
            if (contentType.StartsWith("video")) return "video";
            if (contentType.StartsWith("audio")) return "audio";
            return "document";
        }

        private string GetMimeType(string type)
        {
            return type switch
            {
                "image" => "image/jpeg",
                "video" => "video/mp4",
                "audio" => "audio/mp3",
                "document" => "application/pdf",
                _ => "application/octet-stream"
            };
        }

        private async Task<byte[]> ReadStreamToBytesAsync(Stream stream)
        {
            using var memoryStream = new MemoryStream();
            await stream.CopyToAsync(memoryStream);
            return memoryStream.ToArray();
        }
    }

    public class WhatsAppMessageResponse
    {
        public List<WhatsAppMessage> Messages { get; set; }
    }

    public class WhatsAppMessage
    {
        public string Id { get; set; }
    }

    public class WhatsAppMediaUploadResponse
    {
        public string Id { get; set; }
    }

    public class WhatsAppMediaResponse
    {
        public string Url { get; set; }
        public string MimeType { get; set; }
        public string Id { get; set; }
    }

    public class WhatsAppFullWebhookPayload
    {
        public string Object { get; set; }
        public List<WhatsAppEntry> Entry { get; set; }
    }

    public class WhatsAppEntry
    {
        public string Id { get; set; }
        public List<WhatsAppChange> Changes { get; set; }
    }

    public class WhatsAppChange
    {
        public string Field { get; set; }
        public WhatsAppChangeValue Value { get; set; }
    }

    public class WhatsAppChangeValue
    {
        public string MessagingProduct { get; set; }
        public WhatsAppMetadata Metadata { get; set; }
        public List<WhatsAppContact> Contacts { get; set; }
        public List<WhatsAppWebhookMessage> Messages { get; set; }
    }

    public class WhatsAppMetadata
    {
        public string DisplayPhoneNumber { get; set; }
        public string PhoneNumberId { get; set; }
    }

    public class WhatsAppContact
    {
        public WhatsAppProfile Profile { get; set; }
        public string WaId { get; set; }
    }

    public class WhatsAppProfile
    {
        public string Name { get; set; }
    }

    public class WhatsAppWebhookMessage
    {
        public string From { get; set; }
        public string Id { get; set; }
        public string Timestamp { get; set; }
        public string Type { get; set; }
        public WhatsAppText Text { get; set; }
        public WhatsAppMedia Image { get; set; }
        public WhatsAppMedia Video { get; set; }
        public WhatsAppMedia Audio { get; set; }
        public WhatsAppMedia Document { get; set; }
    }

    public class WhatsAppText
    {
        public string Body { get; set; }
    }

    public class WhatsAppMedia
    {
        public string Id { get; set; }
        public string MimeType { get; set; }
        public string Filename { get; set; }
    }
}