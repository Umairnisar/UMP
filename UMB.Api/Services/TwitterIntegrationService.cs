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
    public class TwitterIntegrationService : ITwitterIntegrationService
    {
        private readonly IConfiguration _config;
        private readonly AppDbContext _dbContext;
        private readonly HttpClient _httpClient;
        private readonly ILogger<TwitterIntegrationService> _logger;

        public TwitterIntegrationService(
            IConfiguration config,
            AppDbContext dbContext,
            IHttpClientFactory httpClientFactory,
            ILogger<TwitterIntegrationService> logger)
        {
            _config = config;
            _dbContext = dbContext;
            _httpClient = httpClientFactory.CreateClient();
            _logger = logger;
        }

        public string GetAuthorizationUrl(int userId, string accountIdentifier)
        {
            if (string.IsNullOrEmpty(accountIdentifier))
                throw new ArgumentException("AccountIdentifier cannot be null or empty.", nameof(accountIdentifier));

            var clientId = _config["TwitterSettings:ClientId"];
            var redirectUri = _config["TwitterSettings:RedirectUri"];
            var scopes = "tweet.read users.read dm.read dm.write offline.access";

            var authUrl = $"https://twitter.com/i/oauth2/authorize" +
                          $"?response_type=code" +
                          $"&client_id={clientId}" +
                          $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
                          $"&scope={Uri.EscapeDataString(scopes)}" +
                          $"&state={userId}|{accountIdentifier}" +
                          $"&code_challenge=challenge" +
                          $"&code_challenge_method=plain";

            return authUrl;
        }

        public async Task ExchangeCodeForTokenAsync(int userId, string code, string accountIdentifier)
        {
            if (string.IsNullOrEmpty(accountIdentifier))
                throw new ArgumentException("AccountIdentifier cannot be null or empty.", nameof(accountIdentifier));

            try
            {
                var clientId = _config["TwitterSettings:ClientId"];
                var clientSecret = _config["TwitterSettings:ClientSecret"];
                var redirectUri = _config["TwitterSettings:RedirectUri"];
                var tokenUrl = "https://api.twitter.com/2/oauth2/token";

                var requestBody = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    { "grant_type", "authorization_code" },
                    { "code", code },
                    { "redirect_uri", redirectUri },
                    { "client_id", clientId },
                    { "code_verifier", "challenge" }
                });

                var authHeader = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{clientId}:{clientSecret}"));
                _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", authHeader);

                var response = await _httpClient.PostAsync(tokenUrl, requestBody);
                response.EnsureSuccessStatusCode();
                var json = await response.Content.ReadAsStringAsync();
                var tokenData = JsonSerializer.Deserialize<TwitterTokenResponse>(json);

                _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokenData.access_token);
                var profileResponse = await _httpClient.GetAsync("https://api.twitter.com/2/users/me");
                if (!profileResponse.IsSuccessStatusCode)
                {
                    var errorContent = await profileResponse.Content.ReadAsStringAsync();
                    throw new Exception($"Failed to fetch Twitter profile: {errorContent}");
                }

                var profileJson = await profileResponse.Content.ReadAsStringAsync();
                var profileData = JsonSerializer.Deserialize<TwitterProfileResponse>(profileJson);
                var externalAccountId = profileData.data?.id;

                var platformAccount = await _dbContext.PlatformAccounts
                    .FirstOrDefaultAsync(pa => pa.UserId == userId && pa.PlatformType == "Twitter" && pa.AccountIdentifier == accountIdentifier);

                if (platformAccount == null)
                {
                    platformAccount = new PlatformAccount
                    {
                        UserId = userId,
                        PlatformType = "Twitter",
                        AccountIdentifier = accountIdentifier,
                        CreatedAt = DateTime.UtcNow
                    };
                    _dbContext.PlatformAccounts.Add(platformAccount);
                }

                platformAccount.AccessToken = tokenData.access_token;
                platformAccount.RefreshToken = tokenData.refresh_token;
                platformAccount.TokenExpiresAt = DateTime.UtcNow.AddSeconds(tokenData.expires_in);
                platformAccount.ExternalAccountId = externalAccountId;
                platformAccount.UpdatedAt = DateTime.UtcNow;

                await _dbContext.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error exchanging code for Twitter token for user {UserId}, account {AccountIdentifier}", userId, accountIdentifier);
                throw;
            }
        }

        public async Task<List<MessageMetadata>> FetchMessagesAsync(int userId, string accountIdentifier = null)
        {
            try
            {
                var query = _dbContext.PlatformAccounts
                    .Where(pa => pa.UserId == userId && pa.PlatformType == "Twitter");
                if (!string.IsNullOrEmpty(accountIdentifier))
                {
                    query = query.Where(pa => pa.AccountIdentifier == accountIdentifier);
                }

                var accounts = await query.ToListAsync();
                if (!accounts.Any())
                {
                    _logger.LogInformation("No Twitter accounts found for user {UserId}", userId);
                    return new List<MessageMetadata>();
                }

                var allMessages = new List<MessageMetadata>();
                foreach (var account in accounts)
                {
                    if (string.IsNullOrEmpty(account.AccountIdentifier))
                    {
                        _logger.LogWarning("Skipping Twitter account with null or empty AccountIdentifier for user {UserId}", userId);
                        continue;
                    }

                    if (string.IsNullOrEmpty(account.AccessToken))
                    {
                        _logger.LogWarning("Skipping Twitter account {AccountIdentifier} due to missing AccessToken", account.AccountIdentifier);
                        continue;
                    }

                    try
                    {
                        await EnsureValidAccessTokenAsync(account);
                        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);

                        var response = await _httpClient.GetAsync("https://api.twitter.com/2/dm_conversations/with");
                        if (!response.IsSuccessStatusCode)
                        {
                            var errorContent = await response.Content.ReadAsStringAsync();
                            _logger.LogWarning("Failed to fetch Twitter DM conversations for account {AccountIdentifier}: {StatusCode} - {ErrorContent}",
                                account.AccountIdentifier, response.StatusCode, errorContent);
                            continue;
                        }

                        var json = await response.Content.ReadAsStringAsync();
                        var conversations = JsonSerializer.Deserialize<TwitterDMConversationsResponse>(json);

                        var messageMetadataList = new List<MessageMetadata>();
                        foreach (var conv in conversations?.data ?? new List<TwitterDMConversation>())
                        {
                            var messagesResponse = await _httpClient.GetAsync($"https://api.twitter.com/2/dm_conversations/{conv.dm_conversation_id}/dm_events");
                            if (!messagesResponse.IsSuccessStatusCode)
                            {
                                var errorContent = await messagesResponse.Content.ReadAsStringAsync();
                                _logger.LogWarning("Failed to fetch DMs for conversation {ConversationId} for account {AccountIdentifier}: {StatusCode} - {ErrorContent}",
                                    conv.dm_conversation_id, account.AccountIdentifier, messagesResponse.StatusCode, errorContent);
                                continue;
                            }

                            var messagesJson = await messagesResponse.Content.ReadAsStringAsync();
                            var messages = JsonSerializer.Deserialize<TwitterDMEventsResponse>(messagesJson);

                            foreach (var msg in messages?.data ?? new List<TwitterDMEvent>())
                            {
                                if (msg.event_type != "MessageCreate")
                                    continue;

                                var senderId = msg.sender_id;
                                var isFromUser = senderId == account.ExternalAccountId;
                                var from = isFromUser ? "You" : senderId;

                                var messageMetadata = new MessageMetadata
                                {
                                    UserId = userId,
                                    PlatformType = "Twitter",
                                    ExternalMessageId = msg.id,
                                    AccountIdentifier = account.AccountIdentifier,
                                    Subject = "Twitter DM",
                                    Snippet = msg.text.Length > 100 ? msg.text.Substring(0, 97) + "..." : msg.text,
                                    Body = msg.text,
                                    From = from,
                                    ReceivedAt = DateTime.UtcNow, // Twitter API v2 doesn't provide exact timestamp
                                    IsRead = false,
                                    IsNew = true,
                                    IsAutoReplied = false
                                };

                                messageMetadataList.Add(messageMetadata);
                            }
                        }

                        var existingIds = await _dbContext.MessageMetadatas
                            .Where(m => m.UserId == userId && m.PlatformType == "Twitter" && m.AccountIdentifier == account.AccountIdentifier)
                            .Select(m => m.ExternalMessageId)
                            .ToListAsync();

                        var newMessages = messageMetadataList.Where(m => !existingIds.Contains(m.ExternalMessageId)).ToList();
                        if (newMessages.Any())
                        {
                            await _dbContext.MessageMetadatas.AddRangeAsync(newMessages);
                            await _dbContext.SaveChangesAsync();
                            _logger.LogInformation("Saved {Count} new Twitter DMs for account {AccountIdentifier}", newMessages.Count, account.AccountIdentifier);
                        }

                        allMessages.AddRange(messageMetadataList);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error processing Twitter DMs for account {AccountIdentifier}", account.AccountIdentifier);
                        continue;
                    }
                }

                await _dbContext.MessageMetadatas
                    .Where(m => m.UserId == userId && m.PlatformType == "Twitter" && !allMessages.Select(x => x.ExternalMessageId).Contains(m.ExternalMessageId))
                    .ExecuteUpdateAsync(setters => setters.SetProperty(m => m.IsNew, false));

                return allMessages.OrderByDescending(m => m.ReceivedAt).ToList();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching Twitter DMs for user {UserId}, account {AccountIdentifier}", userId, accountIdentifier);
                return new List<MessageMetadata>();
            }
        }

        public async Task<string> SendMessageAsync(int userId, string recipientId, string message, string accountIdentifier)
        {
            var account = await _dbContext.PlatformAccounts
                .FirstOrDefaultAsync(pa => pa.UserId == userId && pa.PlatformType == "Twitter" && pa.AccountIdentifier == accountIdentifier);

            if (account == null)
                throw new Exception($"No Twitter account connected for {accountIdentifier}.");

            await EnsureValidAccessTokenAsync(account);
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);

            var payload = new
            {
                text = message,
                participant_ids = new[] { recipientId }
            };

            var content = new StringContent(
                JsonSerializer.Serialize(payload),
                Encoding.UTF8,
                "application/json");

            var response = await _httpClient.PostAsync("https://api.twitter.com/2/dm_conversations", content);
            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                throw new Exception($"Failed to send Twitter DM: {errorContent}");
            }

            var responseContent = await response.Content.ReadAsStringAsync();
            var messageId = JsonSerializer.Deserialize<TwitterDMResponse>(responseContent)?.data?.dm_event_id ?? Guid.NewGuid().ToString();

            var messageMetadata = new MessageMetadata
            {
                UserId = userId,
                PlatformType = "Twitter",
                ExternalMessageId = messageId,
                AccountIdentifier = account.AccountIdentifier,
                Subject = "Twitter DM",
                Snippet = message.Length > 100 ? message.Substring(0, 97) + "..." : message,
                Body = message,
                From = "You",
                ReceivedAt = DateTime.UtcNow,
                IsRead = true,
                IsNew = false,
                IsAutoReplied = false
            };

            _dbContext.MessageMetadatas.Add(messageMetadata);
            await _dbContext.SaveChangesAsync();
            return messageId;
        }

        private async Task EnsureValidAccessTokenAsync(PlatformAccount account)
        {
            if (string.IsNullOrEmpty(account.AccessToken))
                throw new Exception($"No access token available for Twitter account {account.AccountIdentifier}.");

            if (account.TokenExpiresAt > DateTime.UtcNow.AddMinutes(-5))
            {
                _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);
                var userResponse = await _httpClient.GetAsync("https://api.twitter.com/2/users/me");
                if (userResponse.IsSuccessStatusCode)
                    return;

                var errorContent = await userResponse.Content.ReadAsStringAsync();
                _logger.LogWarning("Invalid Twitter access token for account {AccountIdentifier}: {ErrorContent}", account.AccountIdentifier, errorContent);
            }

            if (string.IsNullOrEmpty(account.RefreshToken))
                throw new Exception($"No refresh token available for Twitter account {account.AccountIdentifier}.");

            var clientId = _config["TwitterSettings:ClientId"];
            var clientSecret = _config["TwitterSettings:ClientSecret"];
            var tokenUrl = "https://api.twitter.com/2/oauth2/token";

            var requestBody = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                { "grant_type", "refresh_token" },
                { "refresh_token", account.RefreshToken },
                { "client_id", clientId }
            });

            var authHeader = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{clientId}:{clientSecret}"));
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", authHeader);

            var response = await _httpClient.PostAsync(tokenUrl, requestBody);
            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                _logger.LogError("Failed to refresh Twitter token for account {AccountIdentifier}: {ErrorContent}", account.AccountIdentifier, errorContent);
                throw new Exception("Failed to refresh Twitter access token.");
            }

            var json = await response.Content.ReadAsStringAsync();
            var tokenData = JsonSerializer.Deserialize<TwitterTokenResponse>(json);

            account.AccessToken = tokenData.access_token;
            account.RefreshToken = tokenData.refresh_token;
            account.TokenExpiresAt = DateTime.UtcNow.AddSeconds(tokenData.expires_in);
            account.UpdatedAt = DateTime.UtcNow;

            await _dbContext.SaveChangesAsync();
        }
    }

    public class TwitterTokenResponse
    {
        public string access_token { get; set; }
        public string refresh_token { get; set; }
        public int expires_in { get; set; }
    }

    public class TwitterProfileResponse
    {
        public TwitterUserData data { get; set; }
    }

    public class TwitterUserData
    {
        public string id { get; set; }
        public string name { get; set; }
        public string username { get; set; }
    }

    public class TwitterDMConversationsResponse
    {
        public List<TwitterDMConversation> data { get; set; }
    }

    public class TwitterDMConversation
    {
        public string dm_conversation_id { get; set; }
        public List<string> participant_ids { get; set; }
    }

    public class TwitterDMEventsResponse
    {
        public List<TwitterDMEvent> data { get; set; }
    }

    public class TwitterDMEvent
    {
        public string id { get; set; }
        public string event_type { get; set; }
        public string text { get; set; }
        public string sender_id { get; set; }
    }

    public class TwitterDMResponse
    {
        public TwitterDMData data { get; set; }
    }

    public class TwitterDMData
    {
        public string dm_event_id { get; set; }
    }
}