﻿using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System;
using System.Linq;
using System.Threading.Tasks;
using UMB.Api.Services;
using UMB.Api.Services.Integrations;
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
        private readonly ILinkedInIntegrationService _linkedinService;
        private readonly ITwitterIntegrationService _twitterService;

        public MessagesController(
            IMessageService messageService,
            AppDbContext dbContext,
            ILinkedInIntegrationService linkedinService,
            ITwitterIntegrationService twitterService)
        {
            _messageService = messageService;
            _dbContext = dbContext;
            _linkedinService = linkedinService;
            _twitterService = twitterService;
        }

        [HttpGet("authorize/{platform}")]
        public IActionResult GetAuthorizationUrl(string platform, [FromQuery] int userId, [FromQuery] string accountIdentifier)
        {
            try
            {
                string authUrl = platform.ToLower() switch
                {
                    "linkedin" => _linkedinService.GetAuthorizationUrl(userId, accountIdentifier),
                    "twitter" => _twitterService.GetAuthorizationUrl(userId, accountIdentifier),
                    _ => throw new ArgumentException($"Authorization not supported for platform: {platform}")
                };
                return Ok(new { AuthorizationUrl = authUrl });
            }
            catch (Exception ex)
            {
                return BadRequest($"Error generating authorization URL: {ex.Message}");
            }
        }

        [HttpGet("callback/{platform}")]
        public async Task<IActionResult> Callback(string platform, [FromQuery] int userId, [FromQuery] string code, [FromQuery] string accountIdentifier, [FromQuery] string state)
        {
            try
            {
                if (!state.StartsWith($"{userId}|"))
                    return BadRequest("Invalid state parameter");

                await (platform.ToLower() switch
                {
                    "linkedin" => _linkedinService.ExchangeCodeForTokenAsync(userId, code, accountIdentifier),
                    "twitter" => _twitterService.ExchangeCodeForTokenAsync(userId, code, accountIdentifier),
                    _ => throw new ArgumentException($"Callback not supported for platform: {platform}")
                });

                return Ok("Account connected successfully");
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"Error connecting account: {ex.Message}");
            }
        }

        [HttpGet("all")]
        public async Task<IActionResult> GetAllMessages([FromQuery] bool? unread, [FromQuery] string? platform)
        {
            try
            {
                var userId = GetCurrentUserId();
                var messages = await _messageService.GetConsolidatedMessages(userId, unread, platform);
                return Ok(messages);
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"Error retrieving messages: {ex.Message}");
            }
        }

        [HttpPost("send")]
        public async Task<IActionResult> SendMessage([FromForm] SendMessageRequest request)
        {
            try
            {
                var userId = GetCurrentUserId();
                // userId = 1; // Remove in production

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

                // Validate attachments
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
        public List<IFormFile>? Attachments { get; set; } = new List<IFormFile>();
    }

    public class MarkMessageReadRequest
    {
        public string AccountIdentifier { get; set; }
    }

    public class AttachmentDownloadRequest
    {
        public string MessageId { get; set; }
        public string AttachmentId { get; set; }
    }
}