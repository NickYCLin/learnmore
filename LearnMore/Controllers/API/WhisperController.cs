using LearnMore.Models;
using LearnMore.Services;
using Microsoft.AspNetCore.Mvc;

namespace LearnMore.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class WhisperController : ControllerBase
    {
        private readonly IWhisperTranscribeWorkflowService _transcribeWorkflow;
        private readonly IYouTubeSubtitleDownloadService _subtitleDownloadService;

        public WhisperController(
            IWhisperTranscribeWorkflowService transcribeWorkflow,
            IYouTubeSubtitleDownloadService subtitleDownloadService)
        {
            _transcribeWorkflow = transcribeWorkflow;
            _subtitleDownloadService = subtitleDownloadService;
        }

        [HttpPost("transcribe")]
        public async Task<IActionResult> Transcribe([FromBody] TranscribeRequest request)
        {
            if (!ControllerAccessGuard.IsSignedIn(this))
            {
                return ControllerAccessGuard.LoginRequired(this);
            }

            if (request == null || string.IsNullOrEmpty(request.YouTubeUrl))
            {
                return BadRequest("YouTube URL is required.");
            }

            try
            {
                var transcription = await _transcribeWorkflow.ExecuteAsync(request);
                return Ok(new { transcription });
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"An error occurred: {ex.Message}");
            }
        }

        [HttpGet("subtitles")]
        public async Task<IActionResult> TryGetYouTubeSubtitlesAsync(string youTubeUrl, CancellationToken cancellationToken = default)
        {
            if (!ControllerAccessGuard.IsSignedIn(this))
            {
                return ControllerAccessGuard.LoginRequired(this);
            }

            var subtitles = await _subtitleDownloadService.TryDownloadSubtitlesAsync(youTubeUrl, cancellationToken);
            return Ok(subtitles);
        }
    }
}
