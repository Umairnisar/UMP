using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using UMB.Api.Services.Integrations;
using UMB.Model.Models;

namespace UMB.Api.Controllers
{
    [ApiController]
    [Route("api/whatsapp")]
    public class WhatsAppController : ControllerBase
    {
        private readonly IWhatsAppIntegrationService _whatsAppService;
        private readonly AppDbContext _dbContext;
        private readonly IConfiguration _configuration;
        private readonly ILogger<WhatsAppController> _logger;

        public WhatsAppController(
            IWhatsAppIntegrationService whatsAppService,
            AppDbContext dbContext,
            IConfiguration configuration,
            ILogger<WhatsAppController> logger)
        {
            _whatsAppService = whatsAppService;
            _dbContext = dbContext;
            _configuration = configuration;
            _logger = logger;
        }

        // Endpoint to connect WhatsApp with provided credentials
        [HttpPost("connect")]
        [Authorize]
        public async Task<IActionResult> ConnectWhatsApp([FromBody] WhatsAppCredentialsRequest request)
        {
            try
            {
                var userId = int.Parse(User.Claims.First(c => c.Type == "userId").Value);

                // First, try to validate the credentials
                var isValid = await _whatsAppService.ValidateCredentialsAsync(
                    request.PhoneNumberId,
                    request.AccessToken,
                    request.PhoneNumber
                );

                if (!isValid)
                {
                    return BadRequest(new
                    {
                        success = false,
                        message = "Invalid WhatsApp Business API credentials. Please check and try again."
                    });
                }

                // 1. First, store the WhatsApp-specific data in WhatsAppConnections
                var existingConnection = await _dbContext.WhatsAppConnections
                    .FirstOrDefaultAsync(c => c.UserId == userId);

                if (existingConnection != null)
                {
                    // Update existing connection
                    existingConnection.PhoneNumberId = request.PhoneNumberId;
                    existingConnection.AccessToken = request.AccessToken;
                    existingConnection.PhoneNumber = request.PhoneNumber;
                    existingConnection.BusinessName = request.BusinessName;
                    existingConnection.IsConnected = true;
                    existingConnection.UpdatedAt = DateTime.UtcNow;
                }
                else
                {
                    // Create new connection
                    var newConnection = new WhatsAppConnection
                    {
                        UserId = userId,
                        PhoneNumberId = request.PhoneNumberId,
                        AccessToken = request.AccessToken,
                        PhoneNumber = request.PhoneNumber,
                        BusinessName = request.BusinessName,
                        IsConnected = true,
                        CreatedAt = DateTime.UtcNow,
                        UpdatedAt = DateTime.UtcNow
                    };

                    _dbContext.WhatsAppConnections.Add(newConnection);
                }

                // 2. Now, also store in PlatformAccount for consistency
                var platformAccount = await _dbContext.PlatformAccounts
                    .FirstOrDefaultAsync(pa => pa.UserId == userId && pa.PlatformType == "WhatsApp");

                if (platformAccount != null)
                {
                    // Update existing entry
                    platformAccount.AccessToken = request.AccessToken;
                    platformAccount.ExternalAccountId = request.PhoneNumberId;
                    platformAccount.TokenExpiresAt = DateTime.UtcNow.AddDays(60); // Long expiry for API token
                }
                else
                {
                    // Create new platform account entry
                    platformAccount = new PlatformAccount
                    {
                        UserId = userId,
                        PlatformType = "WhatsApp",
                        AccessToken = request.AccessToken,
                        RefreshToken = null, // WhatsApp doesn't use refresh tokens
                        ExternalAccountId = request.PhoneNumberId,
                        TokenExpiresAt = DateTime.UtcNow.AddDays(60) // Long expiry for API token
                    };

                    _dbContext.PlatformAccounts.Add(platformAccount);
                }

                await _dbContext.SaveChangesAsync();

                // If saving was successful, generate a platform ID for the response
                var platformId = platformAccount?.Id.ToString() ?? Guid.NewGuid().ToString();

                // Return success response with platform info for frontend
                return Ok(new
                {
                    success = true,
                    platform = new
                    {
                        id = platformId,
                        type = "whatsapp",
                        name = string.IsNullOrEmpty(request.BusinessName) ? "WhatsApp" : request.BusinessName,
                        isConnected = true,
                        userId = userId.ToString(),
                        phoneNumber = request.PhoneNumber
                    },
                    message = "Successfully connected to WhatsApp Business API."
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error connecting to WhatsApp");
                return StatusCode(500, new
                {
                    success = false,
                    message = $"Error connecting to WhatsApp: {ex.Message}"
                });
            }
        }

        [HttpDelete("disconnect")]
        [Authorize]
        public async Task<IActionResult> DisconnectWhatsApp()
        {
            try
            {
                var userId = int.Parse(User.Claims.First(c => c.Type == "userId").Value);

                // 1. Update WhatsAppConnection
                var connection = await _dbContext.WhatsAppConnections
                    .FirstOrDefaultAsync(c => c.UserId == userId);

                if (connection != null)
                {
                    connection.IsConnected = false;
                    connection.UpdatedAt = DateTime.UtcNow;
                }

                // 2. Remove or update PlatformAccount entry
                var platformAccount = await _dbContext.PlatformAccounts
                    .FirstOrDefaultAsync(pa => pa.UserId == userId && pa.PlatformType == "WhatsApp");

                if (platformAccount != null)
                {
                    // Option 1: Remove the entry completely
                    _dbContext.PlatformAccounts.Remove(platformAccount);

                    // Option 2: Update the entry to show as disconnected (if your system supports that)
                    // platformAccount.TokenExpiresAt = DateTime.UtcNow.AddDays(-1); // Set as expired
                }

                await _dbContext.SaveChangesAsync();

                return Ok(new
                {
                    success = true,
                    message = "Successfully disconnected from WhatsApp Business API."
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error disconnecting from WhatsApp");
                return StatusCode(500, new
                {
                    success = false,
                    message = $"Error disconnecting from WhatsApp: {ex.Message}"
                });
            }
        }
        // Endpoint to send a WhatsApp message
        [HttpPost("send")]
        [Authorize]
        public async Task<IActionResult> SendMessage([FromBody] WhatsAppSendRequest request)
        {
            try
            {
                var userId = int.Parse(User.Claims.First(c => c.Type == "userId").Value);

                // Get user's WhatsApp connection
                var connection = await _dbContext.WhatsAppConnections
                    .FirstOrDefaultAsync(c => c.UserId == userId && c.IsConnected);

                if (connection == null)
                {
                    return BadRequest(new
                    {
                        success = false,
                        message = "WhatsApp is not connected. Please connect WhatsApp first."
                    });
                }

                string messageId;

                if (!string.IsNullOrEmpty(request.TemplateName))
                {
                    // Send template message
                    messageId = await _whatsAppService.SendTemplateMessageAsync(
                        userId,
                        request.To,
                        request.TemplateName,
                        request.LanguageCode ?? "en_US");
                }
                else
                {
                    // Send text message
                    messageId = await _whatsAppService.SendMessageAsync(
                        userId,
                        request.Body,
                        request.To);
                }

                return Ok(new
                {
                    success = true,
                    messageId,
                    status = "sent"
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending WhatsApp message");
                return StatusCode(500, new
                {
                    success = false,
                    message = $"Error sending WhatsApp message: {ex.Message}"
                });
            }
        }

        // Retrieve messages from the database
        [HttpGet("messages")]
        [Authorize]
        public async Task<IActionResult> GetMessages()
        {
            try
            {
                var userId = int.Parse(User.Claims.First(c => c.Type == "userId").Value);

                var messages = await _dbContext.MessageMetadatas
                    .Where(m => m.UserId == userId && m.PlatformType == "WhatsApp")
                    .OrderByDescending(m => m.ReceivedAt)
                    .Take(50)
                    .ToListAsync();

                return Ok(new
                {
                    success = true,
                    messages
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving WhatsApp messages");
                return StatusCode(500, new
                {
                    success = false,
                    message = $"Error retrieving WhatsApp messages: {ex.Message}"
                });
            }
        }

        // Webhook to receive WhatsApp messages
        [HttpPost("webhook")]
        public async Task<IActionResult> ReceiveWebhook()
        {
            try
            {
                // Read the request body
                using var reader = new StreamReader(Request.Body, Encoding.UTF8);
                var body = await reader.ReadToEndAsync();

                _logger.LogInformation("Received WhatsApp webhook payload: {Payload}", body);

                try
                {
                    // Parse the webhook payload
                    var options = new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true,
                        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                    };

                    // First check if it's a full webhook payload or just a change object
                    var isFullPayload = body.Contains("\"object\"") || body.Contains("\"Object\"");

                    if (isFullPayload)
                    {
                        var fullPayload = JsonSerializer.Deserialize<WhatsAppFullWebhookPayload>(body, options);

                        if (fullPayload?.Entry != null)
                        {
                            foreach (var entry in fullPayload.Entry)
                            {
                                if (entry.Changes != null)
                                {
                                    foreach (var change in entry.Changes)
                                    {
                                        await _whatsAppService.ProcessWhatsAppMessageChangeAsync(change);
                                    }
                                }
                            }
                        }
                    }
                    else
                    {
                        // Try to parse as just a change object
                        var change = JsonSerializer.Deserialize<WhatsAppChange>(body, options);
                        await _whatsAppService.ProcessWhatsAppMessageChangeAsync(change);
                    }
                }
                catch (JsonException ex)
                {
                    _logger.LogError(ex, "Error parsing webhook payload: {Body}", body);
                }

                // Always return OK for webhook - Meta expects a 200 response
                return Ok();
            }
            catch (Exception ex)
            {
                // Log the error but return OK to acknowledge receipt
                _logger.LogError(ex, "Error processing webhook: {Message}", ex.Message);
                return Ok();
            }
        }

        // Webhook verification endpoint
        [HttpGet("webhook")]
        public IActionResult VerifyWebhook([FromQuery(Name = "hub.mode")] string hub_mode, [FromQuery(Name = "hub.verify_token")] string hub_verify_token, [FromQuery(Name = "hub.challenge")] string hub_challenge)
        {
            _logger.LogInformation("Verifying webhook with mode: {Mode}, token: {Token}", hub_mode, hub_verify_token);

            var verifyToken = _configuration["WhatsAppSettings:WebhookVerifyToken"];

            if (hub_mode == "subscribe" && hub_verify_token == verifyToken)
            {
                _logger.LogInformation("Webhook verification successful");
                return Ok(hub_challenge);
            }

            _logger.LogWarning("Webhook verification failed");
            return BadRequest();
        }

        // Mark a message as read
        [HttpPost("messages/{messageId}/read")]
        [Authorize]
        public async Task<IActionResult> MarkAsRead(string messageId)
        {
            try
            {
                var userId = int.Parse(User.Claims.First(c => c.Type == "userId").Value);

                await _whatsAppService.MarkMessageAsReadAsync(userId, messageId);

                return Ok(new { success = true });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error marking message as read: {MessageId}", messageId);
                return StatusCode(500, new
                {
                    success = false,
                    message = $"Error marking message as read: {ex.Message}"
                });
            }
        }
    }

    public class WhatsAppCredentialsRequest
    {
        public string PhoneNumberId { get; set; }
        public string AccessToken { get; set; }
        public string PhoneNumber { get; set; }
        public string BusinessName { get; set; }
    }

    public class WhatsAppSendRequest
    {
        public string To { get; set; }
        public string Body { get; set; }
        public string? TemplateName { get; set; }
        public string? LanguageCode { get; set; }
    }
}