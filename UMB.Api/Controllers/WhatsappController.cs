using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using UMB.Api.Services.Integrations;
using UMB.Model.Models;

namespace UMB.Api.Controllers
{
    [ApiController]
    [Route("api/whatsapp")]
    [Authorize]
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

        [HttpPost("connect")]
        public async Task<IActionResult> ConnectWhatsApp([FromBody] WhatsAppCredentialsRequest request)
        {
            try
            {
                if (string.IsNullOrEmpty(request.PhoneNumber))
                    return BadRequest("PhoneNumber is required.");

                var userId = GetCurrentUserId();

                // Validate credentials
                var isValid = await _whatsAppService.ValidateCredentialsAsync(
                    request.PhoneNumberId,
                    request.AccessToken,
                    request.PhoneNumber);

                if (!isValid)
                    return BadRequest(new { success = false, message = "Invalid WhatsApp Business API credentials." });

                // Store in WhatsAppConnections
                var existingConnection = await _dbContext.WhatsAppConnections
                    .FirstOrDefaultAsync(c => c.UserId == userId && c.PhoneNumber == request.PhoneNumber);

                if (existingConnection != null)
                {
                    // Update existing connection
                    existingConnection.PhoneNumberId = request.PhoneNumberId;
                    existingConnection.AccessToken = request.AccessToken;
                    existingConnection.BusinessName = request.BusinessName;
                    existingConnection.IsConnected = true;
                    existingConnection.IsActive = true; // Set as active by default
                    existingConnection.UpdatedAt = DateTime.UtcNow;

                    // Deactivate other WhatsApp accounts
                    await _dbContext.WhatsAppConnections
                        .Where(c => c.UserId == userId && c.PhoneNumber != request.PhoneNumber)
                        .ExecuteUpdateAsync(c => c.SetProperty(x => x.IsActive, false));
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
                        IsActive = true, // Set as active by default
                        CreatedAt = DateTime.UtcNow,
                        UpdatedAt = DateTime.UtcNow
                    };

                    _dbContext.WhatsAppConnections.Add(newConnection);

                    // Deactivate other WhatsApp accounts
                    await _dbContext.WhatsAppConnections
                        .Where(c => c.UserId == userId)
                        .ExecuteUpdateAsync(c => c.SetProperty(x => x.IsActive, false));
                }

                // Store in PlatformAccount for consistency
                var platformAccount = await _dbContext.PlatformAccounts
                    .FirstOrDefaultAsync(pa => pa.UserId == userId && pa.AccountIdentifier == request.PhoneNumber && pa.PlatformType == "WhatsApp");

                if (platformAccount != null)
                {
                    // Update existing entry
                    platformAccount.AccessToken = request.AccessToken;
                    platformAccount.ExternalAccountId = request.PhoneNumberId;
                    platformAccount.AccountIdentifier = request.PhoneNumber;
                    platformAccount.IsActive = true;
                    platformAccount.TokenExpiresAt = DateTime.UtcNow.AddDays(60);
                }
                else
                {
                    // Create new platform account entry
                    platformAccount = new PlatformAccount
                    {
                        UserId = userId,
                        PlatformType = "WhatsApp",
                        AccountIdentifier = request.PhoneNumber,
                        AccessToken = request.AccessToken,
                        RefreshToken = null,
                        ExternalAccountId = request.PhoneNumberId,
                        IsActive = true,
                        TokenExpiresAt = DateTime.UtcNow.AddDays(60)
                    };

                    _dbContext.PlatformAccounts.Add(platformAccount);

                    // Deactivate other WhatsApp PlatformAccounts
                    await _dbContext.PlatformAccounts
                        .Where(pa => pa.UserId == userId && pa.PlatformType == "WhatsApp" && pa.AccountIdentifier != request.PhoneNumber)
                        .ExecuteUpdateAsync(pa => pa.SetProperty(x => x.IsActive, false));
                }

                await _dbContext.SaveChangesAsync();

                var platformId = platformAccount.Id.ToString();

                return Ok(new
                {
                    success = true,
                    platform = new
                    {
                        id = platformId,
                        type = "whatsapp",
                        name = string.IsNullOrEmpty(request.BusinessName) ? "WhatsApp" : request.BusinessName,
                        accountIdentifier = request.PhoneNumber,
                        isConnected = true,
                        isActive = true,
                        userId = userId.ToString(),
                        phoneNumber = request.PhoneNumber
                    },
                    message = "Successfully connected to WhatsApp Business API."
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error connecting to WhatsApp");
                return StatusCode(500, new { success = false, message = $"Error connecting to WhatsApp: {ex.Message}" });
            }
        }

        [HttpGet("accounts")]
        public async Task<IActionResult> GetWhatsAppAccounts()
        {
            var userId = GetCurrentUserId();
            var accounts = await _dbContext.WhatsAppConnections
                .Where(c => c.UserId == userId && c.IsConnected)
                .ToListAsync();

            var result = accounts.Select(c => new
            {
                id = c.Id.ToString(),
                phoneNumber = c.PhoneNumber,
                businessName = c.BusinessName,
                isActive = c.IsActive,
                isConnected = c.IsConnected,
                userId = c.UserId.ToString()
            });

            return Ok(result);
        }

        [HttpPost("switch")]
        public async Task<IActionResult> SwitchWhatsAppAccount([FromBody] SwitchAccountRequest request)
        {
            if (string.IsNullOrEmpty(request.AccountIdentifier))
                return BadRequest("PhoneNumber is required.");

            var userId = GetCurrentUserId();
            var connection = await _dbContext.WhatsAppConnections
                .FirstOrDefaultAsync(c => c.UserId == userId && c.PhoneNumber == request.AccountIdentifier && c.IsConnected);

            if (connection == null)
                return NotFound("WhatsApp account not found.");

            // Update WhatsAppConnections
            await _dbContext.WhatsAppConnections
                .Where(c => c.UserId == userId)
                .ExecuteUpdateAsync(c => c.SetProperty(x => x.IsActive, false));

            connection.IsActive = true;
            connection.UpdatedAt = DateTime.UtcNow;

            // Update PlatformAccounts
            await _dbContext.PlatformAccounts
                .Where(pa => pa.UserId == userId && pa.PlatformType == "WhatsApp")
                .ExecuteUpdateAsync(pa => pa.SetProperty(x => x.IsActive, false));

            var platformAccount = await _dbContext.PlatformAccounts
                .FirstOrDefaultAsync(pa => pa.UserId == userId && pa.AccountIdentifier == request.AccountIdentifier && pa.PlatformType == "WhatsApp");

            if (platformAccount != null)
                platformAccount.IsActive = true;

            await _dbContext.SaveChangesAsync();

            return Ok($"Active WhatsApp account switched to {request.AccountIdentifier}.");
        }

        [HttpDelete("disconnect/{phoneNumber}")]
        public async Task<IActionResult> DisconnectWhatsApp(string phoneNumber)
        {
            try
            {
                var userId = GetCurrentUserId();

                // Update WhatsAppConnection
                var connection = await _dbContext.WhatsAppConnections
                    .FirstOrDefaultAsync(c => c.UserId == userId && c.PhoneNumber == phoneNumber);

                if (connection != null)
                {
                    connection.IsConnected = false;
                    connection.IsActive = false;
                    connection.UpdatedAt = DateTime.UtcNow;
                }

                // Update PlatformAccount
                var platformAccount = await _dbContext.PlatformAccounts
                    .FirstOrDefaultAsync(pa => pa.UserId == userId && pa.AccountIdentifier == phoneNumber && pa.PlatformType == "WhatsApp");

                if (platformAccount != null)
                {
                    _dbContext.PlatformAccounts.Remove(platformAccount);
                }

                await _dbContext.SaveChangesAsync();

                return Ok(new { success = true, message = $"WhatsApp account {phoneNumber} disconnected." });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error disconnecting WhatsApp");
                return StatusCode(500, new { success = false, message = $"Error disconnecting WhatsApp: {ex.Message}" });
            }
        }

        [HttpPost("send")]
        public async Task<IActionResult> SendMessage([FromBody] WhatsAppSendRequest request)
        {
            try
            {
                var userId = GetCurrentUserId();
                var connection = await _dbContext.WhatsAppConnections
                    .FirstOrDefaultAsync(c => c.UserId == userId && c.IsConnected && c.IsActive);

                if (connection == null)
                    return BadRequest(new { success = false, message = "No active WhatsApp account connected." });

                string messageId;

                if (!string.IsNullOrEmpty(request.TemplateName))
                {
                    messageId = await _whatsAppService.SendTemplateMessageAsync(
                        userId,
                        request.To,
                        request.TemplateName,
                        request.LanguageCode ?? "en_US",
                        connection.PhoneNumber);
                }
                else
                {
                    await _whatsAppService.SendMessageAsync(
                        userId,
                        request.To,
                        request.Body,
                        connection.PhoneNumber);
                    messageId = Guid.NewGuid().ToString(); // Placeholder, as SendMessageAsync returns void
                }

                return Ok(new { success = true, messageId, status = "sent" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending WhatsApp message");
                return StatusCode(500, new { success = false, message = $"Error sending WhatsApp message: {ex.Message}" });
            }
        }

        [HttpGet("messages")]
        public async Task<IActionResult> GetMessages()
        {
            try
            {
                var userId = GetCurrentUserId();
                var activeConnection = await _dbContext.WhatsAppConnections
                    .FirstOrDefaultAsync(c => c.UserId == userId && c.IsConnected && c.IsActive);

                if (activeConnection == null)
                    return BadRequest(new { success = false, message = "No active WhatsApp account connected." });

                var messages = await _dbContext.MessageMetadatas
                    .Where(m => m.UserId == userId && m.PlatformType == "WhatsApp" && m.AccountIdentifier == activeConnection.PhoneNumber)
                    .OrderByDescending(m => m.ReceivedAt)
                    .Take(50)
                    .ToListAsync();

                return Ok(new { success = true, messages });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving WhatsApp messages");
                return StatusCode(500, new { success = false, message = $"Error retrieving WhatsApp messages: {ex.Message}" });
            }
        }

        [HttpPost("webhook")]
        public async Task<IActionResult> ReceiveWebhook()
        {
            try
            {
                using var reader = new StreamReader(Request.Body, Encoding.UTF8);
                var body = await reader.ReadToEndAsync();

                _logger.LogInformation("Received WhatsApp webhook payload: {Payload}", body);

                try
                {
                    var options = new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true,
                        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                    };

                    var isFullPayload = body.Contains("\"object\"") || body.Contains("\"Object\"");

                    if (isFullPayload)
                    {
                        var fullPayload = JsonSerializer.Deserialize<Services.Integrations.WhatsAppFullWebhookPayload>(body, options);
                        await _whatsAppService.ProcessIncomingMessageAsync(fullPayload);
                    }
                    else
                    {
                        var change = JsonSerializer.Deserialize<Services.Integrations.WhatsAppChange>(body, options);
                        var fullPayload = new Services.Integrations.WhatsAppFullWebhookPayload
                        {
                            Object = "whatsapp_business_account",
                            Entry = new List<Services.Integrations.WhatsAppEntry>
                            {
                                new Services.Integrations.WhatsAppEntry
                                {
                                    Id = "0",
                                    Changes = new List<Services.Integrations.WhatsAppChange> { change }
                                }
                            }
                        };
                        await _whatsAppService.ProcessIncomingMessageAsync(fullPayload);
                    }
                }
                catch (JsonException ex)
                {
                    _logger.LogError(ex, "Error parsing webhook payload: {Body}", body);
                }

                return Ok();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing webhook: {Message}", ex.Message);
                return Ok();
            }
        }

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

        [HttpPost("messages/{messageId}/read")]
        public async Task<IActionResult> MarkAsRead(string messageId)
        {
            try
            {
                var userId = GetCurrentUserId();
                var activeConnection = await _dbContext.WhatsAppConnections
                    .FirstOrDefaultAsync(c => c.UserId == userId && c.IsConnected && c.IsActive);

                if (activeConnection == null)
                    return BadRequest(new { success = false, message = "No active WhatsApp account connected." });

                await _whatsAppService.MarkMessageAsReadAsync(userId, messageId, activeConnection.PhoneNumber);
                return Ok(new { success = true });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error marking message as read: {MessageId}", messageId);
                return StatusCode(500, new { success = false, message = $"Error marking message as read: {ex.Message}" });
            }
        }

        private int GetCurrentUserId()
        {
            var userIdClaim = User.Claims.FirstOrDefault(c => c.Type == "userId");
            return userIdClaim != null ? int.Parse(userIdClaim.Value) : 0;
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
        public string TemplateName { get; set; }
        public string LanguageCode { get; set; }
    }

    public class SwitchAccountRequest
    {
        public string AccountIdentifier { get; set; }
    }
}