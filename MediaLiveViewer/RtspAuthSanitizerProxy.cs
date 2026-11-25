using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace MediaLiveViewer
{
    /// <summary>
    /// Local loopback proxy that strips 'algorithm="SHA-256"' from RTSP WWW-Authenticate
    /// challenge headers returned by Milestone Open Network Bridge.
    /// Milestone advertises both SHA-256 and MD5, but FFmpeg fails when attempting SHA-256 digest auth.
    /// Stripping SHA-256 forces FFmpeg to use standard MD5 digest authentication which Milestone accepts.
    /// </summary>
    public sealed class RtspAuthSanitizerProxy : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly string _targetHost;
        private readonly int _targetPort;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private bool _disposed;
        private readonly string _lanIp;

        public string TargetHost => _targetHost;
        public int TargetPort => _targetPort;
        public int LocalPort => ((IPEndPoint)_listener.LocalEndpoint).Port;
        public string LanIp => _lanIp;
        public bool IsActive => !_disposed;

        public RtspAuthSanitizerProxy(string targetHost, int targetPort, int preferredPort = 5554)
        {
            _targetHost = targetHost;
            _targetPort = targetPort;
            _lanIp = GetLanIpAddress();

            try
            {
                _listener = new TcpListener(IPAddress.Any, preferredPort);
                _listener.Start();
            }
            catch
            {
                _listener = new TcpListener(IPAddress.Any, 0);
                _listener.Start();
            }

            Task.Run(() => AcceptLoopAsync(_cts.Token));
        }

        public static string GetLanIpAddress()
        {
            try
            {
                using (var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, 0))
                {
                    socket.Connect("192.168.4.10", 65530);
                    var ep = socket.LocalEndPoint as IPEndPoint;
                    if (ep != null && !ep.Address.ToString().StartsWith("127."))
                    {
                        return ep.Address.ToString();
                    }
                }
            }
            catch { }

            try
            {
                using (var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, 0))
                {
                    socket.Connect("8.8.8.8", 65530);
                    var ep = socket.LocalEndPoint as IPEndPoint;
                    if (ep != null && !ep.Address.ToString().StartsWith("127."))
                    {
                        return ep.Address.ToString();
                    }
                }
            }
            catch { }

            try
            {
                var activeIp = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                    .Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up &&
                                n.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Loopback &&
                                n.Description.IndexOf("virtual", StringComparison.OrdinalIgnoreCase) < 0 &&
                                n.Description.IndexOf("vmware", StringComparison.OrdinalIgnoreCase) < 0)
                    .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                    .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork &&
                                !a.Address.ToString().StartsWith("127.") &&
                                !a.Address.ToString().StartsWith("169.254.") &&
                                !a.Address.ToString().StartsWith("10.64.30."))
                    .Select(a => a.Address.ToString())
                    .FirstOrDefault();

                if (!string.IsNullOrEmpty(activeIp)) return activeIp;
            }
            catch { }

            return "127.0.0.1";
        }

        private async Task AcceptLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && !_disposed)
            {
                try
                {
                    var client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
                    var ignored = ForwardConnectionAsync(client, ct);
                }
                catch (ObjectDisposedException) { break; }
                catch (SocketException) { break; }
                catch (Exception ex)
                {
                    if (ct.IsCancellationRequested || _disposed) break;
                    Debug.WriteLine("RtspAuthSanitizerProxy accept error: " + ex.Message);
                }
            }
        }

        private async Task ForwardConnectionAsync(TcpClient client, CancellationToken ct)
        {
            using (client)
            using (var server = new TcpClient())
            {
                try
                {
                    await server.ConnectAsync(_targetHost, _targetPort).ConfigureAwait(false);
                    using (var clientStream = client.GetStream())
                    using (var serverStream = server.GetStream())
                    {
                        var clientToServer = clientStream.CopyToAsync(serverStream, 81920, ct);
                        var serverToClient = ForwardServerToClientAsync(serverStream, clientStream, ct);
                        await Task.WhenAny(clientToServer, serverToClient).ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("RtspAuthSanitizerProxy forward error: " + ex.Message);
                }
            }
        }

        private async Task ForwardServerToClientAsync(NetworkStream serverStream, NetworkStream clientStream, CancellationToken ct)
        {
            var buffer = new byte[16384];

            while (!ct.IsCancellationRequested && !_disposed)
            {
                int bytesRead = await serverStream.ReadAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false);
                if (bytesRead == 0) break;

                // Only inspect text responses (binary interleaved RTP frames start with '$' / 0x24)
                if (buffer[0] != 0x24)
                {
                    var sanitized = TrySanitizeAuthHeader(buffer, bytesRead);
                    if (sanitized != null)
                    {
                        await clientStream.WriteAsync(sanitized, 0, sanitized.Length, ct).ConfigureAwait(false);
                        continue;
                    }
                }

                await clientStream.WriteAsync(buffer, 0, bytesRead, ct).ConfigureAwait(false);
            }
        }

        private static byte[] TrySanitizeAuthHeader(byte[] buffer, int count)
        {
            var text = Encoding.UTF8.GetString(buffer, 0, count);
            if (text.IndexOf("WWW-Authenticate", StringComparison.OrdinalIgnoreCase) >= 0 &&
                text.IndexOf("SHA-256", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                // Remove the WWW-Authenticate header line containing SHA-256
                var sanitized = Regex.Replace(text, @"WWW-Authenticate:[^\r\n]*SHA-256[^\r\n]*(\r\n|\n)?", "", RegexOptions.IgnoreCase);
                return Encoding.UTF8.GetBytes(sanitized);
            }
            return null;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { _cts.Cancel(); } catch { }
            try { _listener.Stop(); } catch { }
            try { _cts.Dispose(); } catch { }
        }
    }
}
