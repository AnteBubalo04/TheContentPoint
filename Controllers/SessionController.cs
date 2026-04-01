using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Mvc;

namespace Booth.API.Controllers
{
    [ApiController]
    [Route("api/session")]
    [EnableCors("AllowAll")]
    public class SessionController : ControllerBase
    {
        private static readonly Dictionary<string, string> _sessions = new();
        private readonly EmailService _email;

        public SessionController(EmailService email)
        {
            _email = email;
        }

        [HttpPost("{id}/email")]
        public async Task<IActionResult> SubmitEmail(string id, [FromBody] string email)
        {
            if (string.IsNullOrWhiteSpace(email))
                return BadRequest("Email is required.");

            _sessions[id] = email;
            return Ok();
        }

        [HttpGet("{id}")]
        public IActionResult GetSession(string id)
        {
            if (_sessions.TryGetValue(id, out var email))
                return Ok(new { email });

            return NotFound();
        }

        [HttpGet("{id}/exists")]
        public IActionResult CheckSessionExists(string id)
        {
            return _sessions.ContainsKey(id) ? Ok() : NotFound();
        }

        [HttpPost("{id}/photo")]
        public async Task<IActionResult> UploadPhoto(string id, IFormFile photo)
        {
            try
            {
                if (photo == null || photo.Length == 0)
                    return BadRequest("No photo uploaded.");

                var folder = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "photos");
                Directory.CreateDirectory(folder);

                var fileName = $"{id}_{DateTime.UtcNow.Ticks}.jpg";
                var path = Path.Combine(folder, fileName);

                // ✅ Zatvori stream prije slanja emaila
                using (var stream = new FileStream(path, FileMode.Create))
                {
                    await photo.CopyToAsync(stream);
                }

                if (_sessions.TryGetValue(id, out var email))
                {
                    await _email.SendPhotoEmailAsync(email, id);
                }

                return Ok(new { file = fileName });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Greška kod spremanja slike: {ex}");
                return StatusCode(500, $"Greška: {ex.Message}");
            }
        }

        [HttpPost("{id}/send-photo")]
        public async Task<IActionResult> SendPhotoToEmail(string id)
        {
            if (!_sessions.TryGetValue(id, out var email))
                return NotFound("Session nije registriran.");

            await _email.SendPhotoEmailAsync(email, id);
            return Ok();
        }
    }
}
