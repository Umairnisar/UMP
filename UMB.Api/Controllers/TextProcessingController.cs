using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using UMB.Api.Dtos;
using UMB.Api.Services;

namespace UMB.Api.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [Authorize]
    public class TextProcessingController : ControllerBase
    {
        private readonly ITextProcessingService _textProcessingService;

        public TextProcessingController(ITextProcessingService textProcessingService)
        {
            _textProcessingService = textProcessingService;
        }

        /// <summary>
        /// Summarizes the provided text
        /// </summary>
        /// <param name="request">Request containing the text to summarize</param>
        /// <returns>A summarized version of the input text</returns>
        [HttpPost("summarize")]
        public async Task<IActionResult> SummarizeText([FromBody] TextProcessingRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.Text))
            {
                return BadRequest("Text to summarize cannot be empty.");
            }

            try
            {
                var userId = int.Parse(User.Claims.First(c => c.Type == "userId").Value);
                var result = await _textProcessingService.SummarizeTextAsync(userId, request.Text);
                return Ok(new TextProcessingResponse { ProcessedText = result });
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"Error summarizing text: {ex.Message}");
            }
        }

        /// <summary>
        /// Translates the provided text to the target language
        /// </summary>
        /// <param name="request">Request containing the text to translate and target language</param>
        /// <returns>The translated text</returns>
        [HttpPost("translate")]
        public async Task<IActionResult> TranslateText([FromBody] TranslateRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.Text))
            {
                return BadRequest("Text to translate cannot be empty.");
            }

            if (string.IsNullOrWhiteSpace(request.TargetLanguage))
            {
                return BadRequest("Target language cannot be empty.");
            }

            try
            {
                var userId = int.Parse(User.Claims.First(c => c.Type == "userId").Value);
                var result = await _textProcessingService.TranslateTextAsync(userId, request.Text, request.TargetLanguage);
                return Ok(new TextProcessingResponse { ProcessedText = result });
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"Error translating text: {ex.Message}");
            }
        }
    }
}
