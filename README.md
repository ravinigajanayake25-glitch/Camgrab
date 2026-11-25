# Camgrab & Milestone MIP SDK Integration

Camgrab is a dual-capability camera tool:
1. **Camgrab Console App** (`Program.cs`) — captures still images from RTSP streams via `ffmpeg` and stores them in `shots/`.
2. **MediaLiveViewer GUI** (`MediaLiveViewer/`) — a native Milestone MIP SDK application that logs into Milestone XProtect VMS, inventories connected cameras, builds the Open Network Bridge RTSP URLs (`rtsp://10.64.30.142:554/live/GUID`), and plays live video streams directly in the GUI.

---

## 1. Project Organization (Cleaned Repository)

The external `mipsdk-samples-component` repository clone has been cleaned up and consolidated:
- All unnecessary sample projects and git metadata from the clone have been removed.
- Only the required `MediaLiveViewer` project is retained inside `MediaLiveViewer/`.
- No nested `.git` repositories or submodules remain.

```text
camgrab/
├── MediaLiveViewer/              <-- Milestone MIP SDK GUI application
│   ├── CameraInventoryForm.cs    <-- Camera inventory, RTSP URL generator & live GUI player
│   ├── MainForm.cs               <-- Advanced Milestone live stream viewer
│   ├── Program.cs                <-- Application entry point & login handler
│   └── MediaLiveViewer.csproj    <-- WinForms / WPF (.NET Framework 4.7.2)
├── Program.cs                    <-- .NET 8 console snapshot grabber using ffmpeg
├── camgrab.csproj                <-- Console project file
├── README.md
└── shots/                        <-- Snapshot output directory (organized by date: shots/<yyyy-MM-dd>/)
```

---

## 2. Milestone Camera RTSP URLs

### Why Milestone Does Not Provide Direct RTSP URLs
Milestone XProtect VMS manages and records cameras internally via the Milestone Recording Server protocol. Therefore, cameras in the VMS do not store a plain camera RTSP URL property in MIP SDK.

### Milestone Open Network Bridge Format
To expose camera streams as standard RTSP endpoints, Milestone provides the **Open Network Bridge (ONB)**. The stream URL format provided by your instructor is:

```text
rtsp://<Bridge-IP>:<Port>/live/<Camera-GUID>
```

For your server (`10.64.30.142` on port `554`):
- **Camera 61252WLKR**:
  `rtsp://10.64.30.142:554/live/9ac405ca-1db1-4696-9d7f-ebccc74a9b18`
- **Camera 61253WLKL**:
  `rtsp://10.64.30.142:554/live/229802e9-9bb3-4e89-aec7-005c3e44fb94`

*(If authentication is enabled on the Open Network Bridge, prepend credentials: `rtsp://<username>:<password>@10.64.30.142:554/live/<GUID>`)*

---

## 3. MediaLiveViewer GUI Features

When running `MediaLiveViewer`:
1. **Login Dialog**: Enter your Milestone VMS server credentials.
2. **Connected Cameras Table**:
   - Lists all cameras, their GUIDs, servers, and enabled status.
   - Includes a dedicated **RTSP Stream URL** column with the full `rtsp://10.64.30.142:554/live/{GUID}` link.
   - **RTSP Bridge input**: Allows changing the bridge host/port dynamically (e.g. `10.64.30.142:554`).
3. **Live Camera Video Player in GUI**:
   - Displays real-time live video for any selected camera directly in the window using Milestone's `JPEGLiveSource`.
   - **▶ Start Live Feed / ⏹ Stop Feed**: Toggles live video playback.
   - **Copy URL**: Copies the camera's RTSP URL to the clipboard with one click.
   - **📸 Save Snapshot**: Grabs the current video frame and saves it to `shots/`.
   - **🔍 Open Full Viewer**: Opens `MainForm` with resolution, FPS, hardware acceleration, and stream selection controls.

---

## 4. How to Run

### Running the MediaLiveViewer GUI
Open a terminal in `camgrab` or Visual Studio and run:
```powershell
dotnet run --project MediaLiveViewer\MediaLiveViewer.csproj
```
or open `MediaLiveViewer.csproj` directly in Visual Studio and press **F5**.

### Running the Console Snapshot Grabber

Camgrab connects to each configured RTSP stream with FFmpeg, specifically extracts
**I-Frames (Keyframes / Intra-frames)** for high performance and clean images, and saves
a JPEG every **5 seconds** until stopped with **Ctrl+C**. Files are written to
date-specific subdirectories inside the `shots/` folder in the project (`shots/<yyyy-MM-dd>/`).

#### What is an "I-Frame"?
In H.264/H.265 video compression, streams consist of:
- **I-Frames (Intra-frames / Keyframes)**: Complete, standalone photographic frames (like a JPEG) requiring no other frame to decode.
- **P-Frames & B-Frames**: Delta/difference frames that store only movements relative to other frames.

FFmpeg uses `-skip_frame nokey` before `-i` to skip all delta frames and immediately decode the first incoming **I-Frame**. This minimizes CPU/network overhead and eliminates decoding artifacts. *(Note: If your supervisor was referring to web dashboards, an HTML `<iframe>` is used to embed web pages).*

#### How to Run
```powershell
dotnet run
```

#### Customizing the Capture Interval
To customize the gap duration (defaults to 10 seconds):
```powershell
$env:CAPTURE_INTERVAL_SECONDS = "10"
dotnet run
```

#### Filtering Video Frames by Time Range (Date & Time Window)
You can filter all saved video frames for a specific time range both from the **GUI** and the **CLI**:

1. **In MediaLiveViewer GUI**:
   - Click the green **"🕒 Filter Frames by Time"** button in the main toolbar.
   - Choose your **Camera**, **From Date & Time**, and **To Date & Time** (or pick a Quick Preset).
   - Click **"🔍 Filter Video Frames"** to inspect frames, scrub with the playback slider, play at 1–20 FPS, export to a folder, or render directly into an MP4 video clip.

2. **In Camgrab CLI**:
   ```powershell
   # Filter all frames within a time window
   dotnet run -- --filter "2026-10-14 10:00:00" "2026-10-14 11:00:00"

   # Filter for a specific camera
   dotnet run -- --filter "2026-10-14 10:00:00" "2026-10-14 11:00:00" 61252WLKR

   # Filter and export matching frames to a folder
   dotnet run -- --filter "2026-10-14 10:00" "2026-10-14 11:00" 61252WLKR --export ./filtered_shots
   ```

#### Retrieving Image Date and Time
Every snapshot captures and logs the exact Date (`YYYY-MM-DD`) and Time (`HH:MM:SS.fff`):
1. **At Capture**: Printed in real time to the console and saved to `shots/snapshots_metadata.json`.
2. **List All Snapshots**: View all saved images with their camera, retrieved date, time, and file size:
   ```powershell
   dotnet run -- --list
   ```
3. **Inspect a Specific Image**: Retrieve date, time, ISO timestamp, and metadata for any image file:
   ```powershell
   dotnet run -- --info shots/cam46_145727.jpg
   ```

#### Configuration Options
Credentials default to the configured Milestone Open Network Bridge user (`mipuser`), but can be overridden anytime:

* **CLI Options:**
  ```powershell
  # Capture all configured cameras
  dotnet run

  # Capture only one specific camera
  dotnet run -- --camera 61252WLKR

  # Custom credentials or interval
  dotnet run -- --user "mipuser" --pass "your-password" --interval 10
  ```
* **Environment Variables:**
  ```powershell
  $env:CAM_USER = "mipuser"
  $env:CAM_PASS = "your-password"
  $env:CAPTURE_INTERVAL_SECONDS = "10"
  dotnet run
  ```

#### Built-in RTSP Authentication Proxy
Milestone Open Network Bridge sends dual `MD5` and `SHA-256` authentication challenges. Because FFmpeg has a known bug handling simultaneous `SHA-256` RTSP Digest challenges, Camgrab automatically runs an internal transparent proxy on `127.0.0.1` that sanitizes the challenge so FFmpeg authenticates over standard `MD5` without failing with 401 Unauthorized.

---

## 5. API Upload Specification (Matches curl Command)

Every captured snapshot is uploaded via HTTP POST multipart form-data matching:
```bash
curl -F camera_name=Front-Door -F date=2026-10-08 -F time=09:30:15 -F image=@photo.jpg http://10.64.30.96:8000/upload
```
- `camera_name`: Form field with camera stream name
- `date`: Form field in `YYYY-MM-DD` format
- `time`: Form field in `HH:MM:SS` format
- `image`: The JPEG image binary file with burned-in OSD timestamp

---

## 6. Running as a Windows Service

### Configuration (`appsettings.json`)
Streams, intervals, and API URLs are configured in `appsettings.json`:
```json
{
  "SnapshotService": {
    "IntervalSeconds": 10,
    "OutputDirectory": "shots",
    "UploadApiUrl": "http://10.64.30.96:8000/upload",
    "BridgeHost": "10.64.30.142",
    "BridgePort": 554,
    "Credentials": {
      "Username": "mipuser",
      "Password": "M1l3T@123"
    },
    "Streams": [
      { "Name": "61252WLKR", "Guid": "9ac405ca-1db1-4696-9d7f-ebccc74a9b18", "Url": "" },
      { "Name": "61253WLKL", "Guid": "229802e9-9bb3-4e89-aec7-005c3e44fb94", "Url": "" },
      { "Name": "Front-Door", "Guid": "<guid-or-rtsp-url>", "Url": "" }
    ]
  }
}
```

### Installation (Run PowerShell as Administrator)
```powershell
# 1. Register the Windows Service
sc.exe create "CamGrabSnapshotService" binPath= "C:\Services\CamGrabService\camgrab.exe" start= auto

# 2. Start the Windows Service
sc.exe start "CamGrabSnapshotService"

# 3. Check status
sc.exe query "CamGrabSnapshotService"
```

To stop or remove the service:
```powershell
sc.exe stop "CamGrabSnapshotService"
sc.exe delete "CamGrabSnapshotService"
```
