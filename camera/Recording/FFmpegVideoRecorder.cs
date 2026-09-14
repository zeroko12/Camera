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
        private int _framesWritten;

        private volatile bool _isRecording;
        private volatile bool _isPaused;
        private bool _isDisposed;
        public int CurrentQuality { get; set; } = 30;
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
        }

        public bool StartRecording(string outputPath, int inputWidth, int inputHeight, int recordWidth, int recordHeight, double fps, int quality)
        {
            lock (_lock)
            {
                if (_isRecording) return false;

                try
                {
                    CurrentQuality = quality;
                    _currentOutputPath = string.IsNullOrEmpty(outputPath) ? GenerateOutputPath() : outputPath;
                    _recordingStartTime = DateTime.Now;
                    _lastSegmentTime = _recordingStartTime;
                    _framesWritten = 0;

                    // 重置自愈状态（新录制会话 = 新的重试窗口）
                    _initFailCount = 0;
                    _lastInitAttempt = DateTime.MinValue;

                    _options.InputWidth = inputWidth;
                    _options.InputHeight = inputHeight;
                    _options.Width = recordWidth;
                    _options.Height = recordHeight;

                    _encoder = VideoEncoderFactory.CreateWithAutoFallback(
                        inputWidth, inputHeight, recordWidth, recordHeight, _options.RecordFps, quality, _currentOutputPath, _ffmpegPath, _options.CustomText, _options.OverlayTextSize);

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
            // ═══ 死锁修复：先在锁内设置停止标志，再在锁外执行阻塞清理 ═══
            // 问题根因: 帧采集线程在 RecordFrame 中持锁 → 同时 StopRecording 在锁内
            //           调用 Flush→WaitForExit(8000)，两者互相等待形成死锁。
            // 修复方案: 锁内只修改状态标志（原子操作），锁外执行所有阻塞 I/O。
            bool wasRecording;
            lock (_lock)
            {
                wasRecording = _isRecording;
                if (!wasRecording) return;
                _isRecording = false; // 让 RecordFrame 不再进入录制逻辑
                _isPaused = false;
            }

            // 锁外执行：Cleanup 内部会 WaitForExit(8000)，必须在锁外调用
            Logger.LogInfo("[FFmpegRecorder] 停止录制");
            CleanupOutsideLock();
            RecordingStopped?.Invoke(this, EventArgs.Empty);
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
        /// 注意: 帧率控制由上游管道（CameraService.RefreshFrame）负责，
        /// 调用到此方法的帧已经是按 RecordFps 节流后的帧，录制器直接写入即可。
        /// 
        /// 执行流程:
        /// 1. 自愈检查 → 若编码器崩溃，尝试重连（带冷却和重试上限）
        /// 2. 分段检查 → 若达到分段时长，切换到新文件
        /// 3. 编码写入 → 通过管道将帧数据发送给 FFmpeg
        /// </summary>
        public void RecordFrame(Mat frame, DateTime timestamp)
        {
            // 快速路径：无锁检查（volatile 读），录制关闭/暂停时直接返回，避免 Monitor.Enter/Exit 开销
            if (!_isRecording || _isPaused) return;

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

                // ═══ 第二步: 分段检查 + 编码写入 ═══
                // 帧率降采样已由管道层完成，此处无需再做跳帧
                try
                {
                    CheckSegment(timestamp);
                    
                    // 基于时间戳进行帧追赶与同步 (CFR 对准)
                    double elapsedSeconds = (timestamp - _lastSegmentTime).TotalSeconds;
                    int expectedFrames = (int)(elapsedSeconds * _options.RecordFps) + 1;
                    int framesToEncode = expectedFrames - _framesWritten;

                    if (framesToEncode > 0)
                    {
                        // 容错处理：若发生系统休眠或时钟跳变导致帧数落后过多，进行安全限制，避免一次性写入过多重复帧卡死应用
                        if (framesToEncode > _options.RecordFps * 5)
                        {
                            Logger.LogWarning($"[FFmpegRecorder] 检测到时间跳变或系统休眠，限制帧追赶: elapsed={elapsedSeconds}s, expected={expectedFrames}, written={_framesWritten}. 强制重置.");
                            framesToEncode = 1;
                            _framesWritten = expectedFrames - 1;
                        }

                        for (int i = 0; i < framesToEncode; i++)
                        {
                            bool success = _encoder.EncodeFrame(frame, timestamp);
                            if (!success)
                            {
                                Logger.LogWarning($"[FFmpegRecorder] 帧编码失败（管道可能已断裂），销毁编码器并标记黑名单以触发自愈");
                                VideoEncoderFactory.MarkAsFailed(_encoder.Codec);
                                try { _encoder.Dispose(); } catch { }
                                _encoder = null;
                                break;
                            }
                        }
                        _framesWritten += framesToEncode;
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
                    _options.InputWidth, _options.InputHeight, _options.Width, _options.Height, _options.RecordFps, CurrentQuality, _currentOutputPath, _ffmpegPath, _options.CustomText, _options.OverlayTextSize);

                if (_encoder != null && _encoder.IsInitialized)
                {
                    _initFailCount = 0;
                    _lastSegmentTime = DateTime.Now; // 重置分段计时，确保新文件获得完整分段时长
                    _framesWritten = 0;
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

            Logger.LogInfo($"[FFmpegRecorder] 分段切换: 关闭 {_currentOutputPath}");
            // ★ Fix P1-6: 显式 Dispose 一次,避免 RotateSegment + Dispose 双重 Flush 调用
            // 原来的 _encoder.Flush() + _encoder.Dispose() 会触发两次 Cleanup,
            // 每次都创建 _yuvBuffer/_cts Dispose 链。
            var oldEncoder = _encoder;
            _encoder = null;  // 先置 null,确保 RecordFrame 不再访问
            try
            {
                oldEncoder.Dispose();  // Dispose 内部会调 Flush
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"[FFmpegRecorder] 旧编码器 Dispose 异常: {ex.Message}");
            }

            // 如果本分段没有写入任何有效帧，则自动清理删除该空视频文件
            if (_framesWritten == 0 && !string.IsNullOrEmpty(_currentOutputPath) && File.Exists(_currentOutputPath))
            {
                try
                {
                    File.Delete(_currentOutputPath);
                    Logger.LogInfo($"[FFmpegRecorder] 自动清理无数据分段空文件: {_currentOutputPath}");
                }
                catch (Exception ex)
                {
                    Logger.LogWarning($"[FFmpegRecorder] 无法清理删除空视频文件: {ex.Message}");
                }
            }

            string newPath = GenerateOutputPath();
            Logger.LogInfo($"[FFmpegRecorder] 分段切换: 创建 {newPath}");
            _encoder = VideoEncoderFactory.CreateWithAutoFallback(
                _options.InputWidth, _options.InputHeight, _options.Width, _options.Height, _options.RecordFps, CurrentQuality, newPath, _ffmpegPath, _options.CustomText, _options.OverlayTextSize);
            _currentOutputPath = newPath;
            _framesWritten = 0;
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
        /// 清理编码器资源（必须在锁外调用）。
        /// 通过 Flush 确保 FFmpeg 正常关闭以生成可播放的文件。
        /// 注意：此方法会阻塞等待 FFmpeg 进程退出（最长 8 秒），
        ///       因此严禁在持有 _lock 的情况下调用，否则会与 RecordFrame 线程形成死锁。
        /// </summary>
        private void CleanupOutsideLock()
        {
            IVideoEncoder encoderToDispose;
            string outputPathToDelete = null;
            lock (_lock)
            {
                encoderToDispose = _encoder;
                _encoder = null;          // 立即置空，让 RecordFrame 不再使用旧编码器
                _isRecording = false;
                _isPaused = false;
                if (_framesWritten == 0)
                {
                    outputPathToDelete = _currentOutputPath;
                }
            }

            // 在锁外执行阻塞的 Flush（内部 WaitForExit 最长 8 秒）
            if (encoderToDispose != null)
            {
                try
                {
                    encoderToDispose.Flush();
                    encoderToDispose.Dispose();
                }
                catch { }
            }

            // 删除空文件
            if (!string.IsNullOrEmpty(outputPathToDelete) && File.Exists(outputPathToDelete))
            {
                try
                {
                    File.Delete(outputPathToDelete);
                    Logger.LogInfo($"[FFmpegRecorder] 自动清理无数据空文件: {outputPathToDelete}");
                }
                catch (Exception ex)
                {
                    Logger.LogWarning($"[FFmpegRecorder] 无法清理删除空视频文件: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// 清理编码器资源（仅供已持锁的内部路径，如 RotateSegment 调用）。
        /// </summary>
        private void Cleanup()
        {
            string outputPathToDelete = null;
            if (_encoder != null)
            {
                try
                {
                    _encoder.Flush();
                    _encoder.Dispose();
                }
                catch { }
                _encoder = null;
                if (_framesWritten == 0)
                {
                    outputPathToDelete = _currentOutputPath;
                }
            }
            _isRecording = false;
            _isPaused = false;

            if (!string.IsNullOrEmpty(outputPathToDelete) && File.Exists(outputPathToDelete))
            {
                try
                {
                    File.Delete(outputPathToDelete);
                    Logger.LogInfo($"[FFmpegRecorder] 自动清理无数据空文件: {outputPathToDelete}");
                }
                catch (Exception ex)
                {
                    Logger.LogWarning($"[FFmpegRecorder] 无法清理删除空视频文件: {ex.Message}");
                }
            }
        }

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;
            // 先设置停止标志
            lock (_lock)
            {
                _isRecording = false;
                _isPaused = false;
            }
            // 锁外清理
            CleanupOutsideLock();
        }
    }
}
