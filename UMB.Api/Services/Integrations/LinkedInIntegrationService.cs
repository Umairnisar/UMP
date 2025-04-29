using Microsoft.EntityFrameworkCore;
using System.Net.Http;
using System.Text.Json;
using System.Text;
using System.Net.Http.Headers;
using System.Text.Json.Serialization;
using UMB.Model.Models;

namespace UMB.Api.Services.Integrations
{
    public class LinkedInIntegrationService : ILinkedInIntegrationService
    {
        private readonly IConfiguration _config;
        private readonly AppDbContext _dbContext;
        private readonly ILogger<LinkedInIntegrationService> _logger;
        private readonly HttpClient _httpClient;

        public LinkedInIntegrationService(
            IConfiguration config,
            AppDbContext dbContext,
            ILogger<LinkedInIntegrationService> logger,
            IHttpClientFactory httpClientFactory)
        {
            _config = config;
            _dbContext = dbContext;
            _logger = logger;
            _httpClient = httpClientFactory.CreateClient("LinkedIn");
        }

        //public string GetAuthorizationUrl(int userId)
        //{
        //    try
        //    {
        //        var clientId = _config["LinkedInSettings:ClientId"];
        //        if (string.IsNullOrEmpty(clientId))
        //        {
        //            _logger.LogError("LinkedIn ClientId is missing in configuration");
        //            throw new InvalidOperationException("LinkedIn client ID is not configured");
        //        }

        //        var redirectUri = _config["LinkedInSettings:RedirectUri"];
        //        // URL encode the redirect URI to ensure it's properly formatted
        //        var encodedRedirectUri = Uri.EscapeDataString(redirectUri);

        //        // LinkedIn scopes - Note that w_messages requires LinkedIn Marketing Developer Platform approval
        //        var scopes = "r_liteprofile r_emailaddress w_member_social";

        //        var state = Uri.EscapeDataString(userId.ToString());

        //        var url = $"https://www.linkedin.com/oauth/v2/authorization" +
        //                  $"?response_type=code" +
        //                  $"&client_id={clientId}" +
        //                  $"&redirect_uri={encodedRedirectUri}" +
        //                  $"&scope={scopes}" +
        //                  $"&state={state}";

        //        _logger.LogInformation("Generated LinkedIn authorization URL for user {UserId}", userId);
        //        return url;
        //    }
        //    catch (Exception ex)
        //    {
        //        _logger.LogError(ex, "Error generating LinkedIn authorization URL for user {UserId}", userId);
        //        throw;
        //    }
        //}

        public string GetAuthorizationUrl(int userId)
        {
            var clientId = _config["LinkedInSettings:ClientId"];
            var redirectUri = _config["LinkedInSettings:RedirectUri"];
            var scopes = "r_liteprofile%20r_emailaddress%20w_member_social%20w_messages";
            // Check which scopes are needed; LinkedIn often requires special permission for messaging

            var url = $"https://www.linkedin.com/oauth/v2/authorization" +
                      $"?response_type=code" +
                      $"&client_id={clientId}" +
                      $"&redirect_uri={redirectUri}" +
                      $"&scope={scopes}" +
                      $"&state={userId}";

            return url;
        }

        public async Task<bool> ExchangeCodeForTokenAsync(int userId, string code)
        {
            try
            {
                if (string.IsNullOrEmpty(code))
                {
                    _logger.LogError("Cannot exchange empty authorization code for user {UserId}", userId);
                    throw new ArgumentException("Authorization code cannot be empty", nameof(code));
                }

                var clientId = _config["LinkedInSettings:ClientId"];
                var clientSecret = _config["LinkedInSettings:ClientSecret"];
                var redirectUri = _config["LinkedInSettings:RedirectUri"];

                var tokenRequestUrl = "https://www.linkedin.com/oauth/v2/accessToken";

                var requestBody = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    { "grant_type", "authorization_code" },
                    { "code", code },
                    { "redirect_uri", redirectUri },
                    { "client_id", clientId },
                    { "client_secret", clientSecret }
                });

                var response = await _httpClient.PostAsync(tokenRequestUrl, requestBody);

                // Read the response content for error details if needed
                var responseContent = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogError("LinkedIn token exchange failed with status {StatusCode}: {ResponseContent}",
                        response.StatusCode, responseContent);
                    throw new HttpRequestException($"Failed to exchange code for token. Status: {response.StatusCode}, Response: {responseContent}");
                }

                var tokenOptions = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                };

                var tokenData = JsonSerializer.Deserialize<LinkedInTokenResponse>(responseContent, tokenOptions);

                if (tokenData == null || string.IsNullOrEmpty(tokenData.AccessToken))
                {
                    _logger.LogError("LinkedIn returned invalid token data for user {UserId}", userId);
                    throw new InvalidOperationException("Invalid token data received from LinkedIn");
                }

                var account = await _dbContext.PlatformAccounts
                    .FirstOrDefaultAsync(pa => pa.UserId == userId && pa.PlatformType == "LinkedIn");

                if (account == null)
                {
                    account = new PlatformAccount
                    {
                        UserId = userId,
                        PlatformType = "LinkedIn"
                    };
                    _dbContext.PlatformAccounts.Add(account);
                }

                account.AccessToken = tokenData.AccessToken;
                // LinkedIn typically doesn't provide refresh tokens for standard applications
                account.RefreshToken = tokenData.RefreshToken; // This might be null
                account.TokenExpiresAt = DateTime.UtcNow.AddSeconds(tokenData.ExpiresIn);
                account.ExternalAccountId = null; // LinkedIn doesn't provide this in the token response

                await _dbContext.SaveChangesAsync();
                _logger.LogInformation("Successfully exchanged code for LinkedIn token for user {UserId}", userId);

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error exchanging code for LinkedIn token for user {UserId}", userId);
                throw;
            }
        }

        public async Task<List<MessageMetadata>> FetchMessagesAsync(int userId)
        {
            try
            {
                var account = await _dbContext.PlatformAccounts
                    .FirstOrDefaultAsync(pa => pa.UserId == userId && pa.PlatformType == "LinkedIn");

                if (account == null)
                {
                    _logger.LogWarning("No LinkedIn account found for user {UserId}", userId);
                    return new List<MessageMetadata>();
                }

                await EnsureValidAccessTokenAsync(account);

                // LinkedIn's Messaging API requires Marketing Developer Platform approval
                // This is a simplified example using the Conversations API v2
                var url = "https://api.linkedin.com/v2/messaging/conversations";

                _httpClient.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", account.AccessToken);

                var response = await _httpClient.GetAsync(url);
                var responseContent = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogError("LinkedIn API call failed with status {StatusCode}: {ResponseContent}",
                        response.StatusCode, responseContent);

                    // If unauthorized, we should invalidate the token
                    if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                    {
                        account.TokenExpiresAt = DateTime.UtcNow.AddMinutes(-1); // Mark as expired
                        await _dbContext.SaveChangesAsync();
                    }

                    return new List<MessageMetadata>();
                }

                var messages = new List<MessageMetadata>();

                // Parse the response - actual structure will depend on LinkedIn's API response
                try
                {
                    // This is a simplified example - adjust based on actual LinkedIn API response
                    var conversationsResponse = JsonSerializer.Deserialize<LinkedInConversationsResponse>(responseContent);

                    if (conversationsResponse?.Elements != null)
                    {
                        foreach (var conversation in conversationsResponse.Elements)
                        {
                            // Get the most recent message in each conversation
                            if (conversation.Events?.Count > 0)
                            {
                                var latestEvent = conversation.Events[0]; // Assuming sorted by recency

                                var metadata = new MessageMetadata
                                {
                                    PlatformType = "LinkedIn",
                                    ExternalMessageId = latestEvent.MessageId,
                                    Subject = $"Conversation with {conversation.ParticipantsNames?.FirstOrDefault() ?? "Unknown"}",
                                    Snippet = (string)(latestEvent.Text?.Take(100) ?? "No content"),
                                    ReceivedAt = latestEvent.CreatedAt
                                };

                                messages.Add(metadata);
                            }
                        }
                    }
                }
                catch (JsonException ex)
                {
                    _logger.LogError(ex, "Error parsing LinkedIn messages response: {ResponseContent}", responseContent);
                }

                return messages;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching LinkedIn messages for user {UserId}", userId);
                return new List<MessageMetadata>();
            }
        }

        public async Task SendMessageAsync(int userId, string recipientId, string messageText)
        {
            try
            {
                var account = await _dbContext.PlatformAccounts
                    .FirstOrDefaultAsync(pa => pa.UserId == userId && pa.PlatformType == "LinkedIn");

                if (account == null)
                {
                    _logger.LogError("No LinkedIn account found for user {UserId}", userId);
                    throw new InvalidOperationException("No LinkedIn account connected.");
                }

                await EnsureValidAccessTokenAsync(account);

                _httpClient.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", account.AccessToken);

                // LinkedIn's Messaging API v2
                var url = "https://api.linkedin.com/v2/messaging/conversations";

                // This is a simplified structure - the actual API might require a different format
                var payload = new
                {
                    recipients = new[]
                    {
                        new { person = new { id = recipientId } }
                    },
                    messageContent = new
                    {
                        text = messageText
                    }
                };

                var content = new StringContent(
                    JsonSerializer.Serialize(payload),
                    Encoding.UTF8,
                    "application/json");

                var response = await _httpClient.PostAsync(url, content);
                var responseContent = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogError("Failed to send LinkedIn message: {StatusCode} - {Response}",
                        response.StatusCode, responseContent);

                    throw new HttpRequestException($"Failed to send LinkedIn message. Status: {response.StatusCode}, Response: {responseContent}");
                }

                _logger.LogInformation("Successfully sent LinkedIn message to recipient {RecipientId}", recipientId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending LinkedIn message for user {UserId} to recipient {RecipientId}",
                    userId, recipientId);
                throw;
            }
        }

        public async Task EnsureValidAccessTokenAsync(PlatformAccount account)
        {
            try
            {
                // Check if token is still valid
                if (account.TokenExpiresAt.HasValue && account.TokenExpiresAt > DateTime.UtcNow)
                    return; // Token is still valid

                _logger.LogInformation("LinkedIn token expired for account {AccountId}", account.Id);

                // LinkedIn typically doesn't provide refresh tokens for standard applications
                // If we have a refresh token, try to use it
                if (!string.IsNullOrEmpty(account.RefreshToken))
                {
                    try
                    {
                        var clientId = _config["LinkedInSettings:ClientId"];
                        var clientSecret = _config["LinkedInSettings:ClientSecret"];

                        var tokenRequestUrl = "https://www.linkedin.com/oauth/v2/accessToken";

                        var requestBody = new FormUrlEncodedContent(new Dictionary<string, string>
                        {
                            { "grant_type", "refresh_token" },
                            { "refresh_token", account.RefreshToken },
                            { "client_id", clientId },
                            { "client_secret", clientSecret }
                        });

                        var response = await _httpClient.PostAsync(tokenRequestUrl, requestBody);

                        if (response.IsSuccessStatusCode)
                        {
                            var json = await response.Content.ReadAsStringAsync();
                            var tokenData = JsonSerializer.Deserialize<LinkedInTokenResponse>(json);

                            account.AccessToken = tokenData.AccessToken;
                            // Update refresh token if a new one is provided
                            if (!string.IsNullOrEmpty(tokenData.RefreshToken))
                            {
                                account.RefreshToken = tokenData.RefreshToken;
                            }

                            account.TokenExpiresAt = DateTime.UtcNow.AddSeconds(tokenData.ExpiresIn);
                            await _dbContext.SaveChangesAsync();

                            _logger.LogInformation("Successfully refreshed LinkedIn token for account {AccountId}", account.Id);
                            return;
                        }
                        else
                        {
                            var responseContent = await response.Content.ReadAsStringAsync();
                            _logger.LogWarning("Failed to refresh LinkedIn token: {StatusCode} - {Response}",
                                response.StatusCode, responseContent);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error refreshing LinkedIn token for account {AccountId}", account.Id);
                    }
                }

                // If we reach here, we couldn't refresh the token
                throw new InvalidOperationException("LinkedIn token expired and cannot be refreshed. User must re-authorize.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error ensuring valid LinkedIn access token for account {AccountId}", account.Id);
                throw;
            }
        }

        Task ILinkedInIntegrationService.ExchangeCodeForTokenAsync(int userId, string code)
        {
            throw new NotImplementedException();
        }
    }

    // Response models for LinkedIn API
    public class LinkedInTokenResponse
    {
        [JsonPropertyName("access_token")]
        public string AccessToken { get; set; }

        [JsonPropertyName("expires_in")]
        public int ExpiresIn { get; set; }

        [JsonPropertyName("refresh_token")]
        public string RefreshToken { get; set; }
    }

    public class LinkedInConversationsResponse
    {
        public List<LinkedInConversation> Elements { get; set; }
    }

    public class LinkedInConversation
    {
        public string EntityUrn { get; set; }
        public List<string> ParticipantsNames { get; set; }
        public List<LinkedInMessageEvent> Events { get; set; }
    }

    public class LinkedInMessageEvent
    {
        public string MessageId { get; set; }
        public string Text { get; set; }
        public DateTime CreatedAt { get; set; }
    }
}