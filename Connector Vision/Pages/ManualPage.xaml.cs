using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using OpenCvSharp;
using Connector_Vision.Helpers;
using Connector_Vision.Models;
using Connector_Vision.Services;

namespace Connector_Vision.Pages
{
    public partial class ManualPage : Page
    {
        private CameraService _cameraService;
        private InspectionService _inspectionService;
        private InspectionSettings _settings;

        private DispatcherTimer _liveTimer;
        private bool _isLive;
        private bool _isInspecting;
        private bool _isSubscribed;

        public ManualPage(CameraService cameraService, InspectionService inspectionService,
            InspectionSettings settings)
        {
            InitializeComponent();
            _cameraService = cameraService;
            _inspectionService = inspectionService;
            _settings = settings;

            _liveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            _liveTimer.Tick += LiveTick;

            SliderThreshold.Value = _settings.GapThreshold;
            LoadGapWidthTexts();
            SubscribeCamera();
            ZoomPanHelper.Setup(ImgOriginal);
            ZoomPanHelper.Setup(ImgGrayscale);
            ZoomPanHelper.Setup(ImgProfiles);
            ZoomPanHelper.Setup(ImgThreshold);
        }

        public void RefreshSettings(InspectionSettings settings)
        {
            _settings = settings;
            SliderThreshold.Value = _settings.GapThreshold;
            LoadGapWidthTexts();
            SubscribeCamera();
        }

        public void StopPreview()
        {
            _isLive = false;
            _liveTimer.Stop();
        }

        private void SubscribeCamera()
        {
            if (_isSubscribed) return;
            _cameraService.FrameReadyUI += OnFrameReady;
            _isSubscribed = true;
        }

        private void OnFrameReady(WriteableBitmap bmp)
        {
            if (!_isLive)
                ImgOriginal.Source = bmp;
        }

        private void SliderThreshold_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_settings != null)
                _settings.GapThreshold = (int)SliderThreshold.Value;
        }


        private void LoadGapWidthTexts()
        {
            if (_settings.MeasurementLines != null && _settings.MeasurementLines.Count > 0)
            {
                var first = _settings.MeasurementLines[0];
                TxtMaxWidth.Text = first.MaxGapWidth.ToString();
            }
            else
            {
                TxtMaxWidth.Text = "20";
            }
        }

        private void ApplyGapWidthTexts()
        {
            if (_settings.MeasurementLines == null || _settings.MeasurementLines.Count == 0) return;

            int max = 20;
            int.TryParse(TxtMaxWidth.Text, out max);
            if (max < 1) max = 1;

            foreach (var line in _settings.MeasurementLines)
            {
                line.MaxGapWidth = max;
            }
        }

        private void TxtGapWidth_LostFocus(object sender, RoutedEventArgs e)
        {
            ApplyGapWidthTexts();
        }

        private void TxtGapWidth_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
                ApplyGapWidthTexts();
        }

        private void BtnRunOnce_Click(object sender, RoutedEventArgs e)
        {
            RunInspection();
        }

        private void BtnLive_Click(object sender, RoutedEventArgs e)
        {
            if (_isLive)
            {
                StopPreview();
                BtnLive.Content = "Live";
            }
            else
            {
                _isLive = true;
                BtnLive.Content = "Stop";
                _liveTimer.Start();
            }
        }

        private void LiveTick(object sender, EventArgs e)
        {
            if (_isInspecting || !_isLive) return;
            var _ = RunInspection();
        }

        private async Task RunInspection()
        {
            if (_isInspecting) return;
            _isInspecting = true;

            var frame = _cameraService.CaptureSnapshot();
            if (frame == null)
            {
                _isInspecting = false;
                TxtDiagInfo.Text = "No camera frame available";
                return;
            }

            _settings.GapThreshold = (int)SliderThreshold.Value;
            var settings = _settings;

            var result = await Task.Run(() => _inspectionService.Inspect(frame, settings));
            frame.Dispose();

            // Update all 4 views
            if (result.AnnotatedFrame != null)
                ImgOriginal.Source = BitmapHelper.MatToBitmapSource(result.AnnotatedFrame);

            if (result.GrayscaleFrame != null)
                ImgGrayscale.Source = BitmapHelper.MatToBitmapSource(result.GrayscaleFrame);

            if (result.ProfileFrame != null)
                ImgProfiles.Source = BitmapHelper.MatToBitmapSource(result.ProfileFrame);

            if (result.ThresholdFrame != null)
                ImgThreshold.Source = BitmapHelper.MatToBitmapSource(result.ThresholdFrame);

            // Build info text
            string status = result.IsOk ? "OK" : "NG";
            string lineInfo = "";
            if (result.LineResults != null)
            {
                foreach (var lr in result.LineResults)
                    lineInfo += $" L{lr.LineIndex + 1}={lr.GapWidthPx:F1}px";
            }
            TxtDiagInfo.Text = $"{status} | Max gap: {result.MaxGapWidthFound:F1}px |{lineInfo} | {result.InspectionTimeMs:F0}ms";

            // Dispose result Mats
            result.AnnotatedFrame?.Dispose();
            result.GrayscaleFrame?.Dispose();
            result.ProfileFrame?.Dispose();
            result.ThresholdFrame?.Dispose();

            _isInspecting = false;
        }
    }
}
