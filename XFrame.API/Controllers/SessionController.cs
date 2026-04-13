using Microsoft.AspNetCore.Mvc;
using System.Collections.Concurrent;
using System.Diagnostics;
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
        private readonly IHeroRenderQueue _heroRenderQueue;
        private readonly ILogger<SessionController> _logger;

        public SessionController(
            IWebHostEnvironment env,
            IHeroRenderQueue heroRenderQueue,
            ILogger<SessionController> logger)
        {
            _env = env;
            _heroRenderQueue = heroRenderQueue;
            _logger = logger;
        }

        private void NoCache()
        {
            Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate, max-age=0";
            Response.Headers["Pragma"] = "no-cache";
            Response.Headers["Expires"] = "0";
        }

        private (string SessionId, string Email) GetActiveSnapshot()
        {
            lock (_activeLock)
            {
                var activeId = _activeSessionId;

                if (string.IsNullOrWhiteSpace(activeId))
                    return (string.Empty, string.Empty);

                _sessions.TryGetValue(activeId, out var email);
                return (activeId, email ?? string.Empty);
            }
        }

        public sealed class ActiveSessionStateResponse
        {
            public string SessionId { get; set; } = string.Empty;
            public string Email { get; set; } = string.Empty;
        }

        [HttpPost("{id}/register")]
        public IActionResult RegisterSession(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
                return BadRequest("Session id je prazan.");

            lock (_activeLock)
            {
                _sessions.TryAdd(id, string.Empty);
                _activeSessionId = id;
            }

            _logger.LogInformation("Registriran session: {SessionId}", id);
            return Ok(new { SessionId = id });
        }

        [HttpGet("active")]
        public IActionResult GetActiveSession()
        {
            NoCache();

            var snapshot = GetActiveSnapshot();
            return Ok(new { SessionId = snapshot.SessionId });
        }

        [HttpGet("active-state")]
        public IActionResult GetActiveState()
        {
            NoCache();

            var snapshot = GetActiveSnapshot();

            return Ok(new ActiveSessionStateResponse
            {
                SessionId = snapshot.SessionId,
                Email = snapshot.Email
            });
        }

        [HttpGet("active-state/wait")]
        public async Task<IActionResult> WaitForActiveState(
            [FromQuery] string? lastKnownSessionId = null,
            [FromQuery] string? lastKnownEmail = null,
            [FromQuery] int timeoutMs = 15000)
        {
            NoCache();

            if (timeoutMs <= 0)
                timeoutMs = 15000;

            if (timeoutMs > 30000)
                timeoutMs = 30000;

            lastKnownSessionId ??= string.Empty;
            lastKnownEmail ??= string.Empty;

            var sw = Stopwatch.StartNew();

            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                var snapshot = GetActiveSnapshot();

                if (!string.Equals(snapshot.SessionId, lastKnownSessionId, StringComparison.Ordinal) ||
                    !string.Equals(snapshot.Email, lastKnownEmail, StringComparison.Ordinal))
                {
                    return Ok(new ActiveSessionStateResponse
                    {
                        SessionId = snapshot.SessionId,
                        Email = snapshot.Email
                    });
                }

                await Task.Delay(100);
            }

            var finalSnapshot = GetActiveSnapshot();

            return Ok(new ActiveSessionStateResponse
            {
                SessionId = finalSnapshot.SessionId,
                Email = finalSnapshot.Email
            });
        }

        [HttpPost("active/email")]
        public IActionResult SubmitEmailToActive([FromBody] Session session)
        {
            NoCache();

            var email = session.Email?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(email))
                return BadRequest("Email je prazan.");

            string activeId;
            lock (_activeLock)
            {
                activeId = _activeSessionId;

                if (string.IsNullOrWhiteSpace(activeId))
                    return Conflict("Nema aktivnog sessiona.");

                _sessions[activeId] = email;
            }

            _logger.LogInformation("Email spremljen za aktivni session {SessionId}", activeId);

            return Ok(new
            {
                SessionId = activeId,
                Email = email
            });
        }

        [HttpPost("{id}/email")]
        public IActionResult SubmitEmail(string id, [FromBody] Session session)
        {
            NoCache();

            var email = session.Email?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(email))
                return BadRequest("Email je prazan.");

            _sessions[id] = email;

            lock (_activeLock)
            {
                if (string.IsNullOrWhiteSpace(_activeSessionId))
                    _activeSessionId = id;
            }

            _logger.LogInformation("Email spremljen za session {SessionId}", id);

            return Ok(new
            {
                SessionId = id,
                Email = email
            });
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
        [Consumes("multipart/form-data")]
        public async Task<IActionResult> UploadPhoto(string id, IFormFile photo)
        {
            try
            {
                NoCache();

                _logger.LogInformation(
                    "UploadPhoto START za session {SessionId}. File={FileName}, Length={Length}",
                    id,
                    photo?.FileName,
                    photo?.Length ?? 0);

                if (!_sessions.TryGetValue(id, out var email) || string.IsNullOrWhiteSpace(email))
                    return BadRequest("Session ne postoji ili email nije unesen.");

                if (photo == null || photo.Length == 0)
                    return BadRequest("Slika nije primljena.");

                var generatedRoot = Path.Combine(_env.ContentRootPath, "Generated");
                var photosDir = Path.Combine(generatedRoot, "photos");
                Directory.CreateDirectory(photosDir);

                var photoPath = Path.Combine(
                    photosDir,
                    $"{id}_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}.jpg"
                );

                await using (var stream = System.IO.File.Create(photoPath))
                    await photo.CopyToAsync(stream);

                _logger.LogInformation("Slika spremljena u: {PhotoPath}", photoPath);

                var nextSessionId = Guid.NewGuid().ToString();

                lock (_activeLock)
                {
                    _sessions[nextSessionId] = string.Empty;
                    _activeSessionId = nextSessionId;
                }

                await _heroRenderQueue.EnqueueAsync(
                    new HeroRenderJob(
                        SessionId: id,
                        Email: email,
                        PhotoPath: photoPath),
                    HttpContext.RequestAborted);

                _logger.LogInformation(
                    "Hero render job enqueuean. SessionId={SessionId}, NextActiveSessionId={NextSessionId}",
                    id,
                    nextSessionId);

                return Ok(new
                {
                    Accepted = true,
                    SessionId = id,
                    NextSessionId = nextSessionId
                });
            }
            catch (OperationCanceledException)
            {
                return StatusCode(499, "Request cancelled.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "UploadPhoto ERROR za session {SessionId}", id);
                return StatusCode(500, $"UploadPhoto exception: {ex.Message}");
            }
        }
    }
}