using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace XFrame.RenderWorker.Services
{
    public class VideoComposerService
    {
        private readonly IWebHostEnvironment _env;

        private readonly SemaphoreSlim _overlayMetadataLock = new(1, 1);
        private OverlayMetadata? _cachedOverlayMetadata;

        private sealed class OverlayMetadata
        {
            public string Path { get; init; } = string.Empty;
            public double DurationSeconds { get; init; }
            public int Width { get; init; }
            public int Height { get; init; }
        }

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

            // camera.js mapping:
            // left: 51%, top: 44%, width: 67vw, height: 74vh, transform: translate(-50%, -50%) scaleX(-1)
            const int PHOTO_W = 724;
            const int PHOTO_H = 1421;
            const int PHOTO_X = 189;
            const int PHOTO_Y = 134;

            var overlayMeta = await GetOverlayMetadataAsync(overlayPath);
            if (overlayMeta.DurationSeconds <= 0.1)
                throw new Exception($"Overlay duration invalid: {overlayMeta.DurationSeconds}");

            var dur = overlayMeta.DurationSeconds.ToString("0.###", CultureInfo.InvariantCulture);

            var filter =
                $"color=c=black@0.0:s={W}x{H}:d={dur},format=rgba[base];" +
                $"[0:v]" +
                $"scale={PHOTO_W}:{PHOTO_H}:force_original_aspect_ratio=increase," +
                $"crop={PHOTO_W}:{PHOTO_H}," +
                $"hflip," +
                $"format=rgba[photo];" +
                $"[base][photo]overlay={PHOTO_X}:{PHOTO_Y}:format=auto[bg];" +
                $"[1:v]" +
                $"scale={W}:{H}," +
                $"format=rgba[ov];" +
                $"[bg][ov]overlay=0:0:format=auto," +
                $"format=yuv420p[v]";

            var args =
                $"-y -hide_banner -loglevel warning " +
                $"-threads 1 -filter_threads 1 -filter_complex_threads 1 " +
                $"-loop 1 -framerate 30 -i \"{photoPath}\" " +
                $"-i \"{overlayPath}\" " +
                $"-t {dur} " +
                $"-filter_complex \"{filter}\" " +
                $"-map \"[v]\" " +
                $"-r 30 -an " +
                $"-c:v libx264 -preset ultrafast -crf 23 " +
                $"-pix_fmt yuv420p -movflags +faststart " +
                $"-max_muxing_queue_size 256 " +
                $"\"{tempPath}\"";

            var psi = new ProcessStartInfo
            {
                FileName = "ffmpeg",
                Arguments = args,
                RedirectStandardError = true,
                RedirectStandardOutput = false,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = new Process { StartInfo = psi };

            try
            {
                proc.Start();

                try
                {
                    proc.PriorityClass = ProcessPriorityClass.BelowNormal;
                }
                catch
                {
                }
            }
            catch (Exception ex)
            {
                throw new Exception($"Ne mogu pokrenuti ffmpeg. Je li u PATH-u? Error: {ex.Message}");
            }

            var stderrTask = CaptureBoundedStderrAsync(proc.StandardError, maxLines: 200);

            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));

            try
            {
                await proc.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(true); } catch { }

                var errSoFar = await SafeTask(stderrTask);

                throw new Exception(
                    "FFmpeg timeout. Proces je prekinut.\n" +
                    $"ARGS:\n{args}\n\n" +
                    $"STDERR:\n{errSoFar}"
                );
            }
            finally
            {
                if (proc.HasExited == false)
                {
                    try { proc.Kill(true); } catch { }
                }
            }

            var stderr = await SafeTask(stderrTask);

            if (proc.ExitCode != 0 || !File.Exists(tempPath))
            {
                SafeDelete(tempPath);

                throw new Exception(
                    "FFmpeg failed.\n" +
                    $"ExitCode: {proc.ExitCode}\n\n" +
                    $"ARGS:\n{args}\n\n" +
                    $"STDERR:\n{stderr}"
                );
            }

            try
            {
                File.Move(tempPath, finalPath, overwrite: true);
                return finalPath;
            }
            finally
            {
                SafeDelete(tempPath);
            }
        }

        private async Task<OverlayMetadata> GetOverlayMetadataAsync(string overlayPath)
        {
            var cached = _cachedOverlayMetadata;
            if (cached != null && string.Equals(cached.Path, overlayPath, StringComparison.OrdinalIgnoreCase))
                return cached;

            await _overlayMetadataLock.WaitAsync();
            try
            {
                cached = _cachedOverlayMetadata;
                if (cached != null && string.Equals(cached.Path, overlayPath, StringComparison.OrdinalIgnoreCase))
                    return cached;

                var meta = await ProbeOverlayMetadataAsync(overlayPath);
                _cachedOverlayMetadata = meta;
                return meta;
            }
            finally
            {
                _overlayMetadataLock.Release();
            }
        }

        private static async Task<OverlayMetadata> ProbeOverlayMetadataAsync(string path)
        {
            var args = $"-v error -show_streams -show_format -of json \"{path}\"";

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

            try
            {
                proc.Start();

                try
                {
                    proc.PriorityClass = ProcessPriorityClass.BelowNormal;
                }
                catch
                {
                }
            }
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

            double duration = 0;
            int width = 0;
            int height = 0;

            if (doc.RootElement.TryGetProperty("format", out var formatEl) &&
                formatEl.TryGetProperty("duration", out var durEl))
            {
                var durStr = durEl.GetString();
                if (!string.IsNullOrWhiteSpace(durStr) &&
                    double.TryParse(durStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var d))
                {
                    duration = d;
                }
            }

            if (doc.RootElement.TryGetProperty("streams", out var streamsEl) &&
                streamsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var streamEl in streamsEl.EnumerateArray())
                {
                    if (streamEl.TryGetProperty("codec_type", out var codecTypeEl) &&
                        string.Equals(codecTypeEl.GetString(), "video", StringComparison.OrdinalIgnoreCase))
                    {
                        if (streamEl.TryGetProperty("width", out var widthEl) && widthEl.TryGetInt32(out var w))
                            width = w;

                        if (streamEl.TryGetProperty("height", out var heightEl) && heightEl.TryGetInt32(out var h))
                            height = h;

                        break;
                    }
                }
            }

            return new OverlayMetadata
            {
                Path = path,
                DurationSeconds = duration,
                Width = width,
                Height = height
            };
        }

        private static async Task<string> CaptureBoundedStderrAsync(StreamReader reader, int maxLines)
        {
            var queue = new Queue<string>(maxLines);

            while (!reader.EndOfStream)
            {
                var line = await reader.ReadLineAsync();
                if (line == null)
                    continue;

                if (queue.Count >= maxLines)
                    queue.Dequeue();

                queue.Enqueue(line);
            }

            var sb = new StringBuilder();
            foreach (var line in queue)
                sb.AppendLine(line);

            return sb.ToString();
        }

        private static void SafeDelete(string path)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
            }
        }

        private static async Task<string> SafeTask(Task<string> t)
        {
            try { return await t; } catch { return ""; }
        }
    }
}
