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
        private readonly EmailService _emailService;
        private readonly VideoComposerService _videoComposer;

        public SessionController(IWebHostEnvironment env, EmailService emailService, VideoComposerService videoComposer)
        {
            _env = env;
            _emailService = emailService;
            _videoComposer = videoComposer;
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
            _sessions[id] = "";
            _activeSessionId = id;
            Console.WriteLine($"[API] Registriran session: {id}");
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

            _sessions[id] = session.Email;
            Console.WriteLine($"[API] Email spremljen za session {id}: {session.Email}");
            return Ok();
        }

        [HttpGet("{id}")]
        public IActionResult GetSession(string id)
        {
            // ✅ KLJUČNO: i ovo mora biti no-cache (na nekim uređajima se GET zna cache-at)
            NoCache();

            if (_sessions.TryGetValue(id, out var email))
                return Ok(new Session { Id = id, Email = email });

            return NotFound();
        }

        [HttpPost("{id}/photo")]
        public async Task<IActionResult> UploadPhoto(string id, IFormFile photo)
        {
            try
            {
                NoCache();

                Console.WriteLine($"[API] UploadPhoto START za session {id}");
                Console.WriteLine($"[API] photo = {(photo == null ? "null" : $"{photo.FileName}, {photo.Length} bytes")}");

                if (!_sessions.TryGetValue(id, out var email) || string.IsNullOrWhiteSpace(email))
                    return BadRequest("Session ne postoji ili email nije unesen.");

                if (photo == null || photo.Length == 0)
                    return BadRequest("Slika nije primljena.");

                var generatedRoot = Path.Combine(_env.ContentRootPath, "Generated");
                var photosDir = Path.Combine(generatedRoot, "photos");
                var videosDir = Path.Combine(generatedRoot, "videos");
                Directory.CreateDirectory(photosDir);
                Directory.CreateDirectory(videosDir);

                var photoPath = Path.Combine(
                    photosDir,
                    $"{id}_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}.jpg"
                );

                await using (var stream = System.IO.File.Create(photoPath))
                    await photo.CopyToAsync(stream);

                Console.WriteLine($"[API] Slika spremljena u: {photoPath}");
                Console.WriteLine("[API] Composing MP4 (photo + overlay2 alpha)...");

                var mp4Path = await _videoComposer.CreateHeroVideoFromPhotoAsync(photoPath, videosDir);

                Console.WriteLine($"[API] MP4 napravljen: {mp4Path}");
                await _emailService.SendHeroMp4Email(email, mp4Path);
                Console.WriteLine($"[API] Email poslan na {email} s MP4 (foto+hero).");

                lock (_activeLock)
                {
                    var newId = Guid.NewGuid().ToString();
                    _sessions[newId] = "";
                    _activeSessionId = newId;
                    Console.WriteLine($"[API] Kreiran novi aktivni session: {_activeSessionId}");
                }

                return Ok();
            }
            catch (Exception ex)
            {
                Console.WriteLine("[API] UploadPhoto ERROR: " + ex);
                return StatusCode(500, $"UploadPhoto exception: {ex.Message}");
            }
        }
    }
}
