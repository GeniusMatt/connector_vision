using System;
using System.IO;
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

        // Debug log file
        private static readonly string _debugLogPath = System.IO.Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "debug_coords.log");

        private static void LogDebug(string msg)
        {
            try
            {
                System.IO.File.AppendAllText(_debugLogPath, $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\r\n");
            }
            catch { }
        }

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
            ResumeFromPreview();
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

            ResumeFromPreview();
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

            // GetPosition(ImgCamera) returns Image-local coords, automatically
            // inverting RenderTransform (zoom/pan). The Image control is sized to
            // the uniform-scaled bitmap and centered within CameraContainer.
            var pos = e.GetPosition(ImgCamera);

            double nx = (pos.X - offsetX) / renderedW;
            double ny = (pos.Y - offsetY) / renderedH;

            int debugPx = (int)(nx * srcW);
            int debugPy = (int)(ny * srcH);
            LogDebug($"[ClickToNorm] imgLocal=({pos.X:F1},{pos.Y:F1}) offset=({offsetX:F1},{offsetY:F1}) " +
                $"rendered=({renderedW:F1},{renderedH:F1}) => nx={nx:F4} ny={ny:F4} => pixel=({debugPx},{debugPy})");

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

            // Map normalized coords to Image-local space, then use TranslatePoint
            // to convert to CameraContainer/Canvas space. This accounts for the
            // Image being centered within the container (layout offset) and any
            // zoom/pan RenderTransform.
            double imgLocalX = offsetX + normX * renderedW;
            double imgLocalY = offsetY + normY * renderedH;
            var containerPt = ImgCamera.TranslatePoint(
                new System.Windows.Point(imgLocalX, imgLocalY), CameraContainer);
            cx = containerPt.X;
            cy = containerPt.Y;
        }

        private void UpdateLineOverlay()
        {
            OverlayCanvas.Children.Clear();
            if (_settings.MeasurementLines == null || _settings.MeasurementLines.Count == 0) return;
            if (ImgCamera.Source == null) return;

            var bmpDbg = ImgCamera.Source as BitmapSource;
            if (bmpDbg != null)
            {
                LogDebug($"[Overlay] ImgCamera.Source: pixel={bmpDbg.PixelWidth}x{bmpDbg.PixelHeight} " +
                    $"dip={bmpDbg.Width:F0}x{bmpDbg.Height:F0} dpi={bmpDbg.DpiX:F0}x{bmpDbg.DpiY:F0} " +
                    $"ctl={ImgCamera.ActualWidth:F1}x{ImgCamera.ActualHeight:F1}");
            }

            for (int i = 0; i < _settings.MeasurementLines.Count; i++)
            {
                var ml = _settings.MeasurementLines[i];
                NormalizedToCanvas(ml.X1, ml.Y1, out double cx1, out double cy1);
                NormalizedToCanvas(ml.X2, ml.Y2, out double cx2, out double cy2);

                LogDebug($"[Overlay] L{i + 1}: norm=({ml.X1:F4},{ml.Y1:F4})->({ml.X2:F4},{ml.Y2:F4}) " +
                    $"canvas=({cx1:F1},{cy1:F1})->({cx2:F1},{cy2:F1})");

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

                // Debug: log the stored line coordinates and corresponding pixel positions
                var bmpSrc = ImgCamera.Source as BitmapSource;
                if (bmpSrc != null)
                {
                    int fw = bmpSrc.PixelWidth, fh = bmpSrc.PixelHeight;
                    LogDebug($"[LineCreated] norm=({newLine.X1:F4},{newLine.Y1:F4})->({newLine.X2:F4},{newLine.Y2:F4}) " +
                        $"pixel=({(int)(newLine.X1 * fw)},{(int)(newLine.Y1 * fh)})->({(int)(newLine.X2 * fw)},{(int)(newLine.Y2 * fh)}) " +
                        $"bmpSize={fw}x{fh}");
                }

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
                ResumeFromPreview();
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
            ResumeFromPreview();
            RunPreview();
        }

        private async void RunPreview()
        {
            if (_isPreviewing || _isLiveTesting || _isInspecting) return;
            _isPreviewing = true;
            // Keep overlay visible during preview for diagnostic comparison
            // OverlayCanvas.Visibility = Visibility.Collapsed;

            try
            {
                var frame = _cameraService.CaptureSnapshot();
                if (frame == null)
                {
                    TxtPreviewInfo.Text = "No camera frame available";
                    _isPreviewing = false;
                    return;
                }

                // Log layout diagnostics
                LogDebug($"[Preview] CameraContainer: {CameraContainer.ActualWidth:F1}x{CameraContainer.ActualHeight:F1}");
                LogDebug($"[Preview] ImgCamera: {ImgCamera.ActualWidth:F1}x{ImgCamera.ActualHeight:F1}");
                LogDebug($"[Preview] OverlayCanvas: {OverlayCanvas.ActualWidth:F1}x{OverlayCanvas.ActualHeight:F1}");

                // Check position of ImgCamera relative to CameraContainer
                var imgPos = ImgCamera.TranslatePoint(new System.Windows.Point(0, 0), CameraContainer);
                var canvasPos = OverlayCanvas.TranslatePoint(new System.Windows.Point(0, 0), CameraContainer);
                LogDebug($"[Preview] ImgCamera origin in Container: ({imgPos.X:F1},{imgPos.Y:F1})");
                LogDebug($"[Preview] OverlayCanvas origin in Container: ({canvasPos.X:F1},{canvasPos.Y:F1})");

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
                        var previewBmp = BitmapHelper.MatToBitmapSource(result.AnnotatedFrame);
                        LogDebug($"[Preview] AnnotatedBmp: pixel={previewBmp.PixelWidth}x{previewBmp.PixelHeight} " +
                            $"dip={previewBmp.Width:F0}x{previewBmp.Height:F0} dpi={previewBmp.DpiX:F0}x{previewBmp.DpiY:F0}");
                        ImgCamera.Source = previewBmp;
                        result.AnnotatedFrame.Dispose();

                        // Log expected screen position of annotation vs overlay position
                        if (settings.MeasurementLines != null && settings.MeasurementLines.Count > 0)
                        {
                            var ml = settings.MeasurementLines[0];
                            double scale = Math.Min(ImgCamera.ActualWidth / previewBmp.PixelWidth,
                                                    ImgCamera.ActualHeight / previewBmp.PixelHeight);
                            double renderW = previewBmp.PixelWidth * scale;
                            double renderH = previewBmp.PixelHeight * scale;
                            double offX = (ImgCamera.ActualWidth - renderW) / 2;
                            double offY = (ImgCamera.ActualHeight - renderH) / 2;
                            int px1 = (int)(ml.X1 * previewBmp.PixelWidth);
                            int py1 = (int)(ml.Y1 * previewBmp.PixelHeight);
                            double screenX = offX + px1 * scale + imgPos.X;
                            double screenY = offY + py1 * scale + imgPos.Y;
                            NormalizedToCanvas(ml.X1, ml.Y1, out double cx, out double cy);
                            LogDebug($"[Preview] L1 annotation pixel=({px1},{py1}) => screen in container=({screenX:F1},{screenY:F1})");
                            LogDebug($"[Preview] L1 overlay canvas=({cx:F1},{cy:F1}) canvasOriginOffset=({canvasPos.X:F1},{canvasPos.Y:F1})");
                            LogDebug($"[Preview] DIFFERENCE: dX={screenX - (cx + canvasPos.X):F1} dY={screenY - (cy + canvasPos.Y):F1}");
                        }
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
                    _isPreviewing = false;
                }

                // Keep preview visible — _isPreviewing stays true
                // Camera feed resumes when user clicks Preview again, draws a line,
                // changes a param, or starts Live Test
            }
            catch (Exception ex)
            {
                TxtPreviewInfo.Text = $"Preview error: {ex.Message}";
                LogDebug($"[Preview] Error: {ex}");
                _isPreviewing = false;
            }
        }

        private void ResumeFromPreview()
        {
            if (!_isPreviewing) return;
            _isPreviewing = false;
            // OnFrameReady will restore live feed + overlay on next camera frame
        }

        #endregion
    }
}
