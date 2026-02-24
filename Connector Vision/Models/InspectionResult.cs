using System.Collections.Generic;
using OpenCvSharp;

namespace Connector_Vision.Models
{
    public class LineResult
    {
        public int LineIndex { get; set; }
        public double GapWidthPx { get; set; }
        public int GapStart { get; set; }
        public int GapEnd { get; set; }
        public byte[] ProfileData { get; set; }
        public bool IsOk { get; set; }
    }

    public class InspectionResult
    {
        public bool IsOk { get; set; }
        public List<LineResult> LineResults { get; set; } = new List<LineResult>();
        public double MaxGapWidthFound { get; set; }
        public double InspectionTimeMs { get; set; }

        // Visualization Mats
        public Mat AnnotatedFrame { get; set; }
        public Mat GrayscaleFrame { get; set; }
        public Mat ProfileFrame { get; set; }
        public Mat ThresholdFrame { get; set; }
    }
}
