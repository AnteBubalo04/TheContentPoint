using System.Threading.Channels;

namespace XFrame.RenderWorker.Services
{
    public sealed record RenderJob(
        string SessionId,
        string Email,
        string PhotoPath);

    public interface IRenderJobQueue
    {
        ValueTask EnqueueAsync(RenderJob job, CancellationToken cancellationToken = default);
        ValueTask<RenderJob> DequeueAsync(CancellationToken cancellationToken);
    }

    public class RenderJobQueue : IRenderJobQueue
    {
        private readonly Channel<RenderJob> _channel;

        public RenderJobQueue()
        {
            var options = new BoundedChannelOptions(200)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false
            };

            _channel = Channel.CreateBounded<RenderJob>(options);
        }

        public ValueTask EnqueueAsync(RenderJob job, CancellationToken cancellationToken = default)
        {
            return _channel.Writer.WriteAsync(job, cancellationToken);
        }

        public ValueTask<RenderJob> DequeueAsync(CancellationToken cancellationToken)
        {
            return _channel.Reader.ReadAsync(cancellationToken);
        }
    }

    public class RenderJobWorker : BackgroundService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly IRenderJobQueue _queue;
        private readonly ILogger<RenderJobWorker> _logger;

        public RenderJobWorker(
            IServiceProvider serviceProvider,
            IRenderJobQueue queue,
            ILogger<RenderJobWorker> logger)
        {
            _serviceProvider = serviceProvider;
            _queue = queue;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("RenderJobWorker started.");

            while (!stoppingToken.IsCancellationRequested)
            {
                RenderJob job;

                try
                {
                    job = await _queue.DequeueAsync(stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                try
                {
                    using var scope = _serviceProvider.CreateScope();

                    var env = scope.ServiceProvider.GetRequiredService<IWebHostEnvironment>();
                    var videoComposer = scope.ServiceProvider.GetRequiredService<VideoComposerService>();
                    var emailService = scope.ServiceProvider.GetRequiredService<EmailService>();

                    var videosDir = Path.Combine(env.ContentRootPath, "Generated", "videos");
                    Directory.CreateDirectory(videosDir);

                    _logger.LogInformation(
                        "Render job started. SessionId={SessionId}, Email={Email}, PhotoPath={PhotoPath}",
                        job.SessionId,
                        job.Email,
                        job.PhotoPath);

                    var mp4Path = await videoComposer.CreateHeroVideoFromPhotoAsync(job.PhotoPath, videosDir);

                    _logger.LogInformation(
                        "Render finished. SessionId={SessionId}, Output={OutputPath}",
                        job.SessionId,
                        mp4Path);

                    await emailService.SendHeroMp4Email(job.Email, mp4Path);

                    _logger.LogInformation(
                        "Email sent. SessionId={SessionId}, Email={Email}",
                        job.SessionId,
                        job.Email);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "Render/email failed. SessionId={SessionId}, Email={Email}, PhotoPath={PhotoPath}",
                        job.SessionId,
                        job.Email,
                        job.PhotoPath);
                }
            }

            _logger.LogInformation("RenderJobWorker stopped.");
        }
    }
}