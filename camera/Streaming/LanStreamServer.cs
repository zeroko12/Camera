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
    /// 局域网轻量高性能流媒体服务器。
    /// 基于 TcpListener，不依赖 Windows HttpListener 的管理员特权，
    /// 提供 Web 监控大屏、MJPEG 视频流、快照 JPEG 和状态 API。
    /// </summary>
    public class LanStreamServer : IDisposable
    {
        private TcpListener _listener;
        private CancellationTokenSource _cts;
        private Task _listenTask;
        private readonly int _port;
        private readonly Func<string> _systemTitleProvider;
        private readonly Func<IEnumerable<CameraInfo>> _cameraInfoProvider;

        // 每个活动 MJPEG 客户端上下文
        private class StreamClient
        {
            public string Id { get; } = Guid.NewGuid().ToString("N");
            public int CameraIndex { get; set; }
            public Channel<byte[]> FrameChannel { get; }
            public TcpClient TcpClient { get; set; }
            public NetworkStream Stream { get; set; }

            public StreamClient(int cameraIndex)
            {
                CameraIndex = cameraIndex;
                // 单槽丢弃旧帧：保证极低延迟与零内存堆积
                var opts = new BoundedChannelOptions(1)
                {
                    FullMode = BoundedChannelFullMode.DropOldest,
                    SingleReader = true,
                    SingleWriter = false
                };
                FrameChannel = Channel.CreateBounded<byte[]>(opts);
            }
        }

        // 活跃订阅者映射: ClientId -> StreamClient
        private readonly ConcurrentDictionary<string, StreamClient> _clients = new();

        // 最新各通道快照缓存: CameraIndex -> byte[]
        private readonly ConcurrentDictionary<int, byte[]> _latestSnapshots = new();

        public int Port => _port;
        public bool IsRunning { get; private set; }
        public int ClientCount => _clients.Count;

        public event EventHandler ClientCountChanged;

        public LanStreamServer(
            int port,
            Func<string> systemTitleProvider,
            Func<IEnumerable<CameraInfo>> cameraInfoProvider)
        {
            _port = port > 0 ? port : 8080;
            _systemTitleProvider = systemTitleProvider ?? (() => "监控");
            _cameraInfoProvider = cameraInfoProvider ?? (() => Array.Empty<CameraInfo>());
        }

        /// <summary>
        /// 启动服务器。
        /// </summary>
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

                _listenTask = Task.Run(() => AcceptClientsLoopAsync(_cts.Token));
                Logger.LogInfo($"[LanStreamServer] 局域网推流服务已成功启动，正在监听端口: {_port}");
                return true;
            }
            catch (Exception ex)
            {
                Logger.LogError($"[LanStreamServer] 启动推流服务失败 (端口 {_port})", ex);
                Stop();
                return false;
            }
        }

        /// <summary>
        /// 停止服务器并释放所有客户端连接。
        /// </summary>
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

            // 强制断开所有已连接客户端
            foreach (var kvp in _clients)
            {
                try
                {
                    kvp.Value.TcpClient?.Close();
                }
                catch { }
            }
            _clients.Clear();
            _latestSnapshots.Clear();
            ClientCountChanged?.Invoke(this, EventArgs.Empty);

            Logger.LogInfo("[LanStreamServer] 局域网推流服务已停止");
        }

        /// <summary>
        /// 查询指定通道当前是否有活动的观看者（用于上游决策是否跳过 JPEG 压缩）。
        /// </summary>
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

        /// <summary>
        /// 广播指定通道的最新 JPEG 帧。
        /// </summary>
        public void BroadcastFrame(int cameraIndex, byte[] jpegBytes)
        {
            if (!IsRunning || jpegBytes == null || jpegBytes.Length == 0) return;

            // 缓存最新快照
            _latestSnapshots[cameraIndex] = jpegBytes;

            // 推入所有订阅了该通道或全部通道的客户端队列
            foreach (var client in _clients.Values)
            {
                if (client.CameraIndex == cameraIndex || client.CameraIndex == -1)
                {
                    client.FrameChannel.Writer.TryWrite(jpegBytes);
                }
            }
        }

        private async Task AcceptClientsLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested && IsRunning)
            {
                try
                {
                    var tcpClient = await _listener.AcceptTcpClientAsync(token).ConfigureAwait(false);
                    // 异步处理单个客户端 HTTP 请求
                    _ = Task.Run(() => HandleClientAsync(tcpClient, token), token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    if (!IsRunning) break;
                    Logger.LogWarning($"[LanStreamServer] 接收客户端连接异常: {ex.Message}");
                    await Task.Delay(50, token).ConfigureAwait(false);
                }
            }
        }

        private async Task HandleClientAsync(TcpClient tcpClient, CancellationToken serverToken)
        {
            using var client = tcpClient;
            client.NoDelay = true;
            client.ReceiveTimeout = 5000;
            client.SendTimeout = 5000;

            StreamClient streamSubscription = null;

            try
            {
                using var networkStream = client.GetStream();
                using var reader = new StreamReader(networkStream, System.Text.Encoding.UTF8, leaveOpen: true);

                // 读取 HTTP 请求行，例如: GET /stream/0 HTTP/1.1
                string requestLine = await reader.ReadLineAsync().ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(requestLine)) return;

                var parts = requestLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2) return;

                string method = parts[0].ToUpperInvariant();
                string rawUrl = parts[1];

                // 略过剩余请求头
                while (!string.IsNullOrEmpty(await reader.ReadLineAsync().ConfigureAwait(false)))
                {
                    // 消费 HTTP Headers
                }

                if (method != "GET" && method != "HEAD")
                {
                    await WriteResponseAsync(networkStream, "405 Method Not Allowed", "text/plain", "Method Not Allowed").ConfigureAwait(false);
                    return;
                }

                // URL 路径解析
                string path = rawUrl;
                int qIdx = path.IndexOf('?');
                if (qIdx >= 0) path = path.Substring(0, qIdx);
                path = path.TrimEnd('/');
                if (string.IsNullOrEmpty(path)) path = "/";

                // 1. 根路径或 /index.html: 返回现代 Web 监控看板
                if (path == "/" || path == "/index.html")
                {
                    string html = WebDashboardContent.GetHtml(_systemTitleProvider(), _port);
                    await WriteResponseAsync(networkStream, "200 OK", "text/html; charset=utf-8", html).ConfigureAwait(false);
                    return;
                }

                // 2. /api/status: 返回系统与摄像头状态 JSON
                if (path == "/api/status")
                {
                    string json = BuildStatusJson();
                    await WriteResponseAsync(networkStream, "200 OK", "application/json; charset=utf-8", json).ConfigureAwait(false);
                    return;
                }

                // 3. /snapshot/{index}: 返回指定通道的最新单帧 JPEG
                if (path.StartsWith("/snapshot"))
                {
                    int camIndex = ParseCameraIndex(path, "/snapshot");
                    if (_latestSnapshots.TryGetValue(camIndex, out var snapshotBytes) && snapshotBytes != null)
                    {
                        await WriteBinaryResponseAsync(networkStream, "200 OK", "image/jpeg", snapshotBytes).ConfigureAwait(false);
                    }
                    else
                    {
                        // 暂无快照
                        byte[] placeholder = GetPlaceholderJpeg();
                        await WriteBinaryResponseAsync(networkStream, "200 OK", "image/jpeg", placeholder).ConfigureAwait(false);
                    }
                    return;
                }

                // 4. /stream/{index} 或 /stream: 返回 MJPEG 视频流
                if (path.StartsWith("/stream"))
                {
                    int camIndex = ParseCameraIndex(path, "/stream");

                    streamSubscription = new StreamClient(camIndex)
                    {
                        TcpClient = client,
                        Stream = networkStream
                    };

                    _clients[streamSubscription.Id] = streamSubscription;
                    NotifyClientCountChanged();

                    // 发送 MJPEG 首部
                    string mjpegHeader = "HTTP/1.1 200 OK\r\n" +
                                         "Server: CameraMonitor-LanStream\r\n" +
                                         "Connection: close\r\n" +
                                         "Cache-Control: no-cache, no-store, must-revalidate, pre-check=0, post-check=0, max-age=0\r\n" +
                                         "Pragma: no-cache\r\n" +
                                         "Expires: -1\r\n" +
                                         "Access-Control-Allow-Origin: *\r\n" +
                                         "Content-Type: multipart/x-mixed-replace; boundary=--frame\r\n\r\n";

                    byte[] headerBytes = System.Text.Encoding.ASCII.GetBytes(mjpegHeader);
                    await networkStream.WriteAsync(headerBytes, 0, headerBytes.Length, serverToken).ConfigureAwait(false);
                    await networkStream.FlushAsync(serverToken).ConfigureAwait(false);

                    // 循环推帧 (带超时探测，无新帧或断连时主动推送离线帧，避免浏览器假死无感知)
                    var channelReader = streamSubscription.FrameChannel.Reader;
                    while (!serverToken.IsCancellationRequested && client.Connected)
                    {
                        byte[] frameBytes = null;
                        try
                        {
                            using var readCts = CancellationTokenSource.CreateLinkedTokenSource(serverToken);
                            readCts.CancelAfter(1500);
                            frameBytes = await channelReader.ReadAsync(readCts.Token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (!serverToken.IsCancellationRequested)
                        {
                            // 1.5 秒内未收到新帧，主动推送带有实时时间戳的离线警示帧
                            frameBytes = GetOfflineFrame(camIndex);
                        }

                        if (frameBytes == null || frameBytes.Length == 0) continue;

                        string partHeader = $"--frame\r\nContent-Type: image/jpeg\r\nContent-Length: {frameBytes.Length}\r\n\r\n";
                        byte[] partHeaderBytes = System.Text.Encoding.ASCII.GetBytes(partHeader);

                        await networkStream.WriteAsync(partHeaderBytes, 0, partHeaderBytes.Length, serverToken).ConfigureAwait(false);
                        await networkStream.WriteAsync(frameBytes, 0, frameBytes.Length, serverToken).ConfigureAwait(false);
                        await networkStream.WriteAsync(System.Text.Encoding.ASCII.GetBytes("\r\n"), 0, 2, serverToken).ConfigureAwait(false);
                        await networkStream.FlushAsync(serverToken).ConfigureAwait(false);
                    }
                    return;
                }

                // 5. 404
                await WriteResponseAsync(networkStream, "404 Not Found", "text/plain", "Not Found").ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 客户端正常断开或超时，退出流
            }
            finally
            {
                if (streamSubscription != null)
                {
                    _clients.TryRemove(streamSubscription.Id, out _);
                    NotifyClientCountChanged();
                }
            }
        }

        private void NotifyClientCountChanged()
        {
            try
            {
                ClientCountChanged?.Invoke(this, EventArgs.Empty);
            }
            catch { }
        }

        private static int ParseCameraIndex(string path, string prefix)
        {
            string sub = path.Substring(prefix.Length).Trim('/');
            if (int.TryParse(sub, out int idx))
            {
                return idx;
            }
            return 0; // 默认通道 0
        }

        private async Task WriteResponseAsync(Stream stream, string status, string contentType, string content)
        {
            byte[] bodyBytes = System.Text.Encoding.UTF8.GetBytes(content);
            string header = $"HTTP/1.1 {status}\r\n" +
                            $"Server: CameraMonitor-LanStream\r\n" +
                            $"Content-Type: {contentType}\r\n" +
                            $"Content-Length: {bodyBytes.Length}\r\n" +
                            $"Connection: close\r\n" +
                            $"Access-Control-Allow-Origin: *\r\n\r\n";
            byte[] headerBytes = System.Text.Encoding.ASCII.GetBytes(header);
            await stream.WriteAsync(headerBytes, 0, headerBytes.Length).ConfigureAwait(false);
            await stream.WriteAsync(bodyBytes, 0, bodyBytes.Length).ConfigureAwait(false);
            await stream.FlushAsync().ConfigureAwait(false);
        }

        private async Task WriteBinaryResponseAsync(Stream stream, string status, string contentType, byte[] data)
        {
            string header = $"HTTP/1.1 {status}\r\n" +
                            $"Server: CameraMonitor-LanStream\r\n" +
                            $"Content-Type: {contentType}\r\n" +
                            $"Content-Length: {data.Length}\r\n" +
                            $"Connection: close\r\n" +
                            $"Cache-Control: no-cache\r\n" +
                            $"Access-Control-Allow-Origin: *\r\n\r\n";
            byte[] headerBytes = System.Text.Encoding.ASCII.GetBytes(header);
            await stream.WriteAsync(headerBytes, 0, headerBytes.Length).ConfigureAwait(false);
            await stream.WriteAsync(data, 0, data.Length).ConfigureAwait(false);
            await stream.FlushAsync().ConfigureAwait(false);
        }

        private string BuildStatusJson()
        {
            var cameras = _cameraInfoProvider();
            var sb = new StringBuilder();
            sb.Append("{");
            sb.Append("\"server\":{");
            sb.Append($"\"isStreaming\":{IsRunning.ToString().ToLowerInvariant()},");
            sb.Append($"\"port\":{_port},");
            sb.Append($"\"clientCount\":{ClientCount}");
            sb.Append("},");

            sb.Append("\"cameras\":[");
            bool first = true;
            foreach (var cam in cameras)
            {
                if (!first) sb.Append(",");
                first = false;
                sb.Append("{");
                sb.Append($"\"index\":{cam.Index},");
                sb.Append($"\"name\":\"{EscapeJson(cam.Name)}\",");
                sb.Append($"\"isConnected\":{cam.IsConnected.ToString().ToLowerInvariant()},");
                sb.Append($"\"width\":{cam.Width},");
                sb.Append($"\"height\":{cam.Height},");
                sb.Append($"\"fps\":{cam.Fps:F1},");
                sb.Append($"\"lastFrameAgeMs\":{cam.LastFrameAgeMs:F0}");
                sb.Append("}");
            }
            sb.Append("]}");
            return sb.ToString();
        }

        private static string EscapeJson(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r");
        }

        // 离线占位帧缓存: CameraIndex -> (生成时间, JPEG字节)
        private readonly ConcurrentDictionary<int, (DateTime Time, byte[] Bytes)> _offlineFrameCache = new();

        /// <summary>
        /// 生成带有通道编号和当前时间戳的高对比度离线警示帧，确保客户端直观探测到信号中断。
        /// </summary>
        private byte[] GetOfflineFrame(int camIndex)
        {
            var now = DateTime.Now;
            if (_offlineFrameCache.TryGetValue(camIndex, out var cached))
            {
                if ((now - cached.Time).TotalSeconds < 1.0)
                {
                    return cached.Bytes;
                }
            }

            try
            {
                using var mat = new OpenCvSharp.Mat(360, 640, OpenCvSharp.MatType.CV_8UC3, new OpenCvSharp.Scalar(25, 20, 15));
                OpenCvSharp.Cv2.Rectangle(mat, new OpenCvSharp.Rect(12, 12, 616, 336), new OpenCvSharp.Scalar(55, 45, 40), 2);

                // 警示红条
                OpenCvSharp.Cv2.Rectangle(mat, new OpenCvSharp.Rect(170, 95, 300, 42), new OpenCvSharp.Scalar(45, 45, 210), -1);
                OpenCvSharp.Cv2.PutText(mat, "CAMERA DISCONNECTED", new OpenCvSharp.Point(185, 124),
                    OpenCvSharp.HersheyFonts.HersheySimplex, 0.65, new OpenCvSharp.Scalar(255, 255, 255), 2);

                // 通道与状态
                string title = $"CH-{camIndex + 1}  NO SIGNAL";
                OpenCvSharp.Cv2.PutText(mat, title, new OpenCvSharp.Point(215, 185),
                    OpenCvSharp.HersheyFonts.HersheySimplex, 0.75, new OpenCvSharp.Scalar(180, 190, 205), 2);

                // 实时更新的时间戳
                string timeStr = now.ToString("yyyy-MM-dd HH:mm:ss");
                OpenCvSharp.Cv2.PutText(mat, timeStr, new OpenCvSharp.Point(225, 230),
                    OpenCvSharp.HersheyFonts.HersheySimplex, 0.55, new OpenCvSharp.Scalar(120, 130, 145), 1);

                OpenCvSharp.Cv2.ImEncode(".jpg", mat, out var buf);
                _offlineFrameCache[camIndex] = (now, buf);
                return buf;
            }
            catch
            {
                return GetPlaceholderJpeg();
            }
        }

        // 占位 JPEG 图（简单黑底灰图，用于无信号通道）
        private static byte[] _placeholderJpegCache;
        private static byte[] GetPlaceholderJpeg()
        {
            if (_placeholderJpegCache != null) return _placeholderJpegCache;
            try
            {
                using var mat = new OpenCvSharp.Mat(360, 640, OpenCvSharp.MatType.CV_8UC3, new OpenCvSharp.Scalar(20, 24, 33));
                OpenCvSharp.Cv2.PutText(mat, "NO SIGNAL / CAMERA OFFLINE", new OpenCvSharp.Point(120, 190),
                    OpenCvSharp.HersheyFonts.HersheySimplex, 0.9, new OpenCvSharp.Scalar(140, 150, 170), 2);
                OpenCvSharp.Cv2.ImEncode(".jpg", mat, out var buf);
                _placeholderJpegCache = buf;
                return buf;
            }
            catch
            {
                return Array.Empty<byte>();
            }
        }

        public void Dispose()
        {
            Stop();
            _cts?.Dispose();
        }
    }

    /// <summary>
    /// 状态查询传输对象
    /// </summary>
    public class CameraInfo
    {
        public int Index { get; set; }
        public string Name { get; set; }
        public bool IsConnected { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public double Fps { get; set; }
        public double LastFrameAgeMs { get; set; }
    }
}
