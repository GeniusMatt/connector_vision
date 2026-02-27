using System.Collections.Generic;
using System.Runtime.Serialization;

namespace Connector_Vision.Models
{
    [DataContract]
    [KnownType(typeof(MeasurementLine))]
    public class InspectionSettings
    {
        // Camera settings
        [DataMember] public int CameraIndex { get; set; } = 0;
        [DataMember] public string CameraResolution { get; set; } = "Auto";
        [DataMember] public string CurrentModelName { get; set; } = "";

        // Camera hardware properties (focus, exposure, etc. - restored on startup)
        [DataMember] public bool CamPropertiesSaved { get; set; } = false;
        [DataMember] public double CamFocus { get; set; }
        [DataMember] public double CamExposure { get; set; }
        [DataMember] public double CamBrightness { get; set; }
        [DataMember] public double CamContrast { get; set; }
        [DataMember] public double CamSaturation { get; set; }
        [DataMember] public double CamGain { get; set; }
        [DataMember] public double CamWhiteBalance { get; set; }
        [DataMember] public double CamSharpness { get; set; }
        [DataMember] public double CamBacklightComp { get; set; }
        [DataMember] public double CamAutoFocus { get; set; }
        [DataMember] public double CamAutoExposure { get; set; }

        // Measurement lines (1-3 lines drawn across the connector junction)
        [DataMember] public List<MeasurementLine> MeasurementLines { get; set; } = new List<MeasurementLine>();

        // Edge detection parameters
        [DataMember] public int GapThreshold { get; set; } = 80;
        [DataMember] public int GaussianBlurSize { get; set; } = 5;
        [DataMember] public int EdgeMarginPercent { get; set; } = 10;
        [DataMember] public int EdgeDetectionMode { get; set; } = 0; // 0=Strongest Pair, 1=First & Last

        public void CopyInspectionParametersFrom(InspectionSettings other)
        {
            MeasurementLines = new List<MeasurementLine>();
            if (other.MeasurementLines != null)
            {
                foreach (var line in other.MeasurementLines)
                {
                    var copy = new MeasurementLine(line.X1, line.Y1, line.X2, line.Y2);
                    copy.MinGapWidth = line.MinGapWidth;
                    copy.MaxGapWidth = line.MaxGapWidth;
                    MeasurementLines.Add(copy);
                }
            }
            GapThreshold = other.GapThreshold;
            GaussianBlurSize = other.GaussianBlurSize;
            EdgeMarginPercent = other.EdgeMarginPercent;
            EdgeDetectionMode = other.EdgeDetectionMode;
        }
    }
}
