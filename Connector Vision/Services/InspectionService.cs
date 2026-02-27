using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using OpenCvSharp;
using Connector_Vision.Models;

namespace Connector_Vision.Services
{
    public class InspectionService
    {
        private static readonly string _debugLogPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "debug_coords.log");

        private static void LogDebug(string msg)
        {
            try { File.AppendAllText(_debugLogPath, $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\r\n"); } catch { }
        }

        public InspectionResult Inspect(Mat frame, InspectionSettings settings)
        {
            var sw = Stopwatch.StartNew();
            var result = new InspectionResult();

            if (settings.MeasurementLines == null || settings.MeasurementLines.Count == 0)
            {
                result.IsOk = false;
                result.AnnotatedFrame = frame.Clone();
                Cv2.PutText(result.AnnotatedFrame, "NO MEASUREMENT LINES", new Point(30, 60),
                    HersheyFonts.HersheySimplex, 1.5, Scalar.Red, 3);
                sw.Stop();
                result.InspectionTimeMs = sw.Elapsed.TotalMilliseconds;
                return result;
            }

            int w = frame.Width;
            int h = frame.Height;

            // 1. BGR → Grayscale → GaussianBlur
            using (var gray = new Mat())
            using (var blurred = new Mat())
            {
                Cv2.CvtColor(frame, gray, ColorConversionCodes.BGR2GRAY);

                int blur = settings.GaussianBlurSize;
                if (blur % 2 == 0) blur++;
                if (blur < 1) blur = 1;
                Cv2.GaussianBlur(gray, blurred, new Size(blur, blur), 0);

                // Store grayscale for ManualPage diagnostic view
                result.GrayscaleFrame = gray.Clone();

                double maxGap = 0;
                bool allOk = true;

                // Initialize EMA state if line count changed
                int lineCount = settings.MeasurementLines.Count;
                if (_emaGapWidths == null || _emaLineCount != lineCount)
                {
                    _emaGapWidths = new double[lineCount];
                    _emaLineCount = lineCount;
                }

                // 2. For each measurement line: extract band-averaged profile and find edge distance
                for (int i = 0; i < lineCount; i++)
                {
                    var line = settings.MeasurementLines[i];
                    line.ToPixelCoords(w, h, out int px1, out int py1, out int px2, out int py2);

                    double[] bandProfile = ExtractBandAveragedProfile(blurred, px1, py1, px2, py2);
                    FindEdgeDistance(bandProfile, settings.GapThreshold,
                        settings.EdgeMarginPercent, settings.EdgeDetectionMode,
                        out double edge1Pos, out double edge2Pos, out double gapWidth);

                    // Apply temporal EMA smoothing
                    if (gapWidth > 0)
                    {
                        if (_emaGapWidths[i] == 0)
                            _emaGapWidths[i] = gapWidth; // seed with first valid measurement
                        else
                            _emaGapWidths[i] = EmaAlpha * gapWidth + (1 - EmaAlpha) * _emaGapWidths[i];
                    }
                    double smoothedGap = gapWidth > 0 ? _emaGapWidths[i] : gapWidth;

                    bool lineOk = smoothedGap <= line.MaxGapWidth;
                    if (!lineOk) allOk = false;
                    if (smoothedGap > maxGap) maxGap = smoothedGap;

                    // Convert band-averaged profile to byte[] for visualization
                    byte[] profileBytes = new byte[bandProfile.Length];
                    for (int j = 0; j < bandProfile.Length; j++)
                    {
                        double v = bandProfile[j];
                        if (v < 0) v = 0;
                        if (v > 255) v = 255;
                        profileBytes[j] = (byte)v;
                    }

                    result.LineResults.Add(new LineResult
                    {
                        LineIndex = i,
                        GapWidthPx = smoothedGap,
                        GapStart = edge1Pos,
                        GapEnd = edge2Pos,
                        ProfileData = profileBytes,
                        IsOk = lineOk
                    });
                }

                result.MaxGapWidthFound = maxGap;
                result.IsOk = allOk;

                // 3. Create annotated frame
                result.AnnotatedFrame = CreateAnnotatedFrame(frame, settings, result);

                // 4. Create profile chart visualization
                result.ProfileFrame = CreateProfileChart(result.LineResults, settings);

                // 5. Create edge view
                result.ThresholdFrame = CreateEdgeView(blurred, settings);
            }

            sw.Stop();
            result.InspectionTimeMs = sw.Elapsed.TotalMilliseconds;
            return result;
        }

        /// <summary>
        /// Extract grayscale intensity profile along a line using Bresenham traversal.
        /// </summary>
        private byte[] ExtractProfile(Mat gray, int x1, int y1, int x2, int y2)
        {
            // Bresenham line algorithm to get all pixel coordinates along the line
            var points = new List<Point>();
            int dx = Math.Abs(x2 - x1);
            int dy = Math.Abs(y2 - y1);
            int sx = x1 < x2 ? 1 : -1;
            int sy = y1 < y2 ? 1 : -1;
            int err = dx - dy;
            int cx = x1, cy = y1;

            int maxW = gray.Width;
            int maxH = gray.Height;

            while (true)
            {
                if (cx >= 0 && cx < maxW && cy >= 0 && cy < maxH)
                    points.Add(new Point(cx, cy));

                if (cx == x2 && cy == y2) break;

                int e2 = 2 * err;
                if (e2 > -dy) { err -= dy; cx += sx; }
                if (e2 < dx) { err += dx; cy += sy; }
            }

            // Extract intensity values
            byte[] profile = new byte[points.Count];
            var indexer = gray.GetGenericIndexer<byte>();
            for (int i = 0; i < points.Count; i++)
            {
                profile[i] = indexer[points[i].Y, points[i].X];
            }
            return profile;
        }

        /// <summary>
        /// Extract band-averaged intensity profile: sample 2*BandHalfWidth+1 parallel lines
        /// perpendicular to the measurement direction and average their intensities.
        /// Returns double[] to preserve sub-byte precision from averaging.
        /// </summary>
        private double[] ExtractBandAveragedProfile(Mat gray, int x1, int y1, int x2, int y2)
        {
            double dx = x2 - x1;
            double dy = y2 - y1;
            double len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 1) return new double[0];

            // Perpendicular unit vector
            double perpX = -dy / len;
            double perpY = dx / len;

            // Collect profiles from parallel lines
            var profiles = new List<byte[]>();
            for (int offset = -BandHalfWidth; offset <= BandHalfWidth; offset++)
            {
                int sx1 = x1 + (int)Math.Round(offset * perpX);
                int sy1 = y1 + (int)Math.Round(offset * perpY);
                int sx2 = x2 + (int)Math.Round(offset * perpX);
                int sy2 = y2 + (int)Math.Round(offset * perpY);

                byte[] p = ExtractProfile(gray, sx1, sy1, sx2, sy2);
                if (p.Length > 0) profiles.Add(p);
            }

            if (profiles.Count == 0) return new double[0];

            // Use center profile length as reference
            int refLen = profiles[0].Length;
            double[] averaged = new double[refLen];
            int validCount = 0;

            foreach (var p in profiles)
            {
                if (p.Length != refLen) continue; // skip mismatched lengths
                validCount++;
                for (int i = 0; i < refLen; i++)
                    averaged[i] += p[i];
            }

            if (validCount > 0)
            {
                for (int i = 0; i < refLen; i++)
                    averaged[i] /= validCount;
            }

            return averaged;
        }

        private const int UpsampleFactor = 4;
        private const int BandHalfWidth = 3;    // 7 parallel lines total
        private const double EmaAlpha = 0.3;    // current frame weight

        private double[] _emaGapWidths;
        private int _emaLineCount;

        /// <summary>
        /// Upsample a byte profile using Catmull-Rom cubic interpolation.
        /// </summary>
        private double[] UpsampleProfile(byte[] profile, int factor)
        {
            int origLen = profile.Length;
            if (origLen < 2) return new double[] { profile.Length > 0 ? profile[0] : 0 };

            int newLen = (origLen - 1) * factor + 1;
            double[] result = new double[newLen];

            for (int i = 0; i < origLen - 1; i++)
            {
                // Catmull-Rom control points: p0, p1, p2, p3
                double p0 = (i > 0) ? profile[i - 1] : profile[i];
                double p1 = profile[i];
                double p2 = profile[i + 1];
                double p3 = (i + 2 < origLen) ? profile[i + 2] : profile[i + 1];

                for (int j = 0; j < factor; j++)
                {
                    double t = (double)j / factor;
                    double t2 = t * t;
                    double t3 = t2 * t;

                    // Catmull-Rom spline formula
                    double val = 0.5 * (
                        (2.0 * p1) +
                        (-p0 + p2) * t +
                        (2.0 * p0 - 5.0 * p1 + 4.0 * p2 - p3) * t2 +
                        (-p0 + 3.0 * p1 - 3.0 * p2 + p3) * t3);

                    // Clamp to [0, 255]
                    if (val < 0) val = 0;
                    if (val > 255) val = 255;

                    result[i * factor + j] = val;
                }
            }
            // Last sample
            result[newLen - 1] = profile[origLen - 1];
            return result;
        }

        /// <summary>
        /// Upsample a double profile using Catmull-Rom cubic interpolation.
        /// Overload for band-averaged profiles that are already double[].
        /// </summary>
        private double[] UpsampleProfile(double[] profile, int factor)
        {
            int origLen = profile.Length;
            if (origLen < 2) return new double[] { origLen > 0 ? profile[0] : 0 };

            int newLen = (origLen - 1) * factor + 1;
            double[] result = new double[newLen];

            for (int i = 0; i < origLen - 1; i++)
            {
                double p0 = (i > 0) ? profile[i - 1] : profile[i];
                double p1 = profile[i];
                double p2 = profile[i + 1];
                double p3 = (i + 2 < origLen) ? profile[i + 2] : profile[i + 1];

                for (int j = 0; j < factor; j++)
                {
                    double t = (double)j / factor;
                    double t2 = t * t;
                    double t3 = t2 * t;

                    double val = 0.5 * (
                        (2.0 * p1) +
                        (-p0 + p2) * t +
                        (2.0 * p0 - 5.0 * p1 + 4.0 * p2 - p3) * t2 +
                        (-p0 + 3.0 * p1 - 3.0 * p2 + p3) * t3);

                    if (val < 0) val = 0;
                    if (val > 255) val = 255;

                    result[i * factor + j] = val;
                }
            }
            result[newLen - 1] = profile[origLen - 1];
            return result;
        }

        /// <summary>
        /// Parabolic sub-pixel peak refinement around a discrete peak position.
        /// </summary>
        private double SubPixelPeak(double[] gradient, int peakPos)
        {
            if (peakPos <= 0 || peakPos >= gradient.Length - 1)
                return peakPos;

            double a = gradient[peakPos - 1];
            double b = gradient[peakPos];
            double c = gradient[peakPos + 1];

            double denom = a - 2.0 * b + c;
            if (Math.Abs(denom) < 1e-6)
                return peakPos;

            double offset = 0.5 * (a - c) / denom;
            // Clamp refinement to ±0.5 to prevent wild jumps on noisy/flat gradients
            if (offset > 0.5) offset = 0.5;
            if (offset < -0.5) offset = -0.5;
            return peakPos + offset;
        }

        /// <summary>
        /// Find the distance between the two strongest edges (intensity transitions) in the profile.
        /// Uses 4x profile upsampling + parabolic sub-pixel peak fitting for fractional precision.
        /// </summary>
        private void FindEdgeDistance(double[] profile, int edgeThreshold,
            int marginPercent, int mode,
            out double edge1Pos, out double edge2Pos, out double gapWidth)
        {
            edge1Pos = 0;
            edge2Pos = 0;
            gapWidth = 0;

            if (profile.Length < 5) return;

            int origLen = profile.Length;
            LogDebug($"[SubPixel] Profile length={origLen}, upsampling {UpsampleFactor}x");

            // Upsample profile for sub-pixel resolution
            double[] upsampled = UpsampleProfile(profile, UpsampleFactor);
            int uLen = upsampled.Length;

            // Apply edge margin: skip first/last N% of the upsampled profile
            int margin = (int)(uLen * marginPercent / 100.0);
            int searchStart = Math.Max(UpsampleFactor, margin);
            int searchEnd = Math.Min(uLen - 1 - UpsampleFactor, uLen - 1 - margin);
            if (searchStart >= searchEnd) return;

            // Compute absolute gradient with kernel width = factor (preserves threshold magnitude)
            double[] absGrad = new double[uLen];
            for (int i = UpsampleFactor; i < uLen - UpsampleFactor; i++)
                absGrad[i] = Math.Abs(upsampled[i + UpsampleFactor] - upsampled[i - UpsampleFactor]);

            if (mode == 1)
            {
                // First & Last mode: find first edge from start, last edge from end
                int firstEdge = -1;
                for (int i = searchStart; i <= searchEnd; i++)
                {
                    if (absGrad[i] >= edgeThreshold)
                    {
                        firstEdge = i;
                        break;
                    }
                }

                int lastEdge = -1;
                for (int i = searchEnd; i >= searchStart; i--)
                {
                    if (absGrad[i] >= edgeThreshold)
                    {
                        lastEdge = i;
                        break;
                    }
                }

                LogDebug($"[SubPixel] Mode=FirstLast, firstEdge={firstEdge}, lastEdge={lastEdge}");

                if (firstEdge >= 0 && lastEdge >= 0 && lastEdge > firstEdge)
                {
                    double sp1 = SubPixelPeak(absGrad, firstEdge);
                    double sp2 = SubPixelPeak(absGrad, lastEdge);
                    edge1Pos = sp1 / UpsampleFactor;
                    edge2Pos = sp2 / UpsampleFactor;
                    gapWidth = edge2Pos - edge1Pos;
                    LogDebug($"[SubPixel] Raw edges: {firstEdge}/{UpsampleFactor}={firstEdge / (double)UpsampleFactor:F2}, {lastEdge}/{UpsampleFactor}={lastEdge / (double)UpsampleFactor:F2}");
                    LogDebug($"[SubPixel] SubPixel edges: {sp1:F3}/{UpsampleFactor}={edge1Pos:F3}, {sp2:F3}/{UpsampleFactor}={edge2Pos:F3}");
                    LogDebug($"[SubPixel] Gap: raw={(lastEdge - firstEdge) / (double)UpsampleFactor:F2} -> subpixel={gapWidth:F3}");
                }
                else if (firstEdge >= 0)
                {
                    double sp1 = SubPixelPeak(absGrad, firstEdge);
                    edge1Pos = sp1 / UpsampleFactor;
                    edge2Pos = edge1Pos;
                    gapWidth = 0;
                    LogDebug($"[SubPixel] Only one edge found at {firstEdge}, gap=0");
                }
            }
            else
            {
                // Strongest Pair mode: find two strongest edges
                int strongest1Pos = -1;
                double strongest1Val = 0;
                for (int i = searchStart; i <= searchEnd; i++)
                {
                    if (absGrad[i] >= edgeThreshold && absGrad[i] > strongest1Val)
                    {
                        strongest1Val = absGrad[i];
                        strongest1Pos = i;
                    }
                }

                LogDebug($"[SubPixel] Mode=StrongestPair, strongest1Pos={strongest1Pos}, val={strongest1Val:F1}");
                if (strongest1Pos < 0) return;

                int minSep = 3 * UpsampleFactor;
                int strongest2Pos = -1;
                double strongest2Val = 0;
                for (int i = searchStart; i <= searchEnd; i++)
                {
                    if (Math.Abs(i - strongest1Pos) < minSep) continue;
                    if (absGrad[i] >= edgeThreshold && absGrad[i] > strongest2Val)
                    {
                        strongest2Val = absGrad[i];
                        strongest2Pos = i;
                    }
                }

                LogDebug($"[SubPixel] strongest2Pos={strongest2Pos}, val={strongest2Val:F1}");
                if (strongest2Pos >= 0)
                {
                    int rawE1 = Math.Min(strongest1Pos, strongest2Pos);
                    int rawE2 = Math.Max(strongest1Pos, strongest2Pos);
                    double sp1 = SubPixelPeak(absGrad, rawE1);
                    double sp2 = SubPixelPeak(absGrad, rawE2);
                    edge1Pos = sp1 / UpsampleFactor;
                    edge2Pos = sp2 / UpsampleFactor;
                    gapWidth = edge2Pos - edge1Pos;
                    LogDebug($"[SubPixel] Raw edges: {rawE1}/{UpsampleFactor}={rawE1 / (double)UpsampleFactor:F2}, {rawE2}/{UpsampleFactor}={rawE2 / (double)UpsampleFactor:F2}");
                    LogDebug($"[SubPixel] SubPixel edges: {sp1:F3}/{UpsampleFactor}={edge1Pos:F3}, {sp2:F3}/{UpsampleFactor}={edge2Pos:F3}");
                    LogDebug($"[SubPixel] Gap: raw={(rawE2 - rawE1) / (double)UpsampleFactor:F2} -> subpixel={gapWidth:F3}");
                }
                else
                {
                    double sp1 = SubPixelPeak(absGrad, strongest1Pos);
                    edge1Pos = sp1 / UpsampleFactor;
                    edge2Pos = edge1Pos;
                    gapWidth = 0;
                    LogDebug($"[SubPixel] Only one edge found at {strongest1Pos}, gap=0");
                }
            }
        }

        /// <summary>
        /// Create annotated frame with measurement lines color-coded green/red and gap labels.
        /// </summary>
        private Mat CreateAnnotatedFrame(Mat frame, InspectionSettings settings, InspectionResult result)
        {
            var annotated = frame.Clone();
            int w = frame.Width;
            int h = frame.Height;

            for (int i = 0; i < settings.MeasurementLines.Count; i++)
            {
                var line = settings.MeasurementLines[i];
                line.ToPixelCoords(w, h, out int px1, out int py1, out int px2, out int py2);

                LogDebug($"[Annotate] L{i + 1}: norm=({line.X1:F4},{line.Y1:F4})->({line.X2:F4},{line.Y2:F4}) " +
                    $"pixel=({px1},{py1})->({px2},{py2}) frameSize={w}x{h}");

                var lineResult = result.LineResults[i];
                var color = lineResult.IsOk ? new Scalar(0, 255, 0) : new Scalar(0, 0, 255);

                // Draw the measurement line
                Cv2.Line(annotated, new Point(px1, py1), new Point(px2, py2), color, 1);

                // Draw line endpoints (circles)
                Cv2.Circle(annotated, new Point(px1, py1), 3, color, -1);
                Cv2.Circle(annotated, new Point(px2, py2), 3, color, -1);

                // Draw gap region markers (orange) along the line
                if (lineResult.GapWidthPx > 0 && lineResult.ProfileData != null && lineResult.ProfileData.Length > 0)
                {
                    double totalLen = lineResult.ProfileData.Length;
                    double ratioStart = lineResult.GapStart / totalLen;
                    double ratioEnd = lineResult.GapEnd / totalLen;

                    int gx1 = px1 + (int)((px2 - px1) * ratioStart);
                    int gy1 = py1 + (int)((py2 - py1) * ratioStart);
                    int gx2 = px1 + (int)((px2 - px1) * ratioEnd);
                    int gy2 = py1 + (int)((py2 - py1) * ratioEnd);

                    // Draw orange line over gap region
                    Cv2.Line(annotated, new Point(gx1, gy1), new Point(gx2, gy2),
                        new Scalar(0, 165, 255), 2);

                    // Draw cyan edge markers at detected edge positions
                    Cv2.Circle(annotated, new Point(gx1, gy1), 3, new Scalar(255, 255, 0), 1);
                    Cv2.Circle(annotated, new Point(gx2, gy2), 3, new Scalar(255, 255, 0), 1);
                }

                // Draw gap width label
                int labelX = (px1 + px2) / 2 + 10;
                int labelY = (py1 + py2) / 2 - 10;
                string label = $"L{i + 1}: {lineResult.GapWidthPx:F1}px [<={line.MaxGapWidth}]";
                Cv2.PutText(annotated, label, new Point(labelX, labelY),
                    HersheyFonts.HersheySimplex, 0.45, color, 1);
            }

            // Draw overall verdict
            string verdict = result.IsOk ? "OK" : "NG";
            var verdictColor = result.IsOk ? new Scalar(0, 255, 0) : new Scalar(0, 0, 255);
            Cv2.PutText(annotated, verdict, new Point(30, 50),
                HersheyFonts.HersheySimplex, 1.5, verdictColor, 3);

            return annotated;
        }

        /// <summary>
        /// Create intensity profile chart visualization showing all lines' profiles with threshold line.
        /// </summary>
        private Mat CreateProfileChart(List<LineResult> lineResults, InspectionSettings settings)
        {
            int chartW = 600;
            int chartH = 300;
            var chart = new Mat(chartH, chartW, MatType.CV_8UC3, new Scalar(30, 30, 40));

            if (lineResults.Count == 0) return chart;

            // Find max profile length
            int maxLen = 0;
            foreach (var lr in lineResults)
            {
                if (lr.ProfileData != null && lr.ProfileData.Length > maxLen)
                    maxLen = lr.ProfileData.Length;
            }
            if (maxLen == 0) return chart;

            int margin = 40;
            int plotW = chartW - margin * 2;
            int plotH = chartH - margin * 2;

            // Draw grid
            Cv2.Rectangle(chart, new Point(margin, margin), new Point(margin + plotW, margin + plotH),
                new Scalar(60, 60, 70), 1);

            // Draw edge threshold reference line (gradient scale maps to same 0-255 range)
            int threshY = margin + plotH - (int)(settings.GapThreshold / 255.0 * plotH);
            Cv2.Line(chart, new Point(margin, threshY), new Point(margin + plotW, threshY),
                new Scalar(0, 200, 255), 1);
            Cv2.PutText(chart, $"Edge={settings.GapThreshold}", new Point(margin + plotW + 2, threshY + 4),
                HersheyFonts.HersheySimplex, 0.35, new Scalar(0, 200, 255), 1);

            // Colors for up to 3 lines
            Scalar[] colors = new Scalar[]
            {
                new Scalar(255, 100, 100),   // Blue-ish
                new Scalar(100, 255, 100),   // Green-ish
                new Scalar(100, 100, 255)    // Red-ish
            };

            // Draw profiles
            for (int li = 0; li < lineResults.Count; li++)
            {
                var lr = lineResults[li];
                if (lr.ProfileData == null || lr.ProfileData.Length < 2) continue;

                var color = colors[li % colors.Length];
                int len = lr.ProfileData.Length;

                for (int p = 1; p < len; p++)
                {
                    int x1 = margin + (int)((p - 1.0) / maxLen * plotW);
                    int y1 = margin + plotH - (int)(lr.ProfileData[p - 1] / 255.0 * plotH);
                    int x2 = margin + (int)((double)p / maxLen * plotW);
                    int y2 = margin + plotH - (int)(lr.ProfileData[p] / 255.0 * plotH);
                    Cv2.Line(chart, new Point(x1, y1), new Point(x2, y2), color, 1);
                }

                // Draw vertical edge markers
                if (lr.GapStart > 0 || lr.GapEnd > 0)
                {
                    int ex1 = margin + (int)((double)lr.GapStart / maxLen * plotW);
                    int ex2 = margin + (int)((double)lr.GapEnd / maxLen * plotW);
                    // Cyan dashed vertical lines at edge positions
                    Cv2.Line(chart, new Point(ex1, margin), new Point(ex1, margin + plotH),
                        new Scalar(255, 255, 0), 1);
                    Cv2.Line(chart, new Point(ex2, margin), new Point(ex2, margin + plotH),
                        new Scalar(255, 255, 0), 1);
                }

                // Label
                Cv2.PutText(chart, $"L{li + 1}: dist={lr.GapWidthPx:F1}px",
                    new Point(margin + 5, margin + 15 + li * 15),
                    HersheyFonts.HersheySimplex, 0.4, color, 1);
            }

            // Axis labels
            Cv2.PutText(chart, "0", new Point(margin - 15, margin + plotH + 5),
                HersheyFonts.HersheySimplex, 0.3, new Scalar(150, 150, 160), 1);
            Cv2.PutText(chart, "255", new Point(margin - 30, margin + 10),
                HersheyFonts.HersheySimplex, 0.3, new Scalar(150, 150, 160), 1);
            Cv2.PutText(chart, "Intensity Profile", new Point(chartW / 2 - 50, chartH - 5),
                HersheyFonts.HersheySimplex, 0.4, new Scalar(150, 150, 160), 1);

            return chart;
        }

        /// <summary>
        /// Create edge detection view using Canny edges.
        /// </summary>
        private Mat CreateEdgeView(Mat blurredGray, InspectionSettings settings)
        {
            var edges = new Mat();
            int high = Math.Max(settings.GapThreshold, 10);
            int low = high / 2;
            Cv2.Canny(blurredGray, edges, low, high);
            var bgr = new Mat();
            Cv2.CvtColor(edges, bgr, ColorConversionCodes.GRAY2BGR);
            edges.Dispose();
            return bgr;
        }

        /// <summary>
        /// Reset EMA state. Call on model switch to prevent cross-model bleed.
        /// </summary>
        public void ResetEma()
        {
            _emaGapWidths = null;
            _emaLineCount = 0;
        }
    }
}
