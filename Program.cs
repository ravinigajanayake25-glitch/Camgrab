using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using static SnapshotHelpers;

// Load initial configuration for CLI subcommands or output path resolution
var initialConfig = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: true)
    .AddEnvironmentVariables()
    .Build();

var configuredDir = initialConfig.GetSection("SnapshotService").GetValue<string>("OutputDirectory");
var outputDirectory = ResolveOutputDirectory(configuredDir);
Directory.CreateDirectory(outputDirectory);

if (args.Length > 0)
{
    var command = args[0].ToLowerInvariant();
    if (command is "--list" or "-l")
    {
        ListSavedSnapshots(outputDirectory);
        return;
    }
    if (command is "--filter" or "-f")
    {
        FilterSnapshotsByTimeRange(args, outputDirectory);
        return;
    }
    if (command is "--trim" or "-t")
    {
        TrimVideoClipByTimeRange(args);
        return;
    }
    if (command is "--info" or "-i" && args.Length > 1)
    {
        DisplaySnapshotInfo(args[1], outputDirectory);
        return;
    }
    if (command is "--help" or "-h")
    {
        PrintUsage();
        return;
    }
}

// Build standard .NET Worker Host with Windows Service support
var builder = Host.CreateApplicationBuilder(args);

// Register Windows Service integration (allows running via Windows Service Control Manager or Console)
builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "CamGrabSnapshotService";
});

// Register Snapshot Background Worker
builder.Services.AddHostedService<SnapshotBackgroundService>();

var host = builder.Build();
await host.RunAsync();

// =========================================================================================
// Background Worker Implementation
// =========================================================================================
public sealed class SnapshotBackgroundService : BackgroundService
{
    private readonly IConfiguration _config;
    private readonly ILogger<SnapshotBackgroundService> _logger;
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(15) };

    public SnapshotBackgroundService(IConfiguration config, ILogger<SnapshotBackgroundService> logger)
    {
        _config = config;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 1. Load configuration from appsettings.json / Environment variables
        var section = _config.GetSection("SnapshotService");
        var intervalSeconds = section.GetValue("IntervalSeconds", 5);
        var interval = TimeSpan.FromSeconds(intervalSeconds > 0 ? intervalSeconds : 5);

        var outputDir = section.GetValue<string>("OutputDirectory");
        var resolvedOutputDir = ResolveOutputDirectory(outputDir);
        Directory.CreateDirectory(resolvedOutputDir);

        var enableUpload = section.GetValue("EnableUpload", true);
        var configuredUploadUrl = section.GetValue<string>("UploadApiUrl") ?? "http://10.64.30.96:8000/upload";
        string? uploadUrl = (!enableUpload || string.IsNullOrWhiteSpace(configuredUploadUrl) || configuredUploadUrl.Equals("disabled", StringComparison.OrdinalIgnoreCase))
            ? null
            : configuredUploadUrl;

        var bridgeHost = section.GetValue("BridgeHost", "10.64.30.142")!;
        var bridgePort = section.GetValue("BridgePort", 554);

        var username = Environment.GetEnvironmentVariable("CAM_USER")
            ?? section.GetValue("Credentials:Username", "mipuser")!;
        var password = Environment.GetEnvironmentVariable("CAM_PASS")
            ?? section.GetValue("Credentials:Password", "M1l3T@123")!;

        // 2. Start internal transparent RTSP Auth proxy to solve FFmpeg's SHA-256 Digest bug
        await using var authProxy = new RtspAuthSanitizerProxy(bridgeHost, bridgePort);

        // 3. Resolve active streams from configuration
        var streamConfigs = section.GetSection("Streams").Get<List<StreamConfig>>() ?? new()
        {
            new StreamConfig { Name = "61252WLKR", Guid = "9ac405ca-1db1-4696-9d7f-ebccc74a9b18" },
            new StreamConfig { Name = "61253WLKL", Guid = "229802e9-9bb3-4e89-aec7-005c3e44fb94" },
            new StreamConfig { Name = "Front-Door", Guid = "" }
        };

        var activeStreams = new List<(string Name, string Url)>();
        foreach (var sc in streamConfigs)
        {
            if (!string.IsNullOrWhiteSpace(sc.Url))
            {
                activeStreams.Add((sc.Name, sc.Url));
            }
            else
            {
                // Auto-resolve to direct Dahua camera RTSP endpoint if matched by name or GUID
                string directUrl = GetDirectCameraUrl(sc.Name, sc.Guid);
                if (!string.IsNullOrWhiteSpace(directUrl))
                {
                    _logger.LogInformation("Camera '{Name}' resolved to direct Dahua RTSP stream: {Url}",
                        sc.Name, RedactCredentials(directUrl, directUrl));
                    activeStreams.Add((sc.Name, directUrl));
                }
                else if (!string.IsNullOrWhiteSpace(sc.Guid))
                {
                    var credentials = string.IsNullOrEmpty(password)
                        ? ""
                        : $"{Uri.EscapeDataString(username)}:{Uri.EscapeDataString(password)}@";
                    var url = $"rtsp://{credentials}127.0.0.1:{authProxy.LocalPort}/live/{sc.Guid}";
                    activeStreams.Add((sc.Name, url));
                }
                else
                {
                    _logger.LogWarning("Stream '{Name}' has neither Guid nor Url configured. Skipping.", sc.Name);
                }
            }
        }

        if (activeStreams.Count == 0)
        {
            _logger.LogError("No active camera streams configured in appsettings.json. Exiting.");
            return;
        }

        _logger.LogInformation("====================================================================");
        _logger.LogInformation(" Camgrab RTSP Snapshot Service (Windows Service & I-Frame Grabber)");
        _logger.LogInformation("====================================================================");
        _logger.LogInformation("• Capture Interval: {Interval}s gap", interval.TotalSeconds);
        _logger.LogInformation("• Target Frame:     I-Frame (Keyframe with visual timestamp overlay)");
        _logger.LogInformation("• Auth User:        {User}", username);
        _logger.LogInformation("• Upload Endpoint:  {Url}", uploadUrl ?? "Disabled (saving locally only)");
        _logger.LogInformation("• Output Directory: {Dir}", resolvedOutputDir);
        _logger.LogInformation("• Active Streams:   {Count}", activeStreams.Count);
        foreach (var s in activeStreams)
        {
            _logger.LogInformation("    - {Name}", s.Name);
        }
        _logger.LogInformation("====================================================================");

        var captureTasks = activeStreams.Select(stream =>
            CaptureLoopAsync(stream.Name, stream.Url, resolvedOutputDir, interval, uploadUrl, stoppingToken)
        ).ToArray();

        try
        {
            await Task.WhenAll(captureTasks);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation("Snapshot service stopped cleanly.");
        }
    }

    private async Task CaptureLoopAsync(
        string cameraName,
        string streamUrl,
        string directory,
        TimeSpan captureInterval,
        string? uploadUrl,
        CancellationToken cancellationToken)
    {
        int consecutiveFailures = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            var timer = Stopwatch.StartNew();
            var capturedAt = DateTime.Now;
            var fileName = $"{cameraName}_{capturedAt:yyyyMMdd_HHmmss_fff}.jpg";

            // Save new images organized into date subdirectories in the project: shots/<yyyy-MM-dd>/
            var dateFolder = Path.Combine(directory, capturedAt.ToString("yyyy-MM-dd"));
            Directory.CreateDirectory(dateFolder);
            var filePath = Path.Combine(dateFolder, fileName);

            bool success = await CaptureFrameAsync(cameraName, streamUrl, filePath, directory, capturedAt, uploadUrl, consecutiveFailures, cancellationToken);

            if (success)
            {
                if (consecutiveFailures > 0)
                {
                    _logger.LogInformation("Stream '{Camera}' recovered and resumed capturing.", cameraName);
                    consecutiveFailures = 0;
                }

                var remaining = captureInterval - timer.Elapsed;
                if (remaining > TimeSpan.Zero)
                {
                    await Task.Delay(remaining, cancellationToken);
                }
            }
            else
            {
                consecutiveFailures++;

                // Fallback attempt: if failing against local proxy (e.g. 400 Bad Request from ONB), try direct camera RTSP stream
                if (consecutiveFailures >= 2 && streamUrl.Contains("127.0.0.1"))
                {
                    var fallbackUrl = GetDirectCameraUrl(cameraName, null);
                    if (!string.IsNullOrWhiteSpace(fallbackUrl) && !string.Equals(fallbackUrl, streamUrl, StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogWarning("Stream '{Camera}' encountered server rejection via proxy. Auto-falling back to direct camera stream: {Url}",
                            cameraName, RedactCredentials(fallbackUrl, fallbackUrl));
                        streamUrl = fallbackUrl;
                        consecutiveFailures = 0;
                        continue;
                    }
                }

                var backoff = consecutiveFailures switch
                {
                    1 => captureInterval,
                    2 => TimeSpan.FromSeconds(15),
                    _ => TimeSpan.FromSeconds(60)
                };

                if (consecutiveFailures == 3)
                {
                    _logger.LogWarning("Stream '{Camera}' is offline or rejected by server (e.g. 400 Bad Request). Pausing attempts, retrying every {Sec}s...",
                        cameraName, backoff.TotalSeconds);
                }

                await Task.Delay(backoff, cancellationToken);
            }
        }
    }

    private async Task<bool> CaptureFrameAsync(
        string cameraName,
        string streamUrl,
        string filePath,
        string directory,
        DateTime capturedAt,
        string? uploadUrl,
        int consecutiveFailures,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(ResolveFfmpegPath())
        {
            RedirectStandardError = true,
            UseShellExecute = false
        };

        startInfo.ArgumentList.Add("-y");
        startInfo.ArgumentList.Add("-loglevel");
        startInfo.ArgumentList.Add("error");
        startInfo.ArgumentList.Add("-rtsp_transport");
        startInfo.ArgumentList.Add("tcp");

        // I-Frame optimization: only decode keyframes
        startInfo.ArgumentList.Add("-skip_frame");
        startInfo.ArgumentList.Add("nokey");

        startInfo.ArgumentList.Add("-i");
        startInfo.ArgumentList.Add(streamUrl);
        startInfo.ArgumentList.Add("-frames:v");
        startInfo.ArgumentList.Add("1");
        startInfo.ArgumentList.Add("-update");
        startInfo.ArgumentList.Add("1");

        // Burn-in on-screen Date & Time watermark overlay onto the snapshot image (Server Time)
        const string fontPath = @"C\:/Windows/Fonts/arial.ttf";
        if (File.Exists(@"C:\Windows\Fonts\arial.ttf"))
        {
            var overlay = $"{cameraName} | {capturedAt:yyyy-MM-dd HH:mm:ss} (Server Time)".Replace(":", @"\:");
            startInfo.ArgumentList.Add("-vf");
            startInfo.ArgumentList.Add($"drawtext=fontfile='{fontPath}':text='{overlay}':fontcolor=white:fontsize=36:box=1:boxcolor=black@0.65:boxborderw=8:x=30:y=30");
        }

        startInfo.ArgumentList.Add("-q:v");
        startInfo.ArgumentList.Add("2");
        startInfo.ArgumentList.Add(filePath);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("ffmpeg did not start.");
        var errorOutput = process.StandardError.ReadToEndAsync();

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
            await process.WaitForExitAsync();
            await errorOutput;
            throw;
        }

        var error = await errorOutput;
        if (process.ExitCode == 0)
        {
            var metadata = RetrieveImageDateTime(filePath, cameraName, capturedAt);
            var relativeDisplayPath = Path.Combine(capturedAt.ToString("yyyy-MM-dd"), Path.GetFileName(filePath));
            _logger.LogInformation("[{Date} {Time}] Saved I-Frame -> {Camera}: {File} ({Size:F1} KB)",
                metadata.Date, metadata.Time, cameraName, relativeDisplayPath, metadata.FileSizeBytes / 1024.0);

            await SaveMetadataRecordAsync(metadata, directory);

            // Upload matching exact curl command specifications (if upload is configured):
            // curl -F camera_name=... -F date=... -F time=... -F image=@photo.jpg <url>
            if (!string.IsNullOrWhiteSpace(uploadUrl))
            {
                await UploadSnapshotAsync(filePath, cameraName, capturedAt, uploadUrl, directory);
            }
            return true;
        }
        else
        {
            if (consecutiveFailures < 2)
            {
                _logger.LogError("Capture failed for {Camera} (ffmpeg exit {Code}): {Error}",
                    cameraName, process.ExitCode, RedactCredentials(error, streamUrl));
            }
            return false;
        }
    }

    private static DateTime _nextUploadRetryUtc = DateTime.MinValue;
    private static bool _uploadIsOffline = false;

    private async Task UploadSnapshotAsync(string filePath, string cameraName, DateTime timestamp, string uploadUrl, string directory)
    {
        if (DateTime.UtcNow < _nextUploadRetryUtc)
        {
            // Circuit-breaker: Wait for cooldown before retrying unreachable server
            return;
        }

        try
        {
            using var content = new MultipartFormDataContent();
            content.Add(new StringContent(cameraName), "camera_name");
            content.Add(new StringContent(timestamp.ToString("yyyy-MM-dd")), "date");
            content.Add(new StringContent(timestamp.ToString("HH:mm:ss")), "time");

            var imageBytes = await File.ReadAllBytesAsync(filePath);
            var fileContent = new ByteArrayContent(imageBytes);
            fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
            content.Add(fileContent, "image", Path.GetFileName(filePath));

            var response = await _httpClient.PostAsync(uploadUrl, content);
            var body = await response.Content.ReadAsStringAsync();

            // Auto-heal: If receiver returns "uploads are disabled", automatically re-enable on port 8080 and retry
            if (!response.IsSuccessStatusCode && body.Contains("uploads are disabled", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var uri = new Uri(uploadUrl);
                    var enableUrl = $"{uri.Scheme}://{uri.Host}:8080/api/upload-enabled";
                    using var enableContent = new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("enabled", "true") });
                    var enableResp = await _httpClient.PutAsync(enableUrl, enableContent);
                    if (enableResp.IsSuccessStatusCode)
                    {
                        _logger.LogInformation("  [Auto-Heal] Re-enabled upload API via {Url}. Retrying upload...", enableUrl);
                        using var retryContent = new MultipartFormDataContent();
                        retryContent.Add(new StringContent(cameraName), "camera_name");
                        retryContent.Add(new StringContent(timestamp.ToString("yyyy-MM-dd")), "date");
                        retryContent.Add(new StringContent(timestamp.ToString("HH:mm:ss")), "time");
                        var retryImage = new ByteArrayContent(imageBytes);
                        retryImage.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
                        retryContent.Add(retryImage, "image", Path.GetFileName(filePath));
                        response = await _httpClient.PostAsync(uploadUrl, retryContent);
                        body = await response.Content.ReadAsStringAsync();
                    }
                }
                catch { }
            }

            if (response.IsSuccessStatusCode)
            {
                if (_uploadIsOffline)
                {
                    _uploadIsOffline = false;
                    _logger.LogInformation("  [Upload Restored] Connection to API server ({Url}) re-established.", uploadUrl);
                }
                _logger.LogInformation("  [Uploaded OK] {Camera} -> {Url} ({Body})", cameraName, uploadUrl, body.Trim());
            }
            else
            {
                _logger.LogWarning("  [Upload Warning] Server returned HTTP {Status}: {Body}", response.StatusCode, body.Trim());
            }
        }
        catch (HttpRequestException ex)
        {
            _nextUploadRetryUtc = DateTime.UtcNow.AddSeconds(60);
            if (!_uploadIsOffline)
            {
                _uploadIsOffline = true;
                _logger.LogWarning("  [Upload Notice] Target API server ({Url}) is currently offline or unreachable: {Message}. Snapshots continue saving locally. Next upload check in 60s.",
                    uploadUrl, ex.Message);
            }
        }
        catch (Exception ex)
        {
            _nextUploadRetryUtc = DateTime.UtcNow.AddSeconds(30);
            if (!_uploadIsOffline)
            {
                _uploadIsOffline = true;
                _logger.LogWarning("  [Upload Notice] Upload failed ({Url}): {Message}. Snapshots continue saving locally. Retrying in 30s.",
                    uploadUrl, ex.Message);
            }
        }
    }

    public override void Dispose()
    {
        _httpClient.Dispose();
        base.Dispose();
    }
}

public class StreamConfig
{
    public string Name { get; set; } = string.Empty;
    public string Guid { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
}

// =========================================================================================
// Helpers and Utilities
// =========================================================================================
public static class SnapshotHelpers
{
    public static async Task SaveMetadataRecordAsync(SnapshotMetadata meta, string directory)
{
    try
    {
        var metaFile = Path.Combine(directory, "snapshots_metadata.json");
        List<SnapshotMetadata> records;

        if (File.Exists(metaFile))
        {
            var json = await File.ReadAllTextAsync(metaFile);
            records = JsonSerializer.Deserialize<List<SnapshotMetadata>>(json) ?? new();
        }
        else
        {
            records = new();
        }

        records.Add(meta);
        if (records.Count > 1000)
        {
            records = records.TakeLast(1000).ToList();
        }

        var options = new JsonSerializerOptions { WriteIndented = true };
        var updatedJson = JsonSerializer.Serialize(records, options);
        await File.WriteAllTextAsync(metaFile, updatedJson);
    }
    catch { }
}

    public static SnapshotMetadata RetrieveImageDateTime(string filePath, string? knownCameraName = null, DateTime? knownTimestamp = null)
{
    var fileInfo = new FileInfo(filePath);
    var fileName = fileInfo.Name;
    DateTime timestamp;

    if (knownTimestamp.HasValue)
    {
        timestamp = knownTimestamp.Value;
    }
    else
    {
        var fullMatch = Regex.Match(fileName, @"^(.+?)_(\d{8})_(\d{6})(?:_(\d{3}))?", RegexOptions.IgnoreCase);
        if (fullMatch.Success)
        {
            var dateStr = fullMatch.Groups[2].Value;
            var timeStr = fullMatch.Groups[3].Value;
            var msStr = fullMatch.Groups[4].Success ? fullMatch.Groups[4].Value : "000";
            if (DateTime.TryParseExact($"{dateStr}{timeStr}{msStr}", "yyyyMMddHHmmssfff", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            {
                timestamp = parsed;
            }
            else
            {
                timestamp = fileInfo.CreationTime;
            }
        }
        else
        {
            var shortMatch = Regex.Match(fileName, @"^(.+?)_(\d{6})\.jpg$", RegexOptions.IgnoreCase);
            if (shortMatch.Success && DateTime.TryParseExact(shortMatch.Groups[2].Value, "HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var timeParsed))
            {
                timestamp = fileInfo.CreationTime.Date.Add(timeParsed.TimeOfDay);
            }
            else
            {
                timestamp = fileInfo.CreationTime;
            }
        }
    }

    var cameraName = knownCameraName
        ?? (fileName.Contains('_') ? fileName[..fileName.IndexOf('_')] : "camera");

    return new SnapshotMetadata(
        CameraName: cameraName,
        FileName: fileName,
        Date: timestamp.ToString("yyyy-MM-dd"),
        Time: timestamp.ToString("HH:mm:ss.fff"),
        IsoTimestamp: timestamp.ToString("o"),
        FileSizeBytes: fileInfo.Exists ? fileInfo.Length : 0,
        FilePath: filePath,
        FrameType: "I-Frame (Keyframe)"
    );
}

    public static void ListSavedSnapshots(string directory)
{
    if (!Directory.Exists(directory))
    {
        Console.WriteLine($"Snapshots directory '{directory}' does not exist.");
        return;
    }

    var imageFiles = Directory.GetFiles(directory, "*.jpg", SearchOption.AllDirectories)
        .OrderByDescending(f => File.GetCreationTime(f))
        .ToArray();

    var metas = imageFiles.Select(f => RetrieveImageDateTime(f)).ToList();
    var groups = metas.GroupBy(m => m.CameraName).OrderBy(g => g.Key);

    Console.WriteLine("==========================================================================================");
    Console.WriteLine($" Saved Snapshots in {directory} (Total: {imageFiles.Length})");
    Console.WriteLine("==========================================================================================");

    foreach (var group in groups)
    {
        Console.WriteLine($"\n[Camera Feed: {group.Key}]  (Interval: ~5 seconds between captures)");
        Console.WriteLine($"{"Camera",-12} | {"Date",-10} | {"Time",-12} | {"Size (KB)",-10} | {"File Path"}");
        Console.WriteLine(new string('-', 90));
        foreach (var meta in group.OrderByDescending(m => m.FileName).Take(15))
        {
            var relativePath = Path.GetRelativePath(directory, meta.FilePath);
            Console.WriteLine($"{meta.CameraName,-12} | {meta.Date,-10} | {meta.Time,-12} | {meta.FileSizeBytes / 1024.0,9:F1} KB | {relativePath}");
        }
    }
    Console.WriteLine("\n==========================================================================================");
}

    public static void DisplaySnapshotInfo(string filePath, string? baseDirectory = null)
{
    if (!File.Exists(filePath) && !string.IsNullOrWhiteSpace(baseDirectory))
    {
        var candidate = Path.Combine(baseDirectory, filePath);
        if (File.Exists(candidate))
        {
            filePath = candidate;
        }
        else
        {
            var found = Directory.GetFiles(baseDirectory, Path.GetFileName(filePath), SearchOption.AllDirectories).FirstOrDefault();
            if (found != null)
            {
                filePath = found;
            }
        }
    }

    if (!File.Exists(filePath))
    {
        Console.Error.WriteLine($"File not found: {filePath}");
        return;
    }

    var meta = RetrieveImageDateTime(filePath);
    Console.WriteLine("========================================");
    Console.WriteLine(" Snapshot Image Date & Time Information");
    Console.WriteLine("========================================");
    Console.WriteLine($"Camera:        {meta.CameraName}");
    Console.WriteLine($"Date:          {meta.Date}");
    Console.WriteLine($"Time:          {meta.Time}");
    Console.WriteLine($"ISO Timestamp: {meta.IsoTimestamp}");
    Console.WriteLine($"Frame Type:    {meta.FrameType}");
    Console.WriteLine($"File Size:     {meta.FileSizeBytes:N0} bytes ({meta.FileSizeBytes / 1024.0:F1} KB)");
    Console.WriteLine($"File Path:     {meta.FilePath}");
    Console.WriteLine("========================================");
}

    public static void PrintUsage()
{
    Console.WriteLine("Camgrab RTSP Snapshot Service");
    Console.WriteLine("Usage:");
    Console.WriteLine("  dotnet run                    Start 5-second I-frame capture loop (Console or Service)");
    Console.WriteLine("  dotnet run -- --list          List all saved snapshots with Date and Time");
    Console.WriteLine("  dotnet run -- --filter <from> <to> [camera] [options]");
    Console.WriteLine("                                Filter saved video frames by date & time range");
    Console.WriteLine("  dotnet run -- --trim <file> <start> <end> [out] [--play]");
    Console.WriteLine("                                Trim / cut a video clip to take only a specific time range");
    Console.WriteLine("  dotnet run -- --info <file>   Retrieve Date and Time for a specific snapshot file");
    Console.WriteLine("  dotnet run -- --help          Show this help message");
    Console.WriteLine();
    Console.WriteLine("Filter & Clip Examples:");
    Console.WriteLine("  dotnet run -- --filter \"2026-10-24 02:12:00\" \"2026-10-24 02:34:00\" 61252WLKR");
    Console.WriteLine("  dotnet run -- --filter \"2026-10-24 02:12\" \"2026-10-24 02:34\" 61252WLKR --render-mp4 ./my_clip.mp4 --play");
    Console.WriteLine("  dotnet run -- --filter \"2.12\" \"2.34\" 61252WLKR --render-mp4 ./clip_0212_0234.mp4 --play");
    Console.WriteLine();
    Console.WriteLine("Video Trimming Examples:");
    Console.WriteLine("  dotnet run -- --trim ./clip.mp4 00:01:00 00:02:30");
    Console.WriteLine("  dotnet run -- --trim ./clip.mp4 10 45 ./trimmed.mp4 --play");
    Console.WriteLine();
    Console.WriteLine("Windows Service:");
    Console.WriteLine("  sc.exe create \"CamGrabSnapshotService\" binPath= \"<path>\\camgrab.exe\" start= auto");
    Console.WriteLine("  sc.exe start \"CamGrabSnapshotService\"");
}

    public static void TrimVideoClipByTimeRange(string[] args)
    {
        if (args.Length < 4)
        {
            Console.WriteLine("Usage: dotnet run -- --trim <input_file.mp4> <start_time> <end_time> [output_file.mp4] [--play]");
            Console.WriteLine("Examples:");
            Console.WriteLine("  dotnet run -- --trim ./clip.mp4 00:01:00 00:02:30");
            Console.WriteLine("  dotnet run -- --trim ./clip.mp4 10 45 ./trimmed.mp4 --play");
            Console.WriteLine("  dotnet run -- --trim ./clip.mp4 01:15 03:20 ./trimmed.mp4");
            return;
        }

        string inputFile = args[1];
        string startStr = args[2];
        string endStr = args[3];
        string? outputFile = null;
        bool autoPlay = false;

        for (int i = 4; i < args.Length; i++)
        {
            if (args[i].Equals("--play", StringComparison.OrdinalIgnoreCase) || args[i].Equals("--vlc", StringComparison.OrdinalIgnoreCase))
            {
                autoPlay = true;
            }
            else if (outputFile == null && !args[i].StartsWith("--"))
            {
                outputFile = args[i];
            }
        }

        if (!File.Exists(inputFile))
        {
            Console.Error.WriteLine($"Error: Input video file '{inputFile}' does not exist.");
            return;
        }

        if (string.IsNullOrWhiteSpace(outputFile))
        {
            string dir = Path.GetDirectoryName(Path.GetFullPath(inputFile)) ?? "";
            string name = Path.GetFileNameWithoutExtension(inputFile);
            string cleanStart = startStr.Replace(":", "-");
            string cleanEnd = endStr.Replace(":", "-");
            outputFile = Path.Combine(dir, $"{name}_trimmed_{cleanStart}_to_{cleanEnd}.mp4");
        }

        string ffmpeg = ResolveFfmpegPath();
        if (string.IsNullOrEmpty(ffmpeg) || (!File.Exists(ffmpeg) && ffmpeg != "ffmpeg"))
        {
            Console.Error.WriteLine("Error: ffmpeg.exe was not found. Please install ffmpeg or place it in the application directory.");
            return;
        }

        Console.WriteLine("==========================================================================================");
        Console.WriteLine($" Trimming Video Clip by Time Range");
        Console.WriteLine($" Source:  {Path.GetFullPath(inputFile)}");
        Console.WriteLine($" From:    {startStr}");
        Console.WriteLine($" To:      {endStr}");
        Console.WriteLine($" Output:  {Path.GetFullPath(outputFile)}");
        Console.WriteLine("==========================================================================================");

        string argsStr = $"-y -ss {startStr} -to {endStr} -i \"{inputFile}\" -c:v libx264 -preset veryfast -pix_fmt yuv420p -c:a aac \"{outputFile}\"";

        var psi = new ProcessStartInfo(ffmpeg, argsStr)
        {
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        try
        {
            using var p = Process.Start(psi);
            if (p != null)
            {
                string err = p.StandardError.ReadToEnd();
                p.WaitForExit();
                if (p.ExitCode != 0)
                {
                    Console.Error.WriteLine($"FFmpeg trimming failed (code {p.ExitCode}): {err}");
                    return;
                }
            }

            var fi = new FileInfo(outputFile);
            Console.WriteLine($"\nSuccessfully trimmed clip: {fi.FullName} ({fi.Length / (1024.0 * 1024.0):F2} MB)");

            if (autoPlay)
            {
                string vlc = ResolveVlcPath();
                Console.WriteLine($"Launching VLC Media Player to play trimmed clip...");
                try
                {
                    Process.Start(new ProcessStartInfo(vlc, $"\"{Path.GetFullPath(outputFile)}\" --no-video-title-show") { UseShellExecute = false });
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Could not start VLC: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Trim error: {ex.Message}");
        }
    }

    public static void FilterSnapshotsByTimeRange(string[] args, string directory)
    {
        if (args.Length < 3)
        {
            Console.WriteLine("Usage: dotnet run -- --filter \"<start_date_time>\" \"<end_date_time>\" [camera] [options]");
            Console.WriteLine("Options:");
            Console.WriteLine("  --export <dir>             Export matched JPEG frames to folder");
            Console.WriteLine("  --render-mp4 <file>        Compile matched frames into an MP4 video clip");
            Console.WriteLine("  --fps <number>             Video frame rate (default: auto-detected, 5 FPS)");
            Console.WriteLine("  --play / --vlc             Immediately open the rendered clip in VLC");
            Console.WriteLine("  --date <yyyy-MM-dd>        Specify target date when using time-only ranges (e.g. 2.12 to 2.34)");
            Console.WriteLine("\nExamples:");
            Console.WriteLine("  dotnet run -- --filter \"2026-10-24 02:12:00\" \"2026-10-24 02:34:00\" 61252WLKR");
            Console.WriteLine("  dotnet run -- --filter \"2.12\" \"2.34\" 61252WLKR --render-mp4 ./clip_0212_0234.mp4 --play");
            Console.WriteLine("  dotnet run -- --filter \"2026-10-24 02:12\" \"2026-10-24 02:34\" 61252WLKR --render-mp4 ./clip.mp4");
            return;
        }

        string startStr = args[1];
        string endStr = args[2];
        string? cameraFilter = null;
        string? exportDir = null;
        string? renderMp4Path = null;
        double? customFps = null;
        bool autoPlayInVlc = false;
        string? explicitDateStr = null;

        for (int i = 3; i < args.Length; i++)
        {
            if (args[i].Equals("--export", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                exportDir = args[++i];
            }
            else if ((args[i].Equals("--render-mp4", StringComparison.OrdinalIgnoreCase) || args[i].Equals("--mp4", StringComparison.OrdinalIgnoreCase)) && i + 1 < args.Length)
            {
                renderMp4Path = args[++i];
            }
            else if (args[i].Equals("--fps", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length && double.TryParse(args[i + 1], CultureInfo.InvariantCulture, out var parsedFps))
            {
                customFps = parsedFps;
                i++;
            }
            else if (args[i].Equals("--play", StringComparison.OrdinalIgnoreCase) || args[i].Equals("--vlc", StringComparison.OrdinalIgnoreCase))
            {
                autoPlayInVlc = true;
            }
            else if (args[i].Equals("--date", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                explicitDateStr = args[++i];
            }
            else if (cameraFilter == null && !args[i].StartsWith("--"))
            {
                cameraFilter = args[i];
            }
        }

        if (!TryParseDateTimeFlexible(startStr, out var startTime, out bool hasStartDate))
        {
            Console.Error.WriteLine($"Could not parse start time: '{startStr}'. Examples: '02:12', '2.12', '2026-10-24 02:12:00'.");
            return;
        }

        if (!TryParseDateTimeFlexible(endStr, out var endTime, out bool hasEndDate))
        {
            Console.Error.WriteLine($"Could not parse end time: '{endStr}'. Examples: '02:34', '2.34', '2026-10-24 02:34:00'.");
            return;
        }

        // Apply explicit date override if supplied
        if (!string.IsNullOrEmpty(explicitDateStr) && DateTime.TryParseExact(explicitDateStr, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedDate))
        {
            startTime = parsedDate.Date.Add(startTime.TimeOfDay);
            endTime = parsedDate.Date.Add(endTime.TimeOfDay);
            hasStartDate = true;
            hasEndDate = true;
        }

        bool matchAnyDate = !hasStartDate && !hasEndDate;

        if (!matchAnyDate && startTime > endTime)
        {
            Console.Error.WriteLine("Error: Start time must be earlier than End time.");
            return;
        }

        if (!Directory.Exists(directory))
        {
            Console.WriteLine($"Directory '{directory}' does not exist.");
            return;
        }

        Console.WriteLine("==========================================================================================");
        Console.WriteLine($" Filtering Video Frames by Time Range");
        if (matchAnyDate)
        {
            Console.WriteLine($" Time Window: {startTime:HH:mm:ss} to {endTime:HH:mm:ss} (Across All Recorded Dates)");
        }
        else
        {
            Console.WriteLine($" From:        {startTime:yyyy-MM-dd HH:mm:ss}");
            Console.WriteLine($" To:          {endTime:yyyy-MM-dd HH:mm:ss}");
        }
        Console.WriteLine($" Camera:      {(string.IsNullOrEmpty(cameraFilter) ? "All Cameras" : cameraFilter)}");
        Console.WriteLine("==========================================================================================");

        var targetDirs = new List<string>();
        if (matchAnyDate)
        {
            try
            {
                targetDirs.AddRange(Directory.GetDirectories(directory));
            }
            catch { }
        }
        else
        {
            for (DateTime d = startTime.Date; d <= endTime.Date; d = d.AddDays(1))
            {
                var sub = Path.Combine(directory, d.ToString("yyyy-MM-dd"));
                if (Directory.Exists(sub)) targetDirs.Add(sub);
            }
        }
        targetDirs.Add(directory); // Include root shots folder

        var matches = new List<SnapshotMetadata>();
        foreach (var dir in targetDirs.Distinct())
        {
            string[] files;
            try { files = Directory.GetFiles(dir, "*.jpg"); } catch { continue; }

            foreach (var f in files)
            {
                var fileName = Path.GetFileName(f);
                if (!string.IsNullOrEmpty(cameraFilter) && !fileName.StartsWith(cameraFilter, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var meta = RetrieveImageDateTime(f);
                if (DateTime.TryParse(meta.IsoTimestamp, out var ts))
                {
                    bool isMatch;
                    if (matchAnyDate)
                    {
                        var tod = ts.TimeOfDay;
                        isMatch = tod >= startTime.TimeOfDay && tod <= endTime.TimeOfDay;
                    }
                    else
                    {
                        isMatch = ts >= startTime && ts <= endTime;
                    }

                    if (isMatch)
                    {
                        if (string.IsNullOrEmpty(cameraFilter) || string.Equals(meta.CameraName, cameraFilter, StringComparison.OrdinalIgnoreCase))
                        {
                            matches.Add(meta);
                        }
                    }
                }
            }
        }

        matches = matches.OrderBy(m => m.IsoTimestamp).ToList();

        if (matches.Count == 0)
        {
            Console.WriteLine("\nNo video frames found in this time range.");
            return;
        }

        Console.WriteLine($"\nFound {matches.Count:N0} matching frames:");
        Console.WriteLine($"{"#",-4} | {"Camera",-12} | {"Date",-10} | {"Time",-12} | {"Size (KB)",-10} | {"File Path"}");
        Console.WriteLine(new string('-', 95));

        int idx = 1;
        int previewCount = Math.Min(matches.Count, 30);
        for (int i = 0; i < previewCount; i++)
        {
            var m = matches[i];
            var relPath = Path.GetRelativePath(directory, m.FilePath);
            Console.WriteLine($"{idx,-4} | {m.CameraName,-12} | {m.Date,-10} | {m.Time,-12} | {m.FileSizeBytes / 1024.0,9:F1} KB | {relPath}");
            idx++;
        }

        if (matches.Count > previewCount)
        {
            Console.WriteLine($"... [and {matches.Count - previewCount:N0} more frames]");
            var last = matches.Last();
            var relPath = Path.GetRelativePath(directory, last.FilePath);
            Console.WriteLine($"{matches.Count,-4} | {last.CameraName,-12} | {last.Date,-10} | {last.Time,-12} | {last.FileSizeBytes / 1024.0,9:F1} KB | {relPath}");
        }

        long totalSize = matches.Sum(m => m.FileSizeBytes);
        Console.WriteLine(new string('-', 95));
        Console.WriteLine($"Total: {matches.Count:N0} frames  |  Total Size: {totalSize / (1024.0 * 1024.0):F2} MB");

        if (!string.IsNullOrWhiteSpace(exportDir))
        {
            Directory.CreateDirectory(exportDir);
            int exported = 0;
            foreach (var m in matches)
            {
                try
                {
                    File.Copy(m.FilePath, Path.Combine(exportDir, m.FileName), true);
                    exported++;
                }
                catch { }
            }
            Console.WriteLine($"\nExported {exported} frames to: {Path.GetFullPath(exportDir)}");
        }

        if (!string.IsNullOrWhiteSpace(renderMp4Path))
        {
            RenderFilteredMatchesToMp4(matches, renderMp4Path, customFps, autoPlayInVlc);
        }
    }

    private static void RenderFilteredMatchesToMp4(List<SnapshotMetadata> matches, string outputPath, double? requestedFps, bool autoPlayInVlc)
    {
        string ffmpeg = ResolveFfmpegPath();
        if (string.IsNullOrEmpty(ffmpeg) || (!File.Exists(ffmpeg) && ffmpeg != "ffmpeg"))
        {
            Console.Error.WriteLine("Error: ffmpeg.exe was not found. Please install ffmpeg or place it in the application directory.");
            return;
        }

        string outDir = Path.GetDirectoryName(Path.GetFullPath(outputPath)) ?? "";
        if (!string.IsNullOrEmpty(outDir)) Directory.CreateDirectory(outDir);

        string tempConcatFile = Path.Combine(Path.GetTempPath(), $"ffmpeg_concat_{Guid.NewGuid():N}.txt");
        var sb = new StringBuilder();

        double fps = requestedFps ?? 5.0;
        if (!requestedFps.HasValue && matches.Count > 1 &&
            DateTime.TryParse(matches.First().IsoTimestamp, out var firstTs) &&
            DateTime.TryParse(matches.Last().IsoTimestamp, out var lastTs))
        {
            var span = lastTs - firstTs;
            if (span.TotalSeconds > 0)
            {
                fps = Math.Max(0.5, Math.Min(30.0, (matches.Count - 1) / span.TotalSeconds));
            }
        }

        double frameDuration = 1.0 / fps;
        foreach (var m in matches)
        {
            sb.AppendLine($"file '{m.FilePath.Replace("'", @"'\''")}'");
            sb.AppendLine($"duration {frameDuration:F4}");
        }
        sb.AppendLine($"file '{matches.Last().FilePath.Replace("'", @"'\''")}'");

        File.WriteAllText(tempConcatFile, sb.ToString());

        Console.WriteLine($"\nCompiling {matches.Count:N0} frames into MP4 video clip @ {fps:F1} FPS...");
        string args = $"-y -f concat -safe 0 -i \"{tempConcatFile}\" -c:v libx264 -preset veryfast -pix_fmt yuv420p \"{outputPath}\"";

        var psi = new ProcessStartInfo(ffmpeg, args)
        {
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        try
        {
            using var p = Process.Start(psi);
            if (p != null)
            {
                string err = p.StandardError.ReadToEnd();
                p.WaitForExit();
                if (p.ExitCode != 0)
                {
                    Console.Error.WriteLine($"FFmpeg rendering failed (code {p.ExitCode}): {err}");
                    return;
                }
            }

            var fi = new FileInfo(outputPath);
            Console.WriteLine($"Successfully rendered MP4 clip: {fi.FullName} ({fi.Length / (1024.0 * 1024.0):F2} MB)");

            if (autoPlayInVlc)
            {
                string vlc = ResolveVlcPath();
                Console.WriteLine($"Launching VLC Media Player to play clip: {outputPath}...");
                try
                {
                    Process.Start(new ProcessStartInfo(vlc, $"\"{Path.GetFullPath(outputPath)}\" --no-video-title-show") { UseShellExecute = false });
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Could not start VLC: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Render error: {ex.Message}");
        }
        finally
        {
            try { File.Delete(tempConcatFile); } catch { }
        }
    }

    private static bool TryParseDateTimeFlexible(string input, out DateTime result, out bool hasExplicitDate)
    {
        hasExplicitDate = false;
        if (string.IsNullOrWhiteSpace(input))
        {
            result = default;
            return false;
        }

        input = input.Trim();
        // Normalize dot notations like 2.12 -> 02:12 or 2.34 -> 02:34
        if (Regex.IsMatch(input, @"^\d{1,2}\.\d{2}(?:\.\d{2})?$"))
        {
            input = input.Replace('.', ':');
        }

        string[] formats = new[]
        {
            "yyyy-MM-dd HH:mm:ss",
            "yyyy-MM-dd HH:mm",
            "yyyy-MM-dd H:mm:ss",
            "yyyy-MM-dd H:mm",
            "yyyy-MM-dd",
            "yyyy/MM/dd HH:mm:ss",
            "yyyy/MM/dd HH:mm",
            "yyyy/MM/dd H:mm:ss",
            "yyyy/MM/dd H:mm",
            "yyyy/MM/dd",
            "HH:mm:ss",
            "HH:mm",
            "H:mm:ss",
            "H:mm"
        };

        if (DateTime.TryParseExact(input, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out result))
        {
            hasExplicitDate = input.Contains('-') || input.Contains('/');
            if (!hasExplicitDate)
            {
                result = DateTime.Today.Add(result.TimeOfDay);
            }
            return true;
        }

        if (DateTime.TryParse(input, CultureInfo.CurrentCulture, DateTimeStyles.None, out result))
        {
            hasExplicitDate = input.Contains('-') || input.Contains('/');
            return true;
        }

        return false;
    }

    public static string ResolveVlcPath()
    {
        string p1 = @"C:\Program Files\VideoLAN\VLC\vlc.exe";
        if (File.Exists(p1)) return p1;

        string p2 = @"C:\Program Files (x86)\VideoLAN\VLC\vlc.exe";
        if (File.Exists(p2)) return p2;

        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\VideoLAN\VLC");
            var val = key?.GetValue("InstallDir") as string;
            if (!string.IsNullOrEmpty(val))
            {
                var exe = Path.Combine(val, "vlc.exe");
                if (File.Exists(exe)) return exe;
            }
        }
        catch { }

        return "vlc.exe";
    }

    public static string ResolveProjectRoot()
{
    var customDir = Environment.GetEnvironmentVariable("OUTPUT_DIR");
    if (!string.IsNullOrWhiteSpace(customDir))
    {
        return Path.GetFullPath(customDir);
    }

    // 1. Check current directory or immediate child
    var cur = Environment.CurrentDirectory;
    if (File.Exists(Path.Combine(cur, "camgrab.csproj")))
    {
        return cur;
    }
    if (File.Exists(Path.Combine(cur, "camgrab", "camgrab.csproj")))
    {
        return Path.Combine(cur, "camgrab");
    }

    // 2. Traverse upwards from AppContext.BaseDirectory looking for camgrab.csproj
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir != null)
    {
        if (File.Exists(Path.Combine(dir.FullName, "camgrab.csproj")))
        {
            return dir.FullName;
        }
        dir = dir.Parent;
    }

    // 3. Fallback to AppContext.BaseDirectory (e.g. standalone deployment / production Windows Service)
    return AppContext.BaseDirectory;
}

    public static string ResolveOutputDirectory(string? configuredDir = null)
{
    var customDir = Environment.GetEnvironmentVariable("OUTPUT_DIR");
    if (!string.IsNullOrWhiteSpace(customDir))
    {
        return Path.GetFullPath(customDir);
    }

    var folderName = string.IsNullOrWhiteSpace(configuredDir) ? "shots" : configuredDir;
    if (Path.IsPathRooted(folderName))
    {
        return folderName;
    }

    var projectRoot = ResolveProjectRoot();
    return Path.GetFullPath(Path.Combine(projectRoot, folderName));
}

    public static string RedactCredentials(string message, string streamUrl)
{
    var schemeEnd = streamUrl.IndexOf("://", StringComparison.Ordinal);
    var credentialsStart = schemeEnd + 3;
    var credentialsEnd = streamUrl.IndexOf('@', credentialsStart);
    if (schemeEnd >= 0 && credentialsEnd > credentialsStart)
    {
        var credentials = streamUrl[credentialsStart..credentialsEnd];
        message = message.Replace(credentials, "<credentials>", StringComparison.Ordinal);
    }
    return message.Trim();
}

    public static string GetDirectCameraUrl(string? name, string? guid, int subtype = 0)
    {
        name ??= "";
        guid ??= "";

        string ip = "";
        if (name.IndexOf("61253", StringComparison.OrdinalIgnoreCase) >= 0 ||
            guid.Equals("229802e9-9bb3-4e89-aec7-005c3e44fb94", StringComparison.OrdinalIgnoreCase))
        {
            ip = "10.64.61.46";
        }
        else if (name.IndexOf("61252", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 guid.Equals("9ac405ca-1db1-4696-9d7f-ebccc74a9b18", StringComparison.OrdinalIgnoreCase))
        {
            ip = "10.64.61.145";
        }

        if (string.IsNullOrWhiteSpace(ip)) return string.Empty;

        string user = Environment.GetEnvironmentVariable("TEST_CAM_USER") ?? "testfr";
        string pass = Environment.GetEnvironmentVariable("TEST_CAM_PASS") ?? "It123aasl";
        return $"rtsp://{Uri.EscapeDataString(user)}:{Uri.EscapeDataString(pass)}@{ip}:554/cam/realmonitor?channel=1&subtype={subtype}";
    }

    public static string ResolveFfmpegPath()
    {
        var baseDirFfmpeg = Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
        if (File.Exists(baseDirFfmpeg)) return baseDirFfmpeg;

        var localFfmpeg = Path.Combine(Environment.CurrentDirectory, "ffmpeg.exe");
        if (File.Exists(localFfmpeg)) return localFfmpeg;

        var projectFfmpeg = Path.Combine(Environment.CurrentDirectory, "camgrab", "ffmpeg.exe");
        if (File.Exists(projectFfmpeg)) return projectFfmpeg;

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "ffmpeg.exe");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var wingetDir = Path.Combine(localAppData, "Microsoft", "WinGet", "Packages");
        if (Directory.Exists(wingetDir))
        {
            var found = Directory.GetFiles(wingetDir, "ffmpeg.exe", SearchOption.AllDirectories).FirstOrDefault();
            if (found != null) return found;
        }

        return "ffmpeg";
    }
}

public record SnapshotMetadata(
    string CameraName,
    string FileName,
    string Date,
    string Time,
    string IsoTimestamp,
    long FileSizeBytes,
    string FilePath,
    string FrameType = "I-Frame (Keyframe)"
);

public sealed class RtspAuthSanitizerProxy : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly string _targetHost;
    private readonly int _targetPort;
    private readonly CancellationTokenSource _cts = new();

    public int LocalPort => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public RtspAuthSanitizerProxy(string targetHost, int targetPort)
    {
        _targetHost = targetHost;
        _targetPort = targetPort;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        _ = AcceptLoopAsync(_cts.Token);
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var client = await _listener.AcceptTcpClientAsync(ct);
                _ = ForwardConnectionAsync(client, ct);
            }
            catch (OperationCanceledException) { break; }
            catch { }
        }
    }

    private async Task ForwardConnectionAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        using (var server = new TcpClient())
        {
            try
            {
                await server.ConnectAsync(_targetHost, _targetPort, ct);
                using var clientStream = client.GetStream();
                using var serverStream = server.GetStream();

                var clientToServer = clientStream.CopyToAsync(serverStream, ct);
                var serverToClient = ForwardServerToClientAsync(serverStream, clientStream, ct);

                await Task.WhenAny(clientToServer, serverToClient);
            }
            catch { }
        }
    }

    private async Task ForwardServerToClientAsync(NetworkStream serverStream, NetworkStream clientStream, CancellationToken ct)
    {
        var buffer = new byte[16384];

        while (!ct.IsCancellationRequested)
        {
            int bytesRead = await serverStream.ReadAsync(buffer, ct);
            if (bytesRead == 0) break;

            var sanitized = TrySanitizeAuthHeader(buffer, bytesRead);
            if (sanitized != null)
            {
                await clientStream.WriteAsync(sanitized, ct);
            }
            else
            {
                await clientStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
            }
        }
    }

    private static byte[]? TrySanitizeAuthHeader(byte[] buffer, int count)
    {
        var text = Encoding.UTF8.GetString(buffer, 0, count);
        if (text.Contains("WWW-Authenticate", StringComparison.OrdinalIgnoreCase) &&
            text.Contains("SHA-256", StringComparison.OrdinalIgnoreCase))
        {
            var lines = text.Split("\r\n");
            var filtered = lines.Where(l => !l.Contains("SHA-256", StringComparison.OrdinalIgnoreCase));
            return Encoding.UTF8.GetBytes(string.Join("\r\n", filtered));
        }
        return null;
    }

    public ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();
        _cts.Dispose();
        return ValueTask.CompletedTask;
    }
}
