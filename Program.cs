using System.Diagnostics;

string pass = Environment.GetEnvironmentVariable("CAM_PASS") ?? "";
string[] cams = { "10.64.61.145", "10.64.61.46" };
Directory.CreateDirectory("shots");

foreach (var ip in cams)
{
    string url = $"rtsp://testfr:{pass}@{ip}:554/cam/realmonitor?channel=1&subtype=0";
    string file = Path.Combine("shots", $"cam{ip.Split('.')[3]}_{DateTime.Now:HHmmss}.jpg");

    var psi = new ProcessStartInfo("ffmpeg",
        $"-y -loglevel error -rtsp_transport tcp -i \"{url}\" -frames:v 1 -update 1 \"{file}\"")
    {
        RedirectStandardError = true,
        UseShellExecute = false
    };

    using var p = Process.Start(psi)!;
    string err = await p.StandardError.ReadToEndAsync();
    await p.WaitForExitAsync();

    Console.WriteLine(p.ExitCode == 0 ? $"Saved: {file}" : $"Failed {ip}: {err}");
}