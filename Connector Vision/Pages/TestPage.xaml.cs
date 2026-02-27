using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Connector_Vision.Helpers;
using Connector_Vision.Models;
using Connector_Vision.Services;

namespace Connector_Vision.Pages
{
    public partial class TestPage : Page
    {
        private CameraService _cameraService;
        private InspectionService _inspectionService;
        private SoundService _soundService;
        private InspectionSettings _settings;
        private SettingsManager _settingsManager;

        private DispatcherTimer _inspectionTimer;
        private bool _isRunning;
        private bool _isInspecting;
        private bool _isSubscribed;

        // Statistics
        private int _totalCount;
        private int _okCount;
        private int _ngCount;

        // Per-line gap display TextBlocks
        private TextBlock[] _lineGapTexts;

        public TestPage(CameraService cameraService, InspectionService inspectionService,
            SoundService soundService, InspectionSettings settings, SettingsManager settingsManager)
        {
            InitializeComponent();
            _cameraService = cameraService;
            _inspectionService = inspectionService;
            _soundService = soundService;
            _settings = settings;
            _settingsManager = settingsManager;

            _lineGapTexts = new TextBlock[] { TxtLine1Gap, TxtLine2Gap, TxtLine3Gap };

            _inspectionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
            _inspectionTimer.Tick += InspectionTick;

            PopulateModelComboBox();
            SubscribeCamera();
            ZoomPanHelper.Setup(ImgCamera);
        }

        public void RefreshOnNavigate()
        {
            PopulateModelComboBox();
            SubscribeCamera();
        }

        public void StopInspection()
        {
            _isRunning = false;
            _inspectionTimer.Stop();
            _soundService.StopNgAlert();
            Dispatcher.Invoke(() =>
            {
                BtnStartStop.Content = "START";
                BtnStartStop.Style = (Style)Application.Current.FindResource("AccentButton");
            });
        }

        private void SubscribeCamera()
        {
            if (_isSubscribed) return;
            _cameraService.FrameReadyUI += OnFrameReady;
            _isSubscribed = true;
        }

        private void OnFrameReady(WriteableBitmap bmp)
        {
            if (!_isRunning)
            {
                ImgCamera.Source = bmp;
                TxtNoFeed.Visibility = Visibility.Collapsed;
            }
        }

        public void PopulateModelComboBox()
        {
            CmbModel.Items.Clear();
            var models = _settingsManager.GetModelNames();
            foreach (var m in models)
                CmbModel.Items.Add(m);

            if (!string.IsNullOrEmpty(_settings.CurrentModelName))
            {
                for (int i = 0; i < CmbModel.Items.Count; i++)
                {
                    if (CmbModel.Items[i].ToString() == _settings.CurrentModelName)
                    {
                        CmbModel.SelectedIndex = i;
                        break;
                    }
                }
            }
        }

        private void CmbModel_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CmbModel.SelectedItem == null) return;
            string name = CmbModel.SelectedItem.ToString();
            if (name == _settings.CurrentModelName) return;

            var modelSettings = _settingsManager.LoadModel(name);
            if (modelSettings == null) return;

            int camIdx = _settings.CameraIndex;
            string camRes = _settings.CameraResolution;

            _settings.CopyInspectionParametersFrom(modelSettings);
            _settings.CameraIndex = camIdx;
            _settings.CameraResolution = camRes;
            _settings.CurrentModelName = name;
            _settingsManager.Save(_settings);
            _inspectionService.ResetEma();
        }

        private void BtnStartStop_Click(object sender, RoutedEventArgs e)
        {
            if (_isRunning)
            {
                StopInspection();
            }
            else
            {
                // Check: requires measurement lines
                if (_settings.MeasurementLines == null || _settings.MeasurementLines.Count == 0)
                {
                    MessageBox.Show("No measurement lines configured.\nGo to Model page to draw lines.",
                        "Cannot Start", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                _isRunning = true;
                BtnStartStop.Content = "STOP";
                BtnStartStop.Style = (Style)Application.Current.FindResource("DangerButton");
                _inspectionTimer.Start();
            }
        }

        private async void InspectionTick(object sender, EventArgs e)
        {
            if (_isInspecting || !_isRunning) return;
            _isInspecting = true;

            var frame = _cameraService.CaptureSnapshot();
            if (frame == null)
            {
                _isInspecting = false;
                return;
            }

            var settings = _settings;
            var result = await Task.Run(() => _inspectionService.Inspect(frame, settings));
            frame.Dispose();

            if (!_isRunning)
            {
                DisposeResult(result);
                _isInspecting = false;
                return;
            }

            // Update display
            if (result.AnnotatedFrame != null)
            {
                ImgCamera.Source = BitmapHelper.MatToBitmapSource(result.AnnotatedFrame);
                ImgLastResult.Source = BitmapHelper.MatToBitmapSource(result.AnnotatedFrame);
                TxtNoFeed.Visibility = Visibility.Collapsed;
            }

            // Update statistics
            _totalCount++;
            if (result.IsOk)
                _okCount++;
            else
                _ngCount++;

            TxtTotal.Text = _totalCount.ToString();
            TxtOk.Text = _okCount.ToString();
            TxtNg.Text = _ngCount.ToString();
            TxtOkRate.Text = _totalCount > 0 ? $"{(double)_okCount / _totalCount * 100:F1}%" : "--";

            // Update result indicator
            if (result.IsOk)
            {
                TxtResult.Text = "OK";
                TxtResult.Foreground = new SolidColorBrush(Color.FromRgb(0x00, 0xCC, 0x00));
                BorderResult.Background = new SolidColorBrush(Color.FromRgb(0x1B, 0x5E, 0x20));
                _soundService.StopNgAlert();
            }
            else
            {
                TxtResult.Text = "NG";
                TxtResult.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x33, 0x33));
                BorderResult.Background = new SolidColorBrush(Color.FromRgb(0x7F, 0x00, 0x00));
                _soundService.PlayNgAlert();
            }

            // Per-line gap details
            for (int i = 0; i < 3; i++)
            {
                if (i < result.LineResults.Count)
                {
                    var lr = result.LineResults[i];
                    _lineGapTexts[i].Text = $"{lr.GapWidthPx:F1} px";
                    _lineGapTexts[i].Foreground = lr.IsOk
                        ? new SolidColorBrush(Color.FromRgb(0x00, 0xCC, 0x00))
                        : new SolidColorBrush(Color.FromRgb(0xFF, 0x33, 0x33));
                }
                else
                {
                    _lineGapTexts[i].Text = "--";
                    _lineGapTexts[i].Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF));
                }
            }

            TxtMaxGap.Text = $"{result.MaxGapWidthFound:F1} px";
            TxtMaxGap.Foreground = result.IsOk
                ? new SolidColorBrush(Color.FromRgb(0x00, 0xCC, 0x00))
                : new SolidColorBrush(Color.FromRgb(0xFF, 0x33, 0x33));
            TxtInspTime.Text = $"{result.InspectionTimeMs:F0} ms";

            DisposeResult(result);
            _isInspecting = false;
        }

        private void DisposeResult(InspectionResult result)
        {
            result.AnnotatedFrame?.Dispose();
            result.GrayscaleFrame?.Dispose();
            result.ProfileFrame?.Dispose();
            result.ThresholdFrame?.Dispose();
        }

        private void BtnReset_Click(object sender, RoutedEventArgs e)
        {
            _totalCount = 0;
            _okCount = 0;
            _ngCount = 0;
            TxtTotal.Text = "0";
            TxtOk.Text = "0";
            TxtNg.Text = "0";
            TxtOkRate.Text = "--";
            TxtLine1Gap.Text = "--";
            TxtLine2Gap.Text = "--";
            TxtLine3Gap.Text = "--";
            TxtMaxGap.Text = "--";
            TxtInspTime.Text = "--";
            TxtResult.Text = "READY";
            TxtResult.Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x99));
            BorderResult.Background = new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x3D));
            ImgLastResult.Source = null;
        }
    }
}
