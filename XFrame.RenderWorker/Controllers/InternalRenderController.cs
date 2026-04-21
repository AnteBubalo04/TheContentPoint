using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using XFrame.RenderWorker.Models;
using XFrame.RenderWorker.Services;

namespace XFrame.RenderWorker.Controllers
{
    [ApiController]
    [Route("internal/render-jobs")]
    public class InternalRenderController : ControllerBase
    {
        private readonly IWebHostEnvironment _env;
        private readonly IRenderJobQueue _queue;
        private readonly InternalApiSettings _internalApiSettings;

        public InternalRenderController(
            IWebHostEnvironment env,
            IRenderJobQueue queue,
            IOptions<InternalApiSettings> internalApiOptions)
        {
            _env = env;
            _queue = queue;
            _internalApiSettings = internalApiOptions.Value;
        }

        [HttpPost]
        [RequestSizeLimit(20 * 1024 * 1024)]
        [RequestFormLimits(MultipartBodyLengthLimit = 20 * 1024 * 1024)]
        public async Task<IActionResult> EnqueueRenderJob(
            [FromForm] string sessionId,
            [FromForm] string email,
            IFormFile photo)
        {
            var incomingApiKey = Request.Headers["X-Internal-Api-Key"].FirstOrDefault();

            if (string.IsNullOrWhiteSpace(_internalApiSettings.ApiKey))
                return StatusCode(500, "Internal API key is not configured.");

            if (!string.Equals(incomingApiKey, _internalApiSettings.ApiKey, StringComparison.Ordinal))
                return Unauthorized("Invalid internal API key.");

            if (string.IsNullOrWhiteSpace(sessionId))
                return BadRequest("sessionId je prazan.");

            if (string.IsNullOrWhiteSpace(email))
                return BadRequest("email je prazan.");

            if (photo == null || photo.Length == 0)
                return BadRequest("photo nije primljen.");

            if (photo.Length > 20 * 1024 * 1024)
                return BadRequest("Slika je prevelika.");

            var allowedContentTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "image/jpeg",
                "image/jpg",
                "image/png",
                "image/webp"
            };

            if (!allowedContentTypes.Contains(photo.ContentType ?? string.Empty))
                return BadRequest("Nepodrzan format slike.");

            var generatedRoot = Path.Combine(_env.ContentRootPath, "Generated");
            var photosDir = Path.Combine(generatedRoot, "photos");
            Directory.CreateDirectory(photosDir);

            var extension = ".jpg";
            if (string.Equals(photo.ContentType, "image/png", StringComparison.OrdinalIgnoreCase))
                extension = ".png";
            else if (string.Equals(photo.ContentType, "image/webp", StringComparison.OrdinalIgnoreCase))
                extension = ".webp";

            var photoPath = Path.Combine(
                photosDir,
                $"{sessionId}_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}{extension}"
            );

            await using (var stream = System.IO.File.Create(photoPath))
                await photo.CopyToAsync(stream);

            await _queue.EnqueueAsync(new RenderJob(sessionId, email, photoPath));

            return Accepted(new
            {
                sessionId,
                queued = true
            });
        }
    }
}