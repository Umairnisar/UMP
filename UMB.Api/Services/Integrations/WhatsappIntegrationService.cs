using Microsoft.EntityFrameworkCore;
using UMB.Model.Models;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using System.Net.Http.Headers;
using System.Text.Json.Serialization;

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

        // Validate WhatsApp credentials by making a simple API call
        public async Task<bool> ValidateCredentialsAsync(string phoneNumberId, string accessToken, string phoneNumber)
        {
            try
            {
                // Create a temporary HttpClient with the provided token
                using var tempClient = new HttpClient();
                tempClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

                // Call a simple endpoint to verify credentials - use the business profile info endpoint
                var response = await tempClient.GetAsync($"{_apiUrl}/{phoneNumberId}/whatsapp_business_profile");

                // Return true if the call was successful
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error validating WhatsApp credentials");
                return false;
            }
        }

        // Get messages from database
        public async Task<List<MessageMetadata>> FetchMessagesAsync(int userId)
        {
            try
            {
                // Check if user has a WhatsApp connection
                var connection = await _dbContext.WhatsAppConnections
                    .FirstOrDefaultAsync(c => c.UserId == userId && c.IsConnected);

                if (connection == null)
                    return new List<MessageMetadata>();

                // Get messages from our database
                var messages = await _dbContext.MessageMetadatas
                    .Where(m => m.UserId == userId && m.PlatformType == "WhatsApp")
                    .OrderByDescending(m => m.ReceivedAt)
                    .Take(50) // Limit to recent messages
                    .ToListAsync();

                return messages;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching WhatsApp messages for user {UserId}", userId);
                return new List<MessageMetadata>();
            }
        }

        // Send a WhatsApp text message
        public async Task<string> SendMessageAsync(int userId, string message, string recipientPhoneNumber)
        {
            try
            {
                var connection = await _dbContext.WhatsAppConnections
                    .FirstOrDefaultAsync(c => c.UserId == userId && c.IsConnected);

                if (connection == null)
                    throw new Exception("No WhatsApp account connected.");

                // Format the recipient phone number to WhatsApp format if needed
                recipientPhoneNumber = FormatPhoneNumber(recipientPhoneNumber);

                // Prepare message payload
                var payload = new
                {
                    messaging_product = "whatsapp",
                    recipient_type = "individual",
                    to = recipientPhoneNumber,
                    type = "text",
                    text = new
                    {
                        body = message
                    }
                };

                var content = new StringContent(
                    JsonSerializer.Serialize(payload),
                    Encoding.UTF8,
                    "application/json");

                // Create a new HttpClient for this request
                using var client = new HttpClient();
                client.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", connection.AccessToken);

                var response = await client.PostAsync(
                    $"{_apiUrl}/{connection.PhoneNumberId}/messages",
                    content);

                if (!response.IsSuccessStatusCode)
                {
                    var errorContent = await response.Content.ReadAsStringAsync();
                    throw new Exception($"Failed to send WhatsApp message: {errorContent}");
                }

                var responseContent = await response.Content.ReadAsStringAsync();
                var responseObj = JsonSerializer.Deserialize<WhatsAppMessageResponse>(responseContent);

                var messageId = responseObj?.messages?.FirstOrDefault()?.id ?? Guid.NewGuid().ToString();

                // Save to our database
                var messageMetadata = new MessageMetadata
                {
                    UserId = userId,
                    PlatformType = "WhatsApp",
                    ExternalMessageId = messageId,
                    Subject = "WhatsApp Message",
                    Snippet = message,
                    Body = message,
                    From = "You", // Indicating it's an outgoing message
                    ReceivedAt = DateTime.UtcNow,
                    IsRead = true // Messages sent by the user are already read
                };

                _dbContext.MessageMetadatas.Add(messageMetadata);
                await _dbContext.SaveChangesAsync();

                return messageId;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending WhatsApp message for user {UserId}", userId);
                throw;
            }
        }

        // Send a WhatsApp template message
        public async Task<string> SendTemplateMessageAsync(int userId, string recipientPhoneNumber, string templateName, string languageCode)
        {
            try
            {
                var connection = await _dbContext.WhatsAppConnections
                    .FirstOrDefaultAsync(c => c.UserId == userId && c.IsConnected);

                if (connection == null)
                    throw new Exception("No WhatsApp account connected.");

                // Format the recipient phone number to WhatsApp format if needed
                recipientPhoneNumber = FormatPhoneNumber(recipientPhoneNumber);

                // Prepare message payload for template
                var payload = new
                {
                    messaging_product = "whatsapp",
                    to = recipientPhoneNumber,
                    type = "template",
                    template = new
                    {
                        name = templateName,
                        language = new
                        {
                            code = languageCode
                        }
                    }
                };

                var content = new StringContent(
                    JsonSerializer.Serialize(payload),
                    Encoding.UTF8,
                    "application/json");

                // Create a new HttpClient for this request
                using var client = new HttpClient();
                client.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", connection.AccessToken);

                var response = await client.PostAsync(
                    $"{_apiUrl}/{connection.PhoneNumberId}/messages",
                    content);

                if (!response.IsSuccessStatusCode)
                {
                    var errorContent = await response.Content.ReadAsStringAsync();
                    throw new Exception($"Failed to send WhatsApp template message: {errorContent}");
                }

                var responseContent = await response.Content.ReadAsStringAsync();
                var responseObj = JsonSerializer.Deserialize<WhatsAppMessageResponse>(responseContent);

                var messageId = responseObj?.messages?.FirstOrDefault()?.id ?? Guid.NewGuid().ToString();

                // Save to our database
                var messageMetadata = new MessageMetadata
                {
                    UserId = userId,
                    PlatformType = "WhatsApp",
                    ExternalMessageId = messageId,
                    Subject = "WhatsApp Template Message",
                    Snippet = $"Template: {templateName}",
                    Body = $"Template: {templateName}",
                    From = "You", // Indicating it's an outgoing message
                    ReceivedAt = DateTime.UtcNow,
                    IsRead = true // Messages sent by the user are already read
                };

                _dbContext.MessageMetadatas.Add(messageMetadata);
                await _dbContext.SaveChangesAsync();

                return messageId;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending WhatsApp template message for user {UserId}", userId);
                throw;
            }
        }

        // Process a message received via webhook
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

        // Process a single WhatsApp message change
        public async Task ProcessWhatsAppMessageChangeAsync(WhatsAppChange change)
        {
            try
            {
                if (change?.Field != "messages" || change.Value?.Messages == null)
                    return;

                var phoneNumberId = change.Value.Metadata?.PhoneNumberId;
                if (string.IsNullOrEmpty(phoneNumberId))
                    return;

                // Find the user associated with this phone number ID
                var connection = await _dbContext.WhatsAppConnections
                    .FirstOrDefaultAsync(c => c.PhoneNumberId == phoneNumberId && c.IsConnected);

                if (connection == null)
                {
                    _logger.LogWarning("No user found for WhatsApp phone number ID: {PhoneNumberId}", phoneNumberId);
                    return;
                }

                foreach (var message in change.Value.Messages)
                {
                    // Skip if we've already processed this message
                    var existingMessage = await _dbContext.MessageMetadatas
                        .AnyAsync(m => m.ExternalMessageId == message.Id);

                    if (existingMessage)
                    {
                        _logger.LogInformation("Skipping already processed message: {MessageId}", message.Id);
                        continue;
                    }

                    // Get contact info for the sender
                    var contact = change.Value.Contacts?.FirstOrDefault();
                    var contactName = contact?.Profile?.Name ?? message.From;

                    // Create a message metadata
                    var messageMetadata = new MessageMetadata
                    {
                        UserId = connection.UserId,
                        PlatformType = "WhatsApp",
                        ExternalMessageId = message.Id,
                        Subject = "WhatsApp Message",
                        Snippet = message.Text?.Body ?? "[Media Message]",
                        Body = message.Text?.Body ?? "[Media Message]",
                        From = contactName,
                        fromNumber = message.From,
                        ReceivedAt = DateTimeOffset.FromUnixTimeSeconds(long.Parse(message.Timestamp)).DateTime,
                        IsRead = false
                    };

                    // Save to database
                    _dbContext.MessageMetadatas.Add(messageMetadata);
                }

                await _dbContext.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing WhatsApp message change: {Message}", ex.Message);
            }
        }
        // Mark a message as read
        public async Task MarkMessageAsReadAsync(int userId, string messageId)
        {
            try
            {
                // Find the message in the database
                var message = await _dbContext.MessageMetadatas
                    .FirstOrDefaultAsync(m => m.UserId == userId && m.ExternalMessageId == messageId);

                if (message == null)
                    throw new Exception($"Message with ID {messageId} not found");

                // Update the read status in our database
                message.IsRead = true;
                await _dbContext.SaveChangesAsync();

                // Get the connection details
                var connection = await _dbContext.WhatsAppConnections
                    .FirstOrDefaultAsync(c => c.UserId == userId && c.IsConnected);

                if (connection == null)
                    return; // No need to update on WhatsApp API if connection is not available

                // Send mark as read to WhatsApp API
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

                // Create a new HttpClient for this request
                using var client = new HttpClient();
                client.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", connection.AccessToken);

                await client.PostAsync(
                    $"{_apiUrl}/{connection.PhoneNumberId}/messages",
                    content);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error marking WhatsApp message as read: {MessageId}", messageId);
                throw;
            }
        }

        // Helper to format phone number for WhatsApp API
        private string FormatPhoneNumber(string phoneNumber)
        {
            // Remove any non-digit characters
            var digitsOnly = new string(phoneNumber.Where(char.IsDigit).ToArray());

            // Ensure it has country code
            if (!digitsOnly.StartsWith("1") && digitsOnly.Length == 10)
            {
                // Assume US number and add +1
                return $"+1{digitsOnly}";
            }

            if (!digitsOnly.StartsWith("+"))
            {
                return $"+{digitsOnly}";
            }

            return digitsOnly;
        }

        private string GetSenderName(List<WhatsAppContact> contacts)
        {
            if (contacts == null || contacts.Count == 0) return "Unknown";

            var contact = contacts[0];
            return contact.Profile?.Name ?? contact.WaId ?? "Unknown";
        }
    }

    // DTO classes for JSON responses
    public class WhatsAppFullWebhookPayload
    {
        [JsonPropertyName("object")]
        public string Object { get; set; }

        [JsonPropertyName("entry")]
        public List<WhatsAppEntry> Entry { get; set; }
    }

    public class WhatsAppEntry
    {
        [JsonPropertyName("id")]
        public string Id { get; set; }

        [JsonPropertyName("changes")]
        public List<WhatsAppChange> Changes { get; set; }
    }

    public class WhatsAppChange
    {
        [JsonPropertyName("field")]
        public string Field { get; set; }

        [JsonPropertyName("value")]
        public WhatsAppValue Value { get; set; }
    }

    public class WhatsAppValue
    {
        [JsonPropertyName("messaging_product")]
        public string MessagingProduct { get; set; }

        [JsonPropertyName("metadata")]
        public WhatsAppMetadata Metadata { get; set; }

        [JsonPropertyName("contacts")]
        public List<WhatsAppContact> Contacts { get; set; }

        [JsonPropertyName("messages")]
        public List<WhatsAppMessage> Messages { get; set; }
    }

    public class WhatsAppMetadata
    {
        [JsonPropertyName("display_phone_number")]
        public string DisplayPhoneNumber { get; set; }

        [JsonPropertyName("phone_number_id")]
        public string PhoneNumberId { get; set; }
    }

    public class WhatsAppContact
    {
        [JsonPropertyName("profile")]
        public WhatsAppProfile Profile { get; set; }

        [JsonPropertyName("wa_id")]
        public string WaId { get; set; }
    }

    public class WhatsAppProfile
    {
        [JsonPropertyName("name")]
        public string Name { get; set; }
    }

    public class WhatsAppMessage
    {
        [JsonPropertyName("from")]
        public string From { get; set; }

        [JsonPropertyName("id")]
        public string Id { get; set; }

        [JsonPropertyName("timestamp")]
        public string Timestamp { get; set; }

        [JsonPropertyName("type")]
        public string Type { get; set; }

        [JsonPropertyName("text")]
        public WhatsAppMessageText Text { get; set; }
    }

    public class WhatsAppMessageText
    {
        [JsonPropertyName("body")]
        public string Body { get; set; }
    }

    // Message response model
    public class WhatsAppMessageResponse
    {
        [JsonPropertyName("messaging_product")]
        public string messaging_product { get; set; }

        [JsonPropertyName("contacts")]
        public List<WhatsAppResponseContact> contacts { get; set; }

        [JsonPropertyName("messages")]
        public List<WhatsAppResponseMessage> messages { get; set; }

        public class WhatsAppResponseContact
        {
            [JsonPropertyName("input")]
            public string input { get; set; }

            [JsonPropertyName("wa_id")]
            public string wa_id { get; set; }
        }

        public class WhatsAppResponseMessage
        {
            [JsonPropertyName("id")]
            public string id { get; set; }
        }
    }
}