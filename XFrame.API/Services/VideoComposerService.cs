using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;

namespace XFrame.API.Services
{
    public class VideoComposerService
    {
        private readonly IWebHostEnvironment _env;

        public VideoComposerService(IWebHostEnvironment env)
        {
            _env = env;
        }

        public async Task<string> CreateHeroVideoFromPhotoAsync(string photoPath, string outputDir)
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

            var overlaySeconds = await ProbeDurationSecondsAsync(overlayPath);
            if (overlaySeconds <= 0.1)
                throw new Exception($"Overlay duration invalid: {overlaySeconds}");

            var dur = overlaySeconds.ToString("0.###", CultureInfo.InvariantCulture);

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
                FileName = "ffmpeg",
                Arguments = args,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = new Process { StartInfo = psi };

            try { proc.Start(); }
            catch (Exception ex)
            {
                throw new Exception($"Ne mogu pokrenuti ffmpeg. Je li u PATH-u? Error: {ex.Message}");
            }

            var readErrTask = proc.StandardError.ReadToEndAsync();
            var readOutTask = proc.StandardOutput.ReadToEndAsync();

            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));

            try
            {
                await proc.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(true); } catch { }

                var errSoFar = await SafeTask(readErrTask);
                var outSoFar = await SafeTask(readOutTask);

                throw new Exception(
                    "FFmpeg timeout. Proces je prekinut.\n" +
                    $"ARGS:\n{args}\n\n" +
                    $"STDERR:\n{errSoFar}\n" +
                    $"STDOUT:\n{outSoFar}"
                );
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
            return finalPath;
        }

        private static async Task<double> ProbeDurationSecondsAsync(string path)
        {
            var args = $"-v error -show_format -of json \"{path}\"";

            var psi = new ProcessStartInfo
            {
                FileName = "ffprobe",
                Arguments = args,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = new Process { StartInfo = psi };

            try { proc.Start(); }
            catch (Exception ex)
            {
                throw new Exception($"Ne mogu pokrenuti ffprobe. Je li u PATH-u? Error: {ex.Message}");
            }

            var json = await proc.StandardOutput.ReadToEndAsync();
            var err = await proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync();

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

        private static async Task<string> SafeTask(Task<string> t)
        {
            try { return await t; } catch { return ""; }
        }
    }
}
