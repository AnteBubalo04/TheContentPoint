using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace XFrame.API.Services
{
    public class VideoComposerService
    {
        private readonly IWebHostEnvironment _env;
        private readonly IConfiguration _configuration;
        private readonly ILogger<VideoComposerService> _logger;

        private static readonly ConcurrentDictionary<string, double> _durationCache = new();

        public VideoComposerService(
            IWebHostEnvironment env,
            IConfiguration configuration,
            ILogger<VideoComposerService> logger)
        {
            _env = env;
            _configuration = configuration;
            _logger = logger;
        }

        public async Task<string> CreateHeroVideoFromPhotoAsync(
            string photoPath,
            string outputDir,
            CancellationToken cancellationToken = default)
        {
            if (!File.Exists(photoPath))
                throw new ArgumentException("Photo does not exist.", nameof(photoPath));

            var overlayPath = Path.Combine(_env.WebRootPath ?? "wwwroot", "overlay2.webm");
            if (!File.Exists(overlayPath))
                throw new ArgumentException($"overlay2.webm not found: {overlayPath}");

            Directory.CreateDirectory(outputDir);

            var finalPath = Path.Combine(
                outputDir,
                $"{Path.GetFileNameWithoutExtension(photoPath)}_hero_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}.mp4"
            );

            var tempPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}_hero.mp4");

            const int W = 1080;
            const int H = 1920;

            var overlaySeconds = await ProbeDurationSecondsCachedAsync(overlayPath, cancellationToken);
            if (overlaySeconds <= 0.1)
                throw new Exception($"Overlay duration invalid: {overlaySeconds}");

            var dur = overlaySeconds.ToString("0.###", CultureInfo.InvariantCulture);

            var ffmpegThreads = GetConfiguredThreadCount();

            var filter =
                $"[0:v]" +
                $"scale={W}:{H}:force_original_aspect_ratio=increase," +
                $"crop={W}:{H}," +
                $"format=rgba,setsar=1[bg];" +

                $"[1:v]" +
                $"scale={W}:{H}:force_original_aspect_ratio=increase," +
                $"crop={W}:{H}," +
                $"format=rgba,setsar=1," +
                $"fps=30,setpts=PTS-STARTPTS[hero];" +

                $"[bg][hero]overlay=0:0:format=auto:shortest=1[outv];" +
                $"[outv]format=yuv420p[v]";

            var args =
                $"-y -hide_banner " +
                $"-threads {ffmpegThreads} " +
                $"-loop 1 -framerate 30 -i \"{photoPath}\" " +
                $"-c:v libvpx-vp9 -i \"{overlayPath}\" " +
                $"-t {dur} " +
                $"-filter_complex \"{filter}\" " +
                $"-map \"[v]\" " +
                $"-r 30 -an " +
                $"-c:v libx264 -preset veryfast -crf 20 " +
                $"-pix_fmt yuv420p -movflags +faststart " +
                $"\"{tempPath}\"";

            var psi = new ProcessStartInfo
            {
                FileName = ResolveExecutable("VideoTools:FFmpegPath", "ffmpeg"),
                Arguments = args,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = new Process { StartInfo = psi };

            try
            {
                proc.Start();
                TryLowerProcessPriority(proc);
            }
            catch (Exception ex)
            {
                throw new Exception($"Ne mogu pokrenuti ffmpeg. Postavi VideoTools:FFmpegPath ili dodaj ffmpeg u PATH. Error: {ex.Message}");
            }

            var readErrTask = proc.StandardError.ReadToEndAsync();
            var readOutTask = proc.StandardOutput.ReadToEndAsync();

            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            try
            {
                await proc.WaitForExitAsync(linkedCts.Token);
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(true); } catch { }

                var errSoFar = await SafeTask(readErrTask);
                var outSoFar = await SafeTask(readOutTask);

                if (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    throw new Exception(
                        "FFmpeg timeout. Proces je prekinut.\n" +
                        $"ARGS:\n{args}\n\n" +
                        $"STDERR:\n{errSoFar}\n" +
                        $"STDOUT:\n{outSoFar}"
                    );
                }

                throw new OperationCanceledException(
                    "FFmpeg processing canceled.",
                    null,
                    cancellationToken.IsCancellationRequested ? cancellationToken : linkedCts.Token);
            }

            var stderr = await SafeTask(readErrTask);
            var stdout = await SafeTask(readOutTask);

            if (proc.ExitCode != 0 || !File.Exists(tempPath))
            {
                throw new Exception(
                    "FFmpeg failed.\n" +
                    $"ExitCode: {proc.ExitCode}\n\n" +
                    $"ARGS:\n{args}\n\n" +
                    $"STDERR:\n{stderr}\n" +
                    $"STDOUT:\n{stdout}"
                );
            }

            File.Move(tempPath, finalPath, overwrite: true);

            _logger.LogInformation(
                "Hero video composed successfully. Input={PhotoPath}, Overlay={OverlayPath}, Output={FinalPath}, Threads={Threads}",
                photoPath,
                overlayPath,
                finalPath,
                ffmpegThreads);

            return finalPath;
        }

        private async Task<double> ProbeDurationSecondsCachedAsync(string path, CancellationToken cancellationToken)
        {
            if (_durationCache.TryGetValue(path, out var cached) && cached > 0)
                return cached;

            var duration = await ProbeDurationSecondsAsync(path, cancellationToken);
            if (duration > 0)
                _durationCache[path] = duration;

            return duration;
        }

        private async Task<double> ProbeDurationSecondsAsync(string path, CancellationToken cancellationToken)
        {
            var args = $"-v error -show_format -of json \"{path}\"";

            var psi = new ProcessStartInfo
            {
                FileName = ResolveExecutable("VideoTools:FFprobePath", "ffprobe"),
                Arguments = args,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = new Process { StartInfo = psi };

            try
            {
                proc.Start();
                TryLowerProcessPriority(proc);
            }
            catch (Exception ex)
            {
                throw new Exception($"Ne mogu pokrenuti ffprobe. Postavi VideoTools:FFprobePath ili dodaj ffprobe u PATH. Error: {ex.Message}");
            }

            var jsonTask = proc.StandardOutput.ReadToEndAsync();
            var errTask = proc.StandardError.ReadToEndAsync();

            await proc.WaitForExitAsync(cancellationToken);

            var json = await jsonTask;
            var err = await errTask;

            if (proc.ExitCode != 0)
                throw new Exception($"ffprobe failed.\nARGS: {args}\nERR:\n{err}");

            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.TryGetProperty("format", out var formatEl) &&
                formatEl.TryGetProperty("duration", out var durEl))
            {
                var durStr = durEl.GetString();
                if (double.TryParse(durStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var d))
                    return d;
            }

            return 0;
        }

        private int GetConfiguredThreadCount()
        {
            var configured = _configuration["VideoTools:FFmpegThreads"];

            if (int.TryParse(configured, out var parsed) && parsed > 0)
                return parsed;

            return 1;
        }

        private void TryLowerProcessPriority(Process process)
        {
            try
            {
                process.PriorityClass = ProcessPriorityClass.BelowNormal;
            }
            catch
            {
            }
        }

        private string ResolveExecutable(string configKey, string defaultName)
        {
            var configured = _configuration[configKey];
            if (!string.IsNullOrWhiteSpace(configured))
                return configured;

            var fileName = OperatingSystem.IsWindows() ? $"{defaultName}.exe" : defaultName;
            var bundledPath = Path.Combine(_env.ContentRootPath, "tools", fileName);

            if (File.Exists(bundledPath))
                return bundledPath;

            return defaultName;
        }

        private static async Task<string> SafeTask(Task<string> t)
        {
            try { return await t; } catch { return string.Empty; }
        }
    }
}