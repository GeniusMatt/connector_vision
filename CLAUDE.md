# Connector Vision

## Purpose
WPF vision inspection app to check if connectors are fully inserted by measuring gap width via ROI measurement lines.

## Tech Stack
- .NET Framework 4.8 (WPF)
- OpenCvSharp4 4.10.0.20241108
- DirectShowLib.Standard 2.1.0
- MSBuild (Visual Studio 2022)

## Architecture Rules
- 4-page architecture: Test / Model / Setting / Manual
- All XAML in .xaml files, no UI creation in code-behind
- Dark theme (#1A1A2E background)
- Camera auto-opens on startup
- Normalized coordinates (0.0-1.0) for measurement lines
- DataContractJsonSerializer with KnownType for MeasurementLine

## Folder Structure
```
connector vision\
├── Connector Vision.sln
├── CLAUDE.md
└── Connector Vision\
    ├── App.xaml / .cs
    ├── MainWindow.xaml / .cs
    ├── Pages\
    │   ├── TestPage.xaml / .cs      (production runtime, OK/NG, statistics)
    │   ├── ModelPage.xaml / .cs     (line drawing, model management, preview)
    │   ├── SettingPage.xaml / .cs   (camera device/resolution config)
    │   └── ManualPage.xaml / .cs    (2x2 diagnostic grid, live inspection)
    ├── Services\
    │   ├── CameraService.cs         (DSHOW capture, WriteableBitmap display)
    │   ├── DirectShowHelper.cs      (device enumeration, property pages)
    │   ├── InspectionService.cs     (gap measurement: Bresenham + dark valley)
    │   └── SoundService.cs          (NG alert wav playback)
    ├── Models\
    │   ├── InspectionSettings.cs    (camera props + measurement lines + gap params)
    │   ├── InspectionResult.cs      (per-line gap results, visualization Mats)
    │   └── MeasurementLine.cs       (normalized X1/Y1/X2/Y2 endpoints)
    ├── Helpers\
    │   ├── BitmapHelper.cs          (Mat↔BitmapSource, WriteableBitmap updates)
    │   ├── SettingsManager.cs       (JSON load/save, model management)
    │   └── ZoomPanHelper.cs         (mouse wheel zoom, right-click pan)
    ├── Properties\AssemblyInfo.cs
    └── Resources\ng_alert.wav
```

## Key Entry Points
- `MainWindow.xaml.cs` — App startup, camera init, page navigation
- `InspectionService.Inspect()` — Core gap measurement algorithm
- `ModelPage.ImgCamera_Click()` — Line drawing coordinate mapping

## Current Status
- v1.0 initial implementation complete
- All 4 pages implemented
- Build succeeds (Release)

## Gotchas
- ZoomPanHelper RenderTransform must be inverted when mapping click coordinates back to image pixels
- SettingsManager uses KnownTypes for MeasurementLine serialization
- Max 3 measurement lines per model
- Blur size must be odd (auto-corrected in InspectionService)
