using OpenCvSharp;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace camera.Streaming
{
    public enum StreamProtocol
    {
        Http = 0,
        Rtsp = 1
    }

    /// <summary>
    /// 局域网推流全局管理器（单例），协调 HTTP/RTSP 服务启停、帧率节流、图像压缩和广播分发。
    /// </summary>
    public sealed class LanStreamManager : IDisposable
    {
        private static readonly Lazy<LanStreamManager> _instance =
            new(() => new LanStreamManager(), System.Threading.LazyThreadSafetyMode.ExecutionAndPublication);

        public static LanStreamManager Instance => _instance.Value;

        private LanStreamServer _httpServer;
        private RtspStreamServer _rtspServer;
        private readonly object _lock = new();

        private bool _isEnabled = false;
        private StreamProtocol _protocol = StreamProtocol.Http;
        private int _port = 8080;
        private int _targetFps = 15;
        private int _quality = 70;

        // 通道上一次编码并推流的时间戳: CameraIndex -> DateTime
        private readonly ConcurrentDictionary<int, DateTime> _lastStreamTime = new();

        private Func<string> _systemTitleProvider;
        private Func<IEnumerable<CameraInfo>> _cameraInfoProvider;
        private int[] _encodeParams;

        public bool IsEnabled => _isEnabled;
        public bool IsRunning => _protocol == StreamProtocol.Rtsp
            ? (_rtspServer?.IsRunning ?? false)
            : (_httpServer?.IsRunning ?? false);

        public StreamProtocol Protocol => _protocol;
        public int Port => _port;
        public int TargetFps => _targetFps;
        public int Quality => _quality;
        public int ClientCount => _protocol == StreamProtocol.Rtsp
            ? (_rtspServer?.ClientCount ?? 0)
            : (_httpServer?.ClientCount ?? 0);

        public event EventHandler StatusChanged;
        public event EventHandler ClientCountChanged;

        private LanStreamManager()
        {
            UpdateEncodeParams();
        }

        private void UpdateEncodeParams()
        {
            _encodeParams = new int[]
            {
                (int)ImwriteFlags.JpegQuality, Math.Clamp(_quality, 10, 100),
                (int)ImwriteFlags.JpegOptimize, 1
            };
        }

        public void RegisterProvider(Func<string> systemTitleProvider, Func<IEnumerable<CameraInfo>> cameraInfoProvider)
        {
            _systemTitleProvider = systemTitleProvider;
            _cameraInfoProvider = cameraInfoProvider;
        }

        public void Configure(StreamProtocol protocol, int port, int targetFps, int quality)
        {
            bool needRestart = false;
            lock (_lock)
            {
                if (_protocol != protocol)
                {
                    _protocol = protocol;
                    if (IsRunning) needRestart = true;
                }
                if (_port != port && port > 0)
                {
                    _port = port;
                    if (IsRunning) needRestart = true;
                }
                if (targetFps > 0) _targetFps = targetFps;
                if (quality > 0)
                {
                    _quality = quality;
                    UpdateEncodeParams();
                }
            }

            if (needRestart)
            {
                Restart();
            }
        }

        public bool Start()
        {
            lock (_lock)
            {
                _isEnabled = true;
                if (IsRunning) return true;

                bool ok = false;
                if (_protocol == StreamProtocol.Rtsp)
                {
                    StopHttpServer();
                    _rtspServer = new RtspStreamServer(
                        _port > 0 ? _port : 8554,
                        _systemTitleProvider ?? (() => "监控"),
                        _cameraInfoProvider ?? (() => Array.Empty<CameraInfo>()));
                    _rtspServer.ClientCountChanged += (s, e) => ClientCountChanged?.Invoke(this, EventArgs.Empty);
                    ok = _rtspServer.Start();
                }
                else
                {
                    StopRtspServer();
                    _httpServer = new LanStreamServer(
                        _port > 0 ? _port : 8080,
                        _systemTitleProvider ?? (() => "监控"),
                        _cameraInfoProvider ?? (() => Array.Empty<CameraInfo>()));
                    _httpServer.ClientCountChanged += (s, e) => ClientCountChanged?.Invoke(this, EventArgs.Empty);
                    ok = _httpServer.Start();
                }

                StatusChanged?.Invoke(this, EventArgs.Empty);
                return ok;
            }
        }

        public void Stop()
        {
            lock (_lock)
            {
                _isEnabled = false;
                StopHttpServer();
                StopRtspServer();
                _lastStreamTime.Clear();
                StatusChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        private void StopHttpServer()
        {
            if (_httpServer != null)
            {
                _httpServer.Stop();
                _httpServer.Dispose();
                _httpServer = null;
            }
        }

        private void StopRtspServer()
        {
            if (_rtspServer != null)
            {
                _rtspServer.Stop();
                _rtspServer.Dispose();
                _rtspServer = null;
            }
        }

        public bool Restart()
        {
            Stop();
            return Start();
        }

        public bool Toggle()
        {
            if (IsRunning)
            {
                Stop();
                return false;
            }
            else
            {
                return Start();
            }
        }

        public bool HasSubscribers(int cameraIndex)
        {
            if (!IsRunning) return false;
            return _protocol == StreamProtocol.Rtsp
                ? (_rtspServer != null && _rtspServer.HasSubscribers(cameraIndex))
                : (_httpServer != null && _httpServer.HasSubscribers(cameraIndex));
        }

        /// <summary>
        /// 获取指定通道距离上一帧推流的时间差（毫秒），用于断连与画面停滞探测。
        /// </summary>
        public double GetLastFrameAgeMs(int cameraIndex)
        {
            if (_lastStreamTime.TryGetValue(cameraIndex, out var lastTime))
            {
                return Math.Max(0.0, (DateTime.Now - lastTime).TotalMilliseconds);
            }
            return 999999.0;
        }

        public void OnCameraFrame(int cameraIndex, Mat frame, DateTime timestamp)
        {
            if (!IsRunning || frame == null || frame.Empty() || frame.IsDisposed) return;
            if (!HasSubscribers(cameraIndex)) return;

            double intervalMs = 1000.0 / Math.Max(1, _targetFps);
            DateTime now = DateTime.Now;

            if (_lastStreamTime.TryGetValue(cameraIndex, out var lastTime))
            {
                if ((now - lastTime).TotalMilliseconds < intervalMs - 2.0)
                {
                    return;
                }
            }

            _lastStreamTime[cameraIndex] = now;

            try
            {
                Cv2.ImEncode(".jpg", frame, out byte[] jpegBytes, _encodeParams);
                if (jpegBytes != null && jpegBytes.Length > 0)
                {
                    if (_protocol == StreamProtocol.Rtsp)
                    {
                        _rtspServer?.BroadcastFrame(cameraIndex, jpegBytes, frame.Width, frame.Height);
                    }
                    else
                    {
                        _httpServer?.BroadcastFrame(cameraIndex, jpegBytes);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"[LanStreamManager] 通道 {cameraIndex} 推流编码异常: {ex.Message}");
            }
        }

        public List<string> GetStreamUrls()
        {
            var ips = NetworkHelper.GetLocalIpAddresses();
            var list = new List<string>();

            if (_protocol == StreamProtocol.Rtsp)
            {
                var cams = _cameraInfoProvider?.Invoke()?.ToList() ?? new List<CameraInfo>();
                if (cams.Count > 0)
                {
                    foreach (var ip in ips)
                    {
                        foreach (var c in cams)
                        {
                            list.Add($"rtsp://{ip}:{_port}/live/{c.Index}");
                        }
                    }
                }
                else
                {
                    foreach (var ip in ips)
                    {
                        list.Add($"rtsp://{ip}:{_port}/live/0");
                    }
                }
            }
            else
            {
                list.AddRange(ips.Select(ip => $"http://{ip}:{_port}/"));
            }

            return list;
        }

        public string GetPrimaryStreamUrl()
        {
            string ip = NetworkHelper.GetPrimaryLocalIpAddress();
            if (_protocol == StreamProtocol.Rtsp)
            {
                return $"rtsp://{ip}:{_port}/live/0";
            }
            return $"http://{ip}:{_port}/";
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
