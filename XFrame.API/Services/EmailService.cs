using System;
using System.IO;
using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Options;
using XFrame.API.Models;

namespace XFrame.API.Services
{
    public class EmailService
    {
        private readonly SmtpSettings _settings;

        // ✅ OVDJE PROMIJENI PO POTREBI (bez mijenjanja controllera)
        private const string BrandHandle = "@brend";
        private const string BrandLink = "http://linkbrenda.com";
        private const string ProductHandle = "@thecontentpoint";
        private const string InstagramLink = "https://www.instagram.com/";

        public EmailService(IOptions<SmtpSettings> config)
        {
            _settings = config.Value;
        }

        public async Task SendHeroMp4Email(string toEmail, string mp4Path)
        {
            if (string.IsNullOrWhiteSpace(toEmail))
                throw new ArgumentException("Email adresa je prazna.", nameof(toEmail));

            if (string.IsNullOrWhiteSpace(mp4Path) || !File.Exists(mp4Path))
                throw new ArgumentException("MP4 ne postoji.", nameof(mp4Path));

            var copyText = $"{BrandHandle} {BrandLink} {ProductHandle}";

            var body =
$@"Step 1 — Download your video
Download your video to your phone (the video is attached to this email).

Step 2 — Copy this text
Copy and paste this exact text into your Instagram Story:
{copyText}

Step 3 — Open Instagram and post
Open Instagram using this link:
{InstagramLink}

Then: swipe left to open Stories → select the downloaded video from your gallery → paste the text from Step 2 onto the video → tap Share.

Step 4 — Get your confirmation DM
After you post the Story and tag {ProductHandle}, you’ll receive a confirmation DM from {ProductHandle}.
Show that DM at the bar to claim your drink/gift.
";

            using var message = new MailMessage(_settings.From, toEmail)
            {
                Subject = "Your video + Instagram Story steps",
                Body = body,
                IsBodyHtml = false
            };

            message.Attachments.Add(new Attachment(mp4Path, "video/mp4"));

            using var client = new SmtpClient(_settings.Server, _settings.Port)
            {
                Credentials = new NetworkCredential(_settings.Username, _settings.Password),
                EnableSsl = _settings.EnableSsl
            };

            await client.SendMailAsync(message);
        }
    }
}
