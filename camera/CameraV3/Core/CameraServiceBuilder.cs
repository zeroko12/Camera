using System;
using camera.Capture;
using camera.CameraV3.Abstractions;
using camera.CameraV3.Pipeline;
using camera.CameraV3.Preview;
using camera.CameraV3.Recording;

namespace camera.CameraV3.Core
{
    public class CameraServiceBuilder
    {
        private readonly CameraServiceOptions _options = new CameraServiceOptions();
        private ICameraCapture? _capture;
        private IFramePipeline? _pipeline;
        private IPreviewController? _previewController;
        private IVideoRecorder? _recorder;

        public static CameraServiceBuilder Create()
        {
            return new CameraServiceBuilder();
        }

        public CameraServiceBuilder ConfigureOptions(Action<CameraServiceOptions> configure)
        {
            configure(_options);
            return this;
        }

        public CameraServiceBuilder UseCapture(ICameraCapture capture)
        {
            _capture = capture;
            return this;
        }

        public CameraServiceBuilder UseAutoCapture(int cameraIndex)
        {
            _options.CameraIndex = cameraIndex;
            return this;
        }

        public CameraServiceBuilder UsePipeline(IFramePipeline pipeline)
        {
            _pipeline = pipeline;
            return this;
        }

        public CameraServiceBuilder UseDefaultPipeline(Action<FramePipelineBuilder>? configure = null)
        {
            var builder = FramePipelineBuilder.Create();
            configure?.Invoke(builder);
            _pipeline = builder.Build();
            return this;
        }

        public CameraServiceBuilder UsePreview(IPreviewController previewController)
        {
            _previewController = previewController;
            return this;
        }

        public CameraServiceBuilder UseDefaultPreview()
        {
            _previewController = new PreviewController();
            return this;
        }

        public CameraServiceBuilder UseRecorder(IVideoRecorder recorder)
        {
            _recorder = recorder;
            return this;
        }

        public CameraServiceBuilder UseDefaultRecorder()
        {
            var recordingOptions = new RecordingOptions
            {
                CameraIndex = _options.CameraIndex,
                Width = _options.Width,
                Height = _options.Height,
                Fps = _options.Fps,
                RecordFps = _options.RecordFps,
                SaveDirectory = _options.SaveDirectory,
                FileNamePattern = _options.FileNamePattern,
                SegmentDuration = TimeSpan.FromMinutes(_options.SegmentMinutes)
            };
            _recorder = new FFmpegVideoRecorder(recordingOptions);
            return this;
        }

        public CameraServiceV3 Build()
        {
            return new CameraServiceV3(_options, _capture, _pipeline, _previewController, _recorder);
        }
    }
}
