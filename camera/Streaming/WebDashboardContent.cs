namespace camera.Streaming
{
    /// <summary>
    /// 提供局域网监控网页 (Web Dashboard) 的完整内嵌 HTML/CSS/JS 内容。
    /// 纯原生实现，不依赖外部 CDN 或互联网连接，保证在封闭的工厂/车间局域网内完全可用。
    /// 内置主动断连探测、通道级掉线遮罩、服务断开全局告警与自动恢复重拉流机制。
    /// </summary>
    public static class WebDashboardContent
    {
        public static string GetHtml(string systemTitle, int port)
        {
            return @"<!DOCTYPE html>
<html lang=""zh-CN"">
<head>
    <meta charset=""UTF-8"">
    <meta name=""viewport"" content=""width=device-width, initial-scale=1.0, maximum-scale=1.0, user-scalable=no"">
    <title>" + systemTitle + @" - 局域网实时监控</title>
    <style>
        :root {
            --bg-main: #0B0F19;
            --bg-card: #151D2F;
            --bg-card-hover: #1E293B;
            --accent: #3B82F6;
            --accent-glow: rgba(59, 130, 246, 0.4);
            --success: #10B981;
            --danger: #EF4444;
            --warning: #F59E0B;
            --text-primary: #F8FAFC;
            --text-secondary: #94A3B8;
            --border: rgba(255, 255, 255, 0.08);
            --border-active: rgba(59, 130, 246, 0.5);
        }

        * {
            box-sizing: border-box;
            margin: 0;
            padding: 0;
            -webkit-tap-highlight-color: transparent;
        }

        body {
            font-family: -apple-system, BlinkMacSystemFont, ""Segoe UI"", Roboto, ""Helvetica Neue"", Arial, ""PingFang SC"", ""Microsoft YaHei"", sans-serif;
            background-color: var(--bg-main);
            color: var(--text-primary);
            min-height: 100vh;
            display: flex;
            flex-direction: column;
            overflow-x: hidden;
        }

        /* 顶部导航栏 */
        header {
            background: rgba(21, 29, 47, 0.85);
            backdrop-filter: blur(12px);
            -webkit-backdrop-filter: blur(12px);
            border-bottom: 1px solid var(--border);
            padding: 12px 20px;
            display: flex;
            justify-content: space-between;
            align-items: center;
            position: sticky;
            top: 0;
            z-index: 100;
        }

        .header-title-box {
            display: flex;
            align-items: center;
            gap: 12px;
        }

        .pulse-indicator {
            width: 10px;
            height: 10px;
            border-radius: 50%;
            background-color: var(--success);
            box-shadow: 0 0 0 rgba(16, 185, 129, 0.7);
            animation: pulse 2s infinite;
            transition: background-color 0.3s;
        }

        @keyframes pulse {
            0% { box-shadow: 0 0 0 0 rgba(16, 185, 129, 0.7); }
            70% { box-shadow: 0 0 0 8px rgba(16, 185, 129, 0); }
            100% { box-shadow: 0 0 0 0 rgba(16, 185, 129, 0); }
        }

        .pulse-indicator.offline {
            background-color: var(--danger) !important;
            box-shadow: 0 0 0 rgba(239, 68, 68, 0.7) !important;
            animation: pulse-danger 1.5s infinite;
        }

        @keyframes pulse-danger {
            0% { box-shadow: 0 0 0 0 rgba(239, 68, 68, 0.7); }
            70% { box-shadow: 0 0 0 8px rgba(239, 68, 68, 0); }
            100% { box-shadow: 0 0 0 0 rgba(239, 68, 68, 0); }
        }

        .title-text {
            font-size: 16px;
            font-weight: 700;
            letter-spacing: 0.5px;
            display: flex;
            align-items: center;
            gap: 8px;
        }

        .tag-badge {
            font-size: 11px;
            font-weight: 600;
            padding: 2px 8px;
            border-radius: 12px;
            background: rgba(59, 130, 246, 0.15);
            color: #60A5FA;
            border: 1px solid rgba(59, 130, 246, 0.3);
        }

        .header-actions {
            display: flex;
            align-items: center;
            gap: 10px;
        }

        .btn {
            background: rgba(255, 255, 255, 0.06);
            border: 1px solid var(--border);
            color: var(--text-primary);
            padding: 6px 12px;
            border-radius: 6px;
            font-size: 13px;
            cursor: pointer;
            display: inline-flex;
            align-items: center;
            gap: 6px;
            transition: all 0.2s ease;
            text-decoration: none;
            user-select: none;
        }

        .btn:hover {
            background: rgba(255, 255, 255, 0.12);
            border-color: rgba(255, 255, 255, 0.2);
        }

        .btn:active {
            transform: scale(0.97);
        }

        .btn-primary {
            background: var(--accent);
            border-color: var(--accent);
            color: #FFFFFF;
        }

        .btn-primary:hover {
            background: #2563EB;
            box-shadow: 0 0 12px var(--accent-glow);
        }

        /* 全局断连告警横幅 */
        .global-disconnect-banner {
            background: linear-gradient(90deg, #991B1B, #DC2626);
            color: #FFFFFF;
            padding: 9px 20px;
            font-size: 13px;
            font-weight: 500;
            display: flex;
            align-items: center;
            justify-content: center;
            box-shadow: 0 4px 16px rgba(220, 38, 38, 0.45);
            border-bottom: 1px solid rgba(255, 255, 255, 0.2);
            position: sticky;
            top: 57px;
            z-index: 99;
            animation: slideDown 0.25s ease-out;
        }

        @keyframes slideDown {
            from { transform: translateY(-100%); opacity: 0; }
            to { transform: translateY(0); opacity: 1; }
        }

        .banner-inner {
            display: flex;
            align-items: center;
            gap: 10px;
            max-width: 1200px;
            width: 100%;
        }

        .banner-icon {
            font-size: 18px;
            animation: shake 2s infinite;
        }

        @keyframes shake {
            0%, 100% { transform: rotate(0deg); }
            10%, 30% { transform: rotate(-8deg); }
            20%, 40% { transform: rotate(8deg); }
        }

        /* 状态信息条 */
        .status-bar {
            background: rgba(15, 23, 42, 0.6);
            border-bottom: 1px solid var(--border);
            padding: 6px 20px;
            display: flex;
            justify-content: space-between;
            font-size: 12px;
            color: var(--text-secondary);
        }

        .status-item {
            display: flex;
            align-items: center;
            gap: 6px;
        }

        /* 监控视频容器 */
        main {
            flex: 1;
            padding: 16px;
            max-width: 1920px;
            margin: 0 auto;
            width: 100%;
        }

        /* 网格视图 */
        .camera-grid {
            display: grid;
            gap: 16px;
            grid-template-columns: repeat(auto-fit, minmax(420px, 1fr));
        }

        @media (max-width: 600px) {
            .camera-grid {
                grid-template-columns: 1fr;
                gap: 12px;
            }
            header {
                padding: 10px 14px;
            }
            .title-text {
                font-size: 14px;
            }
            .status-bar {
                padding: 6px 14px;
            }
        }

        /* 摄像头卡片 */
        .camera-card {
            background: var(--bg-card);
            border: 1px solid var(--border);
            border-radius: 10px;
            overflow: hidden;
            display: flex;
            flex-direction: column;
            transition: border-color 0.2s, box-shadow 0.2s;
            position: relative;
        }

        .camera-card:hover {
            border-color: var(--border-active);
            box-shadow: 0 4px 20px rgba(0, 0, 0, 0.4);
        }

        .camera-card-header {
            padding: 8px 12px;
            display: flex;
            justify-content: space-between;
            align-items: center;
            background: rgba(0, 0, 0, 0.25);
            border-bottom: 1px solid var(--border);
        }

        .cam-name-box {
            display: flex;
            align-items: center;
            gap: 8px;
            font-weight: 600;
            font-size: 13px;
        }

        .cam-dot {
            width: 8px;
            height: 8px;
            border-radius: 50%;
            background: var(--success);
            transition: background 0.3s;
        }

        .cam-dot.offline {
            background: var(--danger) !important;
        }

        .cam-badge {
            font-size: 10px;
            font-weight: 700;
            padding: 2px 6px;
            border-radius: 4px;
            letter-spacing: 0.5px;
        }

        .cam-badge.online {
            background: rgba(16, 185, 129, 0.15);
            color: #34D399;
            border: 1px solid rgba(16, 185, 129, 0.3);
        }

        .cam-badge.offline {
            background: rgba(239, 68, 68, 0.15);
            color: #F87171;
            border: 1px solid rgba(239, 68, 68, 0.3);
        }

        .cam-meta {
            font-size: 11px;
            color: var(--text-secondary);
            display: flex;
            gap: 8px;
        }

        .video-wrapper {
            position: relative;
            width: 100%;
            background: #000;
            aspect-ratio: 16 / 9;
            overflow: hidden;
            display: flex;
            align-items: center;
            justify-content: center;
            cursor: pointer;
        }

        .video-wrapper img {
            width: 100%;
            height: 100%;
            object-fit: contain;
            display: block;
            transition: transform 0.2s;
        }

        .video-overlay-actions {
            position: absolute;
            top: 8px;
            right: 8px;
            display: flex;
            gap: 6px;
            opacity: 0;
            transition: opacity 0.2s;
            z-index: 20;
        }

        .camera-card:hover .video-overlay-actions {
            opacity: 1;
        }

        .action-btn-sm {
            background: rgba(0, 0, 0, 0.7);
            color: #fff;
            border: 1px solid rgba(255, 255, 255, 0.2);
            border-radius: 4px;
            padding: 4px 8px;
            font-size: 11px;
            cursor: pointer;
            backdrop-filter: blur(4px);
        }

        .action-btn-sm:hover {
            background: var(--accent);
            border-color: var(--accent);
        }

        /* 摄像头断连/离线遮罩 (通道级/服务级) */
        .camera-offline-overlay {
            position: absolute;
            inset: 0;
            background: rgba(11, 15, 25, 0.88);
            backdrop-filter: blur(6px);
            -webkit-backdrop-filter: blur(6px);
            display: flex;
            flex-direction: column;
            align-items: center;
            justify-content: center;
            gap: 8px;
            z-index: 15;
            color: #F8FAFC;
            transition: opacity 0.2s ease;
            pointer-events: auto;
        }

        .camera-offline-overlay.hidden {
            display: none !important;
        }

        .camera-offline-overlay .offline-icon {
            font-size: 32px;
            line-height: 1;
            margin-bottom: 2px;
            animation: pulse-danger 1.5s infinite;
        }

        .camera-offline-overlay .offline-title {
            font-size: 14px;
            font-weight: 700;
            color: #F87171;
            letter-spacing: 0.5px;
        }

        .camera-offline-overlay .offline-sub {
            font-size: 12px;
            color: #94A3B8;
        }

        /* 聚焦全屏单路视图 */
        .single-view-container {
            display: none;
            flex-direction: column;
            gap: 12px;
            width: 100%;
            height: calc(100vh - 120px);
        }

        .single-view-header {
            display: flex;
            justify-content: space-between;
            align-items: center;
        }

        .single-video-box {
            flex: 1;
            background: #000;
            border-radius: 10px;
            border: 1px solid var(--border);
            position: relative;
            display: flex;
            align-items: center;
            justify-content: center;
            overflow: hidden;
        }

        .single-video-box img {
            max-width: 100%;
            max-height: 100%;
            width: 100%;
            height: 100%;
            object-fit: contain;
        }

        /* 加载中占位 */
        .offline-placeholder {
            position: absolute;
            inset: 0;
            display: flex;
            flex-direction: column;
            align-items: center;
            justify-content: center;
            background: rgba(11, 15, 25, 0.85);
            color: var(--text-secondary);
            font-size: 13px;
            gap: 8px;
            z-index: 10;
            display: none;
        }

        .loading-spinner {
            width: 32px;
            height: 32px;
            border: 3px solid rgba(255, 255, 255, 0.1);
            border-top-color: var(--accent);
            border-radius: 50%;
            animation: spin 1s linear infinite;
        }

        @keyframes spin {
            to { transform: rotate(360deg); }
        }

        /* 页脚 */
        footer {
            text-align: center;
            padding: 12px 20px;
            font-size: 11px;
            color: var(--text-secondary);
            border-top: 1px solid var(--border);
            background: var(--bg-main);
        }
    </style>
</head>
<body>
    <header>
        <div class=""header-title-box"">
            <div class=""pulse-indicator"" id=""pulseDot""></div>
            <div class=""title-text"">
                <span>" + systemTitle + @"</span>
                <span class=""tag-badge"">局域网监控</span>
            </div>
        </div>
        <div class=""header-actions"">
            <button class=""btn"" id=""btnToggleFit"" title=""切换拉伸适应 / 铺满"">
                <span id=""fitIcon"">⛶</span> <span id=""fitText"">适应</span>
            </button>
            <button class=""btn"" id=""btnReloadAll"" title=""刷新所有画面流"">
                ⟳ 刷新
            </button>
            <button class=""btn btn-primary"" id=""btnFullscreen"" title=""全屏查看"">
                ⛶ 全屏
            </button>
        </div>
    </header>

    <!-- 主机断连常驻警示横幅 (服务断开时由 JS 自动激活展示) -->
    <div id=""globalDisconnectBanner"" class=""global-disconnect-banner"" style=""display: none;"">
        <div class=""banner-inner"">
            <span class=""banner-icon"">⚠️</span>
            <span class=""banner-text"">
                <strong>推流服务已断连:</strong> 与监控主机连接中断，请确认推流开关是否开启或网络是否畅通。正在自动重连 (重试第 <span id=""reconnectAttempts"">0</span> 次)...
            </span>
        </div>
    </div>

    <div class=""status-bar"">
        <div class=""status-item"">
            <span>● 连接状态:</span> <strong id=""statusNetwork"" style=""color: var(--success);"">正常</strong>
        </div>
        <div class=""status-item"">
            <span>在线客户端:</span> <strong id=""clientCount"">1</strong>
        </div>
        <div class=""status-item"">
            <span>当前时间:</span> <span id=""currentTime"">--:--:--</span>
        </div>
    </div>

    <main>
        <!-- 网格视图 -->
        <div class=""camera-grid"" id=""cameraGrid"">
            <!-- 动态生成相机卡片 -->
        </div>

        <!-- 单通道大屏聚焦视图 -->
        <div class=""single-view-container"" id=""singleView"">
            <div class=""single-view-header"">
                <div style=""display:flex; align-items:center; gap:10px;"">
                    <button class=""btn"" id=""btnBackToGrid"">← 返回全部通道</button>
                    <h3 id=""singleCamTitle"">通道详情</h3>
                </div>
                <div style=""display:flex; gap:8px;"">
                    <button class=""btn"" id=""btnSnapshotSingle"">📷 截取快照</button>
                </div>
            </div>
            <div class=""single-video-box"" id=""singleVideoBox"">
                <img id=""singleStreamImg"" src="""" alt=""实时监控画面"" />
                <!-- 单路视图离线遮罩 -->
                <div class=""camera-offline-overlay hidden"" id=""singleOfflineOverlay"">
                    <div class=""offline-icon"">🔌</div>
                    <div class=""offline-title"" id=""singleOfflineTitle"">摄像头未连接</div>
                    <div class=""offline-sub"" id=""singleOfflineSub"">通道信号中断或推流已停止</div>
                </div>
                <div class=""offline-placeholder"" id=""singleLoading"">
                    <div class=""loading-spinner""></div>
                    <span>正在连接视频流...</span>
                </div>
            </div>
        </div>
    </main>

    <footer>
        " + systemTitle + @" · 局域网实时流媒体服务已就绪 · 端口: " + port + @"
    </footer>

    <script>
        let isSingleMode = false;
        let currentSingleIndex = 0;
        let objectFitMode = 'contain';
        let camerasData = [];

        // 服务与网络连接状态追踪
        let isServerOnline = true;
        let consecutiveFailures = 0;
        let reconnectAttempts = 0;
        let wasOffline = false;

        function updateClock() {
            const now = new Date();
            document.getElementById('currentTime').innerText = now.toLocaleTimeString();
        }
        setInterval(updateClock, 1000);
        updateClock();

        // 快速心跳与状态轮询 (1500ms 间隔，配备 1500ms 快速超时中断)
        async function fetchStatus() {
            const controller = new AbortController();
            const timeoutId = setTimeout(() => controller.abort(), 1500);

            try {
                const res = await fetch('/api/status?t=' + Date.now(), { signal: controller.signal });
                clearTimeout(timeoutId);

                if (!res.ok) throw new Error('HTTP ' + res.status);
                const data = await res.json();

                // 若主机已手动停止推流
                if (data.server && data.server.isStreaming === false) {
                    handleServerDisconnect('推流已在主机端停止');
                    return;
                }

                // 连接已恢复
                if (!isServerOnline || wasOffline) {
                    handleServerReconnect();
                }

                isServerOnline = true;
                consecutiveFailures = 0;
                reconnectAttempts = 0;
                document.getElementById('globalDisconnectBanner').style.display = 'none';
                document.getElementById('statusNetwork').innerText = '正常';
                document.getElementById('statusNetwork').style.color = 'var(--success)';
                const pulse = document.getElementById('pulseDot');
                if (pulse) pulse.className = 'pulse-indicator';

                if (data.server && data.server.clientCount !== undefined) {
                    document.getElementById('clientCount').innerText = data.server.clientCount;
                }

                // 细粒度更新通道状态
                if (data.cameras) {
                    updateCamerasState(data.cameras);
                }
            } catch (err) {
                clearTimeout(timeoutId);
                consecutiveFailures++;
                if (consecutiveFailures >= 2) {
                    handleServerDisconnect('无法连接到监控主机');
                }
            }
        }

        // 处理主机服务断开
        function handleServerDisconnect(reason) {
            isServerOnline = false;
            wasOffline = true;
            reconnectAttempts++;

            const banner = document.getElementById('globalDisconnectBanner');
            if (banner) banner.style.display = 'flex';
            const attElem = document.getElementById('reconnectAttempts');
            if (attElem) attElem.innerText = reconnectAttempts;

            const st = document.getElementById('statusNetwork');
            if (st) {
                st.innerText = '已断开';
                st.style.color = 'var(--danger)';
            }

            const pulse = document.getElementById('pulseDot');
            if (pulse) pulse.className = 'pulse-indicator offline';

            // 让所有卡片显示“服务已断开”遮罩
            camerasData.forEach(cam => {
                showCardOverlay(cam.index, '🔌', '推流服务断开', '正在尝试自动重连...');
            });

            if (isSingleMode) {
                showSingleOverlay('🔌', '推流服务断开', '正在尝试自动重连...');
            }
        }

        // 处理主机重新上线恢复
        function handleServerReconnect() {
            wasOffline = false;
            // 自动刷新所有视频流，重新建立长连接
            reloadAllStreams();
        }

        // 重新拉取所有视频流（加时间戳防缓存）
        function reloadAllStreams() {
            const t = Date.now();
            if (isSingleMode) {
                const singleImg = document.getElementById('singleStreamImg');
                if (singleImg) singleImg.src = '/stream/' + currentSingleIndex + '?t=' + t;
            }
            camerasData.forEach(cam => {
                const img = document.getElementById('stream-' + cam.index);
                if (img) img.src = '/stream/' + cam.index + '?t=' + t;
            });
        }

        // 细粒度更新通道状态与遮罩（不破坏已有 DOM，杜绝画面刷新闪烁）
        function updateCamerasState(newCameras) {
            if (newCameras.length !== camerasData.length) {
                camerasData = newCameras;
                renderCameras();
                return;
            }

            newCameras.forEach((cam, idx) => {
                camerasData[idx] = cam;
                const card = document.getElementById('cam-card-' + cam.index);
                if (!card) return;

                // 通道在线条件: isConnected 为真且最近产生帧
                const isOnline = cam.isConnected === true && (cam.lastFrameAgeMs === undefined || cam.lastFrameAgeMs < 4000);
                const badge = card.querySelector('.cam-badge');
                const dot = card.querySelector('.cam-dot');
                const meta = card.querySelector('.cam-meta');

                if (badge) {
                    badge.className = 'cam-badge ' + (isOnline ? 'online' : 'offline');
                    badge.innerText = isOnline ? '在线' : '离线';
                }
                if (dot) {
                    dot.className = 'cam-dot ' + (isOnline ? '' : 'offline');
                }
                if (meta) {
                    meta.innerHTML = isOnline
                        ? `<span>${cam.width && cam.height ? cam.width + '×' + cam.height : ''}</span><span>${cam.fps ? Math.round(cam.fps) + ' FPS' : ''}</span>`
                        : `<span style=""color:var(--danger)"">离线 · 0 FPS</span>`;
                }

                if (!isServerOnline) {
                    showCardOverlay(cam.index, '🔌', '推流服务断开', '正在尝试自动重连...');
                } else if (!isOnline) {
                    showCardOverlay(cam.index, '📷', '摄像头未连接', '通道信号中断或设备已拔出');
                } else {
                    hideCardOverlay(cam.index);
                }

                // 同步单路聚焦视图
                if (isSingleMode && currentSingleIndex === cam.index) {
                    if (!isServerOnline) {
                        showSingleOverlay('🔌', '推流服务断开', '正在尝试自动重连...');
                    } else if (!isOnline) {
                        showSingleOverlay('📷', '摄像头未连接', '通道 ' + (cam.index + 1) + ' 已掉线或无信号');
                    } else {
                        hideSingleOverlay();
                    }
                }
            });
        }

        function showCardOverlay(camIndex, icon, title, sub) {
            const overlay = document.getElementById('overlay-' + camIndex);
            if (overlay) {
                overlay.classList.remove('hidden');
                const t = document.getElementById('overlay-title-' + camIndex);
                if (t) t.innerText = title;
                const s = document.getElementById('overlay-sub-' + camIndex);
                if (s) s.innerText = sub;
                const i = overlay.querySelector('.offline-icon');
                if (i) i.innerText = icon;
            }
        }

        function hideCardOverlay(camIndex) {
            const overlay = document.getElementById('overlay-' + camIndex);
            if (overlay) overlay.classList.add('hidden');
        }

        function showSingleOverlay(icon, title, sub) {
            const overlay = document.getElementById('singleOfflineOverlay');
            if (overlay) {
                overlay.classList.remove('hidden');
                const t = document.getElementById('singleOfflineTitle');
                if (t) t.innerText = title;
                const s = document.getElementById('singleOfflineSub');
                if (s) s.innerText = sub;
                const i = overlay.querySelector('.offline-icon');
                if (i) i.innerText = icon;
            }
        }

        function hideSingleOverlay() {
            const overlay = document.getElementById('singleOfflineOverlay');
            if (overlay) overlay.classList.add('hidden');
        }

        // 渲染所有摄像头卡片
        function renderCameras() {
            const grid = document.getElementById('cameraGrid');
            if (camerasData.length === 0) {
                grid.innerHTML = '<div style=""text-align:center; padding:50px; grid-column:1/-1; color:#94A3B8;"">当前暂无活跃摄像头或通道未启用</div>';
                return;
            }

            grid.innerHTML = '';
            camerasData.forEach(cam => {
                const card = document.createElement('div');
                card.className = 'camera-card';
                card.id = 'cam-card-' + cam.index;

                const isOnline = cam.isConnected === true && (cam.lastFrameAgeMs === undefined || cam.lastFrameAgeMs < 4000);
                const streamUrl = '/stream/' + cam.index;

                card.innerHTML = `
                    <div class=""camera-card-header"">
                        <div class=""cam-name-box"">
                            <div class=""cam-dot ${isOnline ? '' : 'offline'}""></div>
                            <span>${cam.name || '通道 ' + (cam.index + 1)}</span>
                            <span class=""cam-badge ${isOnline ? 'online' : 'offline'}"">${isOnline ? '在线' : '离线'}</span>
                        </div>
                        <div class=""cam-meta"">
                            ${isOnline ? `<span>${cam.width && cam.height ? cam.width + '×' + cam.height : ''}</span><span>${cam.fps ? Math.round(cam.fps) + ' FPS' : ''}</span>` : `<span style=""color:var(--danger)"">离线 · 0 FPS</span>`}
                        </div>
                    </div>
                    <div class=""video-wrapper"" onclick=""showSingle(${cam.index})"">
                        <img id=""stream-${cam.index}"" src=""${streamUrl}"" style=""object-fit: ${objectFitMode};"" alt=""${cam.name}"" onerror=""onStreamError(${cam.index})"" />
                        <div class=""video-overlay-actions"" onclick=""event.stopPropagation();"">
                            <button class=""action-btn-sm"" onclick=""downloadSnapshot(${cam.index})"">📷 快照</button>
                            <button class=""action-btn-sm"" onclick=""showSingle(${cam.index})"">⛶ 放大</button>
                        </div>
                        <!-- 断连/掉线卡片蒙层 -->
                        <div class=""camera-offline-overlay ${isOnline && isServerOnline ? 'hidden' : ''}"" id=""overlay-${cam.index}"">
                            <div class=""offline-icon"">🔌</div>
                            <div class=""offline-title"" id=""overlay-title-${cam.index}"">${!isServerOnline ? '推流服务断开' : '摄像头未连接'}</div>
                            <div class=""offline-sub"" id=""overlay-sub-${cam.index}"">${!isServerOnline ? '正在尝试自动重连...' : '通道信号中断或设备已拔出'}</div>
                        </div>
                        <!-- 缓冲中占位 -->
                        <div class=""offline-placeholder"" id=""placeholder-${cam.index}"">
                            <div class=""loading-spinner""></div>
                            <span>视频流加载中...</span>
                        </div>
                    </div>
                `;
                grid.appendChild(card);
            });
        }

        function onStreamError(index) {
            const ph = document.getElementById('placeholder-' + index);
            if (ph) ph.style.display = 'flex';
            setTimeout(() => {
                const img = document.getElementById('stream-' + index);
                if (img) {
                    img.src = '/stream/' + index + '?retry=' + Date.now();
                }
                if (ph) ph.style.display = 'none';
            }, 2500);
        }

        function showSingle(index) {
            currentSingleIndex = index;
            isSingleMode = true;
            document.getElementById('cameraGrid').style.display = 'none';
            document.getElementById('singleView').style.display = 'flex';
            const cam = camerasData.find(c => c.index === index) || { name: '通道 ' + (index + 1) };
            document.getElementById('singleCamTitle').innerText = cam.name + ' - 实时放大监控';
            const singleImg = document.getElementById('singleStreamImg');
            singleImg.style.objectFit = objectFitMode;
            singleImg.src = '/stream/' + index + '?t=' + Date.now();

            const isOnline = cam.isConnected === true && (cam.lastFrameAgeMs === undefined || cam.lastFrameAgeMs < 4000);
            if (!isServerOnline) {
                showSingleOverlay('🔌', '推流服务断开', '正在尝试自动重连...');
            } else if (!isOnline) {
                showSingleOverlay('📷', '摄像头未连接', '通道 ' + (cam.index + 1) + ' 已掉线或无信号');
            } else {
                hideSingleOverlay();
            }
        }

        document.getElementById('btnBackToGrid').addEventListener('click', () => {
            isSingleMode = false;
            document.getElementById('singleView').style.display = 'none';
            document.getElementById('cameraGrid').style.display = 'grid';
            document.getElementById('singleStreamImg').src = '';
            hideSingleOverlay();
        });

        // 快照下载
        function downloadSnapshot(index) {
            const a = document.createElement('a');
            a.href = '/snapshot/' + index + '?t=' + Date.now();
            a.download = 'snapshot_camera_' + (index + 1) + '_' + Date.now() + '.jpg';
            document.body.appendChild(a);
            a.click();
            document.body.removeChild(a);
        }

        document.getElementById('btnSnapshotSingle').addEventListener('click', () => {
            downloadSnapshot(currentSingleIndex);
        });

        // 切换适应/铺满
        document.getElementById('btnToggleFit').addEventListener('click', () => {
            objectFitMode = objectFitMode === 'contain' ? 'cover' : 'contain';
            document.getElementById('fitText').innerText = objectFitMode === 'contain' ? '适应' : '铺满';
            document.querySelectorAll('.video-wrapper img, #singleStreamImg').forEach(img => {
                img.style.objectFit = objectFitMode;
            });
        });

        // 刷新所有画面流
        document.getElementById('btnReloadAll').addEventListener('click', reloadAllStreams);

        // 全屏
        document.getElementById('btnFullscreen').addEventListener('click', () => {
            if (!document.fullscreenElement) {
                document.documentElement.requestFullscreen().catch(() => {});
            } else {
                document.exitFullscreen().catch(() => {});
            }
        });

        // 初始化加载与高频心跳定时器 (1500ms 快速探测断连与恢复)
        fetchStatus();
        setInterval(fetchStatus, 1500);
    </script>
</body>
</html>";
        }
    }
}
