using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace camera.Streaming
{
    /// <summary>
    /// 轻量高性能 RTSP 1.0 流媒体服务器。
    /// 支持标准 RFC 2435 (RTP/JPEG) 视频流与 TCP interleaved 传输。
    /// 兼容 VLC、PotPlayer、主流 NVR 硬盘录像机及安防监控平台。
    /// </summary>
    public class RtspStreamServer : IDisposable
    {
        private TcpListener _listener;
        private CancellationTokenSource _cts;
        private Task _listenTask;
        private readonly int _port;
        private readonly Func<string> _systemTitleProvider;
        private readonly Func<IEnumerable<CameraInfo>> _cameraInfoProvider;

        public class FramePackage
        {
            public byte[] JpegData { get; set; }
            public int Width { get; set; }
            public int Height { get; set; }
            public uint Timestamp { get; set; }
        }

        private class RtspClient
        {
            public string Id { get; } = Guid.NewGuid().ToString("N");
            public int CameraIndex { get; set; }
            public Channel<FramePackage> FrameChannel { get; }
            public TcpClient TcpClient { get; set; }
            public NetworkStream Stream { get; set; }
            public int RtpChannel { get; set; } = 0;
            public ushort SequenceNumber { get; set; }
            public uint Ssrc { get; } = (uint)Random.Shared.Next(1, int.MaxValue);

            public RtspClient(int cameraIndex)
            {
                CameraIndex = cameraIndex;
                var opts = new BoundedChannelOptions(1)
                {
                    FullMode = BoundedChannelFullMode.DropOldest,
                    SingleReader = true,
                    SingleWriter = false
                };
                FrameChannel = Channel.CreateBounded<FramePackage>(opts);
            }
        }

        private readonly ConcurrentDictionary<string, RtspClient> _clients = new();

        public int Port => _port;
        public bool IsRunning { get; private set; }
        public int ClientCount => _clients.Count;

        public event EventHandler ClientCountChanged;

        public RtspStreamServer(
            int port,
            Func<string> systemTitleProvider,
            Func<IEnumerable<CameraInfo>> cameraInfoProvider)
        {
            _port = port > 0 ? port : 8554;
            _systemTitleProvider = systemTitleProvider ?? (() => "监控");
            _cameraInfoProvider = cameraInfoProvider ?? (() => Array.Empty<CameraInfo>());
        }

        public bool Start()
        {
            if (IsRunning) return true;

            try
            {
                _cts = new CancellationTokenSource();
                _listener = new TcpListener(IPAddress.Any, _port);
                _listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                _listener.Start(100);
                IsRunning = true;

                _listenTask = Task.Run(() => AcceptLoopAsync(_cts.Token));
                Logger.LogInfo($"[RtspStreamServer] RTSP 监控推流服务已启动，正在监听端口: {_port}");
                return true;
            }
            catch (Exception ex)
            {
                Logger.LogError($"[RtspStreamServer] 启动 RTSP 服务失败 (端口 {_port})", ex);
                Stop();
                return false;
            }
        }

        public void Stop()
        {
            if (!IsRunning && _listener == null) return;
            IsRunning = false;

            try
            {
                _cts?.Cancel();
                _listener?.Stop();
            }
            catch { }

            foreach (var kvp in _clients)
            {
                try
                {
                    kvp.Value.TcpClient?.Close();
                }
                catch { }
            }
            _clients.Clear();
            ClientCountChanged?.Invoke(this, EventArgs.Empty);

            Logger.LogInfo("[RtspStreamServer] RTSP 推流服务已停止");
        }

        public bool HasSubscribers(int cameraIndex)
        {
            if (!IsRunning || _clients.IsEmpty) return false;
            foreach (var client in _clients.Values)
            {
                if (client.CameraIndex == cameraIndex || client.CameraIndex == -1)
                {
                    return true;
                }
            }
            return false;
        }

        public void BroadcastFrame(int cameraIndex, byte[] jpegBytes, int width, int height)
        {
            if (!IsRunning || jpegBytes == null || jpegBytes.Length == 0) return;

            // 基于 90kHz 时钟的时间戳
            uint rtpTimestamp = (uint)((DateTime.UtcNow.Ticks / 10000.0) * 90.0);

            var pkg = new FramePackage
            {
                JpegData = jpegBytes,
                Width = width > 0 ? width : 1280,
                Height = height > 0 ? height : 720,
                Timestamp = rtpTimestamp
            };

            foreach (var client in _clients.Values)
            {
                if (client.CameraIndex == cameraIndex || client.CameraIndex == -1)
                {
                    client.FrameChannel.Writer.TryWrite(pkg);
                }
            }
        }

        private async Task AcceptLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested && IsRunning)
            {
                try
                {
                    var tcpClient = await _listener.AcceptTcpClientAsync(token).ConfigureAwait(false);
                    _ = Task.Run(() => HandleClientAsync(tcpClient, token), token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    if (!IsRunning) break;
                    Logger.LogWarning($"[RtspStreamServer] 接收客户端连接异常: {ex.Message}");
                    await Task.Delay(50, token).ConfigureAwait(false);
                }
            }
        }

        private async Task HandleClientAsync(TcpClient tcpClient, CancellationToken serverToken)
        {
            using var client = tcpClient;
            client.NoDelay = true;
            client.ReceiveTimeout = 10000;
            client.SendTimeout = 10000;

            RtspClient activeClient = null;
            string sessionId = Random.Shared.Next(10000000, 99999999).ToString();

            try
            {
                using var stream = client.GetStream();
                using var reader = new StreamReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);

                while (!serverToken.IsCancellationRequested && client.Connected)
                {
                    string line = await reader.ReadLineAsync().ConfigureAwait(false);
                    if (string.IsNullOrEmpty(line))
                    {
                        await Task.Delay(10, serverToken).ConfigureAwait(false);
                        continue;
                    }

                    // 解析请求行: 例如 DESCRIBE rtsp://192.168.1.100:8554/live/0 RTSP/1.0
                    var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 3) continue;

                    string method = parts[0].ToUpperInvariant();
                    string uri = parts[1];
                    string version = parts[2];

                    // 解析 Headers
                    var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    string headerLine;
                    while (!string.IsNullOrEmpty(headerLine = await reader.ReadLineAsync().ConfigureAwait(false)))
                    {
                        int colon = headerLine.IndexOf(':');
                        if (colon > 0)
                        {
                            string k = headerLine.Substring(0, colon).Trim();
                            string v = headerLine.Substring(colon + 1).Trim();
                            headers[k] = v;
                        }
                    }

                    string cseq = headers.TryGetValue("CSeq", out var cs) ? cs : "1";
                    int camIndex = ParseCameraIndexFromUri(uri);

                    if (method == "OPTIONS")
                    {
                        string resp = $"{version} 200 OK\r\n" +
                                      $"CSeq: {cseq}\r\n" +
                                      $"Public: OPTIONS, DESCRIBE, SETUP, TEARDOWN, PLAY, PAUSE\r\n\r\n";
                        byte[] respBytes = System.Text.Encoding.ASCII.GetBytes(resp);
                        await stream.WriteAsync(respBytes, 0, respBytes.Length, serverToken).ConfigureAwait(false);
                        await stream.FlushAsync(serverToken).ConfigureAwait(false);
                    }
                    else if (method == "DESCRIBE")
                    {
                        string sdp = BuildSdp(camIndex, uri);
                        byte[] sdpBytes = System.Text.Encoding.UTF8.GetBytes(sdp);

                        string resp = $"{version} 200 OK\r\n" +
                                      $"CSeq: {cseq}\r\n" +
                                      $"Content-Type: application/sdp\r\n" +
                                      $"Content-Base: {uri}/\r\n" +
                                      $"Content-Length: {sdpBytes.Length}\r\n\r\n";

                        byte[] respBytes = System.Text.Encoding.ASCII.GetBytes(resp);
                        await stream.WriteAsync(respBytes, 0, respBytes.Length, serverToken).ConfigureAwait(false);
                        await stream.WriteAsync(sdpBytes, 0, sdpBytes.Length, serverToken).ConfigureAwait(false);
                        await stream.FlushAsync(serverToken).ConfigureAwait(false);
                    }
                    else if (method == "SETUP")
                    {
                        string transport = headers.TryGetValue("Transport", out var tr) ? tr : "";
                        int interleavedChannel = 0;

                        // 提取 interleaved channel (例如 interleaved=0-1)
                        if (transport.Contains("interleaved="))
                        {
                            int idx = transport.IndexOf("interleaved=") + 12;
                            int end = transport.IndexOfAny(new[] { '-', ';' }, idx);
                            if (end > idx && int.TryParse(transport.Substring(idx, end - idx), out int ch))
                            {
                                interleavedChannel = ch;
                            }
                        }

                        activeClient = new RtspClient(camIndex)
                        {
                            TcpClient = client,
                            Stream = stream,
                            RtpChannel = interleavedChannel
                        };

                        _clients[activeClient.Id] = activeClient;
                        NotifyClientCountChanged();

                        string respTransport = $"RTP/AVP/TCP;unicast;interleaved={interleavedChannel}-{interleavedChannel + 1}";
                        string resp = $"{version} 200 OK\r\n" +
                                      $"CSeq: {cseq}\r\n" +
                                      $"Session: {sessionId}\r\n" +
                                      $"Transport: {respTransport}\r\n\r\n";

                        byte[] respBytes = System.Text.Encoding.ASCII.GetBytes(resp);
                        await stream.WriteAsync(respBytes, 0, respBytes.Length, serverToken).ConfigureAwait(false);
                        await stream.FlushAsync(serverToken).ConfigureAwait(false);
                    }
                    else if (method == "PLAY")
                    {
                        string resp = $"{version} 200 OK\r\n" +
                                      $"CSeq: {cseq}\r\n" +
                                      $"Session: {sessionId}\r\n" +
                                      $"RTP-Info: url={uri}/track0;seq=1\r\n\r\n";

                        byte[] respBytes = System.Text.Encoding.ASCII.GetBytes(resp);
                        await stream.WriteAsync(respBytes, 0, respBytes.Length, serverToken).ConfigureAwait(false);
                        await stream.FlushAsync(serverToken).ConfigureAwait(false);

                        // 启动 TCP Interleaved RTP 循环发包
                        if (activeClient != null)
                        {
                            await StreamLoopAsync(activeClient, serverToken).ConfigureAwait(false);
                            break;
                        }
                    }
                    else if (method == "TEARDOWN")
                    {
                        string resp = $"{version} 200 OK\r\n" +
                                      $"CSeq: {cseq}\r\n" +
                                      $"Session: {sessionId}\r\n\r\n";
                        byte[] respBytes = System.Text.Encoding.ASCII.GetBytes(resp);
                        await stream.WriteAsync(respBytes, 0, respBytes.Length, serverToken).ConfigureAwait(false);
                        await stream.FlushAsync(serverToken).ConfigureAwait(false);
                        break;
                    }
                }
            }
            catch (Exception)
            {
                // 客户端正常断开
            }
            finally
            {
                if (activeClient != null)
                {
                    _clients.TryRemove(activeClient.Id, out _);
                    NotifyClientCountChanged();
                }
            }
        }

        private async Task StreamLoopAsync(RtspClient client, CancellationToken token)
        {
            var reader = client.FrameChannel.Reader;
            var stream = client.Stream;
            byte[] packetBuffer = new byte[1500];

            while (!token.IsCancellationRequested && client.TcpClient.Connected)
            {
                var pkg = await reader.ReadAsync(token).ConfigureAwait(false);
                if (pkg?.JpegData == null || pkg.JpegData.Length == 0) continue;

                byte[] jpeg = pkg.JpegData;
                int width = pkg.Width;
                int height = pkg.Height;
                uint timestamp = pkg.Timestamp;

                int offset = 0;
                int maxChunk = 1400 - 8; // 扣除 8 字节 RFC 2435 头
                byte wBlock = (byte)Math.Clamp(width / 8, 1, 255);
                byte hBlock = (byte)Math.Clamp(height / 8, 1, 255);

                while (offset < jpeg.Length)
                {
                    int chunk = Math.Min(maxChunk, jpeg.Length - offset);
                    bool isLast = (offset + chunk) >= jpeg.Length;

                    int rtpPayloadLen = 8 + chunk;
                    int rtpPacketLen = 12 + rtpPayloadLen;
                    int totalInterleavedLen = 4 + rtpPacketLen;

                    // 1. Interleaved Frame Header (4 字节)
                    packetBuffer[0] = 0x24; // '$'
                    packetBuffer[1] = (byte)client.RtpChannel;
                    packetBuffer[2] = (byte)((rtpPacketLen >> 8) & 0xFF);
                    packetBuffer[3] = (byte)(rtpPacketLen & 0xFF);

                    // 2. RTP Header (12 字节)
                    packetBuffer[4] = 0x80; // V=2
                    packetBuffer[5] = (byte)(isLast ? (0x80 | 26) : 26); // M bit + PT 26 (JPEG)
                    packetBuffer[6] = (byte)((client.SequenceNumber >> 8) & 0xFF);
                    packetBuffer[7] = (byte)(client.SequenceNumber & 0xFF);
                    packetBuffer[8] = (byte)((timestamp >> 24) & 0xFF);
                    packetBuffer[9] = (byte)((timestamp >> 16) & 0xFF);
                    packetBuffer[10] = (byte)((timestamp >> 8) & 0xFF);
                    packetBuffer[11] = (byte)(timestamp & 0xFF);
                    packetBuffer[12] = (byte)((client.Ssrc >> 24) & 0xFF);
                    packetBuffer[13] = (byte)((client.Ssrc >> 16) & 0xFF);
                    packetBuffer[14] = (byte)((client.Ssrc >> 8) & 0xFF);
                    packetBuffer[15] = (byte)(client.Ssrc & 0xFF);

                    // 3. RFC 2435 JPEG Header (8 字节)
                    packetBuffer[16] = 0; // Type-specific
                    packetBuffer[17] = (byte)((offset >> 16) & 0xFF);
                    packetBuffer[18] = (byte)((offset >> 8) & 0xFF);
                    packetBuffer[19] = (byte)(offset & 0xFF);
                    packetBuffer[20] = 1;   // Type (1 = 4:2:0)
                    packetBuffer[21] = 70;  // Q-factor
                    packetBuffer[22] = wBlock;
                    packetBuffer[23] = hBlock;

                    // 4. 数据写入
                    await stream.WriteAsync(packetBuffer, 0, 24, token).ConfigureAwait(false);
                    await stream.WriteAsync(jpeg, offset, chunk, token).ConfigureAwait(false);

                    offset += chunk;
                    client.SequenceNumber++;
                }

                await stream.FlushAsync(token).ConfigureAwait(false);
            }
        }

        private static int ParseCameraIndexFromUri(string uri)
        {
            if (string.IsNullOrEmpty(uri)) return 0;
            try
            {
                int lastSlash = uri.LastIndexOf('/');
                if (lastSlash >= 0 && lastSlash < uri.Length - 1)
                {
                    string sub = uri.Substring(lastSlash + 1);
                    if (int.TryParse(sub, out int idx)) return idx;
                }
            }
            catch { }
            return 0;
        }

        private string BuildSdp(int cameraIndex, string controlUri)
        {
            return "v=0\r\n" +
                   "o=- 0 0 IN IP4 0.0.0.0\r\n" +
                   $"s={_systemTitleProvider()} - Cam {cameraIndex}\r\n" +
                   "c=IN IP4 0.0.0.0\r\n" +
                   "t=0 0\r\n" +
                   "a=range:npt=now-\r\n" +
                   "m=video 0 RTP/AVP 26\r\n" +
                   "a=rtpmap:26 JPEG/90000\r\n" +
                   $"a=control:{controlUri}/track0\r\n";
        }

        private void NotifyClientCountChanged()
        {
            try
            {
                ClientCountChanged?.Invoke(this, EventArgs.Empty);
            }
            catch { }
        }

        public void Dispose()
        {
            Stop();
            _cts?.Dispose();
        }
    }
}
