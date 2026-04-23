using Microsoft.AspNetCore.Mvc;
using System.Collections.Concurrent;
using XFrame.API.Models;
using XFrame.API.Services;

namespace XFrame.API.Controllers
{
    [ApiController]
    [Route("api/session")]
    public class SessionController : ControllerBase
    {
        private static readonly ConcurrentDictionary<string, string> _sessions = new();
        private static string _activeSessionId = string.Empty;
        private static readonly object _activeLock = new();

        private readonly IWebHostEnvironment _env;
        private readonly RenderDispatchService _renderDispatchService;

        public SessionController(
            IWebHostEnvironment env,
            RenderDispatchService renderDispatchService)
        {
            _env = env;
            _renderDispatchService = renderDispatchService;
        }

        private void NoCache()
        {
            Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate, max-age=0";
            Response.Headers["Pragma"] = "no-cache";
            Response.Headers["Expires"] = "0";
        }

        [HttpPost("{id}/register")]
        public IActionResult RegisterSession(string id)
        {
            lock (_activeLock)
            {
                if (string.IsNullOrWhiteSpace(_activeSessionId))
                {
                    _sessions[id] = "";
                    _activeSessionId = id;
                    Console.WriteLine($"[API] Registriran prvi aktivni session: {id}");
                }
                else
                {
                    if (!_sessions.ContainsKey(_activeSessionId))
                        _sessions[_activeSessionId] = "";

                    Console.WriteLine($"[API] Aktivni session već postoji: {_activeSessionId} (ignoram novi register {id})");
                }
            }

            return Ok();
        }

        [HttpGet("active")]
        public IActionResult GetActiveSession()
        {
            NoCache();

            Console.WriteLine($"[API] Vracam aktivni session: {_activeSessionId}");
            return Ok(new { SessionId = _activeSessionId });
        }

        [HttpPost("{id}/email")]
        public IActionResult SubmitEmail(string id, [FromBody] Session session)
        {
            NoCache();

            if (string.IsNullOrWhiteSpace(session.Email))
                return BadRequest("Email je prazan.");

            if (string.IsNullOrWhiteSpace(_activeSessionId))
                return BadRequest("Aktivni session ne postoji.");

            if (id != _activeSessionId)
                return BadRequest("Session nije aktivan.");

            _sessions[id] = session.Email;
            Console.WriteLine($"[API] Email spremljen za session {id}: {session.Email}");
            return Ok();
        }

        [HttpGet("{id}")]
        public IActionResult GetSession(string id)
        {
            NoCache();

            if (_sessions.TryGetValue(id, out var email))
                return Ok(new Session { Id = id, Email = email });

            return NotFound();
        }

        [HttpPost("{id}/photo")]
        [RequestSizeLimit(20 * 1024 * 1024)]
        [RequestFormLimits(MultipartBodyLengthLimit = 20 * 1024 * 1024)]
        public async Task<IActionResult> UploadPhoto(string id, IFormFile photo)
        {
            string? photoPath = null;
            var deleteLocalPhotoAfterDispatch = false;

            try
            {
                NoCache();

                Console.WriteLine($"[API] UploadPhoto START za session {id}");
                Console.WriteLine($"[API] photo = {(photo == null ? "null" : $"{photo.FileName}, {photo.Length} bytes, {photo.ContentType}")}");

                if (!_sessions.TryGetValue(id, out var email) || string.IsNullOrWhiteSpace(email))
                    return BadRequest("Session ne postoji ili email nije unesen.");

                if (photo == null || photo.Length == 0)
                    return BadRequest("Slika nije primljena.");

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

                photoPath = Path.Combine(
                    photosDir,
                    $"{id}_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}{extension}"
                );

                await using (var stream = System.IO.File.Create(photoPath))
                    await photo.CopyToAsync(stream);

                Console.WriteLine($"[API] Slika spremljena u: {photoPath}");
                Console.WriteLine("[API] Dispatching render job to XFrame.RenderWorker...");

                await _renderDispatchService.DispatchAsync(
                    id,
                    email,
                    photoPath,
                    photo.ContentType,
                    HttpContext.RequestAborted);

                deleteLocalPhotoAfterDispatch = true;

                Console.WriteLine($"[API] Render job uspjesno dispatchan za session {id} i email {email}");

                _sessions[id] = "";
                Console.WriteLine($"[API] Session {id} ociscen za sljedeceg korisnika.");

                return Ok();
            }
            catch (Exception ex)
            {
                Console.WriteLine("[API] UploadPhoto ERROR: " + ex);
                return StatusCode(500, $"UploadPhoto exception: {ex.Message}");
            }
            finally
            {
                if (deleteLocalPhotoAfterDispatch && !string.IsNullOrWhiteSpace(photoPath))
                {
                    try
                    {
                        if (System.IO.File.Exists(photoPath))
                            System.IO.File.Delete(photoPath);
                    }
                    catch
                    {
                    }
                }
            }
        }
    }
}