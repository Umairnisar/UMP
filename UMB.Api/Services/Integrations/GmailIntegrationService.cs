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

        public string GetAuthorizationUrl(int userId, string email)
        {
            var redirectUri = _config["GoogleSettings:RedirectUri"];
            var clientId = _config["GoogleSettings:ClientId"];
            var scopes = new[] { GmailService.Scope.GmailReadonly, GmailService.Scope.GmailSend };

            var url = $"https://accounts.google.com/o/oauth2/v2/auth?client_id={clientId}" +
                      $"&redirect_uri={redirectUri}" +
                      $"&scope={string.Join(" ", scopes)}" +
                      $"&response_type=code" +
                      $"&access_type=offline" +
                      $"&prompt=consent" +
                      $"&state={userId}|{email}"; // Include email in state to identify account

            return url;
        }

        public async Task ExchangeCodeForTokenAsync(int userId, string code, string email)
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

            var platformAccount = await _dbContext.PlatformAccounts
                .FirstOrDefaultAsync(pa => pa.UserId == userId && pa.PlatformType == "Gmail" && pa.AccountIdentifier == email);

            if (platformAccount == null)
            {
                platformAccount = new PlatformAccount
                {
                    UserId = userId,
                    PlatformType = "Gmail",
                    AccountIdentifier = email,
                    CreatedAt = DateTime.UtcNow,
                    ExternalAccountId="1",
                    IsActive=true
                };
                _dbContext.PlatformAccounts.Add(platformAccount);
            }

            platformAccount.AccessToken = tokenData.access_token;
            platformAccount.RefreshToken = tokenData.refresh_token;
            platformAccount.TokenExpiresAt = DateTime.UtcNow.AddSeconds(tokenData.expires_in);
            platformAccount.UpdatedAt = DateTime.UtcNow;

            await _dbContext.SaveChangesAsync();
        }

        public async Task<List<MessageMetadata>> FetchMessagesAsync(int userId, string email = null)
        {
            var query = _dbContext.PlatformAccounts
                .Where(pa => pa.UserId == userId && pa.PlatformType == "Gmail");
            if (!string.IsNullOrEmpty(email))
            {
                query = query.Where(pa => pa.AccountIdentifier == email);
            }

            var accounts = await query.ToListAsync();
            if (!accounts.Any())
            {
                return new List<MessageMetadata>();
            }

            var allMessages = new List<MessageMetadata>();
            foreach (var account in accounts)
            {
                await EnsureValidAccessTokenAsync(account);
                var credential = GoogleCredential.FromAccessToken(account.AccessToken);
                var gmailService = new GmailService(new BaseClientService.Initializer
                {
                    HttpClientInitializer = credential,
                    ApplicationName = "UMP"
                });

                var request = gmailService.Users.Messages.List("me");
                request.LabelIds = "INBOX";
                request.MaxResults = 10;
                var response = await request.ExecuteAsync();

                var messagesMetadata = new List<MessageMetadata>();
                if (response?.Messages != null)
                {
                    foreach (var msg in response.Messages)
                    {
                        var fullMessage = await gmailService.Users.Messages.Get("me", msg.Id).ExecuteAsync();
                        bool hasAttachments = fullMessage.Payload?.Parts?.Any(p => !string.IsNullOrEmpty(p.Filename) && p.Body?.AttachmentId != null) ?? false;

                        var messageMetadata = new MessageMetadata
                        {
                            UserId = userId,
                            PlatformType = "Gmail",
                            ExternalMessageId = msg.Id,
                            AccountIdentifier = account.AccountIdentifier, // New: Store account email
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

                        if (hasAttachments)
                        {
                            messageMetadata.Attachments = await GetAttachmentsMetadata(fullMessage, gmailService);
                        }

                        messagesMetadata.Add(messageMetadata);
                    }

                    try
                    {
                        var existingIds = await _dbContext.MessageMetadatas
                            .Where(m => m.UserId == userId && m.PlatformType == "Gmail" && m.AccountIdentifier == account.AccountIdentifier)
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
                        _logger.LogError(ex, "Error saving Gmail messages to database for account {Email}", account.AccountIdentifier);
                    }
                }

                allMessages.AddRange(messagesMetadata);
            }

            return allMessages;
        }

        public async Task<string> SendMessageAsync(int userId, string subject, string body, string toEmail, string accountEmail, List<IFormFile> attachments = null)
        {
            var account = await _dbContext.PlatformAccounts
                .FirstOrDefaultAsync(pa => pa.UserId == userId && pa.PlatformType == "Gmail" && pa.AccountIdentifier == accountEmail);

            if (account == null)
                throw new Exception($"No Gmail account connected for email {accountEmail}.");

            await EnsureValidAccessTokenAsync(account);
            var credential = GoogleCredential.FromAccessToken(account.AccessToken);
            var gmailService = new GmailService(new BaseClientService.Initializer
            {
                HttpClientInitializer = credential,
                ApplicationName = "UMP"
            });

            Message gmailMessage;
            if (attachments != null && attachments.Any())
            {
                gmailMessage = await CreateEmailWithAttachments(toEmail, subject, body, attachments);
            }
            else
            {
                string messageBody = $"From: me\r\nTo: {toEmail}\r\nSubject: {subject}\r\nContent-Type: text/html; charset=utf-8\r\n\r\n{body}";
                byte[] bytes = Encoding.UTF8.GetBytes(messageBody);
                string base64Url = Convert.ToBase64String(bytes)
                    .Replace('+', '-')
                    .Replace('/', '_')
                    .Replace("=", "");
                gmailMessage = new Message { Raw = base64Url };
            }

            var result = await gmailService.Users.Messages.Send(gmailMessage, "me").ExecuteAsync();
            var messageMetadata = new MessageMetadata
            {
                UserId = userId,
                PlatformType = "Gmail",
                ExternalMessageId = result.Id,
                AccountIdentifier = account.AccountIdentifier,
                Subject = subject,
                Snippet = body.Length > 100 ? body.Substring(0, 97) + "..." : body,
                Body = body,
                HtmlBody = body,
                From = account.AccountIdentifier,
                ReceivedAt = DateTime.UtcNow,
                IsRead = true,
                To=toEmail,


                HasAttachments = attachments != null && attachments.Any()
            };

            _dbContext.MessageMetadatas.Add(messageMetadata);
            await _dbContext.SaveChangesAsync();

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
                        AttachmentId = Guid.NewGuid().ToString(),
                        Content = file.Length < 1024 * 1024 ? stream.ToArray() : null,
                        CreatedAt = DateTime.UtcNow
                    };
                    messageAttachments.Add(messageAttachment);
                }
                await _dbContext.MessageAttachments.AddRangeAsync(messageAttachments);
                await _dbContext.SaveChangesAsync();
            }

            return result.Id;
        }

        public async Task<(byte[] Content, string ContentType, string FileName)> GetAttachmentAsync(int userId, string messageId, string attachmentId, string accountEmail)
        {
            var account = await _dbContext.PlatformAccounts
                .FirstOrDefaultAsync(pa => pa.UserId == userId && pa.PlatformType == "Gmail" && pa.AccountIdentifier == accountEmail);

            if (account == null)
                throw new Exception($"No Gmail account connected for email {accountEmail}.");

            await EnsureValidAccessTokenAsync(account);
            var dbMessage = await _dbContext.MessageMetadatas
                .FirstOrDefaultAsync(m => m.UserId == userId && m.ExternalMessageId == messageId && m.AccountIdentifier == accountEmail);

            if (dbMessage != null)
            {
                var dbAttachment = await _dbContext.MessageAttachments
                    .FirstOrDefaultAsync(a => a.MessageMetadataId == dbMessage.Id && a.AttachmentId == attachmentId);

                if (dbAttachment != null && dbAttachment.Content != null)
                {
                    return (dbAttachment.Content, dbAttachment.ContentType, dbAttachment.FileName);
                }
            }

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

            string base64 = attachmentData.Data.Replace('-', '+').Replace('_', '/');
            int padChars = (4 - base64.Length % 4) % 4;
            base64 = base64.PadRight(base64.Length + padChars, '=');
            var content = Convert.FromBase64String(base64);

            var attachment = await _dbContext.MessageAttachments
                .FirstOrDefaultAsync(a => a.AttachmentId == attachmentId);

            var contentType = attachment?.ContentType ?? "application/octet-stream";
            var fileName = attachment?.FileName ?? "attachment";

            return (content, contentType, fileName);
        }

        public async Task EnsureValidAccessTokenAsync(PlatformAccount account)
        {
            if (account.TokenExpiresAt > DateTime.UtcNow)
                return;

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
            account.UpdatedAt = DateTime.UtcNow;
            await _dbContext.SaveChangesAsync();
        }

        private string GetPlainTextBody(Message message)
        {
            if (message.Payload?.Parts == null || !message.Payload.Parts.Any())
            {
                if (message.Payload?.Body?.Data != null)
                {
                    return DecodeBase64Url(message.Payload.Body.Data);
                }
                return message.Snippet ?? string.Empty;
            }

            var textPart = FindMessagePartByMimeType(message.Payload.Parts, "text/plain");
            if (textPart != null && textPart.Body?.Data != null)
            {
                return DecodeBase64Url(textPart.Body.Data);
            }
            return message.Snippet ?? string.Empty;
        }

        private string GetHtmlBody(Message message)
        {
            if (message.Payload?.Parts == null || !message.Payload.Parts.Any())
            {
                if (message.Payload?.MimeType == "text/html" && message.Payload?.Body?.Data != null)
                {
                    return DecodeBase64Url(message.Payload.Body.Data);
                }
                return null;
            }

            var htmlPart = FindMessagePartByMimeType(message.Payload.Parts, "text/html");
            if (htmlPart != null && htmlPart.Body?.Data != null)
            {
                return DecodeBase64Url(htmlPart.Body.Data);
            }
            return null;
        }

        private MessagePart FindMessagePartByMimeType(IList<MessagePart> parts, string mimeType)
        {
            foreach (var part in parts)
            {
                if (part.MimeType == mimeType)
                {
                    return part;
                }
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

        private string DecodeBase64Url(string base64Url)
        {
            string base64 = base64Url.Replace('-', '+').Replace('_', '/');
            int padChars = (4 - base64.Length % 4) % 4;
            base64 = base64.PadRight(base64.Length + padChars, '=');
            byte[] bytes = Convert.FromBase64String(base64);
            return Encoding.UTF8.GetString(bytes);
        }

        private async Task<List<MessageAttachment>> GetAttachmentsMetadata(Message message, GmailService gmailService)
        {
            var attachments = new List<MessageAttachment>();
            await ProcessParts(message.Payload?.Parts, attachments, message.Id, gmailService);
            return attachments;
        }

        private async Task ProcessParts(IList<MessagePart> parts, List<MessageAttachment> attachments, string messageId, GmailService gmailService)
        {
            if (parts == null) return;
            foreach (var part in parts)
            {
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
                    if (attachmentSize < 1024 * 1024)
                    {
                        try
                        {
                            var attachmentRequest = gmailService.Users.Messages.Attachments.Get("me", messageId, part.Body.AttachmentId);
                            var attachmentData = await attachmentRequest.ExecuteAsync();
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
                if (part.Parts != null && part.Parts.Any())
                {
                    await ProcessParts(part.Parts, attachments, messageId, gmailService);
                }
            }
        }

        private async Task<Message> CreateEmailWithAttachments(string toEmail, string subject, string body, List<IFormFile> attachments)
        {
            var boundary = $"boundary_{Guid.NewGuid().ToString().Replace("-", "")}";
            var messageBuilder = new StringBuilder();
            messageBuilder.AppendLine($"From: me");
            messageBuilder.AppendLine($"To: {toEmail}");
            messageBuilder.AppendLine($"Subject: {subject}");
            messageBuilder.AppendLine($"MIME-Version: 1.0");
            messageBuilder.AppendLine($"Content-Type: multipart/mixed; boundary={boundary}");
            messageBuilder.AppendLine();
            messageBuilder.AppendLine($"--{boundary}");
            messageBuilder.AppendLine("Content-Type: text/html; charset=utf-8");
            messageBuilder.AppendLine("Content-Transfer-Encoding: 8bit");
            messageBuilder.AppendLine();
            messageBuilder.AppendLine(body);
            messageBuilder.AppendLine();
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
                    var base64Content = Convert.ToBase64String(fileBytes);
                    for (int i = 0; i < base64Content.Length; i += 76)
                    {
                        int length = Math.Min(76, base64Content.Length - i);
                        messageBuilder.AppendLine(base64Content.Substring(i, length));
                    }
                }
                messageBuilder.AppendLine();
            }
            messageBuilder.AppendLine($"--{boundary}--");
            var bytes = Encoding.UTF8.GetBytes(messageBuilder.ToString());
            var base64Url = Convert.ToBase64String(bytes)
                .Replace('+', '-')
                .Replace('/', '_')
                .Replace("=", "");
            return new Message { Raw = base64Url };
        }
    }
}