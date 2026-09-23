using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace camera.Recording
{
    /// <summary>
    /// 全局存储与磁盘空间管理器（单例）。
    /// 
    /// 核心架构职责:
    /// 1. 【物理采集解耦】: 彻底脱离 CameraService 的实时物理采集主循环，在独立的低优先级后台 Task 中执行空间巡检与文件清理。
    /// 2. 【靶向存储盘隔离】: 仅对 SaveDirectory 所在真实盘符进行空间核算与维护；系统盘(C:)仅作为独立诊断监控，绝不跨盘误删。
    /// 3. 【高低水位回差机制 (Hysteresis Watermark)】:
    ///    - 低水位 (Low Watermark): DiskSpaceThresholdMb (如 10240 MB)
    ///    - 高水位 (High Watermark): DiskSpaceThresholdMb + Max(10240, 5% of total disk) (至少保留 10~20GB 缓冲)
    ///    - 触发清理时批量释放至高水位，彻底消除每 5 分钟反复震荡清理的 Thrashing 现象。
    /// 4. 【活跃文件保护白名单】: 保护正在录制的活跃分段与正在回放的文件，避免误删导致损坏。
    /// 5. 【非模态告警】: 抛出事件与状态更新，杜绝阻塞式 MessageBox 挂起 UI 线程。
    /// </summary>
    public sealed class StorageManager : IDisposable
    {
        private static readonly Lazy<StorageManager> _instance =
            new(() => new StorageManager(), LazyThreadSafetyMode.ExecutionAndPublication);

        public static StorageManager Instance => _instance.Value;

        // ═══════════════════════════════════════════════════════════
        // 配置与状态
        // ═══════════════════════════════════════════════════════════
        private readonly object _configLock = new();
        private readonly object _cleanupLock = new();
        private string _saveDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SavedVideos");
        private long _diskSpaceThresholdMb = 10240; // 默认 10GB
        private bool _overwriteOldFiles = true;

        // 活跃文件白名单（正在录制的文件路径 -> 占用标记）
        private readonly ConcurrentDictionary<string, byte> _activeFiles = new(StringComparer.OrdinalIgnoreCase);

        // 后台巡检定时器（每 120 秒巡检一次，低频且不抢占计算资源）
        private readonly Timer _maintenanceTimer;
        private volatile bool _isCleaning = false;
        private volatile bool _isDisposed = false;
        private DateTime _lastCleanupTime = DateTime.MinValue;

        // 最近一次空间检测缓存
        private long _cachedAvailableMb = 0;
        private long _cachedTotalMb = 0;
        private string _storageDriveRoot = "C:\\";
        private bool _isStorageCritical = false;

        // ═══════════════════════════════════════════════════════════
        // 公共属性与事件
        // ═══════════════════════════════════════════════════════════
        public string SaveDirectory
        {
            get { lock (_configLock) return _saveDirectory; }
            set
            {
                lock (_configLock)
                {
                    if (string.IsNullOrWhiteSpace(value))
                        _saveDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SavedVideos");
                    else
                        _saveDirectory = Path.IsPathRooted(value) ? value : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, value);

                    UpdateDriveInfo();
                }
            }
        }

        public long DiskSpaceThresholdMb
        {
            get { lock (_configLock) return _diskSpaceThresholdMb; }
            set
            {
                lock (_configLock)
                {
                    _diskSpaceThresholdMb = Math.Max(512, value);
                }
            }
        }

        public bool OverwriteOldFiles
        {
            get { lock (_configLock) return _overwriteOldFiles; }
            set { lock (_configLock) _overwriteOldFiles = value; }
        }

        public string StorageDriveRoot => _storageDriveRoot;
        public long AvailableStorageMb => _cachedAvailableMb;
        public double AvailableStorageGb => Math.Round(_cachedAvailableMb / 1024.0, 2);
        public bool IsStorageCritical => _isStorageCritical;

        /// <summary>
        /// 存储状态告警事件（非模态，UI/日志订阅）
        /// </summary>
        public event EventHandler<string> StorageWarning;
        public event EventHandler StorageStatusUpdated;

        // ═══════════════════════════════════════════════════════════
        // 初始化与生命周期
        // ═══════════════════════════════════════════════════════════
        private StorageManager()
        {
            UpdateDriveInfo();

            // 启动后台低优先级维护定时器，初始延迟 10 秒，周期 120 秒
            _maintenanceTimer = new Timer(OnMaintenanceTick, null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(120));
        }

        /// <summary>
        /// 更新目标存储盘根路径与容量缓存
        /// </summary>
        public void UpdateDriveInfo()
        {
            try
            {
                string dir = SaveDirectory;
                string root = Path.GetPathRoot(dir);
                if (string.IsNullOrEmpty(root))
                {
                    root = Path.GetPathRoot(AppDomain.CurrentDomain.BaseDirectory);
                }
                _storageDriveRoot = root;

                DriveInfo driveInfo = new DriveInfo(_storageDriveRoot);
                if (driveInfo.IsReady)
                {
                    _cachedAvailableMb = driveInfo.AvailableFreeSpace / (1024 * 1024);
                    _cachedTotalMb = driveInfo.TotalSize / (1024 * 1024);
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"[StorageManager] 获取存储盘信息异常: {ex.Message}");
            }
        }

        // ═══════════════════════════════════════════════════════════
        // 活跃文件白名单保护
        // ═══════════════════════════════════════════════════════════
        public void RegisterActiveFile(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath)) return;
            try
            {
                string full = Path.GetFullPath(filePath);
                _activeFiles[full] = 1;
            }
            catch { }
        }

        public void UnregisterActiveFile(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath)) return;
            try
            {
                string full = Path.GetFullPath(filePath);
                _activeFiles.TryRemove(full, out _);
            }
            catch { }
        }

        public bool IsFileProtected(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath)) return false;
            try
            {
                string full = Path.GetFullPath(filePath);
                return _activeFiles.ContainsKey(full);
            }
            catch
            {
                return false;
            }
        }

        // ═══════════════════════════════════════════════════════════
        // 录像权限判断（极速内存原子读取，绝不阻塞）
        // ═══════════════════════════════════════════════════════════
        public bool CanStartRecording(out string failureReason)
        {
            failureReason = string.Empty;

            // 1. 刷新即时存储盘空间
            UpdateDriveInfo();

            // 2. 如果开启了覆盖旧文件，哪怕暂时低于阈值也可以开始（后台清理会马上触发腾挪）
            if (_cachedAvailableMb < DiskSpaceThresholdMb && !OverwriteOldFiles)
            {
                failureReason = $"存储盘空间不足({_cachedAvailableMb}MB < {DiskSpaceThresholdMb}MB)且未启用自动覆盖";
                return false;
            }

            // 3. 绝对安全红线（如果存储盘剩余 < 100MB 无论如何都不能录）
            if (_cachedAvailableMb < 100)
            {
                failureReason = $"存储盘可用空间已耗尽({_cachedAvailableMb}MB)，保护系统安全阻止开启录像";
                return false;
            }

            // 4. 独立诊断：系统盘(C:)空间监测（仅记录日志与提示，绝不作为非 C 盘存储时的阻断条件）
            CheckSystemDriveDiagnostic();

            return true;
        }

        /// <summary>
        /// 检查系统盘(C:)空间用于健康诊断（若存储盘也是C盘，由存储盘阈值统一管控）
        /// </summary>
        private void CheckSystemDriveDiagnostic()
        {
            try
            {
                // 如果录像盘就是 C 盘，则已经由存储盘逻辑管控，无需重复诊断
                if (_storageDriveRoot.StartsWith("C", StringComparison.OrdinalIgnoreCase))
                    return;

                DriveInfo cDrive = new DriveInfo("C");
                if (cDrive.IsReady)
                {
                    long cFreeMb = cDrive.AvailableFreeSpace / (1024 * 1024);
                    if (cFreeMb < 2048) // C 盘少于 2GB 时输出警告日志
                    {
                        Logger.LogWarning($"[StorageManager] 提示: 系统盘(C:)空间较低 ({cFreeMb}MB)，建议运维排查系统垃圾。当前录像存储位于盘符 {_storageDriveRoot} (可用: {_cachedAvailableMb}MB)。");
                    }
                }
            }
            catch { }
        }

        // ═══════════════════════════════════════════════════════════
        // 后台定时维护逻辑
        // ═══════════════════════════════════════════════════════════
        private void OnMaintenanceTick(object state)
        {
            if (_isDisposed) return;

            // 异步投递到线程池，绝不阻塞任何调用方
            Task.Run(() =>
            {
                try
                {
                    UpdateDriveInfo();
                    CheckSystemDriveDiagnostic();
                    StorageStatusUpdated?.Invoke(this, EventArgs.Empty);

                    // 检查是否触及低水位线
                    if (_cachedAvailableMb < DiskSpaceThresholdMb)
                    {
                        if (OverwriteOldFiles)
                        {
                            PerformCleanupWatermark();
                        }
                        else
                        {
                            _isStorageCritical = true;
                            string msg = $"[StorageManager] 存储盘空间不足({_cachedAvailableMb}MB < {DiskSpaceThresholdMb}MB)且未开启覆盖，录像面临中断风险。";
                            Logger.LogWarning(msg);
                            StorageWarning?.Invoke(this, msg);
                        }
                    }
                    else
                    {
                        _isStorageCritical = false;
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogError("[StorageManager] 后台空间维护检查发生异常", ex);
                }
            });
        }

        /// <summary>
        /// 手动或按需触发一次后台高低水位清理
        /// </summary>
        public Task TriggerMaintenanceAsync()
        {
            return Task.Run(() =>
            {
                UpdateDriveInfo();
                if (_cachedAvailableMb < DiskSpaceThresholdMb && OverwriteOldFiles)
                {
                    PerformCleanupWatermark();
                }
            });
        }

        /// <summary>
        /// 执行高低水位回差清理算法（彻底终结 5 分钟频发清理的抖动根因）
        /// </summary>
        private void PerformCleanupWatermark()
        {
            if (_isCleaning) return;

            lock (_cleanupLock)
            {
                if (_isCleaning) return;
                _isCleaning = true;

                try
                {
                    UpdateDriveInfo();
                    long currentMb = _cachedAvailableMb;
                    long thresholdMb = DiskSpaceThresholdMb;

                    if (currentMb >= thresholdMb)
                    {
                        return;
                    }

                    // ═══════════════════════════════════════════════════════════
                    // 核心算法: 高低水位回差 (Hysteresis Watermark)
                    // - 低水位: thresholdMb (如 10240 MB)
                    // - 高水位: thresholdMb + 动态充裕缓冲 (至少 10240 MB 或总磁盘容量的 5%)
                    // 举例: 若总盘 500GB，5% = 25GB。高水位 = 10GB + 25GB = 35GB。
                    // 一旦触发清理，一次性腾出 25GB 空间，足够双路录制连续写 3~7 天，杜绝高频振荡！
                    // ═══════════════════════════════════════════════════════════
                    long dynamicBufferMb = Math.Max(10240L, (long)(_cachedTotalMb * 0.05));
                    // 上限保护：避免缓冲设定过大导致把所有历史录像一次性删光
                    if (dynamicBufferMb > 51200L) dynamicBufferMb = 51200L; // 最大单次释放目标缓冲 50GB

                    long targetHighWatermarkMb = thresholdMb + dynamicBufferMb;

                    string fullSaveDir = SaveDirectory;
                    if (!Directory.Exists(fullSaveDir))
                    {
                        return;
                    }

                    Logger.LogInfo($"[StorageManager] 存储盘可用空间({currentMb}MB)低于阈值({thresholdMb}MB)，启动高低水位清理，目标恢复至高水位({targetHighWatermarkMb}MB)...");

                    // 获取所有视频文件（带安全异常保护）
                    var allFiles = Directory.GetFiles(fullSaveDir, "*.mp4", SearchOption.AllDirectories)
                        .Select(f =>
                        {
                            try { return new FileInfo(f); } catch { return null; }
                        })
                        .Where(fi => fi != null && fi.Exists)
                        .OrderBy(fi => fi.LastWriteTime) // 最老的文件最先淘汰
                        .ToList();

                    int deletedCount = 0;
                    long deletedBytes = 0;
                    DateTime safeCutoff = DateTime.Now.AddMinutes(-30); // 绝不碰最近 30 分钟内生成的文件

                    foreach (var fi in allFiles)
                    {
                        // 达到高水位线，立即收工
                        if (currentMb >= targetHighWatermarkMb)
                        {
                            break;
                        }

                        // 保护检查 1: 活跃写入白名单保护
                        if (IsFileProtected(fi.FullName))
                        {
                            continue;
                        }

                        // 保护检查 2: 新生文件保护
                        if (fi.LastWriteTime > safeCutoff)
                        {
                            continue;
                        }

                        try
                        {
                            long len = fi.Length;
                            fi.Delete();
                            deletedCount++;
                            deletedBytes += len;
                            currentMb += (len / (1024 * 1024));

                            // 尝试清理空目录 (日期子目录)
                            var parentDir = fi.Directory;
                            if (parentDir != null && parentDir.Exists &&
                                parentDir.GetFiles().Length == 0 && parentDir.GetDirectories().Length == 0)
                            {
                                try
                                {
                                    parentDir.Delete();
                                    // 递归检查上一级相机目录
                                    var grandParent = parentDir.Parent;
                                    if (grandParent != null && grandParent.Exists &&
                                        grandParent.GetFiles().Length == 0 && grandParent.GetDirectories().Length == 0)
                                    {
                                        try { grandParent.Delete(); } catch { }
                                    }
                                }
                                catch { }
                            }
                        }
                        catch (IOException)
                        {
                            // 文件正在被播放或写入，跳过
                        }
                        catch (Exception ex)
                        {
                            Logger.LogWarning($"[StorageManager] 删除历史文件失败 ({fi.FullName}): {ex.Message}");
                        }
                    }

                    UpdateDriveInfo();
                    long releasedMb = deletedBytes / (1024 * 1024);
                    _lastCleanupTime = DateTime.Now;

                    if (deletedCount > 0)
                    {
                        Logger.LogInfo($"[StorageManager] 高低水位清理完成: 成功删除 {deletedCount} 个历史视频文件，释放空间约 {releasedMb}MB，当前存储盘可用: {_cachedAvailableMb}MB");
                    }

                    if (_cachedAvailableMb < thresholdMb)
                    {
                        _isStorageCritical = true;
                        string alert = $"[StorageManager] 清理已完成但空间依然不足 ({_cachedAvailableMb}MB < {thresholdMb}MB)，磁盘上无更多可清理的旧视频！";
                        Logger.LogError(alert);
                        StorageWarning?.Invoke(this, alert);
                    }
                    else
                    {
                        _isStorageCritical = false;
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogError("[StorageManager] 执行磁盘清理过程发生异常", ex);
                }
                finally
                {
                    _isCleaning = false;
                }
            }
        }

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;
            try
            {
                _maintenanceTimer?.Dispose();
            }
            catch { }
        }
    }
}
