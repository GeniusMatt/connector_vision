using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Connector_Vision.Helpers;
using Connector_Vision.Models;
using Connector_Vision.Services;

namespace Connector_Vision.Pages
{
    public partial class SettingPage : Page
    {
        private CameraService _cameraService;
        private InspectionSettings _settings;
        private SettingsManager _settingsManager;
        private bool _isSubscribed;

        public SettingPage(CameraService cameraService, InspectionSettings settings, SettingsManager settingsManager)
        {
            InitializeComponent();
            _cameraService = cameraService;
            _settings = settings;
            _settingsManager = settingsManager;

            LoadCameraList();
            LoadSettings();
            SubscribeCamera();
            ZoomPanHelper.Setup(ImgPreview);
        }

        public void RefreshSettings(InspectionSettings settings)
        {
            _settings = settings;
            LoadCameraList();
            LoadSettings();
            SubscribeCamera();
        }

        private void LoadCameraList()
        {
            CmbCamera.Items.Clear();
            var cameras = DirectShowHelper.GetVideoDeviceNames();
            foreach (var name in cameras)
                CmbCamera.Items.Add(name);

            if (_settings.CameraIndex >= 0 && _settings.CameraIndex < CmbCamera.Items.Count)
                CmbCamera.SelectedIndex = _settings.CameraIndex;
            else if (CmbCamera.Items.Count > 0)
                CmbCamera.SelectedIndex = 0;
        }

        private void LoadSettings()
        {
            string res = _settings.CameraResolution ?? "Auto";
            for (int i = 0; i < CmbResolution.Items.Count; i++)
            {
                var item = CmbResolution.Items[i] as ComboBoxItem;
                if (item != null && item.Content.ToString() == res)
                {
                    CmbResolution.SelectedIndex = i;
                    break;
                }
            }
        }

        private void SubscribeCamera()
        {
            if (_isSubscribed) return;
            _cameraService.FrameReadyUI += OnFrameReady;
            _cameraService.FpsUpdated += OnFpsUpdated;
            _isSubscribed = true;
        }

        private void OnFrameReady(WriteableBitmap bmp)
        {
            ImgPreview.Source = bmp;
            TxtNoCamera.Visibility = Visibility.Collapsed;
            if (_cameraService.IsRunning)
                TxtCameraInfo.Text = $"Camera: {_cameraService.CameraInfo}";
        }

        private void OnFpsUpdated(double fps)
        {
            TxtFps.Text = $"FPS: {fps:F1}";
        }

        private void CmbCamera_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
        }

        private void BtnCameraProperties_Click(object sender, RoutedEventArgs e)
        {
            int camIndex = CmbCamera.SelectedIndex;
            if (camIndex < 0) return;

            var hwnd = new WindowInteropHelper(Window.GetWindow(this)).Handle;
            DirectShowHelper.ShowPropertyPage(hwnd, camIndex);

            _cameraService.ReadCameraProperties(_settings);
            _settingsManager.Save(_settings);
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            int newCamIndex = CmbCamera.SelectedIndex;
            var resItem = CmbResolution.SelectedItem as ComboBoxItem;
            string newRes = resItem?.Content.ToString() ?? "Auto";

            bool cameraChanged = newCamIndex != _settings.CameraIndex || newRes != _settings.CameraResolution;

            _settings.CameraIndex = newCamIndex;
            _settings.CameraResolution = newRes;
            _settingsManager.Save(_settings);

            if (cameraChanged && newCamIndex >= 0)
            {
                try
                {
                    _cameraService.Stop();
                    _cameraService.Start(newCamIndex, newRes);
                    _cameraService.ApplyCameraProperties(_settings);
                    TxtCameraInfo.Text = $"Camera: {_cameraService.CameraInfo}";
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Failed to open camera: {ex.Message}", "Camera Error",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            else
            {
                _cameraService.ReadCameraProperties(_settings);
                _settingsManager.Save(_settings);
            }
        }
    }
}
