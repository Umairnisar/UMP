using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
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
        private readonly BaseIntegrationService _integrationService;

        public IntegrationController(
            IGmailIntegrationService gmailIntegration,
            ILinkedInIntegrationService linkedinIntegration,
            IOutlookIntegrationService outlookIntegration,
            BaseIntegrationService integrationService
            )
        {
            _gmailIntegration = gmailIntegration;
            _linkedinIntegration = linkedinIntegration;
            _outlookIntegration = outlookIntegration;
            _integrationService = integrationService;
        }

        [HttpGet("gmail/connect")]
        public IActionResult ConnectGmail()
        {
            var userId = GetCurrentUserId(); // from JWT claims, for example
            var url = _gmailIntegration.GetAuthorizationUrl(userId);
            return Ok(new { url = url });
        }

        [HttpGet("gmail/callback")]
        public async Task<IActionResult> GmailCallback([FromQuery] string code, [FromQuery] string state)
        {
            // state = userId that we appended
            var userId = int.Parse(state);
            await _gmailIntegration.ExchangeCodeForTokenAsync(userId, code);
            return Ok("Gmail connected!");

        }

        [HttpGet("linkedin/connect")]
        public IActionResult ConnectLinkedIn()
        {
            var userId = GetCurrentUserId();
            var url = _linkedinIntegration.GetAuthorizationUrl(userId);
            return Redirect(url);
        }

        [HttpGet("linkedin/callback")]
        public async Task<IActionResult> LinkedInCallback([FromQuery] string code, [FromQuery] string state)
        {
            var userId = int.Parse(state);
            await _linkedinIntegration.ExchangeCodeForTokenAsync(userId, code);
            return Ok("LinkedIn connected!");
        }

        [HttpGet("outlook/connect")]
        public IActionResult ConnectOutlook()
        {
            var userId = GetCurrentUserId();
            var url = _outlookIntegration.GetAuthorizationUrl(userId);
            return Ok(new { url = url });

           // return Redirect(url);
        }

        [HttpGet("outlook/callback")]
        public async Task<IActionResult> OutlookCallback([FromQuery] string code, [FromQuery] string state)
        {
            var userId = int.Parse(state);
            await _outlookIntegration.ExchangeCodeForTokenAsync(userId, code);
            return Ok("Outlook connected!");
        }

        [HttpGet("platforms")]
        public async Task<IActionResult> GetPlatforms()
        {
            var userId = GetCurrentUserId();
            var platforms = await _integrationService.GetUserPlatformsAsync(userId);
            
            // Map to client format
            var result = platforms.Select(p => new {
                id = p.Id.ToString(),
                type = p.PlatformType.ToLower(),
                name = p.PlatformType,
                isConnected = true,
                userId = p.UserId.ToString()
            });
            
            return Ok(result);
        }


        [HttpDelete("{platform}")]
        public async Task<IActionResult> RemovePlatformIntegration(string platform)
        {
            var userId = int.Parse(User.Claims.First(c => c.Type == "userId").Value);

            var result = await _integrationService.RemovePlatformIntegration(userId, platform);

            //if (!result)
            //{
            //    return NotFound(new { message = "Platform integration not found or could not be removed." });
            //}

            return Ok(new { message = $"{platform} integration removed successfully." });
        }

        private int GetCurrentUserId()
        {
            // parse from JWT claims
            var userIdClaim = User.Claims.FirstOrDefault(c => c.Type == "userId");
            return userIdClaim != null ? int.Parse(userIdClaim.Value) : 0;
        }
    }
}
