using System;
using System.Diagnostics;
using System.Threading;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using OpenCvSharp;
using Connector_Vision.Helpers;
using Connector_Vision.Models;

namespace Connector_Vision.Services
{
    public class CameraService : IDisposable
    {
        private VideoCapture _capture;
        private Thread _captureThread;
        private volatile bool _isRunning;

        // Pre-allocated buffers (no Clone/new Mat per frame)
        private readonly object _frameLock = new object();
        private Mat _latestFrame = new Mat();

        private readonly object _displayLock = new object();
        private Mat _displayBuffer = new Mat();

        // WriteableBitmap for zero-allocation frame display
        private WriteableBitmap _writeableBitmap;
        private volatile bool _uiUpdatePending;

        // FPS tracking
        private int _frameCount;
        private Stopwatch _fpsStopwatch = new Stopwatch();
        private double _currentFps;

        public event Action<WriteableBitmap> FrameReadyUI;
        public event Action<double> FpsUpdated;
        public bool IsRunning => _isRunning;
        public int CameraIndex { get; private set; } = -1;
        public double CurrentFps => _currentFps;
        public int FrameWidth { get; private set; }
        public int FrameHeight { get; private set; }
        public string CameraInfo { get; private set; } = "";

        public CameraService(Dispatcher dispatcher)
        {
            _dispatcher = dispatcher;
        }

        private Dispatcher _dispatcher;

        private string DecodeFourCC(int fourcc)
        {
            return new string(new char[] {
                (char)(fourcc & 0xFF),
                (char)((fourcc >> 8) & 0xFF),
                (char)((fourcc >> 16) & 0xFF),
                (char)((fourcc >> 24) & 0xFF)
            });
        }

        private double MeasureActualFps(VideoCapture cap, int testFrames)
        {
            var frame = new Mat();
            cap.Read(frame);

            var sw = Stopwatch.StartNew();
            int count = 0;
            for (int i = 0; i < testFrames; i++)
            {
                if (cap.Read(frame) && !frame.Empty())
                    count++;
            }
            sw.Stop();
            frame.Dispose();

            if (count == 0 || sw.ElapsedMilliseconds == 0) return 0;
            return count * 1000.0 / sw.ElapsedMilliseconds;
        }

        public void Start(int cameraIndex)
        {
            Start(cameraIndex, "Auto");
        }

        public void Start(int cameraIndex, string resolution)
        {
            if (_isRunning)
                Stop();

            CameraIndex = cameraIndex;
            int mjpgCode = VideoWriter.FourCC('M', 'J', 'P', 'G');

            if (resolution != null && resolution != "Auto")
            {
                var parts = resolution.Split('x');
                if (parts.Length == 2 && int.TryParse(parts[0], out int reqW) && int.TryParse(parts[1], out int reqH))
                {
                    Debug.WriteLine($"[Camera] Manual resolution: {reqW}x{reqH} MJPG");
                    _capture = new VideoCapture(cameraIndex, VideoCaptureAPIs.DSHOW);
                    if (!_capture.IsOpened())
                        throw new InvalidOperationException($"Failed to open camera index {cameraIndex}");

                    _capture.Set(VideoCaptureProperties.FourCC, mjpgCode);
                    _capture.Set(VideoCaptureProperties.FrameWidth, reqW);
                    _capture.Set(VideoCaptureProperties.FrameHeight, reqH);
                    _capture.Set(VideoCaptureProperties.Fps, 30);
                    _capture.Set(VideoCaptureProperties.BufferSize, 1);
                }
                else
                {
                    resolution = "Auto";
                }
            }

            if (resolution == null || resolution == "Auto")
            {
                _capture = new VideoCapture(cameraIndex, VideoCaptureAPIs.DSHOW);
                if (!_capture.IsOpened())
                    throw new InvalidOperationException($"Failed to open camera index {cameraIndex}");

                _capture.Set(VideoCaptureProperties.FourCC, mjpgCode);
                _capture.Set(VideoCaptureProperties.FrameWidth, 1920);
                _capture.Set(VideoCaptureProperties.FrameHeight, 1080);
                _capture.Set(VideoCaptureProperties.Fps, 30);
                _capture.Set(VideoCaptureProperties.BufferSize, 1);

                int actualFourCC = (int)_capture.Get(VideoCaptureProperties.FourCC);
                int w = (int)_capture.Get(VideoCaptureProperties.FrameWidth);
                int h = (int)_capture.Get(VideoCaptureProperties.FrameHeight);
                string codec = DecodeFourCC(actualFourCC);
                Debug.WriteLine($"[Camera] Attempt 1: DSHOW 1920x1080 MJPG → Got {codec} {w}x{h}");

                double measuredFps = MeasureActualFps(_capture, 10);
                Debug.WriteLine($"[Camera] Measured FPS at {w}x{h} {codec}: {measuredFps:F1}");

                if (measuredFps < 15)
                {
                    Debug.WriteLine("[Camera] FPS too low at 1080p, trying 1280x720...");
                    _capture.Release();
                    _capture.Dispose();

                    _capture = new VideoCapture(cameraIndex, VideoCaptureAPIs.DSHOW);
                    _capture.Set(VideoCaptureProperties.FourCC, mjpgCode);
                    _capture.Set(VideoCaptureProperties.FrameWidth, 1280);
                    _capture.Set(VideoCaptureProperties.FrameHeight, 720);
                    _capture.Set(VideoCaptureProperties.Fps, 30);
                    _capture.Set(VideoCaptureProperties.BufferSize, 1);

                    actualFourCC = (int)_capture.Get(VideoCaptureProperties.FourCC);
                    w = (int)_capture.Get(VideoCaptureProperties.FrameWidth);
                    h = (int)_capture.Get(VideoCaptureProperties.FrameHeight);
                    codec = DecodeFourCC(actualFourCC);

                    measuredFps = MeasureActualFps(_capture, 10);
                    Debug.WriteLine($"[Camera] Measured FPS at {w}x{h} {codec}: {measuredFps:F1}");
                }

                if (measuredFps < 15)
                {
                    Debug.WriteLine("[Camera] FPS too low at 720p, falling back to 640x480...");
                    _capture.Release();
                    _capture.Dispose();

                    _capture = new VideoCapture(cameraIndex, VideoCaptureAPIs.DSHOW);
                    _capture.Set(VideoCaptureProperties.FrameWidth, 640);
                    _capture.Set(VideoCaptureProperties.FrameHeight, 480);
                    _capture.Set(VideoCaptureProperties.Fps, 30);
                    _capture.Set(VideoCaptureProperties.BufferSize, 1);

                    actualFourCC = (int)_capture.Get(VideoCaptureProperties.FourCC);
                    w = (int)_capture.Get(VideoCaptureProperties.FrameWidth);
                    h = (int)_capture.Get(VideoCaptureProperties.FrameHeight);
                    codec = DecodeFourCC(actualFourCC);

                    measuredFps = MeasureActualFps(_capture, 10);
                    Debug.WriteLine($"[Camera] Measured FPS at {w}x{h} {codec}: {measuredFps:F1}");
                }
            }

            if (!_capture.IsOpened())
                throw new InvalidOperationException($"Failed to open camera index {cameraIndex}");

            FrameWidth = (int)_capture.Get(VideoCaptureProperties.FrameWidth);
            FrameHeight = (int)_capture.Get(VideoCaptureProperties.FrameHeight);
            int finalFourCC = (int)_capture.Get(VideoCaptureProperties.FourCC);
            string finalCodec = DecodeFourCC(finalFourCC);
            CameraInfo = $"{FrameWidth}x{FrameHeight} {finalCodec}";

            Debug.WriteLine($"[Camera] FINAL: {CameraInfo}");

            _dispatcher.Invoke(() =>
            {
                _writeableBitmap = BitmapHelper.CreateWriteableBitmap(FrameWidth, FrameHeight);
            });

            _uiUpdatePending = false;
            _frameCount = 0;
            _currentFps = 0;
            _fpsStopwatch.Restart();

            _isRunning = true;
            _captureThread = new Thread(CaptureLoop)
            {
                IsBackground = true,
                Name = "CameraCapture",
                Priority = ThreadPriority.AboveNormal
            };
            _captureThread.Start();
        }

        public void Stop()
        {
            _isRunning = false;
            _captureThread?.Join(2000);
            _captureThread = null;

            _capture?.Release();
            _capture?.Dispose();
            _capture = null;

            _fpsStopwatch.Stop();
        }

        public Mat CaptureSnapshot()
        {
            lock (_frameLock)
            {
                if (_latestFrame != null && !_latestFrame.Empty())
                    return _latestFrame.Clone();
                return null;
            }
        }

        private void CaptureLoop()
        {
            var frame = new Mat();

            while (_isRunning)
            {
                try
                {
                    if (_capture == null || !_capture.IsOpened())
                    {
                        Thread.Sleep(100);
                        continue;
                    }

                    if (!_capture.Read(frame) || frame.Empty())
                    {
                        Thread.Sleep(1);
                        continue;
                    }

                    lock (_frameLock)
                    {
                        frame.CopyTo(_latestFrame);
                    }

                    _frameCount++;
                    if (_fpsStopwatch.ElapsedMilliseconds >= 1000)
                    {
                        _currentFps = _frameCount * 1000.0 / _fpsStopwatch.ElapsedMilliseconds;
                        _frameCount = 0;
                        _fpsStopwatch.Restart();

                        double fps = _currentFps;
                        _dispatcher.BeginInvoke(new Action(() =>
                        {
                            FpsUpdated?.Invoke(fps);
                        }), DispatcherPriority.Background);
                    }

                    if (!_uiUpdatePending)
                    {
                        _uiUpdatePending = true;
                        lock (_displayLock)
                        {
                            frame.CopyTo(_displayBuffer);
                        }

                        _dispatcher.BeginInvoke(new Action(() =>
                        {
                            try
                            {
                                lock (_displayLock)
                                {
                                    BitmapHelper.UpdateWriteableBitmap(_writeableBitmap, _displayBuffer);
                                }
                                FrameReadyUI?.Invoke(_writeableBitmap);
                            }
                            finally
                            {
                                _uiUpdatePending = false;
                            }
                        }), DispatcherPriority.Render);
                    }
                }
                catch (Exception)
                {
                    Thread.Sleep(50);
                }
            }
            frame.Dispose();
        }

        public void ReadCameraProperties(InspectionSettings settings)
        {
            if (_capture == null || !_capture.IsOpened()) return;

            settings.CamAutoFocus = _capture.Get(VideoCaptureProperties.AutoFocus);
            settings.CamAutoExposure = _capture.Get(VideoCaptureProperties.AutoExposure);
            settings.CamFocus = _capture.Get(VideoCaptureProperties.Focus);
            settings.CamExposure = _capture.Get(VideoCaptureProperties.Exposure);
            settings.CamBrightness = _capture.Get(VideoCaptureProperties.Brightness);
            settings.CamContrast = _capture.Get(VideoCaptureProperties.Contrast);
            settings.CamSaturation = _capture.Get(VideoCaptureProperties.Saturation);
            settings.CamGain = _capture.Get(VideoCaptureProperties.Gain);
            settings.CamWhiteBalance = _capture.Get(VideoCaptureProperties.WhiteBalanceBlueU);
            settings.CamSharpness = _capture.Get(VideoCaptureProperties.Sharpness);
            settings.CamBacklightComp = _capture.Get(VideoCaptureProperties.BackLight);
            settings.CamPropertiesSaved = true;

            Debug.WriteLine($"[Camera] Read properties: Focus={settings.CamFocus}, Exposure={settings.CamExposure}, " +
                $"AutoFocus={settings.CamAutoFocus}, AutoExposure={settings.CamAutoExposure}, " +
                $"Brightness={settings.CamBrightness}, Contrast={settings.CamContrast}");
        }

        public void ApplyCameraProperties(InspectionSettings settings)
        {
            if (_capture == null || !_capture.IsOpened() || !settings.CamPropertiesSaved) return;

            Debug.WriteLine($"[Camera] Applying saved properties: Focus={settings.CamFocus}, Exposure={settings.CamExposure}, " +
                $"AutoFocus={settings.CamAutoFocus}, AutoExposure={settings.CamAutoExposure}");

            _capture.Set(VideoCaptureProperties.AutoFocus, settings.CamAutoFocus);
            _capture.Set(VideoCaptureProperties.AutoExposure, settings.CamAutoExposure);
            _capture.Set(VideoCaptureProperties.Focus, settings.CamFocus);
            _capture.Set(VideoCaptureProperties.Exposure, settings.CamExposure);
            _capture.Set(VideoCaptureProperties.Brightness, settings.CamBrightness);
            _capture.Set(VideoCaptureProperties.Contrast, settings.CamContrast);
            _capture.Set(VideoCaptureProperties.Saturation, settings.CamSaturation);
            _capture.Set(VideoCaptureProperties.Gain, settings.CamGain);
            _capture.Set(VideoCaptureProperties.WhiteBalanceBlueU, settings.CamWhiteBalance);
            _capture.Set(VideoCaptureProperties.Sharpness, settings.CamSharpness);
            _capture.Set(VideoCaptureProperties.BackLight, settings.CamBacklightComp);
        }

        public void Dispose()
        {
            Stop();
            _latestFrame?.Dispose();
            _displayBuffer?.Dispose();
        }
    }
}
