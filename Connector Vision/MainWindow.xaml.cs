using System;
using System.Windows;
using System.Windows.Controls;
using Connector_Vision.Helpers;
using Connector_Vision.Models;
using Connector_Vision.Pages;
using Connector_Vision.Services;

namespace Connector_Vision
{
    public partial class MainWindow : Window
    {
        private CameraService _cameraService;
        private InspectionService _inspectionService;
        private SoundService _soundService;
        private SettingsManager _settingsManager;
        private InspectionSettings _settings;

        private TestPage _testPage;
        private ModelPage _modelPage;
        private SettingPage _settingPage;
        private ManualPage _manualPage;

        private Button _activeNavButton;

        public MainWindow()
        {
            InitializeComponent();

            _settingsManager = new SettingsManager();
            _settings = _settingsManager.Load();

            _cameraService = new CameraService(Dispatcher);
            _inspectionService = new InspectionService();
            _soundService = new SoundService();
            _soundService.Initialize();

            _testPage = new TestPage(_cameraService, _inspectionService, _soundService, _settings, _settingsManager);
            _modelPage = new ModelPage(_cameraService, _inspectionService, _settings, _settingsManager);
            _settingPage = new SettingPage(_cameraService, _settings, _settingsManager);
            _manualPage = new ManualPage(_cameraService, _inspectionService, _settings);

            MainFrame.Navigate(_testPage);
            _activeNavButton = BtnTest;
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                _cameraService.Start(_settings.CameraIndex, _settings.CameraResolution);
                _cameraService.ApplyCameraProperties(_settings);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to open camera: {ex.Message}\n\nGo to Setting page to select camera.",
                    "Camera Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            // Load current model if configured
            if (!string.IsNullOrEmpty(_settings.CurrentModelName))
            {
                var modelSettings = _settingsManager.LoadModel(_settings.CurrentModelName);
                if (modelSettings != null)
                {
                    int camIdx = _settings.CameraIndex;
                    string camRes = _settings.CameraResolution;
                    string modelName = _settings.CurrentModelName;

                    _settings.CopyInspectionParametersFrom(modelSettings);

                    _settings.CameraIndex = camIdx;
                    _settings.CameraResolution = camRes;
                    _settings.CurrentModelName = modelName;
                }
            }

            UpdateSidebarModelName();
        }

        private void UpdateSidebarModelName()
        {
            if (!string.IsNullOrEmpty(_settings.CurrentModelName))
                TxtCurrentModel.Text = $"[{_settings.CurrentModelName}]";
            else
                TxtCurrentModel.Text = "";
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            _testPage.StopInspection();
            _modelPage.StopLive();
            _manualPage.StopPreview();
            _cameraService?.Stop();
            _cameraService?.Dispose();
        }

        private void SetActiveNav(Button button)
        {
            if (_activeNavButton != null)
                _activeNavButton.Style = (Style)FindResource("NavButton");
            button.Style = (Style)FindResource("NavButtonActive");
            _activeNavButton = button;
        }

        private void BtnTest_Click(object sender, RoutedEventArgs e)
        {
            _modelPage.StopLive();
            _manualPage.StopPreview();
            SetActiveNav(BtnTest);
            _testPage.RefreshOnNavigate();
            UpdateSidebarModelName();
            MainFrame.Navigate(_testPage);
        }

        private void BtnModel_Click(object sender, RoutedEventArgs e)
        {
            _testPage.StopInspection();
            _manualPage.StopPreview();
            SetActiveNav(BtnModel);
            _modelPage.RefreshSettings(_settings);
            MainFrame.Navigate(_modelPage);
        }

        private void BtnSetting_Click(object sender, RoutedEventArgs e)
        {
            _testPage.StopInspection();
            _modelPage.StopLive();
            _manualPage.StopPreview();
            SetActiveNav(BtnSetting);
            _settingPage.RefreshSettings(_settings);
            MainFrame.Navigate(_settingPage);
        }

        private void BtnManual_Click(object sender, RoutedEventArgs e)
        {
            _testPage.StopInspection();
            _modelPage.StopLive();
            SetActiveNav(BtnManual);
            _manualPage.RefreshSettings(_settings);
            MainFrame.Navigate(_manualPage);
        }
    }
}
