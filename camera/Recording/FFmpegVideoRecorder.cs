using OpenCvSharp;
using System;
using System.IO;
using camera.Encoding;

namespace camera.Recording
{
    /// <summary>
    /// FFmpeg 录像管理器 — 负责录制生命周期、帧率控制、分段切换和故障自愈。
    /// 
    /// 架构层次:
    /// ┌──────────────────┐
    /// │ CameraService    │  — 业务层：控制录制开关、UI 绑定
    /// ├──────────────────┤
    /// │ RecordingProcessor│  — 管道层：将帧路由到录制器
    /// ├──────────────────┤
    /// │ FFmpegVideoRecorder│ ← 本类 — 录制层：帧率控制、分段、自愈
    /// ├──────────────────┤
    /// │ FFmpegVideoEncoder│  — 编码层：FFmpeg 进程管理、管道 I/O
    /// └──────────────────┘
    /// 
    /// 核心机制:
    /// 1. 帧率降采样 — 摄像头原始帧率(30fps) → 录制帧率(15fps)，通过跳帧实现
    /// 2. 定时分段 — 按 SegmentDuration(默认1分钟) 自动切换输出文件
    /// 3. 故障自愈 — 编码进程崩溃后自动重连，带冷却退避和最大重试限制
    /// </summary>
    public class FFmpegVideoRecorder : IVideoRecorder
    {
        private readonly RecordingOptions _options;
        private readonly string _ffmpegPath;
        private IVideoEncoder _encoder;
        private string _currentOutputPath;
        private DateTime _recordingStartTime;
        private DateTime _lastSegmentTime;
        private int _frameCount;
        private int _skipInterval;
        private bool _isRecording;
        private bool _isPaused;
        private bool _isDisposed;
        private int _currentQuality = 30;
        private readonly object _lock = new();

        // ═══════════════════════════════════════════════════════════
        // 故障自愈控制参数
        // 行业参考: Supervisor/watchdog 模式（systemd restart, Kubernetes restartPolicy）
        // - InitCooldown: 防止高频重试导致资源竞争
        // - MaxInitRetries: 防止无限重试（circuit breaker 模式）
        // ═══════════════════════════════════════════════════════════
        private int _initFailCount = 0;
        private DateTime _lastInitAttempt = DateTime.MinValue;
        private static readonly TimeSpan InitCooldown = TimeSpan.FromSeconds(10);
        private const int MaxInitRetries = 3;

        public bool IsRecording => _isRecording;
        public bool IsPaused => _isPaused;
        public string CurrentOutputPath => _currentOutputPath;
        public DateTime RecordingStartTime => _recordingStartTime;
        public string ActiveCodec => _encoder?.Codec ?? "None";

        public event EventHandler RecordingStarted;
        public event EventHandler RecordingStopped;
        public event EventHandler RecordingPaused;
        public event EventHandler RecordingResumed;
        public event EventHandler<Exception> RecordingError;

        public FFmpegVideoRecorder(RecordingOptions options, string ffmpegPath = null)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _ffmpegPath = ffmpegPath ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ffmpeg.exe");
            _skipInterval = Math.Max(1, (int)options.Fps / Math.Max(1, options.RecordFps));
        }

        public bool StartRecording(string outputPath, int width, int height, double fps, int quality)
        {
            lock (_lock)
            {
                if (_isRecording) return false;

                try
                {
                    _currentQuality = quality;
                    _currentOutputPath = string.IsNullOrEmpty(outputPath) ? GenerateOutputPath() : outputPath;
                    _recordingStartTime = DateTime.Now;
                    _lastSegmentTime = _recordingStartTime;
                    _frameCount = 0;

                    // 重置自愈状态（新录制会话 = 新的重试窗口）
                    _initFailCount = 0;
                    _lastInitAttempt = DateTime.MinValue;

                    // 计算帧率降采样间隔: skipInterval = cameraFps / recordFps
                    // 例: 30fps 摄像头 / 15fps 录制 = 每 2 帧取 1 帧
                    _skipInterval = Math.Max(1, width > 0 ? (int)fps / Math.Max(1, _options.RecordFps) : 30 / 15);

                    _encoder = VideoEncoderFactory.CreateWithAutoFallback(
                        width, height, _options.RecordFps, quality, _currentOutputPath, _ffmpegPath);

                    _isRecording = true;
                    _isPaused = false;

                    Logger.LogInfo($"[FFmpegRecorder] 开始录制到文件: {_currentOutputPath}");
                    RecordingStarted?.Invoke(this, EventArgs.Empty);
                    return true;
                }
                catch (Exception ex)
                {
                    Logger.LogError($"[FFmpegRecorder] 启动录制失败: {ex.Message}", ex);
                    RecordingError?.Invoke(this, ex);
                    Cleanup();
                    return false;
                }
            }
        }

        public void StopRecording()
        {
            lock (_lock)
            {
                if (!_isRecording) return;

                Logger.LogInfo("[FFmpegRecorder] 停止录制");
                _isRecording = false;
                Cleanup();
                RecordingStopped?.Invoke(this, EventArgs.Empty);
            }
        }

        public void Pause()
        {
            lock (_lock)
            {
                if (!_isRecording || _isPaused) return;
                _isPaused = true;
                Logger.LogInfo("[FFmpegRecorder] 暂停录制");
                RecordingPaused?.Invoke(this, EventArgs.Empty);
            }
        }

        public void Resume()
        {
            lock (_lock)
            {
                if (!_isRecording || !_isPaused) return;
                _isPaused = false;
                Logger.LogInfo("[FFmpegRecorder] 恢复录制");
                RecordingResumed?.Invoke(this, EventArgs.Empty);
            }
        }

        /// <summary>
        /// 录制一帧视频 — 每个摄像头帧都会调用此方法。
        /// 
        /// 执行流程:
        /// 1. 自愈检查 → 若编码器崩溃，尝试重连（带冷却和重试上限）
        /// 2. 帧率降采样 → 按 skipInterval 跳帧以匹配目标录制帧率
        /// 3. 分段检查 → 若达到分段时长，切换到新文件
        /// 4. 编码写入 → 通过管道将帧数据发送给 FFmpeg
        /// </summary>
        public void RecordFrame(Mat frame, DateTime timestamp)
        {
            lock (_lock)
            {
                if (!_isRecording || _isPaused) return;

                // ═══ 第一步: 故障自愈 (Circuit Breaker 模式) ═══
                // 若编码进程崩溃（进程退出 → IsInitialized=false），执行冷却退避后尝试重连
                if (_encoder == null || !_encoder.IsInitialized)
                {
                    if (!TryRecoverEncoder(frame))
                    {
                        return; // 冷却中 / 达到重试上限 / 重连失败
                    }
                }

                // ═══ 第二步: 帧率降采样 ═══
                // 动态计算跳帧间隔以响应运行时录像帧率（RecordFps）的调节
                int currentSkipInterval = Math.Max(1, _options.Fps > 0 ? (int)_options.Fps / Math.Max(1, _options.RecordFps) : 30 / 15);
                _frameCount++;
                if (_frameCount < currentSkipInterval) return;
                _frameCount = 0;

                // ═══ 第三步: 分段检查 + 编码写入 ═══
                try
                {
                    CheckSegment(timestamp);
                    
                    bool success = _encoder.EncodeFrame(frame, timestamp);
                    if (!success)
                    {
                        Logger.LogWarning($"[FFmpegRecorder] 帧编码失败（管道可能已断裂），销毁编码器并标记黑名单以触发自愈");
                        VideoEncoderFactory.MarkAsFailed(_encoder.Codec);
                        try { _encoder.Dispose(); } catch { }
                        _encoder = null;
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogWarning($"[FFmpegRecorder] 录制单帧异常: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// 尝试恢复崩溃的编码器。
        /// 实现了 Circuit Breaker 模式: 冷却退避 → 有限重试 → 永久熔断。
        /// </summary>
        /// <returns>true=恢复成功，可以继续录制；false=仍在冷却/达到上限/恢复失败</returns>
        private bool TryRecoverEncoder(Mat frame)
        {
            // 冷却检查
            if ((DateTime.Now - _lastInitAttempt) < InitCooldown)
            {
                return false;
            }

            // 熔断检查
            if (_initFailCount >= MaxInitRetries)
            {
                if (_initFailCount == MaxInitRetries)
                {
                    Logger.LogError($"[FFmpegRecorder] 编码器连续崩溃达到上限 {MaxInitRetries} 次，停止重试（熔断）");
                    _initFailCount++; // 防止重复打印
                }
                return false;
            }

            _lastInitAttempt = DateTime.Now;
            Logger.LogWarning($"[FFmpegRecorder] 检测到编码器已断开，尝试自动重连 (第 {_initFailCount + 1}/{MaxInitRetries} 次)...");

            try
            {
                if (_encoder != null)
                {
                    try { _encoder.Dispose(); } catch { }
                    _encoder = null;
                }

                // 使用新路径重新拉起，避免损坏已有视频文件
                _currentOutputPath = GenerateOutputPath();
                _encoder = VideoEncoderFactory.CreateWithAutoFallback(
                    frame.Width, frame.Height, _options.RecordFps, _currentQuality, _currentOutputPath, _ffmpegPath);

                if (_encoder != null && _encoder.IsInitialized)
                {
                    _initFailCount = 0;
                    _lastSegmentTime = DateTime.Now; // 重置分段计时，确保新文件获得完整分段时长
                    Logger.LogInfo($"[FFmpegRecorder] 编码器自愈成功: {_currentOutputPath}");
                    return true;
                }
                else
                {
                    _initFailCount++;
                    Logger.LogError($"[FFmpegRecorder] 编码器自愈失败");
                    return false;
                }
            }
            catch (Exception ex)
            {
                _initFailCount++;
                Logger.LogError($"[FFmpegRecorder] 编码器自愈异常", ex);
                return false;
            }
        }

        /// <summary>
        /// 检查是否达到分段切换时间。使用 wall-clock 时间戳（而非帧计数）确保精确的分段间隔。
        /// </summary>
        private void CheckSegment(DateTime timestamp)
        {
            if (_options.SegmentDuration <= TimeSpan.Zero) return;

            var elapsed = timestamp - _lastSegmentTime;
            if (elapsed >= _options.SegmentDuration)
            {
                Logger.LogInfo($"[FFmpegRecorder] 分段计时达标 ({_options.SegmentDuration.TotalMinutes} 分钟)，执行文件切换...");
                RotateSegment();
                _lastSegmentTime = timestamp;
            }
        }

        /// <summary>
        /// 执行分段切换: 正常关闭当前文件 → 启动新文件。
        /// 
        /// 关键: 先缓存编码器参数再 Dispose，因为 Dispose 后属性不可靠。
        /// </summary>
        private void RotateSegment()
        {
            if (_encoder == null) return;

            // 缓存参数（Dispose 后不可访问）
            int width = _encoder.Width;
            int height = _encoder.Height;

            Logger.LogInfo($"[FFmpegRecorder] 分段切换: 关闭 {_currentOutputPath}");
            _encoder.Flush();
            _encoder.Dispose();

            string newPath = GenerateOutputPath();
            Logger.LogInfo($"[FFmpegRecorder] 分段切换: 创建 {newPath}");
            _encoder = VideoEncoderFactory.CreateWithAutoFallback(
                width, height, _options.RecordFps, _currentQuality, newPath, _ffmpegPath);
            _currentOutputPath = newPath;
        }

        /// <summary>
        /// 生成输出文件路径。目录结构: SaveDirectory/{CameraIndex}/{yyyyMMdd}/{HH-mm-ss}.mp4
        /// </summary>
        private string GenerateOutputPath()
        {
            DateTime now = DateTime.Now;
            string dateStr = now.ToString("yyyyMMdd");
            string timeStr = now.ToString("HH-mm-ss") + ".mp4";
            
            string directory = string.IsNullOrEmpty(_options.SaveDirectory)
                ? AppDomain.CurrentDomain.BaseDirectory
                : _options.SaveDirectory;

            string fullDir = Path.Combine(directory, _options.CameraIndex.ToString(), dateStr);
            if (!Directory.Exists(fullDir))
            {
                Directory.CreateDirectory(fullDir);
            }

            return Path.Combine(fullDir, timeStr);
        }

        /// <summary>
        /// 清理编码器资源。通过 Flush 确保 FFmpeg 正常关闭以生成可播放的文件。
        /// </summary>
        private void Cleanup()
        {
            if (_encoder != null)
            {
                try
                {
                    _encoder.Flush();
                    _encoder.Dispose();
                }
                catch { }
                _encoder = null;
            }
            _isRecording = false;
            _isPaused = false;
        }

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;
            StopRecording();
        }
    }
}
