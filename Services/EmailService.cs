using System.Net;
using System.Net.Mail;

public class EmailService
{
    private readonly IConfiguration _config;

    public EmailService(IConfiguration config)
    {
        _config = config;
    }

    public async Task SendPhotoEmailAsync(string toEmail, string sessionId)
    {
        var sender = _config["Email:Sender"];
        var host = _config["Email:SmtpHost"];
        var portRaw = _config["Email:SmtpPort"];
        var username = _config["Email:Username"];
        var password = _config["Email:Password"];

        if (string.IsNullOrWhiteSpace(sender) ||
            string.IsNullOrWhiteSpace(host) ||
            string.IsNullOrWhiteSpace(portRaw) ||
            string.IsNullOrWhiteSpace(username) ||
            string.IsNullOrWhiteSpace(password) ||
            string.IsNullOrWhiteSpace(toEmail))
        {
            throw new Exception("Email konfiguracija nije potpuna.");
        }

        var port = int.Parse(portRaw);

        var client = new SmtpClient(host, port)
        {
            Credentials = new NetworkCredential(username, password),
            EnableSsl = true
        };

        var folder = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "photos");
        var latestFile = Directory.GetFiles(folder, $"{sessionId}_*.jpg")
                                  .OrderByDescending(f => File.GetCreationTimeUtc(f))
                                  .FirstOrDefault();

        if (latestFile == null)
            throw new FileNotFoundException("Nema snimljene slike za ovaj session.");

        var photoUrl = $"https://yourdomain.com/photos/{Path.GetFileName(latestFile)}";
        var twitterShareLink = $"https://twitter.com/intent/tweet?text=Check%20out%20my%20XFrame%20photo%20from%20the%20event!%20Tagging%20@hrvatskitelekom%20%23XFrame&url={photoUrl}";
        var instagramLink = "https://www.instagram.com/hrvatski.telekom/";

        var body = $@"
            <p>Hvala što ste koristili <strong>XFrame</strong>!</p>
            <p>Vaša fotografija je spremna — kliknite ispod za pregled ili preuzimanje:</p>
            <hr />
            <p>Podijelite svoj trenutak:</p>
            <ul>
                <li><a href='{twitterShareLink}'>🐦 Objavi na Twitteru</a></li>
                <li><a href='{instagramLink}'>📸 Otvori Instagram</a></li>
            </ul>
            <p style='font-style:italic;'>Don’t forget to tag <strong>@hrvatskitelekom</strong> when posting your XFrame!</p>
        ";

        var message = new MailMessage(sender, toEmail)
        {
            Subject = "Vaša Pixie slika 📸",
            IsBodyHtml = true,
            Body = body
        };

        message.Attachments.Add(new Attachment(latestFile));
        await client.SendMailAsync(message);
    }
}
