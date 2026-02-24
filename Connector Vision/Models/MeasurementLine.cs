using System.Runtime.Serialization;

namespace Connector_Vision.Models
{
    [DataContract]
    public class MeasurementLine
    {
        // Normalized coordinates (0.0 - 1.0) so lines remain valid across resolution changes
        [DataMember] public double X1 { get; set; }
        [DataMember] public double Y1 { get; set; }
        [DataMember] public double X2 { get; set; }
        [DataMember] public double Y2 { get; set; }

        // Per-line gap width limits (in pixels)
        [DataMember] public int MinGapWidth { get; set; } = 0;
        [DataMember] public int MaxGapWidth { get; set; } = 20;

        public MeasurementLine() { }

        public MeasurementLine(double x1, double y1, double x2, double y2)
        {
            X1 = x1; Y1 = y1; X2 = x2; Y2 = y2;
        }

        public void ToPixelCoords(int frameWidth, int frameHeight,
            out int px1, out int py1, out int px2, out int py2)
        {
            px1 = (int)(X1 * frameWidth);
            py1 = (int)(Y1 * frameHeight);
            px2 = (int)(X2 * frameWidth);
            py2 = (int)(Y2 * frameHeight);
        }

        public override string ToString()
        {
            return $"({X1:F3},{Y1:F3}) → ({X2:F3},{Y2:F3})  [{MinGapWidth}-{MaxGapWidth}px]";
        }
    }
}
