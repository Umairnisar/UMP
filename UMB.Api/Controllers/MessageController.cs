using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Linq;
using System.Threading.Tasks;
using UMB.Api.Services;
using UMB.Model.Models;

namespace UMB.Api.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    //[Authorize]
    public class MessagesController : ControllerBase
    {
        private readonly IMessageService _messageService;
        private readonly AppDbContext _dbContext;

        public MessagesController(IMessageService messageService, AppDbContext dbContext)
        {
            _messageService = messageService;
            _dbContext = dbContext;
        }

        [HttpGet("all")]
        public async Task<IActionResult> GetAllMessages([FromQuery] bool? unread, [FromQuery] string? platform)
        {
            var userId = GetCurrentUserId();
            userId = 1;
            var messages = await _messageService.GetConsolidatedMessages(userId, unread, platform);
            return Ok(messages);
        }

        [HttpPost("send")]
        public async Task<IActionResult> SendMessage([FromForm] SendMessageRequest request)
        {
            try
            {
                var userId = GetCurrentUserId();
                 userId = 1;

                // Validate active account for the platform
                string accountIdentifier;
                if (request.Platform.Equals("WhatsApp", StringComparison.OrdinalIgnoreCase))
                {
                    var activeConnection = await _dbContext.WhatsAppConnections
                        .FirstOrDefaultAsync(c => c.UserId == userId && c.IsConnected && c.IsActive);
                    if (activeConnection == null)
                        return BadRequest("No active WhatsApp account connected.");
                    accountIdentifier = activeConnection.PhoneNumber;
                }
                else
                {
                    var activeAccount = await _dbContext.PlatformAccounts
                        .FirstOrDefaultAsync(pa => pa.UserId == userId && pa.PlatformType == request.Platform && pa.IsActive);
                    if (activeAccount == null)
                        return BadRequest($"No active {request.Platform} account connected.");
                    accountIdentifier = activeAccount.AccountIdentifier;
                }

                List<IFormFile> attachments = null;
                if (request.Attachments != null && request.Attachments.Any())
                {
                    if (!request.Platform.Equals("Gmail", StringComparison.OrdinalIgnoreCase) &&
                        !request.Platform.Equals("Outlook", StringComparison.OrdinalIgnoreCase) &&
                        !request.Platform.Equals("WhatsApp", StringComparison.OrdinalIgnoreCase))
                    {
                        return BadRequest($"Attachments are not supported for {request.Platform}.");
                    }
                    attachments = request.Attachments.ToList();
                }

                await _messageService.SendMessage(userId, request.Platform, request.Subject, request.Body, request.To, accountIdentifier, attachments);
                return Ok("Message sent!");
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"Error sending message: {ex.Message}");
            }
        }

        [HttpPost("{externalMessageId}/read")]
        public async Task<IActionResult> MarkMessageAsRead(string externalMessageId, [FromBody] MarkMessageReadRequest request)
        {
            try
            {
                var userId = GetCurrentUserId();
                var message = await _messageService.GetMessageByExternalId(userId, externalMessageId, request.AccountIdentifier);
                return Ok();
            }
            catch (KeyNotFoundException ex)
            {
                return NotFound(ex.Message);
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"Error retrieving message: {ex.Message}");
            }
        }

        [HttpPost("attachment/download")]
        public async Task<IActionResult> GetAttachment([FromBody] AttachmentDownloadRequest request)
        {
            try
            {
                var userId = GetCurrentUserId();
                var (content, contentType, fileName) = await _messageService.GetAttachmentAsync(userId, request.MessageId, request.AttachmentId);
                return File(content, contentType, fileName);
            }
            catch (KeyNotFoundException ex)
            {
                return NotFound(ex.Message);
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"Error retrieving attachment: {ex.Message}");
            }
        }

        private int GetCurrentUserId()
        {
            var userIdClaim = User.Claims.FirstOrDefault(c => c.Type == "userId");
            return userIdClaim != null ? int.Parse(userIdClaim.Value) : 0;
        }
    }

    public class SendMessageRequest
    {
        public string Platform { get; set; }
        public string Subject { get; set; }
        public string Body { get; set; }
        public string To { get; set; }
        public List<IFormFile> Attachments { get; set; }
    }

    public class MarkMessageReadRequest
    {
        public string AccountIdentifier { get; set; } // e.g., user1@gmail.com, profile123, +1234567890
    }

    public class AttachmentDownloadRequest
    {
        public string MessageId { get; set; }
        public string AttachmentId { get; set; }
    }
}