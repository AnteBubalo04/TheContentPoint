using System.Net.Http.Headers;
using Microsoft.Extensions.Options;
using XFrame.API.Models;

namespace XFrame.API.Services
{
    public class RenderDispatchService
    {
        private readonly HttpClient _httpClient;
        private readonly RenderWorkerSettings _settings;

        public RenderDispatchService(
            HttpClient httpClient,
            IOptions<RenderWorkerSettings> settings)
        {
            _httpClient = httpClient;
            _settings = settings.Value;
        }

        public async Task DispatchAsync(
            string sessionId,
            string email,
            string photoPath,
            string? contentType,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(_settings.BaseUrl))
                throw new InvalidOperationException("RenderWorker:BaseUrl nije konfiguriran.");

            if (string.IsNullOrWhiteSpace(_settings.ApiKey))
                throw new InvalidOperationException("RenderWorker:ApiKey nije konfiguriran.");

            if (!File.Exists(photoPath))
                throw new FileNotFoundException("Photo file not found.", photoPath);

            using var stream = System.IO.File.OpenRead(photoPath);
            using var multipart = new MultipartFormDataContent();

            multipart.Add(new StringContent(sessionId), "sessionId");
            multipart.Add(new StringContent(email), "email");

            var fileContent = new StreamContent(stream);
            fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse(
                string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType);

            multipart.Add(fileContent, "photo", Path.GetFileName(photoPath));

            using var request = new HttpRequestMessage(HttpMethod.Post, "internal/render-jobs");
            request.Headers.Add("X-Internal-Api-Key", _settings.ApiKey);
            request.Content = multipart;

            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                throw new Exception(
                    $"Render worker dispatch failed. HTTP {(int)response.StatusCode} {response.ReasonPhrase}. Body: {body}");
            }
        }
    }
}