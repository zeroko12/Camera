# Camera Monitor (高性能多路摄像头实时监控与流媒体系统)

[![.NET](https://img.shields.io/badge/.NET-8.0--windows-512BD4?logo=dotnet)](https://dotnet.microsoft.com/)
[![WPF](https://img.shields.io/badge/GUI-WPF%20%2F%20MaterialDesign-0078D7)](https://github.com/MaterialDesignInXAML/MaterialDesignInXamlToolkit)
[![OpenCV](https://img.shields.io/badge/Vision-OpenCvSharp4-5C3EE8?logo=opencv)](https://github.com/shimat/opencvsharp)
[![License](https://img.shields.io/badge/License-MIT-green.svg)](#)

一款基于 **.NET 8 (WPF)** 与 **OpenCvSharp** 开发的高性能多路工业/USB摄像头实时监控、录像与网络推流工作站。专为工控现场、工业检测、实验室监控以及安防采集场景设计，提供极致的性能、平滑的渲染与完备的自动化存储管控。

---

## 🌟 核心特性

### 1. 多路高速采集与拓扑自愈
* **多底层采集引擎**：原生支持 DirectShow、MediaFoundation 与 OpenCV 物理硬件级采集。
* **USB 拓扑与带宽自适应**：支持多路高帧率、高分辨率（1080P/2K/4K）并行采集，具备设备热插拔感知与故障自动重连能力。
* **画面自由变换**：支持各路独立拉伸适配（Uniform / UniformToFill / Fill）、水平镜像与垂直翻转。

### 2. Direct3D HLSL 硬件加速着色器
* **零 CPU 调色**：集成自定义 Direct3D HLSL 像素着色器（Pixel Shader），对比度与亮度调节完全交由 GPU 硬件流水线计算，消除 CPU 密集图像运算开销。
* **自适应细节增强**：集成 CLAHE 自适应直方图均衡化算子，在复杂光照、暗光环境下快速提升图像边缘和局部对比度。

### 3. 多硬件编码视频录制流水线
* **多架构硬件编码**：支持 NVIDIA NVENC、Intel QuickSync (QSV)、AMD AMF 硬件编解码器自动推断与并发会话管理；提供高性能 CPU FFmpeg 作为高兼容性安全兜底。
* **分段与元数据叠加**：支持定时自动分段录制、自定义录像分辨率与帧率，并在帧上低延迟烧录时间戳与通道水印。

### 4. 磁盘智能自愈管理 (StorageManager)
* **高低水位回差机制 (Hysteresis Watermark)**：引入工业级高低水位回差算法，触发空间清理时一次性释放充裕缓冲，彻底终结频繁振荡清理（Thrashing）。
* **灵活空间策略**：支持【循环自动覆盖旧文件】与【达阈值安全停录】双向策略。
* **物理安全红线**：即便在循环覆盖模式下，当检测到无旧文件可删且磁盘逼近极限红线（< 150MB）时，自动触发紧急停止录像以保护数据与系统稳定。
* **活跃文件白名单**：正在写入的分段与播放锁定的文件受白名单保护，绝不被清理任务误删。

### 5. 局域网跨设备推流与 Web 监控看板
* **HTTP / RTSP 双协议**：内置自研轻量级高性能流媒体服务，支持 HTTP (MJPEG) 与 RTSP 协议推流，端口与编码质量自由配置。
* **免插件 Web 监控看板**：局域网内任意手机、平板、工控机无需安装任何客户端软件，使用标准现代浏览器即可访问实时响应式多路监控看板。
* **健康心跳探测**：前端看板集成毫秒级断线自动探测、重试与网络状态徽标展示。

### 6. 现代化工业交互体验
* **沉浸式自动隐藏**：支持鼠标离开窗口自动收起菜单栏、鼠标静止 2.5 秒自动平滑淡出标题栏，还原最纯净的画面监视视野。
* **置顶与鼠标穿透**：支持窗口置顶、无级透明度调节以及全局鼠标穿透（Click-Through），方便工控调试时作为浮动监控层。
* **主题与样式**：基于 Material Design 设计语言，支持深色（Dark）/浅色（Light）模式无缝切换。

### 7. 集中配置与工程化管理
* **跨会话持久化**：统一通过单例 `AppSettingsService` 进行 JSON 本地配置（`%APPDATA%\CameraMonitor\settings.json`），支持防抖落盘与旧版 `App.config` 迁移。
* **标题与名称自定义**：支持通过配置文件直接自定义系统标题、各路相机别名，便于工程化部署与定制。

---

## 🛠️ 技术栈

* **运行时框架**：.NET 8.0 (Windows Desktop)
* **UI 与架构**：WPF, Prism.DryIoc (MVVM), MaterialDesignThemes
* **计算机视觉**：OpenCvSharp4, OpenCvSharp4.runtime.win
* **流媒体与视频处理**：FFmpeg (Git LFS 托管), 自研 HTTP/RTSP 流媒体服务
* **着色器加速**：Direct3D HLSL Pixel Shader 2.0/3.0

---

## 🚀 快速上手

### 环境要求
1. **操作系统**：Windows 10 / Windows 11 (x64)
2. **开发环境**：Visual Studio 2022 (v17.8+) 或安装了 .NET 8 SDK 的 VS Code
3. **Git LFS**：拉取源码前请确保本地已安装 [Git LFS](https://git-lfs.github.com/)：
   ```bash
   git lfs install
   ```

### 克隆与编译
```bash
# 1. 克隆代码仓库（自动拉取 LFS 二进制依赖）
git clone https://github.com/zeroko12/Camera.git
cd Camera

# 2. 还原并构建项目
dotnet restore
dotnet build -c Release

# 3. 运行程序
dotnet run --project camera/camera.csproj -c Release
```

---

## ⚙️ 配置文件说明

程序优先加载 `%APPDATA%\CameraMonitor\settings.json`，未初始化时自动同步 `camera/App.config`。

主要可配置项如下：
* `SystemTitle` / `Title`：监控程序主窗口与标题栏标题。
* `OverwriteOldFiles`：`true` 开启循环覆盖旧录像；`false` 达到阈值即停止。
* `DiskSpaceThreshold`：低水位触发阈值（单位：MB，默认 `10240` 即 10GB）。
* `SaveDirectory`：视频录制存储绝对路径或相对路径。
* `IsStreamingEnabled`：局域网推流总开关（默认 `false` 关闭）。
* `StreamProtocol`：推流协议（`HTTP` 或 `RTSP`）。
* `StreamPort`：推流监听端口（默认 `8080`）。
* `MaxNvencSessions` / `MaxQsvSessions` / `MaxAmfSessions`：各硬件加速通道并发会话上限限制。

---

## 📄 开源许可证

本项目基于 [MIT License](LICENSE) 开源。
