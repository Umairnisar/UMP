using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using UMB.Model.Models;

namespace UMB.Api.Services.Integrations
{
    public class LinkedInIntegrationService : ILinkedInIntegrationService
    {
        private readonly IConfiguration _config;
        private readonly AppDbContext _dbContext;
        private readonly HttpClient _httpClient;
        private readonly ILogger<LinkedInIntegrationService> _logger;

        public LinkedInIntegrationService(
            IConfiguration config,
            AppDbContext dbContext,
            IHttpClientFactory httpClientFactory,
            ILogger<LinkedInIntegrationService> logger)
        {
            _config = config;
            _dbContext = dbContext;
            _httpClient = httpClientFactory.CreateClient();
            _logger = logger;
        }

        //public string GetAuthorizationUrl(int userId, string accountIdentifier)
        //{
        //    var clientId = _config["LinkedInSettings:ClientId"];
        //    var redirectUri = _config["LinkedInSettings:RedirectUri"];
        //    var scopes = "r_emailaddress r_inmail w_member_social r_basicprofile r_messages";

        //    var authUrl = $"https://www.linkedin.com/oauth/v2/authorization" +
        //                  $"?response_type=code" +
        //                  $"&client_id={clientId}" +
        //                  $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
        //                  $"&scope={Uri.EscapeDataString(scopes)}" +
        //                  $"&state={userId}|{accountIdentifier}"; // Include accountIdentifier in state

        //    return authUrl;
        //}
        public string GetAuthorizationUrl(int userId, string accountIdentifier)
        {
            var clientId = _config["LinkedInSettings:ClientId"];
            var redirectUri = _config["LinkedInSettings:RedirectUri"];
            var state = $"{userId}|{accountIdentifier}"; // Combine userId and accountIdentifier
            var scopes = "openid profile email";

            return $"https://www.linkedin.com/oauth/v2/authorization" +
                   $"?response_type=code" +
                   $"&client_id={clientId}" +
                   $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
                   $"&state={Uri.EscapeDataString(state)}" +
                   $"&scope={Uri.EscapeDataString(scopes)}";
        }
        public async Task ExchangeCodeForTokenAsync(int userId, string code, string accountIdentifier)
        {
            try
            {
                var clientId = _config["LinkedInSettings:ClientId"]
                    ?? throw new InvalidOperationException("LinkedInSettings:ClientId is not configured.");
                var clientSecret = _config["LinkedInSettings:ClientSecret"]
                    ?? throw new InvalidOperationException("LinkedInSettings:ClientSecret is not configured.");
                var redirectUri = _config["LinkedInSettings:RedirectUri"]
                    ?? throw new InvalidOperationException("LinkedInSettings:RedirectUri is not configured.");
                var tokenUrl = "https://www.linkedin.com/oauth/v2/accessToken";

                var requestBody = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            { "grant_type", "authorization_code" },
            { "code", code },
            { "redirect_uri", redirectUri },
            { "client_id", clientId },
            { "client_secret", clientSecret }
        });

                var response = await _httpClient.PostAsync(tokenUrl, requestBody);
                if (!response.IsSuccessStatusCode)
                {
                    var errorContent = await response.Content.ReadAsStringAsync();
                    _logger.LogError("LinkedIn token request failed with status {StatusCode}. Response: {ErrorContent}",
                        response.StatusCode, errorContent);
                    throw new HttpRequestException($"Token request failed: {response.StatusCode}. Details: {errorContent}");
                }

                var json = await response.Content.ReadAsStringAsync();
                var tokenData = JsonSerializer.Deserialize<LinkedInTokenResponse>(json);

                var platformAccount = await _dbContext.PlatformAccounts
                    .FirstOrDefaultAsync(pa => pa.UserId == userId && pa.PlatformType == "LinkedIn" && pa.AccountIdentifier == accountIdentifier);

                if (platformAccount == null)
                {
                    platformAccount = new PlatformAccount
                    {
                        UserId = userId,
                        PlatformType = "LinkedIn",
                        AccountIdentifier = accountIdentifier,
                        CreatedAt = DateTime.UtcNow,
                        ExternalAccountId="LinkedIn",
                        RefreshToken=tokenData.access_token
                    };
                    _dbContext.PlatformAccounts.Add(platformAccount);
                }

                platformAccount.AccessToken = tokenData.access_token;
                platformAccount.TokenExpiresAt = DateTime.UtcNow.AddSeconds(tokenData.expires_in);
                platformAccount.UpdatedAt = DateTime.UtcNow;

                await _dbContext.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error exchanging code for LinkedIn token for user {UserId}, account {AccountIdentifier}", userId, accountIdentifier);
                throw;
            }
        }

        //public async Task ExchangeCodeForTokenAsync(int userId, string code, string accountIdentifier)
        //{
        //    try
        //    {
        //        var clientId = _config["LinkedInSettings:ClientId"];
        //        var clientSecret = _config["LinkedInSettings:ClientSecret"];
        //        var redirectUri = _config["LinkedInSettings:RedirectUri"];
        //        var tokenUrl = "https://www.linkedin.com/oauth/v2/accessToken";

        //        var requestBody = new FormUrlEncodedContent(new Dictionary<string, string>
        //        {
        //            { "grant_type", "authorization_code" },
        //            { "code", code },
        //            { "redirect_uri", redirectUri },
        //            { "client_id", clientId },
        //            { "client_secret", clientSecret }
        //        });

        //        var response = await _httpClient.PostAsync(tokenUrl, requestBody);
        //        response.EnsureSuccessStatusCode();
        //        var json = await response.Content.ReadAsStringAsync();
        //        var tokenData = JsonSerializer.Deserialize<LinkedInTokenResponse>(json);

        //        var platformAccount = await _dbContext.PlatformAccounts
        //            .FirstOrDefaultAsync(pa => pa.UserId == userId && pa.PlatformType == "LinkedIn" && pa.AccountIdentifier == accountIdentifier);

        //        if (platformAccount == null)
        //        {
        //            platformAccount = new PlatformAccount
        //            {
        //                UserId = userId,
        //                PlatformType = "LinkedIn",
        //                AccountIdentifier = accountIdentifier, // e.g., LinkedIn profile ID or email
        //                CreatedAt = DateTime.UtcNow
        //            };
        //            _dbContext.PlatformAccounts.Add(platformAccount);
        //        }

        //        platformAccount.AccessToken = tokenData.access_token;
        //        platformAccount.TokenExpiresAt = DateTime.UtcNow.AddSeconds(tokenData.expires_in);
        //        platformAccount.UpdatedAt = DateTime.UtcNow;

        //        await _dbContext.SaveChangesAsync();
        //    }
        //    catch (Exception ex)
        //    {
        //        _logger.LogError(ex, "Error exchanging code for LinkedIn token for user {UserId}, account {AccountIdentifier}", userId, accountIdentifier);
        //        throw;
        //    }
        //}

        public async Task<List<MessageMetadata>> FetchMessagesAsync(int userId, string accountIdentifier = null)
        {
            try
            {
                var query = _dbContext.PlatformAccounts
                    .Where(pa => pa.UserId == userId && pa.PlatformType == "LinkedIn");
                if (!string.IsNullOrEmpty(accountIdentifier))
                {
                    query = query.Where(pa => pa.AccountIdentifier == accountIdentifier);
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
                    _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);

                    var response = await _httpClient.GetAsync("https://api.linkedin.com/v2/messages?q=conversations&fields=id,participants,subject,text,createdAt");
                    if (!response.IsSuccessStatusCode)
                    {
                        _logger.LogWarning("Failed to fetch LinkedIn messages for account {AccountIdentifier}: {StatusCode}", account.AccountIdentifier, response.StatusCode);
                        continue;
                    }

                    var json = await response.Content.ReadAsStringAsync();
                    var messages = JsonSerializer.Deserialize<LinkedInMessagesResponse>(json);

                    var messageMetadataList = new List<MessageMetadata>();
                    foreach (var msg in messages?.elements ?? new List<LinkedInMessage>())
                    {
                        var participants = msg.participants?.Select(p => p.entityUrn).ToList() ?? new List<string>();
                        var sender = participants.FirstOrDefault(p => !p.Contains("urn:li:person:" + account.ExternalAccountId)) ?? "Unknown";

                        var messageMetadata = new MessageMetadata
                        {
                            UserId = userId,
                            PlatformType = "LinkedIn",
                            ExternalMessageId = msg.id,
                            AccountIdentifier = account.AccountIdentifier,
                            Subject = msg.subject ?? "LinkedIn Message",
                            Snippet = msg.text?.Length > 100 ? msg.text.Substring(0, 97) + "..." : msg.text,
                            Body = msg.text,
                            From = sender,
                            ReceivedAt = DateTimeOffset.FromUnixTimeMilliseconds(msg.createdAt).UtcDateTime,
                            IsRead = false
                        };

                        messageMetadataList.Add(messageMetadata);
                    }

                    var existingIds = await _dbContext.MessageMetadatas
                        .Where(m => m.UserId == userId && m.PlatformType == "LinkedIn" && m.AccountIdentifier == account.AccountIdentifier)
                        .Select(m => m.ExternalMessageId)
                        .ToListAsync();

                    var newMessages = messageMetadataList.Where(m => !existingIds.Contains(m.ExternalMessageId)).ToList();
                    if (newMessages.Any())
                    {
                        await _dbContext.MessageMetadatas.AddRangeAsync(newMessages);
                        await _dbContext.SaveChangesAsync();
                    }

                    allMessages.AddRange(messageMetadataList);
                }

                return allMessages.OrderByDescending(m => m.ReceivedAt).ToList();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching LinkedIn messages for user {UserId}, account {AccountIdentifier}", userId, accountIdentifier);
                return new List<MessageMetadata>();
            }
        }

        public async Task<string> SendMessageAsync(int userId, string recipientUrn, string message, string accountIdentifier)
        {
            var account = await _dbContext.PlatformAccounts
                .FirstOrDefaultAsync(pa => pa.UserId == userId && pa.PlatformType == "LinkedIn" && pa.AccountIdentifier == accountIdentifier);

            if (account == null)
                throw new Exception($"No LinkedIn account connected for {accountIdentifier}.");

            await EnsureValidAccessTokenAsync(account);
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);

            var payload = new
            {
                recipients = new[] { new { personUrn = recipientUrn } },
                subject = "Message from UMB",
                text = message
            };

            var content = new StringContent(
                JsonSerializer.Serialize(payload),
                Encoding.UTF8,
                "application/json");

            var response = await _httpClient.PostAsync("https://api.linkedin.com/v2/messages", content);
            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                throw new Exception($"Failed to send LinkedIn message: {errorContent}");
            }

            var responseContent = await response.Content.ReadAsStringAsync();
            var messageId = JsonSerializer.Deserialize<LinkedInMessageResponse>(responseContent)?.id ?? Guid.NewGuid().ToString();

            var messageMetadata = new MessageMetadata
            {
                UserId = userId,
                PlatformType = "LinkedIn",
                ExternalMessageId = messageId,
                AccountIdentifier = account.AccountIdentifier,
                Subject = "LinkedIn Message",
                Snippet = message.Length > 100 ? message.Substring(0, 97) + "..." : message,
                Body = message,
                From = "You",
                ReceivedAt = DateTime.UtcNow,
                IsRead = true
            };

            _dbContext.MessageMetadatas.Add(messageMetadata);
            await _dbContext.SaveChangesAsync();
            return messageId;
        }

        private async Task EnsureValidAccessTokenAsync(PlatformAccount account)
        {
            if (account.TokenExpiresAt > DateTime.UtcNow.AddMinutes(-5))
                return;

            // LinkedIn does not support refresh tokens, so re-authentication is required
            _logger.LogWarning("LinkedIn access token expired for account {AccountIdentifier}. Re-authentication required.", account.AccountIdentifier);
            throw new Exception("LinkedIn access token expired. Please re-authenticate.");
        }
    }

    // Helper classes for JSON deserialization
    public class LinkedInTokenResponse
    {
        public string access_token { get; set; }
        public int expires_in { get; set; }
    }

    public class LinkedInMessagesResponse
    {
        public List<LinkedInMessage> elements { get; set; }
    }

    public class LinkedInMessage
    {
        public string id { get; set; }
        public List<LinkedInParticipant> participants { get; set; }
        public string subject { get; set; }
        public string text { get; set; }
        public long createdAt { get; set; }
    }

    public class LinkedInParticipant
    {
        public string entityUrn { get; set; }
    }

    public class LinkedInMessageResponse
    {
        public string id { get; set; }
    }
}