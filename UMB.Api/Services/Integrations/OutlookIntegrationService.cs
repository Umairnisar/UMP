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
using Microsoft.Graph.Models.ExternalConnectors;

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

        public string GetAuthorizationUrl(int userId)
        {
            var clientId = _config["OutlookSettings:ClientId"];
            var redirectUri = _config["OutlookSettings:RedirectUri"];
            var scopes = string.Join(" ", _scopes);

            var url = $"https://login.microsoftonline.com/common/oauth2/v2.0/authorize" +
                      $"?client_id={clientId}" +
                      $"&response_type=code" +
                      $"&redirect_uri={redirectUri}" +
                      $"&response_mode=query" +
                      $"&scope={scopes}" +
                      $"&state={userId}";
            return url;
        }

        public async Task ExchangeCodeForTokenAsync(int userId, string code)
        {
            var clientId = _config["OutlookSettings:ClientId"];
            var clientSecret = _config["OutlookSettings:ClientSecret"];
            var redirectUri = _config["OutlookSettings:RedirectUri"];

            var tokenRequestUrl = "https://login.microsoftonline.com/common/oauth2/v2.0/token";

            // Instead of using MSAL directly, we'll use HttpClient to get the token
            // This way we can access the refresh token directly
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
                _logger.LogError("Error getting token: {Error}", errorContent);
                throw new Exception($"Failed to get token: {errorContent}");
            }

            var json = await response.Content.ReadAsStringAsync();
            var tokenData = JsonSerializer.Deserialize<TokenResponse>(json);

            // Store tokens in the database
            var account = await _dbContext.PlatformAccounts
                .FirstOrDefaultAsync(pa => pa.UserId == userId && pa.PlatformType == "Outlook");

            if (account == null)
            {
                account = new PlatformAccount
                {
                    UserId = userId,
                    PlatformType = "Outlook"
                };
                _dbContext.PlatformAccounts.Add(account);
            }

            account.AccessToken = tokenData.access_token;
            account.RefreshToken = tokenData.refresh_token; // Store the refresh token directly
            account.TokenExpiresAt = DateTime.UtcNow.AddSeconds(tokenData.expires_in);

            // For Graph Auth, we don't need ExternalAccountId when using refresh tokens directly
            account.ExternalAccountId = null;

            await _dbContext.SaveChangesAsync();
        }

        public async Task<List<MessageMetadata>> FetchMessagesAsync(int userId)
        {
            var account = await _dbContext.PlatformAccounts
                .FirstOrDefaultAsync(pa => pa.UserId == userId && pa.PlatformType == "Outlook");

            if (account == null)
                return new List<MessageMetadata>();

            var isTokenValid = await EnsureValidAccessTokenAsync(account);
            if (!isTokenValid)
            {
                _logger.LogWarning("Unable to refresh token for Outlook account. User needs to re-authenticate.");
                return new List<MessageMetadata>();
            }

            List<MessageMetadata> result = new List<MessageMetadata>();

            try
            {
                // Use Graph SDK
                var graphClient = new GraphServiceClient(new BaseBearerTokenAuthenticationProvider(
                    new TokenProvider(account.AccessToken)));

                // Retrieve top messages
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
                        // Process the message ID properly
                        string externalMessageId;
                        if (!string.IsNullOrEmpty(msg.InternetMessageId))
                        {
                            // Clean up the Internet Message ID
                            externalMessageId = msg.InternetMessageId.Trim('<', '>');
                            externalMessageId = $"{externalMessageId}|{msg.ReceivedDateTime:O}|{msg.Id}";

                        }
                        else
                        {
                            // Fall back to Graph message ID
                            externalMessageId = msg.Id;
                        }

                        // Process the sender's name and email
                        string fromName = msg?.From?.EmailAddress?.Name ?? "Unknown Sender";
                        string fromEmail = msg?.From?.EmailAddress?.Address ?? "unknown@example.com";

                        var metadata = new MessageMetadata
                        {
                            UserId = userId,
                            PlatformType = "Outlook",
                            ExternalMessageId = externalMessageId,
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

                        // If the message has attachments, fetch them
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
                                        // Only store small attachments in the database (< 1MB)
                                        if (att.Size < 1024 * 1024 && att is FileAttachment fileAtt)
                                        {
                                            var messageAttachment = new MessageAttachment
                                            {
                                                FileName = att.Name,
                                                ContentType = att.ContentType,
                                                Size = att.Size ?? 0,
                                                AttachmentId = att.Id,
                                                Content = null, // We'll fetch content later if needed
                                                CreatedAt = DateTime.UtcNow
                                            };
                                            messageAttachments.Add(messageAttachment);
                                        }
                                        else
                                        {
                                            var messageAttachment = new MessageAttachment
                                            {
                                                FileName = att.Name,
                                                ContentType = att.ContentType,
                                                Size = att.Size ?? 0,
                                                AttachmentId = att.Id,
                                                Content = null,
                                                CreatedAt = DateTime.UtcNow
                                            };
                                            messageAttachments.Add(messageAttachment);
                                        }
                                    }

                                    metadata.Attachments = messageAttachments;
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, "Error fetching attachments for Outlook message {MessageId}", msg.Id);
                            }
                        }

                        result.Add(metadata);
                    }

                    try
                    {
                        // Check for existing messages to avoid duplicates
                        var existingIds = await _dbContext.MessageMetadatas
                            .Where(m => m.UserId == userId && m.PlatformType == "Outlook")
                            .Select(m => m.ExternalMessageId)
                            .ToListAsync();

                        var newMessages = result.Where(m => !existingIds.Contains(m.ExternalMessageId)).ToList();

                        if (newMessages.Any())
                        {
                            await _dbContext.MessageMetadatas.AddRangeAsync(newMessages);
                            await _dbContext.SaveChangesAsync();
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error saving Outlook messages to database");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching messages from Outlook");
                throw;
            }

            return result;
        }

        public async Task SendMessageAsync(int userId, string subject, string body, string toEmail, List<IFormFile> attachments = null)
        {
            var account = await _dbContext.PlatformAccounts
                .FirstOrDefaultAsync(pa => pa.UserId == userId && pa.PlatformType == "Outlook");

            if (account == null)
                throw new Exception("No Outlook account connected.");

            var isTokenValid = await EnsureValidAccessTokenAsync(account);
            if (!isTokenValid)
                throw new Exception("Outlook authentication expired. User must re-authenticate.");

            var graphClient = new GraphServiceClient(new BaseBearerTokenAuthenticationProvider(
                new TokenProvider(account.AccessToken)));

            // Construct the message
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

            // Add attachments if provided
            if (attachments != null && attachments.Any())
            {
                // Initialize the attachments list
                message.Attachments = new List<Attachment>();

                foreach (var file in attachments)
                {
                    using (var memStream = new MemoryStream())
                    {
                        await file.CopyToAsync(memStream);
                        byte[] bytes = memStream.ToArray();

                        // Create a FileAttachment object
                        var fileAttachment = new FileAttachment
                        {
                            Name = file.FileName,
                            ContentType = file.ContentType,
                            OdataType = "#microsoft.graph.fileAttachment",
                            IsInline = false
                        };

                        // Convert the byte array to a Base64 string
                        fileAttachment.ContentBytes = bytes; // Use the byte array directly

                        message.Attachments.Add(fileAttachment);
                    }
                }
            }

            // Send using API
            await graphClient.Me.SendMail.PostAsync(new SendMailPostRequestBody
            {
                Message = message,
                SaveToSentItems = true
            });

            // Store sent message in database
            var sentMessage = new MessageMetadata
            {
                UserId = userId,
                PlatformType = "Outlook",
                ExternalMessageId = Guid.NewGuid().ToString(), // Generate a temp ID for sent messages
                Subject = subject,
                Snippet = body.Length > 100 ? body.Substring(0, 97) + "..." : body,
                Body = body,
                HtmlBody = body,
                From = "You", // It's sent by the user
                ReceivedAt = DateTime.UtcNow,
                IsRead = true, // Sent messages are already read
                HasAttachments = attachments != null && attachments.Any()
            };

            _dbContext.MessageMetadatas.Add(sentMessage);
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
                        MessageMetadataId = sentMessage.Id,
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
        }

        public async Task<(byte[] Content, string ContentType, string FileName)> GetAttachmentAsync(int userId, string messageId, string attachmentId)
        {
            var account = await _dbContext.PlatformAccounts
                .FirstOrDefaultAsync(pa => pa.UserId == userId && pa.PlatformType == "Outlook");

            if (account == null)
                throw new Exception("No Outlook account connected.");

            var isTokenValid = await EnsureValidAccessTokenAsync(account);
            if (!isTokenValid)
                throw new Exception("Outlook authentication expired. User must re-authenticate.");

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

            // If not found in DB or content not stored, fetch from Graph API
            try
            {
                var graphClient = new GraphServiceClient(new BaseBearerTokenAuthenticationProvider(
                    new TokenProvider(account.AccessToken)));

                // Get the attachment from Microsoft Graph
                var msgId = messageId.Split('|').Last();
                var attachment = await graphClient.Me.Messages[msgId].Attachments[attachmentId]
                    .GetAsync();

                if (attachment is FileAttachment fileAttachment && fileAttachment.ContentBytes != null)
                {
                    // Convert from Base64 string to byte array
                    byte[] content = fileAttachment.ContentBytes; // Use it directly
                    return (content, attachment.ContentType, attachment.Name);
                }
                else
                {
                    throw new InvalidOperationException("The attachment is not a file attachment or has no content.");
                }
            }
            catch (ODataError ex)
            {
                _logger.LogError(ex, "Error accessing Outlook attachment {AttachmentId} for message {MessageId}", attachmentId, messageId);
                throw new Exception($"Error accessing attachment: {ex.Error?.Message}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error accessing Outlook attachment {AttachmentId}", attachmentId);
                throw;
            }
        }

        public async Task<bool> EnsureValidAccessTokenAsync(PlatformAccount account)
        {
            if (account.TokenExpiresAt > DateTime.UtcNow)
                return true; // still valid

            if (string.IsNullOrEmpty(account.RefreshToken))
            {
                _logger.LogWarning("No refresh token available for account {AccountId}", account.Id);
                return false;
            }

            var clientId = _config["OutlookSettings:ClientId"];
            var clientSecret = _config["OutlookSettings:ClientSecret"];

            try
            {
                // Use the refresh token directly with the token endpoint
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

                    // Update tokens
                    account.AccessToken = tokenData.access_token;

                    // Only update refresh token if a new one was provided
                    if (!string.IsNullOrEmpty(tokenData.refresh_token))
                    {
                        account.RefreshToken = tokenData.refresh_token;
                    }

                    account.TokenExpiresAt = DateTime.UtcNow.AddSeconds(tokenData.expires_in);

                    await _dbContext.SaveChangesAsync();
                    return true;
                }
                else
                {
                    var errorContent = await response.Content.ReadAsStringAsync();
                    _logger.LogError("Error refreshing token: {Error}", errorContent);
                    return false;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error refreshing token for account {AccountId}", account.Id);
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

    // Response classes for token endpoints
    public class TokenResponse
    {
        public string access_token { get; set; }
        public string refresh_token { get; set; }
        public int expires_in { get; set; }
        public string token_type { get; set; }
        public string scope { get; set; }
    }
}