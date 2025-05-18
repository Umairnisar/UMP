using Microsoft.EntityFrameworkCore;
using Microsoft.Identity.Client;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Graph.Me.SendMail;
using Microsoft.Kiota.Abstractions.Authentication;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using UMB.Model.Models;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Http;
using Microsoft.Graph.Models.ODataErrors;
using System.IO;

namespace UMB.Api.Services.Integrations
{
    public class OutlookIntegrationService : IOutlookIntegrationService
    {
        private readonly IConfiguration _config;
        private readonly AppDbContext _dbContext;
        private readonly ILogger<OutlookIntegrationService> _logger;
        private readonly string[] _scopes = new[] { "offline_access", "Mail.Read", "Mail.Send" };

        public OutlookIntegrationService(
            IConfiguration config,
            AppDbContext dbContext,
            ILogger<OutlookIntegrationService> logger)
        {
            _config = config;
            _dbContext = dbContext;
            _logger = logger;
        }

        public string GetAuthorizationUrl(int userId, string accountIdentifier)
        {
            var clientId = _config["OutlookSettings:ClientId"];
            var redirectUri = _config["OutlookSettings:RedirectUri"];
            var scopes = string.Join(" ", _scopes);

            var url = $"https://login.microsoftonline.com/common/oauth2/v2.0/authorize" +
                      $"?client_id={clientId}" +
                      $"&response_type=code" +
                      $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
                      $"&response_mode=query" +
                      $"&scope={Uri.EscapeDataString(scopes)}" +
                      $"&state={userId}|{accountIdentifier}";
            return url;
        }

        public async Task ExchangeCodeForTokenAsync(int userId, string code, string accountIdentifier)
        {
            var clientId = _config["OutlookSettings:ClientId"];
            var clientSecret = _config["OutlookSettings:ClientSecret"];
            var redirectUri = _config["OutlookSettings:RedirectUri"];
            var tokenRequestUrl = "https://login.microsoftonline.com/common/oauth2/v2.0/token";

            var httpClient = new HttpClient();
            var tokenParameters = new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["client_id"] = clientId,
                ["client_secret"] = clientSecret,
                ["code"] = code,
                ["redirect_uri"] = redirectUri,
                ["scope"] = string.Join(" ", _scopes)
            };

            var content = new FormUrlEncodedContent(tokenParameters);
            var response = await httpClient.PostAsync(tokenRequestUrl, content);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                _logger.LogError("Error getting token for account {AccountIdentifier}: {Error}", accountIdentifier, errorContent);
                throw new Exception($"Failed to get token: {errorContent}");
            }

            var json = await response.Content.ReadAsStringAsync();
            var tokenData = JsonSerializer.Deserialize<TokenResponse>(json);

            var account = await _dbContext.PlatformAccounts
                .FirstOrDefaultAsync(pa => pa.UserId == userId && pa.PlatformType == "Outlook" && pa.AccountIdentifier == accountIdentifier);

            if (account == null)
            {
                account = new PlatformAccount
                {
                    UserId = userId,
                    PlatformType = "Outlook",
                    AccountIdentifier = accountIdentifier, // e.g., user@outlook.com
                    ExternalAccountId = "Outlook",
                    CreatedAt = DateTime.UtcNow
                };
                _dbContext.PlatformAccounts.Add(account);
            }

            account.AccessToken = tokenData.access_token;
            account.RefreshToken = tokenData.refresh_token;
            account.TokenExpiresAt = DateTime.UtcNow.AddSeconds(tokenData.expires_in);
            account.ExternalAccountId = "ExternalAccountId";
            account.UpdatedAt = DateTime.UtcNow;

            await _dbContext.SaveChangesAsync();
        }

        public async Task<List<MessageMetadata>> FetchMessagesAsync(int userId, string accountIdentifier = null)
        {
            var query = _dbContext.PlatformAccounts
                .Where(pa => pa.UserId == userId && pa.PlatformType == "Outlook");
            if (!string.IsNullOrEmpty(accountIdentifier))
            {
                query = query.Where(pa => pa.AccountIdentifier == accountIdentifier);
            }

            var accounts = await query.ToListAsync();
            if (!accounts.Any())
                return new List<MessageMetadata>();

            var result = new List<MessageMetadata>();

            foreach (var account in accounts)
            {
                var isTokenValid = await EnsureValidAccessTokenAsync(account);
                if (!isTokenValid)
                {
                    _logger.LogWarning("Unable to refresh token for Outlook account {AccountIdentifier}. User needs to re-authenticate.", account.AccountIdentifier);
                    continue;
                }

                try
                {
                    var graphClient = new GraphServiceClient(new BaseBearerTokenAuthenticationProvider(
                        new TokenProvider(account.AccessToken)));

                    var messages = await graphClient.Me.Messages
                        .GetAsync(requestConfiguration =>
                        {
                            requestConfiguration.QueryParameters.Top = 10;
                            requestConfiguration.QueryParameters.Select = new string[]
                            {
                                "id", "subject", "bodyPreview", "from", "receivedDateTime",
                                "hasAttachments", "importance", "internetMessageId",
                                "isRead", "body"
                            };
                        });

                    if (messages?.Value != null)
                    {
                        foreach (var msg in messages.Value)
                        {
                            string externalMessageId = !string.IsNullOrEmpty(msg.InternetMessageId)
                                ? $"{msg.InternetMessageId.Trim('<', '>')}|{msg.ReceivedDateTime:O}|{msg.Id}"
                                : msg.Id;

                            string fromName = msg?.From?.EmailAddress?.Name ?? "Unknown Sender";
                            string fromEmail = msg?.From?.EmailAddress?.Address ?? "unknown@example.com";

                            var metadata = new MessageMetadata
                            {
                                UserId = userId,
                                PlatformType = "Outlook",
                                ExternalMessageId = externalMessageId,
                                AccountIdentifier = account.AccountIdentifier,
                                Subject = msg.Subject ?? "(No Subject)",
                                Snippet = msg.BodyPreview ?? "",
                                From = fromName,
                                FromEmail = fromEmail,
                                Body = msg.Body?.ContentType == BodyType.Text ? msg.Body?.Content : null,
                                HtmlBody = msg.Body?.ContentType == BodyType.Html ? msg.Body?.Content : null,
                                ReceivedAt = msg.ReceivedDateTime?.DateTime ?? DateTime.UtcNow,
                                IsRead = msg.IsRead ?? false,
                                HasAttachments = msg.HasAttachments ?? false
                            };

                            if (msg.HasAttachments == true)
                            {
                                try
                                {
                                    var attachments = await graphClient.Me.Messages[msg.Id].Attachments
                                        .GetAsync();

                                    if (attachments?.Value != null)
                                    {
                                        var messageAttachments = new List<MessageAttachment>();

                                        foreach (var att in attachments.Value)
                                        {
                                            var messageAttachment = new MessageAttachment
                                            {
                                                FileName = att.Name,
                                                ContentType = att.ContentType,
                                                Size = att.Size ?? 0,
                                                AttachmentId = att.Id,
                                                Content = att is FileAttachment fileAtt && att.Size < 1024 * 1024 ? fileAtt.ContentBytes : null,
                                                CreatedAt = DateTime.UtcNow
                                            };
                                            messageAttachments.Add(messageAttachment);
                                        }

                                        metadata.Attachments = messageAttachments;
                                    }
                                }
                                catch (Exception ex)
                                {
                                    _logger.LogError(ex, "Error fetching attachments for Outlook message {MessageId}, account {AccountIdentifier}", msg.Id, account.AccountIdentifier);
                                }
                            }

                            result.Add(metadata);
                        }

                        var existingIds = await _dbContext.MessageMetadatas
                            .Where(m => m.UserId == userId && m.PlatformType == "Outlook" && m.AccountIdentifier == account.AccountIdentifier)
                            .Select(m => m.ExternalMessageId)
                            .ToListAsync();

                        var newMessages = result.Where(m => !existingIds.Contains(m.ExternalMessageId)).ToList();

                        if (newMessages.Any())
                        {
                            await _dbContext.MessageMetadatas.AddRangeAsync(newMessages);
                            await _dbContext.SaveChangesAsync();
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error fetching messages from Outlook for account {AccountIdentifier}", account.AccountIdentifier);
                    continue;
                }
            }

            return result.OrderByDescending(m => m.ReceivedAt).ToList();
        }

        public async Task SendMessageAsync(int userId, string subject, string body, string toEmail, string accountIdentifier, List<IFormFile> attachments = null)
        {
            var account = await _dbContext.PlatformAccounts
                .FirstOrDefaultAsync(pa => pa.UserId == userId && pa.PlatformType == "Outlook" && pa.AccountIdentifier == accountIdentifier);

            if (account == null)
                throw new Exception($"No Outlook account connected for {accountIdentifier}.");

            var isTokenValid = await EnsureValidAccessTokenAsync(account);
            if (!isTokenValid)
                throw new Exception("Outlook authentication expired. User must re-authenticate.");

            var graphClient = new GraphServiceClient(new BaseBearerTokenAuthenticationProvider(
                new TokenProvider(account.AccessToken)));

            var message = new Message
            {
                Subject = subject,
                Body = new ItemBody
                {
                    ContentType = BodyType.Html,
                    Content = body
                },
                ToRecipients = new List<Recipient>
                {
                    new Recipient
                    {
                        EmailAddress = new EmailAddress
                        {
                            Address = toEmail
                        }
                    }
                }
            };

            if (attachments != null && attachments.Any())
            {
                message.Attachments = new List<Attachment>();

                foreach (var file in attachments)
                {
                    using (var memStream = new MemoryStream())
                    {
                        await file.CopyToAsync(memStream);
                        byte[] bytes = memStream.ToArray();

                        var fileAttachment = new FileAttachment
                        {
                            Name = file.FileName,
                            ContentType = file.ContentType,
                            OdataType = "#microsoft.graph.fileAttachment",
                            IsInline = false,
                            ContentBytes = bytes
                        };

                        message.Attachments.Add(fileAttachment);
                    }
                }
            }

            await graphClient.Me.SendMail.PostAsync(new SendMailPostRequestBody
            {
                Message = message,
                SaveToSentItems = true
            });

            var sentMessage = new MessageMetadata
            {
                UserId = userId,
                PlatformType = "Outlook",
                ExternalMessageId = Guid.NewGuid().ToString(),
                AccountIdentifier = accountIdentifier,
                Subject = subject,
                Snippet = body.Length > 100 ? body.Substring(0, 97) + "..." : body,
                Body = body,
                HtmlBody = body,
                From = "You",
                ReceivedAt = DateTime.UtcNow,
                IsRead = true,
                HasAttachments = attachments != null && attachments.Any()
            };

            _dbContext.MessageMetadatas.Add(sentMessage);
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
                        MessageMetadataId = sentMessage.Id,
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
        }

        public async Task<(byte[] Content, string ContentType, string FileName)> GetAttachmentAsync(int userId, string messageId, string attachmentId, string accountIdentifier)
        {
            var account = await _dbContext.PlatformAccounts
                .FirstOrDefaultAsync(pa => pa.UserId == userId && pa.PlatformType == "Outlook" && pa.AccountIdentifier == accountIdentifier);

            if (account == null)
                throw new Exception($"No Outlook account connected for {accountIdentifier}.");

            var isTokenValid = await EnsureValidAccessTokenAsync(account);
            if (!isTokenValid)
                throw new Exception("Outlook authentication expired. User must re-authenticate.");

            var dbMessage = await _dbContext.MessageMetadatas
                .FirstOrDefaultAsync(m => m.UserId == userId && m.ExternalMessageId == messageId && m.AccountIdentifier == accountIdentifier);

            if (dbMessage != null)
            {
                var dbAttachment = await _dbContext.MessageAttachments
                    .FirstOrDefaultAsync(a => a.MessageMetadataId == dbMessage.Id && a.AttachmentId == attachmentId);

                if (dbAttachment != null && dbAttachment.Content != null)
                {
                    return (dbAttachment.Content, dbAttachment.ContentType, dbAttachment.FileName);
                }
            }

            try
            {
                var graphClient = new GraphServiceClient(new BaseBearerTokenAuthenticationProvider(
                    new TokenProvider(account.AccessToken)));

                var msgId = messageId.Split('|').Last();
                var attachment = await graphClient.Me.Messages[msgId].Attachments[attachmentId]
                    .GetAsync();

                if (attachment is FileAttachment fileAttachment && fileAttachment.ContentBytes != null)
                {
                    return (fileAttachment.ContentBytes, attachment.ContentType, attachment.Name);
                }
                else
                {
                    throw new InvalidOperationException("The attachment is not a file attachment or has no content.");
                }
            }
            catch (ODataError ex)
            {
                _logger.LogError(ex, "Error accessing Outlook attachment {AttachmentId} for message {MessageId}, account {AccountIdentifier}", attachmentId, messageId, accountIdentifier);
                throw new Exception($"Error accessing attachment: {ex.Error?.Message}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error accessing Outlook attachment {AttachmentId}, account {AccountIdentifier}", attachmentId, accountIdentifier);
                throw;
            }
        }

        private async Task<bool> EnsureValidAccessTokenAsync(PlatformAccount account)
        {
            if (account.TokenExpiresAt > DateTime.UtcNow.AddMinutes(-5))
                return true;

            if (string.IsNullOrEmpty(account.RefreshToken))
            {
                _logger.LogWarning("No refresh token available for account {AccountIdentifier}", account.AccountIdentifier);
                return false;
            }

            var clientId = _config["OutlookSettings:ClientId"];
            var clientSecret = _config["OutlookSettings:ClientSecret"];
            var tokenUrl = "https://login.microsoftonline.com/common/oauth2/v2.0/token";
            var httpClient = new HttpClient();

            var refreshParameters = new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["client_id"] = clientId,
                ["client_secret"] = clientSecret,
                ["refresh_token"] = account.RefreshToken,
                ["scope"] = string.Join(" ", _scopes)
            };

            var content = new FormUrlEncodedContent(refreshParameters);
            var response = await httpClient.PostAsync(tokenUrl, content);

            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync();
                var tokenData = JsonSerializer.Deserialize<TokenResponse>(json);

                account.AccessToken = tokenData.access_token;
                if (!string.IsNullOrEmpty(tokenData.refresh_token))
                {
                    account.RefreshToken = tokenData.refresh_token;
                }
                account.TokenExpiresAt = DateTime.UtcNow.AddSeconds(tokenData.expires_in);
                account.UpdatedAt = DateTime.UtcNow;

                await _dbContext.SaveChangesAsync();
                return true;
            }
            else
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                _logger.LogError("Error refreshing token for account {AccountIdentifier}: {Error}", account.AccountIdentifier, errorContent);
                return false;
            }
        }
    }

    public class TokenProvider : IAccessTokenProvider
    {
        private readonly string _accessToken;

        public TokenProvider(string accessToken)
        {
            _accessToken = accessToken;
        }

        public Task<string> GetAuthorizationTokenAsync(Uri uri, Dictionary<string, object> additionalAuthenticationContext = null, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_accessToken);
        }

        public AllowedHostsValidator AllowedHostsValidator { get; } = new AllowedHostsValidator();
    }

    public class TokenResponse
    {
        public string access_token { get; set; }
        public string refresh_token { get; set; }
        public int expires_in { get; set; }
        public string token_type { get; set; }
        public string scope { get; set; }
    }
}