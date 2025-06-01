using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Linq;
using System.Threading.Tasks;
using UMB.Api.Services.Integrations;
using UMB.Model.Models;

namespace UMB.Api.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    //[Authorize]
    public class IntegrationController : ControllerBase
    {
        private readonly IGmailIntegrationService _gmailIntegration;
        private readonly ILinkedInIntegrationService _linkedinIntegration;
        private readonly IOutlookIntegrationService _outlookIntegration;
        private readonly IWhatsAppIntegrationService _whatsAppIntegration;
        private readonly ITwitterIntegrationService _twitterIntegration;
        private readonly IBaseIntegrationService _integrationService;
        private readonly AppDbContext _appDbContext;

        public IntegrationController(
            IGmailIntegrationService gmailIntegration,
            ILinkedInIntegrationService linkedinIntegration,
            IOutlookIntegrationService outlookIntegration,
            IWhatsAppIntegrationService whatsAppIntegration,
            ITwitterIntegrationService twitterIntegration,
            IBaseIntegrationService integrationService,
            AppDbContext appDbContext)
        {
            _gmailIntegration = gmailIntegration;
            _linkedinIntegration = linkedinIntegration;
            _outlookIntegration = outlookIntegration;
            _whatsAppIntegration = whatsAppIntegration;
            _twitterIntegration = twitterIntegration;
            _integrationService = integrationService;
            _appDbContext = appDbContext;
        }

        [HttpPost("gmail/connect")]
        public IActionResult ConnectGmail([FromBody] ConnectAccountRequest request)
        {
            if (string.IsNullOrEmpty(request.AccountIdentifier))
                return BadRequest("AccountIdentifier is required.");

            var userId = GetCurrentUserId();
            var url = _gmailIntegration.GetAuthorizationUrl(userId, request.AccountIdentifier);
            return Ok(new { url });
        }

        [HttpGet("gmail/callback")]
        public async Task<IActionResult> GmailCallback([FromQuery] string code, [FromQuery] string state)
        {
            var parts = state.Split('|'); // Format: userId|accountIdentifier
            if (parts.Length != 2)
                return BadRequest("Invalid state parameter.");

            var userId = int.Parse(parts[0]);
            var accountIdentifier = parts[1];
            await _gmailIntegration.ExchangeCodeForTokenAsync(userId, code, accountIdentifier);
            await SetActiveAccountAsync(userId, "Gmail", accountIdentifier);
            return Ok("Gmail account connected!");
        }

        [HttpGet("gmail/accounts")]
        public async Task<IActionResult> GetGmailAccounts()
        {
            var userId = GetCurrentUserId();
            var accounts = await _integrationService.GetUserPlatformsAsync(userId, "Gmail");
            var result = accounts.Select(p => new
            {
                id = p.Id.ToString(),
                accountIdentifier = p.AccountIdentifier,
                platformType = p.PlatformType,
                isActive = p.IsActive,
                isConnected = p.AccessToken != null,
                userId = p.UserId.ToString()
            });
            return Ok(result);
        }

        [HttpPost("gmail/switch")]
        public async Task<IActionResult> SwitchGmailAccount([FromBody] SwitchAccountRequest request)
        {
            if (string.IsNullOrEmpty(request.AccountIdentifier))
                return BadRequest("AccountIdentifier is required.");

            var userId = GetCurrentUserId();
            var success = await SetActiveAccountAsync(userId, "Gmail", request.AccountIdentifier);
            if (!success)
                return NotFound("Account not found or could not be switched.");

            return Ok($"Active Gmail account switched to {request.AccountIdentifier}.");
        }

        [HttpPost("linkedin/connect")]
        public IActionResult ConnectLinkedIn([FromBody] ConnectAccountRequest request)
        {
            if (string.IsNullOrEmpty(request.AccountIdentifier))
                return BadRequest("AccountIdentifier is required.");

            var userId = GetCurrentUserId();
            var url = _linkedinIntegration.GetAuthorizationUrl(userId, request.AccountIdentifier);
            return Ok(new { url });
        }

        [HttpGet("linkedin/callback")]
        public async Task<IActionResult> LinkedInCallback([FromQuery] string code, [FromQuery] string state)
        {
            var parts = state.Split('|'); // Format: userId|accountIdentifier
            if (parts.Length != 2)
                return BadRequest("Invalid state parameter.");

            var userId = int.Parse(parts[0]);
            var accountIdentifier = parts[1];
            await _linkedinIntegration.ExchangeCodeForTokenAsync(userId, code, accountIdentifier);
            await SetActiveAccountAsync(userId, "LinkedIn", accountIdentifier);
            return Ok("LinkedIn account connected!");
        }

        [HttpGet("linkedin/accounts")]
        public async Task<IActionResult> GetLinkedInAccounts()
        {
            var userId = GetCurrentUserId();
            var accounts = await _integrationService.GetUserPlatformsAsync(userId, "LinkedIn");
            var result = accounts.Select(p => new
            {
                id = p.Id.ToString(),
                accountIdentifier = p.AccountIdentifier,
                platformType = p.PlatformType,
                isActive = p.IsActive,
                isConnected = p.AccessToken != null,
                userId = p.UserId.ToString()
            });
            return Ok(result);
        }

        [HttpPost("linkedin/switch")]
        public async Task<IActionResult> SwitchLinkedInAccount([FromBody] SwitchAccountRequest request)
        {
            if (string.IsNullOrEmpty(request.AccountIdentifier))
                return BadRequest("AccountIdentifier is required.");

            var userId = GetCurrentUserId();
            var success = await SetActiveAccountAsync(userId, "LinkedIn", request.AccountIdentifier);
            if (!success)
                return NotFound("Account not found or could not be switched.");

            return Ok($"Active LinkedIn account switched to {request.AccountIdentifier}.");
        }

        [HttpPost("outlook/connect")]
        public IActionResult ConnectOutlook([FromBody] ConnectAccountRequest request)
        {
            if (string.IsNullOrEmpty(request.AccountIdentifier))
                return BadRequest("AccountIdentifier is required.");

            var userId = GetCurrentUserId();
            var url = _outlookIntegration.GetAuthorizationUrl(userId, request.AccountIdentifier);
            return Ok(new { url });
        }

        [HttpGet("outlook/callback")]
        public async Task<IActionResult> OutlookCallback([FromQuery] string code, [FromQuery] string state)
        {
            var parts = state.Split('|'); // Format: userId|accountIdentifier
            if (parts.Length != 2)
                return BadRequest("Invalid state parameter.");

            var userId = int.Parse(parts[0]);
            var accountIdentifier = parts[1];
            await _outlookIntegration.ExchangeCodeForTokenAsync(userId, code, accountIdentifier);
            await SetActiveAccountAsync(userId, "Outlook", accountIdentifier);
            return Ok("Outlook account connected!");
        }

        [HttpGet("outlook/accounts")]
        public async Task<IActionResult> GetOutlookAccounts()
        {
            var userId = GetCurrentUserId();
            var accounts = await _integrationService.GetUserPlatformsAsync(userId, "Outlook");
            var result = accounts.Select(p => new
            {
                id = p.Id.ToString(),
                accountIdentifier = p.AccountIdentifier,
                platformType = p.PlatformType,
                isActive = p.IsActive,
                isConnected = p.AccessToken != null,
                userId = p.UserId.ToString()
            });
            return Ok(result);
        }

        [HttpPost("outlook/switch")]
        public async Task<IActionResult> SwitchOutlookAccount([FromBody] SwitchAccountRequest request)
        {
            if (string.IsNullOrEmpty(request.AccountIdentifier))
                return BadRequest("AccountIdentifier is required.");

            var userId = GetCurrentUserId();
            var success = await SetActiveAccountAsync(userId, "Outlook", request.AccountIdentifier);
            if (!success)
                return NotFound("Account not found or could not be switched.");

            return Ok($"Active Outlook account switched to {request.AccountIdentifier}.");
        }

        [HttpPost("whatsapp/connect")]
        public async Task<IActionResult> ConnectWhatsApp([FromBody] ConnectWhatsAppRequest request)
        {
            if (string.IsNullOrEmpty(request.PhoneNumberId) || string.IsNullOrEmpty(request.AccessToken) || string.IsNullOrEmpty(request.PhoneNumber))
                return BadRequest("PhoneNumberId, AccessToken, and PhoneNumber are required.");

            var userId = GetCurrentUserId();
            var success = await _whatsAppIntegration.ValidateCredentialsAsync(request.PhoneNumberId, request.AccessToken, request.PhoneNumber);
            if (!success)
                return BadRequest("Invalid WhatsApp credentials.");

            var connection = await _appDbContext.WhatsAppConnections
                .FirstOrDefaultAsync(wc => wc.PhoneNumber == request.PhoneNumber && wc.UserId == userId);

            if (connection != null)
            {
                connection.IsConnected = true;
                connection.PhoneNumberId = request.PhoneNumberId;
                connection.AccessToken = request.AccessToken;
                connection.UpdatedAt = DateTime.UtcNow;
            }
            else
            {
                connection = new WhatsAppConnection
                {
                    UserId = userId,
                    PhoneNumberId = request.PhoneNumberId,
                    AccessToken = request.AccessToken,
                    PhoneNumber = request.PhoneNumber,
                    IsConnected = true,
                    CreatedAt = DateTime.UtcNow
                };
                _appDbContext.WhatsAppConnections.Add(connection);
            }

            await _appDbContext.SaveChangesAsync();
            return Ok("WhatsApp account connected!");
        }

        [HttpGet("whatsapp/accounts")]
        public async Task<IActionResult> GetWhatsAppAccounts()
        {
            var userId = GetCurrentUserId();
            var accounts = await _integrationService.GetUserWhatsAppAccountsAsync(userId);
            var result = accounts.Select(wc => new
            {
                id = wc.Id.ToString(),
                phoneNumber = wc.PhoneNumber,
                platformType = "WhatsApp",
                isActive = true, // WhatsApp doesn't use IsActive, assume true for consistency
                isConnected = wc.IsConnected,
                userId = wc.UserId.ToString()
            });
            return Ok(result);
        }

        [HttpPost("twitter/connect")]
        public IActionResult ConnectTwitter([FromBody] ConnectAccountRequest request)
        {
            if (string.IsNullOrEmpty(request.AccountIdentifier))
                return BadRequest("AccountIdentifier is required.");

            var userId = GetCurrentUserId();
            var url = _twitterIntegration.GetAuthorizationUrl(userId, request.AccountIdentifier);
            return Ok(new { url });
        }

        [HttpGet("twitter/callback")]
        public async Task<IActionResult> TwitterCallback([FromQuery] string code, [FromQuery] string state)
        {
            var parts = state.Split('|'); // Format: userId|accountIdentifier
            if (parts.Length != 2)
                return BadRequest("Invalid state parameter.");

            var userId = int.Parse(parts[0]);
            var accountIdentifier = parts[1];
            await _twitterIntegration.ExchangeCodeForTokenAsync(userId, code, accountIdentifier);
            await SetActiveAccountAsync(userId, "Twitter", accountIdentifier);
            return Ok("Twitter account connected!");
        }

        [HttpGet("twitter/accounts")]
        public async Task<IActionResult> GetTwitterAccounts()
        {
            var userId = GetCurrentUserId();
            var accounts = await _integrationService.GetUserPlatformsAsync(userId, "Twitter");
            var result = accounts.Select(p => new
            {
                id = p.Id.ToString(),
                accountIdentifier = p.AccountIdentifier,
                platformType = p.PlatformType,
                isActive = p.IsActive,
                isConnected = p.AccessToken != null,
                userId = p.UserId.ToString()
            });
            return Ok(result);
        }

        [HttpPost("twitter/switch")]
        public async Task<IActionResult> SwitchTwitterAccount([FromBody] SwitchAccountRequest request)
        {
            if (string.IsNullOrEmpty(request.AccountIdentifier))
                return BadRequest("AccountIdentifier is required.");

            var userId = GetCurrentUserId();
            var success = await SetActiveAccountAsync(userId, "Twitter", request.AccountIdentifier);
            if (!success)
                return NotFound("Account not found or could not be switched.");

            return Ok($"Active Twitter account switched to {request.AccountIdentifier}.");
        }

        [HttpGet("platforms")]
        public async Task<IActionResult> GetPlatforms()
        {
            var userId = GetCurrentUserId();
            var platformAccounts = await _integrationService.GetUserPlatformsAsync(userId);
            var whatsAppAccounts = await _integrationService.GetUserWhatsAppAccountsAsync(userId);

            var result = platformAccounts.Select(p => new
            {
                id = p.Id.ToString(),
                type = p.PlatformType.ToLower(),
                name = p.PlatformType,
                accountIdentifier = p.AccountIdentifier,
                isActive = p.IsActive,
                isConnected = p.AccessToken != null,
                userId = p.UserId.ToString()
            }).Concat(whatsAppAccounts.Select(wc => new
            {
                id = wc.Id.ToString(),
                type = "whatsapp",
                name = "WhatsApp",
                accountIdentifier = wc.PhoneNumber,
                isActive = true, // WhatsApp doesn't use IsActive
                isConnected = wc.IsConnected,
                userId = wc.UserId.ToString()
            }));

            return Ok(result);
        }

        [HttpDelete("{platform}/{accountIdentifier}")]
        public async Task<IActionResult> RemovePlatformIntegration(string platform, string accountIdentifier)
        {
            var userId = GetCurrentUserId();
            var result = await _integrationService.RemovePlatformIntegration(userId, platform, accountIdentifier);
            if (!result)
                return NotFound("Platform account not found or could not be removed.");

            return Ok($"Account {accountIdentifier} for {platform} removed successfully.");
        }

        private int GetCurrentUserId()
        {
            var userIdClaim = User.Claims.FirstOrDefault(c => c.Type == "userId");
            return userIdClaim != null ? int.Parse(userIdClaim.Value) : 0;
        }

        private async Task<bool> SetActiveAccountAsync(int userId, string platformType, string accountIdentifier)
        {
            var accounts = await _integrationService.GetUserPlatformsAsync(userId, platformType);
            var targetAccount = accounts.FirstOrDefault(pa => pa.AccountIdentifier == accountIdentifier);

            if (targetAccount == null)
                return false;

            foreach (var account in accounts)
            {
                account.IsActive = account.AccountIdentifier == accountIdentifier;
            }

            await _appDbContext.SaveChangesAsync();
            return true;
        }
    }

    public class ConnectAccountRequest
    {
        public string AccountIdentifier { get; set; } // e.g., user1@gmail.com, profile123, user@outlook.com
    }

    //public class SwitchAccountRequest
    //{
    //    public string AccountIdentifier { get; set; } // e.g., user1@gmail.com, profile123, user@outlook.com
    //}

    public class ConnectWhatsAppRequest
    {
        public string PhoneNumberId { get; set; }
        public string AccessToken { get; set; }
        public string PhoneNumber { get; set; }
    }
}