using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using UMB.Api.Services;
using UMB.Model.Models;

namespace UMB.Api.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [Authorize]
    public class MessagesController : ControllerBase
    {
        private readonly IMessageService _messageService;

        public MessagesController(IMessageService messageService)
        {
            _messageService = messageService;
        }

        [HttpGet("all")]
        public async Task<IActionResult> GetAllMessages([FromQuery] bool? unread, [FromQuery] string? platform)
        {
            var userId = int.Parse(User.Claims.First(c => c.Type == "userId").Value);
            var messages = await _messageService.GetConsolidatedMessages(userId, unread, platform);
            return Ok(messages);
        }

        [HttpPost("send")]
        public async Task<IActionResult> SendMessage([FromForm] SendMessageRequest request)
        {
            var userId = int.Parse(User.Claims.First(c => c.Type == "userId").Value);

            // Handle file attachments if provided
            List<IFormFile> attachments = null;
            if (request.Attachments != null && request.Attachments.Count > 0)
            {
                attachments = request.Attachments.ToList();
            }

            await _messageService.SendMessage(userId, request.Platform, request.Subject, request.Body, request.To, attachments);
            return Ok("Message sent!");
        }

        [HttpPost("{externalMessageId}/read")]
        public async Task<IActionResult> MarkMessageAsRead(string externalMessageId)
        {
            try
            {
                var userId = int.Parse(User.Claims.First(c => c.Type == "userId").Value);
                var message = await _messageService.GetMessageByExternalId(userId, externalMessageId);
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

        // UPDATED METHOD: Changed from GET to POST and accepting request body
        [HttpPost("attachment/download")]
        public async Task<IActionResult> GetAttachment([FromBody] AttachmentDownloadRequest request)
        {
            try
            {
                var userId = int.Parse(User.Claims.First(c => c.Type == "userId").Value);
                var (content, contentType, fileName) = await _messageService.GetAttachmentAsync(
                    userId,
                    request.MessageId,
                    request.AttachmentId);

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
    }

    public class SendMessageRequest
    {
        public string Platform { get; set; }   // "Gmail", "LinkedIn", "Outlook", "WhatsApp"
        public string Subject { get; set; }
        public string Body { get; set; }
        public string To { get; set; }         // email or userId or phone number
        public List<IFormFile> Attachments { get; set; } // File attachments
    }

    public class AttachmentDownloadRequest
    {
        public string MessageId { get; set; }
        public string AttachmentId { get; set; }
    }
}