using Google.Apis.Auth.OAuth2;
using Google.Apis.Gmail.v1;
using Google.Apis.Gmail.v1.Data;
using Google.Apis.Services;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UMB.Model.Models;

namespace UMB.Api.Services.Integrations
{
    public class GmailIntegrationService : IGmailIntegrationService
    {
        private readonly IConfiguration _config;
        private readonly AppDbContext _dbContext;
        private readonly ILogger<GmailIntegrationService> _logger;

        public GmailIntegrationService(IConfiguration config, AppDbContext dbContext, ILogger<GmailIntegrationService> logger)
        {
            _config = config;
            _dbContext = dbContext;
            _logger = logger;
        }

        // 1) Build the Google OAuth Consent URL
        public string GetAuthorizationUrl(int userId)
        {
            // your redirect url must match what you set in the Google Cloud console
            var redirectUri = _config["GoogleSettings:RedirectUri"];
            var clientId = _config["GoogleSettings:ClientId"];
            var scopes = new[] { GmailService.Scope.GmailReadonly, GmailService.Scope.GmailSend };

            var url = $"https://accounts.google.com/o/oauth2/v2/auth?client_id={clientId}" +
                      $"&redirect_uri={redirectUri}" +
                      $"&scope={string.Join(" ", scopes)}" +
                      $"&response_type=code" +
                      $"&access_type=offline" +
                      $"&prompt=consent" +
                      $"&state={userId}"; // we can pass the userId as "state" to identify

            return url;
        }

        // 2) Exchange authorization code for tokens
        public async Task ExchangeCodeForTokenAsync(int userId, string code)
        {
            var clientId = _config["GoogleSettings:ClientId"];
            var clientSecret = _config["GoogleSettings:ClientSecret"];
            var redirectUri = _config["GoogleSettings:RedirectUri"];

            var tokenRequestUrl = "https://oauth2.googleapis.com/token";

            using var client = new HttpClient();
            var requestBody = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                { "code", code },
                { "client_id", clientId },
                { "client_secret", clientSecret },
                { "redirect_uri", redirectUri },
                { "grant_type", "authorization_code" }
            });

            var response = await client.PostAsync(tokenRequestUrl, requestBody);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync();

            var tokenData = System.Text.Json.JsonSerializer.Deserialize<GoogleTokenResponse>(json);

            // Save to DB
            var platformAccount = await _dbContext.PlatformAccounts
                .FirstOrDefaultAsync(pa => pa.UserId == userId && pa.PlatformType == "Gmail");

            if (platformAccount == null)
            {
                platformAccount = new PlatformAccount
                {
                    UserId = userId,
                    PlatformType = "Gmail",
                };
                _dbContext.PlatformAccounts.Add(platformAccount);
            }

            platformAccount.AccessToken = tokenData.access_token;
            platformAccount.RefreshToken = tokenData.refresh_token; // only provided once
            platformAccount.TokenExpiresAt = DateTime.UtcNow.AddSeconds(tokenData.expires_in);

            await _dbContext.SaveChangesAsync();
        }

        // 3) Fetch messages from Gmail
        public async Task<List<MessageMetadata>> FetchMessagesAsync(int userId)
        {
            var account = await _dbContext.PlatformAccounts
                .FirstOrDefaultAsync(pa => pa.UserId == userId && pa.PlatformType == "Gmail");

            if (account == null)
                return new List<MessageMetadata>();

            await EnsureValidAccessTokenAsync(account);

            // Build the Gmail Service
            var credential = GoogleCredential.FromAccessToken(account.AccessToken);
            var gmailService = new GmailService(new BaseClientService.Initializer
            {
                HttpClientInitializer = credential,
                ApplicationName = "UMP"
            });

            var request = gmailService.Users.Messages.List("me");
            request.LabelIds = "INBOX";
            request.MaxResults = 10; // example
            var response = await request.ExecuteAsync();

            var messagesMetadata = new List<MessageMetadata>();
            if (response?.Messages != null)
            {
                foreach (var msg in response.Messages)
                {
                    // Get the actual message details
                    var fullMessage = await gmailService.Users.Messages.Get("me", msg.Id).ExecuteAsync();

                    // Check if message has attachments
                    bool hasAttachments = fullMessage.Payload?.Parts?.Any(p => !string.IsNullOrEmpty(p.Filename) && p.Body?.AttachmentId != null) ?? false;

                    // Construct a local message metadata
                    var messageMetadata = new MessageMetadata
                    {
                        UserId = userId,
                        PlatformType = "Gmail",
                        ExternalMessageId = msg.Id,
                        Subject = fullMessage.Payload?.Headers?.FirstOrDefault(h => h.Name == "Subject")?.Value,
                        Snippet = fullMessage.Snippet,
                        From = fullMessage.Payload?.Headers?.FirstOrDefault(h => h.Name == "From")?.Value?.Split('<')[0].Trim(),
                        Body = GetPlainTextBody(fullMessage),
                        HtmlBody = GetHtmlBody(fullMessage),
                        FromEmail = fullMessage.Payload?.Headers?.FirstOrDefault(h => h.Name == "From")?.Value?.Split('<').Length > 1
                            ? fullMessage.Payload?.Headers?.FirstOrDefault(h => h.Name == "From")?.Value?.Split('<')[1].Trim('>', ' ')
                            : fullMessage.Payload?.Headers?.FirstOrDefault(h => h.Name == "From")?.Value,
                        ReceivedAt = DateTimeOffset.FromUnixTimeMilliseconds(fullMessage.InternalDate.Value).UtcDateTime,
                        HasAttachments = hasAttachments
                    };

                    // If the message has attachments, process them
                    if (hasAttachments)
                    {
                        messageMetadata.Attachments = await GetAttachmentsMetadata(fullMessage, gmailService);
                    }

                    messagesMetadata.Add(messageMetadata);
                }

                try
                {
                    // Check for existing messages to avoid duplicates
                    var existingIds = await _dbContext.MessageMetadatas
                        .Where(m => m.UserId == userId && m.PlatformType == "Gmail")
                        .Select(m => m.ExternalMessageId)
                        .ToListAsync();

                    var newMessages = messagesMetadata.Where(m => !existingIds.Contains(m.ExternalMessageId)).ToList();

                    if (newMessages.Any())
                    {
                        await _dbContext.MessageMetadatas.AddRangeAsync(newMessages);
                        await _dbContext.SaveChangesAsync();
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error saving Gmail messages to database");
                }
            }

            return messagesMetadata;
        }

        // Get plain text body from the message
        private string GetPlainTextBody(Message message)
        {
            // If there are no parts, try to get the body from the payload directly
            if (message.Payload?.Parts == null || !message.Payload.Parts.Any())
            {
                if (message.Payload?.Body?.Data != null)
                {
                    return DecodeBase64Url(message.Payload.Body.Data);
                }
                return message.Snippet ?? string.Empty;
            }

            // Look for text/plain part
            var textPart = FindMessagePartByMimeType(message.Payload.Parts, "text/plain");
            if (textPart != null && textPart.Body?.Data != null)
            {
                return DecodeBase64Url(textPart.Body.Data);
            }

            // If no text/plain part found, return the snippet
            return message.Snippet ?? string.Empty;
        }

        // Get HTML body from the message
        private string GetHtmlBody(Message message)
        {
            // If there are no parts, try to get the body from the payload directly
            if (message.Payload?.Parts == null || !message.Payload.Parts.Any())
            {
                if (message.Payload?.MimeType == "text/html" && message.Payload?.Body?.Data != null)
                {
                    return DecodeBase64Url(message.Payload.Body.Data);
                }
                return null;
            }

            // Look for text/html part
            var htmlPart = FindMessagePartByMimeType(message.Payload.Parts, "text/html");
            if (htmlPart != null && htmlPart.Body?.Data != null)
            {
                return DecodeBase64Url(htmlPart.Body.Data);
            }

            return null;
        }

        // Helper method to find a message part by MIME type
        private MessagePart FindMessagePartByMimeType(IList<MessagePart> parts, string mimeType)
        {
            foreach (var part in parts)
            {
                if (part.MimeType == mimeType)
                {
                    return part;
                }

                // Recursively search in nested parts
                if (part.Parts != null && part.Parts.Any())
                {
                    var nestedPart = FindMessagePartByMimeType(part.Parts, mimeType);
                    if (nestedPart != null)
                    {
                        return nestedPart;
                    }
                }
            }

            return null;
        }

        // Helper method to decode Base64Url-encoded string
        private string DecodeBase64Url(string base64Url)
        {
            string base64 = base64Url.Replace('-', '+').Replace('_', '/');
            int padChars = (4 - base64.Length % 4) % 4;
            base64 = base64.PadRight(base64.Length + padChars, '=');
            byte[] bytes = Convert.FromBase64String(base64);
            return Encoding.UTF8.GetString(bytes);
        }

        // Get attachments metadata
        private async Task<List<MessageAttachment>> GetAttachmentsMetadata(Message message, GmailService gmailService)
        {
            var attachments = new List<MessageAttachment>();

            // Recursively process all parts including nested ones
            await ProcessParts(message.Payload?.Parts, attachments, message.Id, gmailService);

            return attachments;
        }

        // Process message parts recursively to find attachments
        private async Task ProcessParts(IList<MessagePart> parts, List<MessageAttachment> attachments, string messageId, GmailService gmailService)
        {
            if (parts == null) return;

            foreach (var part in parts)
            {
                // If this part has a filename, it's an attachment
                if (!string.IsNullOrEmpty(part.Filename) && part.Body?.AttachmentId != null)
                {
                    var attachmentSize = part.Body.Size ?? 0;
                    var attachment = new MessageAttachment
                    {
                        FileName = part.Filename,
                        ContentType = part.MimeType,
                        Size = attachmentSize,
                        AttachmentId = part.Body.AttachmentId,
                        CreatedAt = DateTime.UtcNow
                    };

                    // For small attachments (< 1MB), fetch and store the content
                    if (attachmentSize < 1024 * 1024)
                    {
                        try
                        {
                            var attachmentRequest = gmailService.Users.Messages.Attachments.Get("me", messageId, part.Body.AttachmentId);
                            var attachmentData = await attachmentRequest.ExecuteAsync();

                            // Decode attachment data
                            if (!string.IsNullOrEmpty(attachmentData.Data))
                            {
                                string base64 = attachmentData.Data.Replace('-', '+').Replace('_', '/');
                                int padChars = (4 - base64.Length % 4) % 4;
                                base64 = base64.PadRight(base64.Length + padChars, '=');
                                attachment.Content = Convert.FromBase64String(base64);
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Error fetching Gmail attachment: {AttachmentId}", part.Body.AttachmentId);
                        }
                    }

                    attachments.Add(attachment);
                }

                // Recursively process nested parts
                if (part.Parts != null && part.Parts.Any())
                {
                    await ProcessParts(part.Parts, attachments, messageId, gmailService);
                }
            }
        }

        // 4) Send an email
        public async Task<string> SendMessageAsync(int userId, string subject, string body, string toEmail, List<IFormFile> attachments = null)
        {
            var account = await _dbContext.PlatformAccounts
                .FirstOrDefaultAsync(pa => pa.UserId == userId && pa.PlatformType == "Gmail");

            if (account == null)
                throw new Exception("No Gmail account connected.");

            await EnsureValidAccessTokenAsync(account);

            var credential = GoogleCredential.FromAccessToken(account.AccessToken);
            var gmailService = new GmailService(new BaseClientService.Initializer
            {
                HttpClientInitializer = credential,
                ApplicationName = "UMP" // Match the existing app name
            });

            // Create email with attachments if provided
            Message gmailMessage;
            if (attachments != null && attachments.Any())
            {
                gmailMessage = await CreateEmailWithAttachments(toEmail, subject, body, attachments);
            }
            else
            {
                // Create a simple email message without attachments
                string messageBody = $"From: me\r\nTo: {toEmail}\r\nSubject: {subject}\r\nContent-Type: text/html; charset=utf-8\r\n\r\n{body}";
                byte[] bytes = Encoding.UTF8.GetBytes(messageBody);

                // Convert to base64url format
                string base64Url = Convert.ToBase64String(bytes)
                    .Replace('+', '-')
                    .Replace('/', '_')
                    .Replace("=", "");

                // Create the Gmail message
                gmailMessage = new Message
                {
                    Raw = base64Url
                };
            }

            // Send the message
            var result = await gmailService.Users.Messages.Send(gmailMessage, "me").ExecuteAsync();

            // Store in database
            var messageMetadata = new MessageMetadata
            {
                UserId = userId,
                PlatformType = "Gmail",
                ExternalMessageId = result.Id,
                Subject = subject,
                Snippet = body.Length > 100 ? body.Substring(0, 97) + "..." : body,
                Body = body,
                HtmlBody = body, // Assuming body is HTML
                From = "You", // It's sent by the user
                ReceivedAt = DateTime.UtcNow,
                IsRead = true, // Sent messages are already read
                HasAttachments = attachments != null && attachments.Any()
            };

            _dbContext.MessageMetadatas.Add(messageMetadata);
            await _dbContext.SaveChangesAsync();

            // If there were attachments, add them to the database
            if (attachments != null && attachments.Any())
            {
                var messageAttachments = new List<MessageAttachment>();

                foreach (var file in attachments)
                {
                    using var stream = new MemoryStream();
                    await file.CopyToAsync(stream);

                    var messageAttachment = new MessageAttachment
                    {
                        MessageMetadataId = messageMetadata.Id,
                        FileName = file.FileName,
                        ContentType = file.ContentType,
                        Size = file.Length,
                        AttachmentId = Guid.NewGuid().ToString(), // Generate a unique ID
                        // Only store small attachments in the database
                        Content = file.Length < 1024 * 1024 ? stream.ToArray() : null,
                        CreatedAt = DateTime.UtcNow
                    };

                    messageAttachments.Add(messageAttachment);
                }

                await _dbContext.MessageAttachments.AddRangeAsync(messageAttachments);
                await _dbContext.SaveChangesAsync();
            }

            // Return the message ID
            return result.Id;
        }

        // Create an email with attachments
        private async Task<Message> CreateEmailWithAttachments(string toEmail, string subject, string body, List<IFormFile> attachments)
        {
            var boundary = $"boundary_{Guid.NewGuid().ToString().Replace("-", "")}";
            var messageBuilder = new StringBuilder();

            // Add headers
            messageBuilder.AppendLine($"From: me");
            messageBuilder.AppendLine($"To: {toEmail}");
            messageBuilder.AppendLine($"Subject: {subject}");
            messageBuilder.AppendLine($"MIME-Version: 1.0");
            messageBuilder.AppendLine($"Content-Type: multipart/mixed; boundary={boundary}");
            messageBuilder.AppendLine();

            // Add HTML body
            messageBuilder.AppendLine($"--{boundary}");
            messageBuilder.AppendLine("Content-Type: text/html; charset=utf-8");
            messageBuilder.AppendLine("Content-Transfer-Encoding: 8bit");
            messageBuilder.AppendLine();
            messageBuilder.AppendLine(body);
            messageBuilder.AppendLine();

            // Add attachments
            foreach (var attachment in attachments)
            {
                messageBuilder.AppendLine($"--{boundary}");
                messageBuilder.AppendLine($"Content-Type: {attachment.ContentType}");
                messageBuilder.AppendLine($"Content-Disposition: attachment; filename=\"{attachment.FileName}\"");
                messageBuilder.AppendLine("Content-Transfer-Encoding: base64");
                messageBuilder.AppendLine();

                using (var memoryStream = new MemoryStream())
                {
                    await attachment.CopyToAsync(memoryStream);
                    var fileBytes = memoryStream.ToArray();

                    // Convert to base64 and wrap at 76 characters
                    var base64Content = Convert.ToBase64String(fileBytes);
                    for (int i = 0; i < base64Content.Length; i += 76)
                    {
                        int length = Math.Min(76, base64Content.Length - i);
                        messageBuilder.AppendLine(base64Content.Substring(i, length));
                    }
                }

                messageBuilder.AppendLine();
            }

            // Close boundary
            messageBuilder.AppendLine($"--{boundary}--");

            // Convert to base64url format
            var bytes = Encoding.UTF8.GetBytes(messageBuilder.ToString());
            var base64Url = Convert.ToBase64String(bytes)
                .Replace('+', '-')
                .Replace('/', '_')
                .Replace("=", "");

            return new Message { Raw = base64Url };
        }

        // 5) Get attachment
        public async Task<(byte[] Content, string ContentType, string FileName)> GetAttachmentAsync(int userId, string messageId, string attachmentId)
        {
            var account = await _dbContext.PlatformAccounts
                .FirstOrDefaultAsync(pa => pa.UserId == userId && pa.PlatformType == "Gmail");

            if (account == null)
                throw new Exception("No Gmail account connected.");

            await EnsureValidAccessTokenAsync(account);

            // First try to get from database if it's a small attachment we've already stored
            var dbMessage = await _dbContext.MessageMetadatas
                .FirstOrDefaultAsync(m => m.UserId == userId && m.ExternalMessageId == messageId);

            if (dbMessage != null)
            {
                var dbAttachment = await _dbContext.MessageAttachments
                    .FirstOrDefaultAsync(a => a.MessageMetadataId == dbMessage.Id && a.AttachmentId == attachmentId);

                if (dbAttachment != null && dbAttachment.Content != null)
                {
                    return (dbAttachment.Content, dbAttachment.ContentType, dbAttachment.FileName);
                }
            }

            // If not found in DB or content not stored, fetch from Gmail API
            var credential = GoogleCredential.FromAccessToken(account.AccessToken);
            var gmailService = new GmailService(new BaseClientService.Initializer
            {
                HttpClientInitializer = credential,
                ApplicationName = "UMP"
            });

            var attachmentRequest = gmailService.Users.Messages.Attachments.Get("me", messageId, attachmentId);
            var attachmentData = await attachmentRequest.ExecuteAsync();

            if (string.IsNullOrEmpty(attachmentData.Data))
                throw new Exception("Attachment data not found.");

            // Decode attachment data
            string base64 = attachmentData.Data.Replace('-', '+').Replace('_', '/');
            int padChars = (4 - base64.Length % 4) % 4;
            base64 = base64.PadRight(base64.Length + padChars, '=');
            var content = Convert.FromBase64String(base64);

            // Get the attachment metadata if we have it in the database
            var attachment = await _dbContext.MessageAttachments
                .FirstOrDefaultAsync(a => a.AttachmentId == attachmentId);

            var contentType = attachment?.ContentType ?? "application/octet-stream";
            var fileName = attachment?.FileName ?? "attachment";

            return (content, contentType, fileName);
        }

        // 6) Refresh token if needed
        public async Task EnsureValidAccessTokenAsync(PlatformAccount account)
        {
            if (account.TokenExpiresAt > DateTime.UtcNow)
                return; // still valid

            var clientId = _config["GoogleSettings:ClientId"];
            var clientSecret = _config["GoogleSettings:ClientSecret"];

            using var client = new HttpClient();
            var tokenRequestUrl = "https://oauth2.googleapis.com/token";
            var requestBody = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                { "client_id", clientId },
                { "client_secret", clientSecret },
                { "grant_type", "refresh_token" },
                { "refresh_token", account.RefreshToken }
            });

            var response = await client.PostAsync(tokenRequestUrl, requestBody);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            var tokenData = System.Text.Json.JsonSerializer.Deserialize<GoogleTokenResponse>(json);

            account.AccessToken = tokenData.access_token;
            account.TokenExpiresAt = DateTime.UtcNow.AddSeconds(tokenData.expires_in);

            await _dbContext.SaveChangesAsync();
        }
    }
}