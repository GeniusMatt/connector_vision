using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using OpenCvSharp;
using Connector_Vision.Helpers;
using Connector_Vision.Models;
using Connector_Vision.Services;

namespace Connector_Vision.Pages
{
    public partial class ModelPage : Page
    {
        private CameraService _cameraService;
        private InspectionService _inspectionService;
        private InspectionSettings _settings;
        private SettingsManager _settingsManager;

        private DispatcherTimer _liveTestTimer;
        private DispatcherTimer _previewDebounce;
        private bool _isLiveTesting;
        private bool _isInspecting;
        private bool _isPreviewing;
        private bool _isSubscribed;
        private bool _suppressParamEvents;

        // Line drawing state
        private bool _isDrawingLine;
        private bool _hasFirstPoint;
        private double _firstClickNormX;
        private double _firstClickNormY;

        // Endpoint dragging state
        private bool _isDraggingEndpoint;
        private int _dragLineIndex;
        private bool _dragIsPoint1;

        public ModelPage(CameraService cameraService, InspectionService inspectionService,
            InspectionSettings settings, SettingsManager settingsManager)
        {
            InitializeComponent();
            _cameraService = cameraService;
            _inspectionService = inspectionService;
            _settings = settings;
            _settingsManager = settingsManager;

            _liveTestTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            _liveTestTimer.Tick += LiveTestTick;

            _previewDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _previewDebounce.Tick += PreviewDebounce_Tick;

            LoadSettingsToUI();
            RefreshModelList();
            RefreshLineList();
            SubscribeCamera();
            ZoomPanHelper.Setup(ImgCamera);
            OverlayCanvas.RenderTransform = ImgCamera.RenderTransform;
            ImgCamera.MouseLeftButtonDown += ImgCamera_MouseDown;
            ImgCamera.MouseMove += ImgCamera_MouseMove;
            ImgCamera.MouseLeftButtonUp += ImgCamera_MouseUp;
            ImgCamera.SizeChanged += (s, ev) => UpdateLineOverlay();
        }

        public void RefreshSettings(InspectionSettings settings)
        {
            _settings = settings;
            LoadSettingsToUI();
            RefreshModelList();
            RefreshLineList();
            SubscribeCamera();
        }

        public void StopLive()
        {
            _isLiveTesting = false;
            _liveTestTimer.Stop();
            OverlayCanvas.Visibility = Visibility.Visible;
            UpdateLineOverlay();
        }

        private void SubscribeCamera()
        {
            if (_isSubscribed) return;
            _cameraService.FrameReadyUI += OnFrameReady;
            _isSubscribed = true;
        }

        private void OnFrameReady(WriteableBitmap bmp)
        {
            if (!_isLiveTesting && !_isPreviewing && !_isInspecting)
            {
                ImgCamera.Source = bmp;
                if (OverlayCanvas.Visibility != Visibility.Visible)
                {
                    OverlayCanvas.Visibility = Visibility.Visible;
                    UpdateLineOverlay();
                }
            }
        }

        private void LoadSettingsToUI()
        {
            _suppressParamEvents = true;
            SliderGapThreshold.Value = _settings.GapThreshold;
            SliderBlur.Value = _settings.GaussianBlurSize;
            SliderEdgeMargin.Value = _settings.EdgeMarginPercent;
            SliderMinEdgeSep.Value = _settings.MinEdgeSeparation;
            CmbEdgeMode.SelectedIndex = _settings.EdgeDetectionMode;
            _suppressParamEvents = false;
        }

        private void UIToSettings()
        {
            _settings.GapThreshold = (int)SliderGapThreshold.Value;
            _settings.GaussianBlurSize = (int)SliderBlur.Value;
            _settings.EdgeMarginPercent = (int)SliderEdgeMargin.Value;
            _settings.MinEdgeSeparation = (int)SliderMinEdgeSep.Value;
            _settings.EdgeDetectionMode = CmbEdgeMode.SelectedIndex;
        }

        private void OnParamChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressParamEvents || _settings == null) return;
            UIToSettings();

            if (ChkAutoPreview != null && ChkAutoPreview.IsChecked == true && !_isLiveTesting)
            {
                _previewDebounce.Stop();
                _previewDebounce.Start();
            }
        }

        private void CmbEdgeMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressParamEvents || _settings == null) return;
            _settings.EdgeDetectionMode = CmbEdgeMode.SelectedIndex;

            if (ChkAutoPreview != null && ChkAutoPreview.IsChecked == true && !_isLiveTesting)
            {
                _previewDebounce.Stop();
                _previewDebounce.Start();
            }
        }

        private void RefreshModelList()
        {
            CmbModels.Items.Clear();
            var models = _settingsManager.GetModelNames();
            foreach (var m in models)
                CmbModels.Items.Add(m);

            if (!string.IsNullOrEmpty(_settings.CurrentModelName))
            {
                for (int i = 0; i < CmbModels.Items.Count; i++)
                {
                    if (CmbModels.Items[i].ToString() == _settings.CurrentModelName)
                    {
                        CmbModels.SelectedIndex = i;
                        break;
                    }
                }
            }
        }

        private void RefreshLineList()
        {
            LstLines.Items.Clear();
            if (_settings.MeasurementLines != null)
            {
                for (int i = 0; i < _settings.MeasurementLines.Count; i++)
                {
                    var line = _settings.MeasurementLines[i];
                    LstLines.Items.Add($"L{i + 1}: {line}");
                }
            }
            UpdateLineOverlay();
        }

        #region Model Management

        private void BtnNewModel_Click(object sender, RoutedEventArgs e)
        {
            string name = TxtModelName.Text.Trim();
            if (string.IsNullOrEmpty(name))
            {
                MessageBox.Show("Enter a model name.", "Model", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            UIToSettings();
            _settings.CurrentModelName = name;
            _settingsManager.SaveModel(name, _settings);
            _settingsManager.Save(_settings);
            RefreshModelList();
        }

        private void BtnDeleteModel_Click(object sender, RoutedEventArgs e)
        {
            if (CmbModels.SelectedItem == null) return;
            string name = CmbModels.SelectedItem.ToString();

            var result = MessageBox.Show($"Delete model '{name}'?", "Confirm", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result != MessageBoxResult.Yes) return;

            _settingsManager.DeleteModel(name);
            if (_settings.CurrentModelName == name)
                _settings.CurrentModelName = "";
            _settingsManager.Save(_settings);
            RefreshModelList();
        }

        private void CmbModels_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
        }

        private void BtnLoadModel_Click(object sender, RoutedEventArgs e)
        {
            if (CmbModels.SelectedItem == null) return;
            string name = CmbModels.SelectedItem.ToString();

            var modelSettings = _settingsManager.LoadModel(name);
            if (modelSettings == null)
            {
                MessageBox.Show("Failed to load model.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            int camIdx = _settings.CameraIndex;
            string camRes = _settings.CameraResolution;

            _settings.CopyInspectionParametersFrom(modelSettings);
            _settings.CameraIndex = camIdx;
            _settings.CameraResolution = camRes;
            _settings.CurrentModelName = name;
            _settingsManager.Save(_settings);

            LoadSettingsToUI();
            RefreshLineList();
        }

        #endregion

        #region Line Drawing

        private void BtnDrawLine_Click(object sender, RoutedEventArgs e)
        {
            if (_settings.MeasurementLines != null && _settings.MeasurementLines.Count >= 3)
            {
                MessageBox.Show("Maximum 3 measurement lines allowed.", "Limit", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            _isDrawingLine = true;
            _hasFirstPoint = false;
            BtnDrawLine.Content = "Click Point 1...";
            TxtDrawStatus.Text = "Click on the camera image to set the first point of the measurement line.";
            ImgCamera.Cursor = Cursors.Cross;
        }

        private void BtnDeleteLine_Click(object sender, RoutedEventArgs e)
        {
            if (LstLines.SelectedIndex < 0 || _settings.MeasurementLines == null) return;
            _settings.MeasurementLines.RemoveAt(LstLines.SelectedIndex);
            RefreshLineList();
        }

        private void BtnClearLines_Click(object sender, RoutedEventArgs e)
        {
            if (_settings.MeasurementLines == null) return;
            _settings.MeasurementLines.Clear();
            RefreshLineList();
            OverlayCanvas.Children.Clear();
        }

        private void LstLines_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            int idx = LstLines.SelectedIndex;
            if (idx < 0 || _settings.MeasurementLines == null || idx >= _settings.MeasurementLines.Count)
            {
                BorderLineLimits.Visibility = Visibility.Collapsed;
                return;
            }

            var line = _settings.MeasurementLines[idx];
            _suppressParamEvents = true;
            SliderLineMinGap.Value = line.MinGapWidth;
            SliderLineMaxGap.Value = line.MaxGapWidth;
            _suppressParamEvents = false;

            TxtLineLimitsHeader.Text = $"Line {idx + 1} Limits";
            BorderLineLimits.Visibility = Visibility.Visible;
        }

        private void OnLineParamChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressParamEvents || _settings == null) return;

            int idx = LstLines.SelectedIndex;
            if (idx < 0 || _settings.MeasurementLines == null || idx >= _settings.MeasurementLines.Count)
                return;

            var line = _settings.MeasurementLines[idx];
            line.MinGapWidth = (int)SliderLineMinGap.Value;
            line.MaxGapWidth = (int)SliderLineMaxGap.Value;

            // Refresh the list to show updated limits
            RefreshLineList();
            LstLines.SelectedIndex = idx;

            if (ChkAutoPreview != null && ChkAutoPreview.IsChecked == true && !_isLiveTesting)
            {
                _previewDebounce.Stop();
                _previewDebounce.Start();
            }
        }

        #region Mouse Handling (Draw + Drag)

        private System.Windows.Point? ClickToNormalized(MouseEventArgs e)
        {
            var bitmapSource = ImgCamera.Source as BitmapSource;
            if (bitmapSource == null) return null;

            double srcW = bitmapSource.PixelWidth;
            double srcH = bitmapSource.PixelHeight;
            double ctlW = ImgCamera.ActualWidth;
            double ctlH = ImgCamera.ActualHeight;
            if (ctlW <= 0 || ctlH <= 0) return null;

            double uniformScale = Math.Min(ctlW / srcW, ctlH / srcH);
            double renderedW = srcW * uniformScale;
            double renderedH = srcH * uniformScale;
            double offsetX = (ctlW - renderedW) / 2;
            double offsetY = (ctlH - renderedH) / 2;

            var pos = e.GetPosition(ImgCamera);

            var tg = ImgCamera.RenderTransform as TransformGroup;
            if (tg != null && tg.Children.Count >= 2)
            {
                var st = (ScaleTransform)tg.Children[0];
                var tt = (TranslateTransform)tg.Children[1];
                pos = new System.Windows.Point(
                    (pos.X - tt.X) / st.ScaleX,
                    (pos.Y - tt.Y) / st.ScaleY);
            }

            double nx = (pos.X - offsetX) / renderedW;
            double ny = (pos.Y - offsetY) / renderedH;
            return new System.Windows.Point(nx, ny);
        }

        private void NormalizedToCanvas(double normX, double normY, out double cx, out double cy)
        {
            cx = 0; cy = 0;
            var bitmapSource = ImgCamera.Source as BitmapSource;
            if (bitmapSource == null) return;

            double srcW = bitmapSource.PixelWidth;
            double srcH = bitmapSource.PixelHeight;
            double ctlW = ImgCamera.ActualWidth;
            double ctlH = ImgCamera.ActualHeight;
            if (ctlW <= 0 || ctlH <= 0) return;

            double uniformScale = Math.Min(ctlW / srcW, ctlH / srcH);
            double renderedW = srcW * uniformScale;
            double renderedH = srcH * uniformScale;
            double offsetX = (ctlW - renderedW) / 2;
            double offsetY = (ctlH - renderedH) / 2;

            cx = offsetX + normX * renderedW;
            cy = offsetY + normY * renderedH;
        }

        private void UpdateLineOverlay()
        {
            OverlayCanvas.Children.Clear();
            if (_settings.MeasurementLines == null || _settings.MeasurementLines.Count == 0) return;
            if (ImgCamera.Source == null) return;

            for (int i = 0; i < _settings.MeasurementLines.Count; i++)
            {
                var ml = _settings.MeasurementLines[i];
                NormalizedToCanvas(ml.X1, ml.Y1, out double cx1, out double cy1);
                NormalizedToCanvas(ml.X2, ml.Y2, out double cx2, out double cy2);

                // Draw line
                var lineShape = new Line
                {
                    X1 = cx1, Y1 = cy1, X2 = cx2, Y2 = cy2,
                    Stroke = Brushes.Yellow, StrokeThickness = 1.5,
                    IsHitTestVisible = false
                };
                OverlayCanvas.Children.Add(lineShape);

                // Draw endpoint 1 (draggable)
                var ep1 = new Ellipse
                {
                    Width = 14, Height = 14,
                    Fill = Brushes.Cyan, Stroke = Brushes.White, StrokeThickness = 1,
                    Cursor = Cursors.Hand, Tag = new int[] { i, 0 }
                };
                Canvas.SetLeft(ep1, cx1 - 7);
                Canvas.SetTop(ep1, cy1 - 7);
                ep1.MouseLeftButtonDown += Endpoint_MouseDown;
                OverlayCanvas.Children.Add(ep1);

                // Draw endpoint 2 (draggable)
                var ep2 = new Ellipse
                {
                    Width = 14, Height = 14,
                    Fill = Brushes.Cyan, Stroke = Brushes.White, StrokeThickness = 1,
                    Cursor = Cursors.Hand, Tag = new int[] { i, 1 }
                };
                Canvas.SetLeft(ep2, cx2 - 7);
                Canvas.SetTop(ep2, cy2 - 7);
                ep2.MouseLeftButtonDown += Endpoint_MouseDown;
                OverlayCanvas.Children.Add(ep2);

                // Line label
                var label = new TextBlock
                {
                    Text = $"L{i + 1}",
                    Foreground = Brushes.Yellow, FontSize = 11,
                    IsHitTestVisible = false
                };
                Canvas.SetLeft(label, cx1 + 8);
                Canvas.SetTop(label, (cy1 + cy2) / 2 - 8);
                OverlayCanvas.Children.Add(label);
            }
        }

        private void Endpoint_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (_isDrawingLine) return;
            var ellipse = sender as Ellipse;
            if (ellipse == null) return;

            var tag = (int[])ellipse.Tag;
            _dragLineIndex = tag[0];
            _dragIsPoint1 = tag[1] == 0;
            _isDraggingEndpoint = true;
            ImgCamera.CaptureMouse();
            e.Handled = true;
        }

        private void ImgCamera_MouseDown(object sender, MouseButtonEventArgs e)
        {
            // Drag start is handled by Endpoint_MouseDown on the ellipses
        }

        private void ImgCamera_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_isDraggingEndpoint) return;

            var norm = ClickToNormalized(e);
            if (norm == null) return;

            double ny = Math.Max(0, Math.Min(1, norm.Value.Y));
            double nx = Math.Max(0, Math.Min(1, norm.Value.X));

            var line = _settings.MeasurementLines[_dragLineIndex];
            if (_dragIsPoint1)
            {
                line.Y1 = ny;
                line.X1 = nx;
                line.X2 = nx; // Keep vertical
            }
            else
            {
                line.Y2 = ny;
                line.X2 = nx;
                line.X1 = nx; // Keep vertical
            }

            UpdateLineOverlay();
            e.Handled = true;
        }

        private void ImgCamera_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (_isDraggingEndpoint)
            {
                _isDraggingEndpoint = false;
                ImgCamera.ReleaseMouseCapture();
                RefreshLineList();

                if (ChkAutoPreview != null && ChkAutoPreview.IsChecked == true && !_isLiveTesting)
                {
                    _previewDebounce.Stop();
                    _previewDebounce.Start();
                }
                e.Handled = true;
                return;
            }

            // Handle draw line click
            if (e.ClickCount != 1) return;
            if (!_isDrawingLine) return;

            var normCoord = ClickToNormalized(e);
            if (normCoord == null) return;

            double imgX = normCoord.Value.X;
            double imgY = normCoord.Value.Y;

            if (imgX < 0 || imgX > 1 || imgY < 0 || imgY > 1)
            {
                TxtDrawStatus.Text = "Click was outside the image area. Try again.";
                return;
            }

            if (!_hasFirstPoint)
            {
                _firstClickNormX = imgX;
                _firstClickNormY = imgY;
                _hasFirstPoint = true;
                BtnDrawLine.Content = "Click Point 2...";
                TxtDrawStatus.Text = $"Point 1 set at ({imgX:F3}, {imgY:F3}). Click the second point.";
            }
            else
            {
                imgX = _firstClickNormX;
                var newLine = new MeasurementLine(_firstClickNormX, _firstClickNormY, imgX, imgY);
                if (_settings.MeasurementLines == null)
                    _settings.MeasurementLines = new System.Collections.Generic.List<MeasurementLine>();
                _settings.MeasurementLines.Add(newLine);

                RefreshLineList();

                _isDrawingLine = false;
                _hasFirstPoint = false;
                BtnDrawLine.Content = "Draw Line";
                ImgCamera.Cursor = Cursors.Arrow;
                TxtDrawStatus.Text = $"Line added: {newLine}. Drag endpoints to adjust.";

                if (ChkAutoPreview != null && ChkAutoPreview.IsChecked == true && !_isLiveTesting)
                {
                    _previewDebounce.Stop();
                    _previewDebounce.Start();
                }
            }
        }

        #endregion

        #endregion

        #region Preview / Live Test

        private void BtnLiveTest_Click(object sender, RoutedEventArgs e)
        {
            if (_isLiveTesting)
            {
                StopLive();
                BtnLiveTest.Content = "Live Test";
                TxtLiveResult.Text = "";
            }
            else
            {
                _isLiveTesting = true;
                BtnLiveTest.Content = "Stop";
                OverlayCanvas.Visibility = Visibility.Collapsed;
                _liveTestTimer.Start();
            }
        }

        private async void LiveTestTick(object sender, EventArgs e)
        {
            if (_isInspecting || !_isLiveTesting) return;
            _isInspecting = true;

            var frame = _cameraService.CaptureSnapshot();
            if (frame == null)
            {
                _isInspecting = false;
                return;
            }

            UIToSettings();
            var settings = _settings;

            var result = await Task.Run(() => _inspectionService.Inspect(frame, settings));
            frame.Dispose();

            if (result.AnnotatedFrame != null)
            {
                ImgCamera.Source = BitmapHelper.MatToBitmapSource(result.AnnotatedFrame);
                result.AnnotatedFrame.Dispose();
            }
            if (result.ProfileFrame != null)
            {
                ImgProfilePreview.Source = BitmapHelper.MatToBitmapSource(result.ProfileFrame);
                result.ProfileFrame.Dispose();
            }
            if (result.ThresholdFrame != null)
            {
                ImgThresholdPreview.Source = BitmapHelper.MatToBitmapSource(result.ThresholdFrame);
                result.ThresholdFrame.Dispose();
            }
            result.GrayscaleFrame?.Dispose();

            if (result.IsOk)
            {
                TxtLiveResult.Text = "OK";
                TxtLiveResult.Foreground = new SolidColorBrush(Color.FromRgb(0x00, 0xCC, 0x00));
            }
            else
            {
                TxtLiveResult.Text = $"NG - max gap {result.MaxGapWidthFound:F0}px ({result.InspectionTimeMs:F0}ms)";
                TxtLiveResult.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x33, 0x33));
            }

            _isInspecting = false;
        }

        private void BtnSaveSettings_Click(object sender, RoutedEventArgs e)
        {
            UIToSettings();
            _settingsManager.Save(_settings);

            if (!string.IsNullOrEmpty(_settings.CurrentModelName))
                _settingsManager.SaveModel(_settings.CurrentModelName, _settings);

            MessageBox.Show("Settings saved.", "Saved", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void PreviewDebounce_Tick(object sender, EventArgs e)
        {
            _previewDebounce.Stop();
            RunPreview();
        }

        private void BtnPreview_Click(object sender, RoutedEventArgs e)
        {
            RunPreview();
        }

        private async void RunPreview()
        {
            if (_isPreviewing || _isLiveTesting || _isInspecting) return;
            _isPreviewing = true;
            OverlayCanvas.Visibility = Visibility.Collapsed;

            try
            {
                var frame = _cameraService.CaptureSnapshot();
                if (frame == null)
                {
                    TxtPreviewInfo.Text = "No camera frame available";
                    return;
                }

                UIToSettings();
                var settings = _settings;

                InspectionResult result = null;

                if (settings.MeasurementLines != null && settings.MeasurementLines.Count > 0)
                {
                    result = await Task.Run(() => _inspectionService.Inspect(frame, settings));
                }

                frame.Dispose();

                if (result != null)
                {
                    if (result.AnnotatedFrame != null)
                    {
                        ImgCamera.Source = BitmapHelper.MatToBitmapSource(result.AnnotatedFrame);
                        result.AnnotatedFrame.Dispose();
                    }

                    if (result.ProfileFrame != null)
                    {
                        ImgProfilePreview.Source = BitmapHelper.MatToBitmapSource(result.ProfileFrame);
                        result.ProfileFrame.Dispose();
                    }

                    if (result.ThresholdFrame != null)
                    {
                        ImgThresholdPreview.Source = BitmapHelper.MatToBitmapSource(result.ThresholdFrame);
                        result.ThresholdFrame.Dispose();
                    }

                    result.GrayscaleFrame?.Dispose();

                    string verdict = result.IsOk ? "OK" : "NG";
                    string lineInfo = "";
                    foreach (var lr in result.LineResults)
                        lineInfo += $" L{lr.LineIndex + 1}={lr.GapWidthPx:F0}px";

                    TxtPreviewInfo.Text = $"{verdict} | Max gap: {result.MaxGapWidthFound:F0}px |{lineInfo} | {result.InspectionTimeMs:F0}ms";
                }
                else
                {
                    TxtPreviewInfo.Text = "No measurement lines defined. Draw lines first.";
                    ImgProfilePreview.Source = null;
                    ImgThresholdPreview.Source = null;
                }
            }
            catch (Exception ex)
            {
                TxtPreviewInfo.Text = $"Preview error: {ex.Message}";
                System.Diagnostics.Debug.WriteLine($"[Preview] Error: {ex}");
            }
            finally
            {
                _isPreviewing = false;
            }
        }

        #endregion
    }
}
