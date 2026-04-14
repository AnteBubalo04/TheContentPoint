using System.Threading.Channels;

namespace XFrame.API.Services
{
    public sealed record HeroRenderJob(
        string SessionId,
        string Email,
        string PhotoPath);

    public sealed record EmailDispatchJob(
        string SessionId,
        string Email,
        string Mp4Path);

    public interface IHeroRenderQueue
    {
        ValueTask EnqueueAsync(HeroRenderJob job, CancellationToken cancellationToken = default);
        ValueTask<HeroRenderJob> DequeueAsync(CancellationToken cancellationToken);
    }

    public interface IEmailDispatchQueue
    {
        ValueTask EnqueueAsync(EmailDispatchJob job, CancellationToken cancellationToken = default);
        ValueTask<EmailDispatchJob> DequeueAsync(CancellationToken cancellationToken);
    }

    public class HeroRenderQueue : IHeroRenderQueue
    {
        private readonly Channel<HeroRenderJob> _channel;

        public HeroRenderQueue()
        {
            var options = new BoundedChannelOptions(200)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false
            };

            _channel = Channel.CreateBounded<HeroRenderJob>(options);
        }

        public ValueTask EnqueueAsync(HeroRenderJob job, CancellationToken cancellationToken = default)
        {
            return _channel.Writer.WriteAsync(job, cancellationToken);
        }

        public ValueTask<HeroRenderJob> DequeueAsync(CancellationToken cancellationToken)
        {
            return _channel.Reader.ReadAsync(cancellationToken);
        }
    }

    public class EmailDispatchQueue : IEmailDispatchQueue
    {
        private readonly Channel<EmailDispatchJob> _channel;

        public EmailDispatchQueue()
        {
            var options = new BoundedChannelOptions(400)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = false,
                SingleWriter = false
            };

            _channel = Channel.CreateBounded<EmailDispatchJob>(options);
        }

        public ValueTask EnqueueAsync(EmailDispatchJob job, CancellationToken cancellationToken = default)
        {
            return _channel.Writer.WriteAsync(job, cancellationToken);
        }

        public ValueTask<EmailDispatchJob> DequeueAsync(CancellationToken cancellationToken)
        {
            return _channel.Reader.ReadAsync(cancellationToken);
        }
    }

    public class HeroRenderWorker : BackgroundService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly IHeroRenderQueue _queue;
        private readonly IEmailDispatchQueue _emailQueue;
        private readonly ILogger<HeroRenderWorker> _logger;

        public HeroRenderWorker(
            IServiceProvider serviceProvider,
            IHeroRenderQueue queue,
            IEmailDispatchQueue emailQueue,
            ILogger<HeroRenderWorker> logger)
        {
            _serviceProvider = serviceProvider;
            _queue = queue;
            _emailQueue = emailQueue;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("HeroRenderWorker started.");

            while (!stoppingToken.IsCancellationRequested)
            {
                HeroRenderJob job;

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

                    var videoComposer = scope.ServiceProvider.GetRequiredService<VideoComposerService>();
                    var env = scope.ServiceProvider.GetRequiredService<IWebHostEnvironment>();

                    var videosDir = Path.Combine(env.ContentRootPath, "Generated", "videos");
                    Directory.CreateDirectory(videosDir);

                    _logger.LogInformation(
                        "Hero render started. SessionId={SessionId}, Email={Email}, PhotoPath={PhotoPath}",
                        job.SessionId,
                        job.Email,
                        job.PhotoPath);

                    var mp4Path = await videoComposer.CreateHeroVideoFromPhotoAsync(job.PhotoPath, videosDir);

                    await _emailQueue.EnqueueAsync(
                        new EmailDispatchJob(job.SessionId, job.Email, mp4Path),
                        stoppingToken);

                    _logger.LogInformation(
                        "Hero render finished and email queued. SessionId={SessionId}, Output={OutputPath}",
                        job.SessionId,
                        mp4Path);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "Hero render failed. SessionId={SessionId}, Email={Email}, PhotoPath={PhotoPath}",
                        job.SessionId,
                        job.Email,
                        job.PhotoPath);
                }
            }

            _logger.LogInformation("HeroRenderWorker stopped.");
        }
    }

    public class EmailDispatchWorker : BackgroundService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly IEmailDispatchQueue _queue;
        private readonly ILogger<EmailDispatchWorker> _logger;
        private readonly int _consumerCount;

        public EmailDispatchWorker(
            IServiceProvider serviceProvider,
            IEmailDispatchQueue queue,
            ILogger<EmailDispatchWorker> logger)
        {
            _serviceProvider = serviceProvider;
            _queue = queue;
            _logger = logger;
            _consumerCount = Math.Max(2, Math.Min(Environment.ProcessorCount, 4));
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation(
                "EmailDispatchWorker started with {ConsumerCount} consumers.",
                _consumerCount);

            var workers = Enumerable.Range(0, _consumerCount)
                .Select(i => RunConsumerAsync(i + 1, stoppingToken))
                .ToArray();

            await Task.WhenAll(workers);

            _logger.LogInformation("EmailDispatchWorker stopped.");
        }

        private async Task RunConsumerAsync(int consumerId, CancellationToken stoppingToken)
        {
            _logger.LogInformation("EmailDispatchWorker consumer {ConsumerId} started.", consumerId);

            while (!stoppingToken.IsCancellationRequested)
            {
                EmailDispatchJob job;

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
                    var emailService = scope.ServiceProvider.GetRequiredService<EmailService>();

                    _logger.LogInformation(
                        "Email send started. Consumer={ConsumerId}, SessionId={SessionId}, Email={Email}, Mp4Path={Mp4Path}",
                        consumerId,
                        job.SessionId,
                        job.Email,
                        job.Mp4Path);

                    await emailService.SendHeroMp4Email(job.Email, job.Mp4Path);

                    _logger.LogInformation(
                        "Email send finished. Consumer={ConsumerId}, SessionId={SessionId}, Email={Email}",
                        consumerId,
                        job.SessionId,
                        job.Email);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "Email send failed. Consumer={ConsumerId}, SessionId={SessionId}, Email={Email}, Mp4Path={Mp4Path}",
                        consumerId,
                        job.SessionId,
                        job.Email,
                        job.Mp4Path);
                }
            }

            _logger.LogInformation("EmailDispatchWorker consumer {ConsumerId} stopped.", consumerId);
        }
    }
}