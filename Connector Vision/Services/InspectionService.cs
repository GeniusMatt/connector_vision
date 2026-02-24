using System;
using System.Collections.Generic;
using System.Diagnostics;
using OpenCvSharp;
using Connector_Vision.Models;

namespace Connector_Vision.Services
{
    public class InspectionService
    {
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

                // 2. For each measurement line: extract profile and find edge distance
                for (int i = 0; i < settings.MeasurementLines.Count; i++)
                {
                    var line = settings.MeasurementLines[i];
                    line.ToPixelCoords(w, h, out int px1, out int py1, out int px2, out int py2);

                    byte[] profile = ExtractProfile(blurred, px1, py1, px2, py2);
                    FindEdgeDistance(profile, settings.GapThreshold,
                        settings.EdgeMarginPercent, settings.MinEdgeSeparation,
                        settings.EdgeDetectionMode,
                        out int edge1Pos, out int edge2Pos, out int gapWidth);

                    bool lineOk = gapWidth >= line.MinGapWidth && gapWidth <= line.MaxGapWidth;
                    if (!lineOk) allOk = false;
                    if (gapWidth > maxGap) maxGap = gapWidth;

                    result.LineResults.Add(new LineResult
                    {
                        LineIndex = i,
                        GapWidthPx = gapWidth,
                        GapStart = edge1Pos,
                        GapEnd = edge2Pos,
                        ProfileData = profile,
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
        /// Find the distance between the two strongest edges (intensity transitions) in the profile.
        /// Uses gradient magnitude to detect edges. The gap = distance between the two strongest edges.
        /// </summary>
        private void FindEdgeDistance(byte[] profile, int edgeThreshold,
            int marginPercent, int minEdgeSep, int mode,
            out int edge1Pos, out int edge2Pos, out int gapWidth)
        {
            edge1Pos = 0;
            edge2Pos = 0;
            gapWidth = 0;

            if (profile.Length < 5) return;

            int len = profile.Length;

            // Apply edge margin: skip first/last N% of the profile
            int margin = (int)(len * marginPercent / 100.0);
            int searchStart = Math.Max(1, margin);
            int searchEnd = Math.Min(len - 2, len - 1 - margin);
            if (searchStart >= searchEnd) return;

            // Compute absolute gradient using central difference
            int[] absGrad = new int[len];
            for (int i = 1; i < len - 1; i++)
                absGrad[i] = Math.Abs(profile[i + 1] - profile[i - 1]);

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

                if (firstEdge >= 0 && lastEdge >= 0 && lastEdge > firstEdge)
                {
                    edge1Pos = firstEdge;
                    edge2Pos = lastEdge;
                    gapWidth = lastEdge - firstEdge;
                }
                else if (firstEdge >= 0)
                {
                    edge1Pos = firstEdge;
                    edge2Pos = firstEdge;
                    gapWidth = 0;
                }
            }
            else
            {
                // Strongest Pair mode: find two strongest edges
                int strongest1Pos = -1;
                int strongest1Val = 0;
                for (int i = searchStart; i <= searchEnd; i++)
                {
                    if (absGrad[i] >= edgeThreshold && absGrad[i] > strongest1Val)
                    {
                        strongest1Val = absGrad[i];
                        strongest1Pos = i;
                    }
                }

                if (strongest1Pos < 0) return;

                int minSep = Math.Max(minEdgeSep, 3);
                int strongest2Pos = -1;
                int strongest2Val = 0;
                for (int i = searchStart; i <= searchEnd; i++)
                {
                    if (Math.Abs(i - strongest1Pos) < minSep) continue;
                    if (absGrad[i] >= edgeThreshold && absGrad[i] > strongest2Val)
                    {
                        strongest2Val = absGrad[i];
                        strongest2Pos = i;
                    }
                }

                if (strongest2Pos >= 0)
                {
                    edge1Pos = Math.Min(strongest1Pos, strongest2Pos);
                    edge2Pos = Math.Max(strongest1Pos, strongest2Pos);
                    gapWidth = edge2Pos - edge1Pos;
                }
                else
                {
                    edge1Pos = strongest1Pos;
                    edge2Pos = strongest1Pos;
                    gapWidth = 0;
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

                var lineResult = result.LineResults[i];
                var color = lineResult.IsOk ? new Scalar(0, 255, 0) : new Scalar(0, 0, 255);

                // Draw the measurement line
                Cv2.Line(annotated, new Point(px1, py1), new Point(px2, py2), color, 2);

                // Draw line endpoints (circles)
                Cv2.Circle(annotated, new Point(px1, py1), 5, color, -1);
                Cv2.Circle(annotated, new Point(px2, py2), 5, color, -1);

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

                    // Draw thick orange line over gap region
                    Cv2.Line(annotated, new Point(gx1, gy1), new Point(gx2, gy2),
                        new Scalar(0, 165, 255), 4);

                    // Draw cyan edge markers at detected edge positions
                    Cv2.Circle(annotated, new Point(gx1, gy1), 6, new Scalar(255, 255, 0), 2);
                    Cv2.Circle(annotated, new Point(gx2, gy2), 6, new Scalar(255, 255, 0), 2);
                }

                // Draw gap width label
                int labelX = (px1 + px2) / 2 + 10;
                int labelY = (py1 + py2) / 2 - 10;
                string label = $"L{i + 1}: {lineResult.GapWidthPx:F0}px [{line.MinGapWidth}-{line.MaxGapWidth}]";
                Cv2.PutText(annotated, label, new Point(labelX, labelY),
                    HersheyFonts.HersheySimplex, 0.6, color, 2);
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
                Cv2.PutText(chart, $"L{li + 1}: gap={lr.GapWidthPx:F0}px",
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
    }
}
