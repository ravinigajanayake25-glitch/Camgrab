using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace MediaLiveViewer
{
    public class TimeRangeFrameFilterForm : Form
    {
        private readonly string _shotsDirectory;
        private readonly List<FrameRecord> _currentResults = new List<FrameRecord>();

        // Controls
        private ComboBox _cameraCombo;
        private DateTimePicker _dtpStartDate;
        private DateTimePicker _dtpStartTime;
        private DateTimePicker _dtpEndDate;
        private DateTimePicker _dtpEndTime;
        private ComboBox _presetCombo;
        private Button _btnFilter;
        private Button _btnReset;

        private DataGridView _grid;
        private Label _lblSummary;
        private PictureBox _previewBox;
        private Label _lblFrameInfo;

        // Sequence Player Controls
        private Button _btnPlay;
        private ComboBox _fpsCombo;
        private TrackBar _slider;
        private System.Windows.Forms.Timer _playTimer;
        private int _currentIndex = -1;

        // Action Buttons
        private Button _btnOpenImage;
        private Button _btnShowInFolder;
        private Button _btnExportFrames;
        private Button _btnRenderMp4;
        private Button _btnPlayInVlc;
        private string _lastRenderedMp4Path;

        public class FrameRecord
        {
            public string CameraName { get; set; }
            public string FileName { get; set; }
            public string FilePath { get; set; }
            public DateTime Timestamp { get; set; }
            public long FileSizeBytes { get; set; }
            public string DateString => Timestamp.ToString("yyyy-MM-dd");
            public string TimeString => Timestamp.ToString("HH:mm:ss.fff");
        }

        public TimeRangeFrameFilterForm(
            string shotsDirectory,
            string initialCamera = null,
            IEnumerable<VideoOS.Platform.Item> availableCameras = null,
            DateTime? initialStart = null,
            DateTime? initialEnd = null)
        {
            _shotsDirectory = Directory.Exists(shotsDirectory)
                ? shotsDirectory
                : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "shots");

            InitializeComponents(initialCamera, availableCameras);

            if (initialStart.HasValue && initialEnd.HasValue)
            {
                SetDateRange(initialStart.Value, initialEnd.Value);
            }
            else
            {
                LoadDefaultTimeRange();
            }

            Shown += async (s, e) => await ExecuteFilterAsync();
        }

        private void InitializeComponents(string initialCamera, IEnumerable<VideoOS.Platform.Item> availableCameras)
        {
            Text = "Surveillance Video Frame Filter — Time Range Search";
            Width = 1180;
            Height = 780;
            MinimumSize = new Size(950, 650);
            StartPosition = FormStartPosition.CenterParent;
            Font = new Font("Segoe UI", 9f);
            BackColor = Color.FromArgb(248, 250, 252);

            // ==================== Top Header & Filter Panel ====================
            var topPanel = new Panel
            {
                Dock = DockStyle.Top,
                Height = 135,
                Padding = new Padding(12, 10, 12, 8),
                BackColor = Color.White
            };

            var headerLabel = new Label
            {
                Text = "🕒 Surveillance Video Frame Filter (Time-Range Search)",
                Font = new Font("Segoe UI", 12f, FontStyle.Bold),
                ForeColor = Color.FromArgb(15, 23, 42),
                AutoSize = true,
                Location = new Point(12, 8)
            };

            var subHeaderLabel = new Label
            {
                Text = "Filter, inspect, and playback video snapshot frames matching an exact camera and date/time window.",
                Font = new Font("Segoe UI", 8.5f),
                ForeColor = Color.FromArgb(100, 116, 139),
                AutoSize = true,
                Location = new Point(14, 32)
            };

            // Filter row 1: Camera + Presets
            var lblCam = new Label { Text = "Camera:", AutoSize = true, Location = new Point(14, 60) };
            _cameraCombo = new ComboBox
            {
                Location = new Point(72, 57),
                Width = 160,
                DropDownStyle = ComboBoxStyle.DropDownList
            };
            PopulateCamerasList(initialCamera, availableCameras);

            var lblPreset = new Label { Text = "Quick Preset:", AutoSize = true, Location = new Point(250, 60) };
            _presetCombo = new ComboBox
            {
                Location = new Point(332, 57),
                Width = 190,
                DropDownStyle = ComboBoxStyle.DropDownList
            };
            _presetCombo.Items.AddRange(new object[]
            {
                "-- Select Time Preset --",
                "Today (Full Day)",
                "Yesterday",
                "Last 1 Hour",
                "Last 6 Hours",
                "Last 24 Hours",
                "October 14, 2026 (Active Feed)",
                "October 20, 2026 (Active Feed)",
                "October 23, 2026 (Active Feed)",
                "October 24: 02:12 - 02:34 (User Clip Test)",
                "October 24, 2026 (Full Day)",
                "October 25, 2026 (Full Day)",
                "October 26, 2026 (Active Feed)",
                "All Available History"
            });
            _presetCombo.SelectedIndex = 0;
            _presetCombo.SelectedIndexChanged += async (s, e) =>
            {
                OnPresetChanged(s, e);
                await ExecuteFilterAsync();
            };

            // Filter row 2: Start & End DateTime
            var lblFrom = new Label { Text = "From Date & Time:", AutoSize = true, Location = new Point(14, 96), Font = new Font(Font, FontStyle.Bold) };
            _dtpStartDate = new DateTimePicker
            {
                Format = DateTimePickerFormat.Short,
                Location = new Point(135, 93),
                Width = 110
            };
            _dtpStartTime = new DateTimePicker
            {
                Format = DateTimePickerFormat.Time,
                ShowUpDown = true,
                Location = new Point(252, 93),
                Width = 95
            };

            var lblTo = new Label { Text = "To Date & Time:", AutoSize = true, Location = new Point(365, 96), Font = new Font(Font, FontStyle.Bold) };
            _dtpEndDate = new DateTimePicker
            {
                Format = DateTimePickerFormat.Short,
                Location = new Point(470, 93),
                Width = 110
            };
            _dtpEndTime = new DateTimePicker
            {
                Format = DateTimePickerFormat.Time,
                ShowUpDown = true,
                Location = new Point(587, 93),
                Width = 95
            };

            _btnFilter = new Button
            {
                Text = "🔍 Filter Video Frames",
                Location = new Point(700, 89),
                Width = 175,
                Height = 32,
                BackColor = Color.FromArgb(16, 185, 129),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font(Font, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            _btnFilter.FlatAppearance.BorderSize = 0;
            _btnFilter.Click += async (s, e) => await ExecuteFilterAsync();

            _btnReset = new Button
            {
                Text = "Reset Range",
                Location = new Point(885, 89),
                Width = 100,
                Height = 32,
                BackColor = Color.FromArgb(226, 232, 240),
                ForeColor = Color.FromArgb(30, 41, 59),
                FlatStyle = FlatStyle.Flat,
                Cursor = Cursors.Hand
            };
            _btnReset.FlatAppearance.BorderSize = 0;
            _btnReset.Click += (s, e) => LoadDefaultTimeRange();

            topPanel.Controls.Add(headerLabel);
            topPanel.Controls.Add(subHeaderLabel);
            topPanel.Controls.Add(lblCam);
            topPanel.Controls.Add(_cameraCombo);
            topPanel.Controls.Add(lblPreset);
            topPanel.Controls.Add(_presetCombo);
            topPanel.Controls.Add(lblFrom);
            topPanel.Controls.Add(_dtpStartDate);
            topPanel.Controls.Add(_dtpStartTime);
            topPanel.Controls.Add(lblTo);
            topPanel.Controls.Add(_dtpEndDate);
            topPanel.Controls.Add(_dtpEndTime);
            topPanel.Controls.Add(_btnFilter);
            topPanel.Controls.Add(_btnReset);

            // ==================== Main SplitContainer ====================
            var splitMain = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Vertical,
                SplitterDistance = 560
            };

            // -------------------- Left: Results Grid & Summary --------------------
            var leftPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12, 8, 6, 12) };

            _lblSummary = new Label
            {
                Dock = DockStyle.Top,
                Height = 26,
                Font = new Font(Font, FontStyle.Bold),
                ForeColor = Color.FromArgb(30, 41, 59),
                Text = "Ready to filter. Choose a date and time range above and click 'Filter Video Frames'."
            };

            _grid = new DataGridView
            {
                Dock = DockStyle.Fill,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                ReadOnly = true,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                MultiSelect = false,
                AutoGenerateColumns = false,
                RowHeadersVisible = false,
                BackgroundColor = Color.White,
                BorderStyle = BorderStyle.Fixed3D,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill
            };

            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Index", HeaderText = "#", FillWeight = 10, ReadOnly = true });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Camera", HeaderText = "Camera", FillWeight = 25, ReadOnly = true });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Date", HeaderText = "Date", FillWeight = 22, ReadOnly = true });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Time", HeaderText = "Time (Server)", FillWeight = 25, ReadOnly = true });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Size", HeaderText = "Size (KB)", FillWeight = 18, ReadOnly = true });

            _grid.SelectionChanged += OnGridSelectionChanged;
            _grid.CellDoubleClick += (s, e) => OpenCurrentImage();

            leftPanel.Controls.Add(_grid);
            leftPanel.Controls.Add(_lblSummary);
            splitMain.Panel1.Controls.Add(leftPanel);

            // -------------------- Right: Preview, Playback & Actions --------------------
            var rightPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(6, 8, 12, 12) };

            // Preview Box
            _previewBox = new PictureBox
            {
                Dock = DockStyle.Fill,
                BackColor = Color.Black,
                SizeMode = PictureBoxSizeMode.Zoom,
                BorderStyle = BorderStyle.FixedSingle
            };

            _lblFrameInfo = new Label
            {
                Dock = DockStyle.Bottom,
                Height = 28,
                Font = new Font("Consolas", 9f),
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.FromArgb(15, 23, 42),
                Text = "No frame selected."
            };

            // Player Panel
            var playerPanel = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 65,
                Padding = new Padding(0, 4, 0, 4)
            };

            _slider = new TrackBar
            {
                Dock = DockStyle.Top,
                Height = 30,
                Minimum = 0,
                Maximum = 0,
                TickStyle = TickStyle.None
            };
            _slider.Scroll += (s, e) =>
            {
                if (_slider.Value >= 0 && _slider.Value < _currentResults.Count)
                {
                    SelectFrameIndex(_slider.Value);
                }
            };

            var playerButtons = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.LeftToRight,
                Padding = new Padding(0, 4, 0, 0)
            };

            _btnPlay = new Button
            {
                Text = "▶ Play Sequence",
                Width = 125,
                Height = 26,
                BackColor = Color.FromArgb(2, 132, 199),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font(Font, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            _btnPlay.FlatAppearance.BorderSize = 0;
            _btnPlay.Click += ToggleSequencePlay;

            var lblSpeed = new Label { Text = "Speed:", AutoSize = true, Padding = new Padding(6, 5, 0, 0) };
            _fpsCombo = new ComboBox
            {
                Width = 110,
                DropDownStyle = ComboBoxStyle.DropDownList
            };
            _fpsCombo.Items.AddRange(new object[] { "1 Frame/sec", "2 Frames/sec", "5 FPS (Real-time)", "10 FPS", "20 FPS" });
            _fpsCombo.SelectedIndex = 2; // 5 FPS
            _fpsCombo.SelectedIndexChanged += (s, e) => UpdatePlayTimerInterval();

            playerButtons.Controls.Add(_btnPlay);
            playerButtons.Controls.Add(lblSpeed);
            playerButtons.Controls.Add(_fpsCombo);

            playerPanel.Controls.Add(playerButtons);
            playerPanel.Controls.Add(_slider);

            // Action Buttons Panel
            var actionPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                MinimumSize = new Size(0, 75),
                FlowDirection = FlowDirection.LeftToRight,
                Padding = new Padding(0, 6, 0, 6),
                WrapContents = true
            };

            _btnOpenImage = new Button
            {
                Text = "🖼 Open Image",
                Width = 115,
                Height = 28,
                Cursor = Cursors.Hand
            };
            _btnOpenImage.Click += (s, e) => OpenCurrentImage();

            _btnShowInFolder = new Button
            {
                Text = "📂 Reveal in Folder",
                Width = 135,
                Height = 28,
                Cursor = Cursors.Hand
            };
            _btnShowInFolder.Click += (s, e) => ShowCurrentImageInFolder();

            _btnExportFrames = new Button
            {
                Text = "💾 Export Filtered Frames...",
                Width = 180,
                Height = 28,
                BackColor = Color.FromArgb(79, 70, 229),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font(Font, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            _btnExportFrames.FlatAppearance.BorderSize = 0;
            _btnExportFrames.Click += (s, e) => ExportFilteredFrames();

            _btnRenderMp4 = new Button
            {
                Text = "🎬 Render Video (MP4)...",
                Width = 165,
                Height = 28,
                BackColor = Color.FromArgb(234, 88, 12),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font(Font, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            _btnRenderMp4.FlatAppearance.BorderSize = 0;
            _btnRenderMp4.Click += async (s, e) => await RenderFilteredFramesToMp4Async();

            _btnPlayInVlc = new Button
            {
                Text = "▶ Play Clip in VLC",
                Width = 150,
                Height = 28,
                BackColor = Color.FromArgb(2, 132, 199),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font(Font, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            _btnPlayInVlc.FlatAppearance.BorderSize = 0;
            _btnPlayInVlc.Click += async (s, e) => await PlayFilteredClipInVlcAsync();

            var btnTrimVideo = new Button
            {
                Text = "✂️ Trim Video File...",
                Width = 150,
                Height = 28,
                BackColor = Color.FromArgb(14, 165, 233),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font(Font, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            btnTrimVideo.FlatAppearance.BorderSize = 0;
            btnTrimVideo.Click += (s, e) => ShowTrimVideoDialog();

            actionPanel.Controls.Add(_btnOpenImage);
            actionPanel.Controls.Add(_btnShowInFolder);
            actionPanel.Controls.Add(_btnExportFrames);
            actionPanel.Controls.Add(_btnRenderMp4);
            actionPanel.Controls.Add(_btnPlayInVlc);
            actionPanel.Controls.Add(btnTrimVideo);

            rightPanel.Controls.Add(_previewBox);
            rightPanel.Controls.Add(_lblFrameInfo);
            rightPanel.Controls.Add(playerPanel);
            rightPanel.Controls.Add(actionPanel);

            splitMain.Panel2.Controls.Add(rightPanel);

            Controls.Add(splitMain);
            Controls.Add(topPanel);

            _playTimer = new System.Windows.Forms.Timer();
            _playTimer.Tick += OnPlayTimerTick;
            UpdatePlayTimerInterval();

            FormClosing += (s, e) =>
            {
                _playTimer?.Stop();
                _playTimer?.Dispose();
                DisposePreviewImage();
            };
        }

        private void PopulateCamerasList(string initialCamera, IEnumerable<VideoOS.Platform.Item> availableCameras)
        {
            _cameraCombo.Items.Clear();
            _cameraCombo.Items.Add("[All Cameras]");

            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (availableCameras != null)
            {
                foreach (var cam in availableCameras)
                {
                    if (!string.IsNullOrWhiteSpace(cam.Name)) names.Add(cam.Name);
                }
            }

            // Also check cameras discovered from shots directory
            try
            {
                if (Directory.Exists(_shotsDirectory))
                {
                    var files = Directory.GetFiles(_shotsDirectory, "*.jpg", SearchOption.AllDirectories).Take(200);
                    foreach (var f in files)
                    {
                        var name = Path.GetFileName(f);
                        int underscore = name.IndexOf('_');
                        if (underscore > 0)
                        {
                            names.Add(name.Substring(0, underscore));
                        }
                    }
                }
            }
            catch { }

            names.Add("61252WLKR");
            names.Add("61253WLKL");

            foreach (var n in names.OrderBy(x => x))
            {
                _cameraCombo.Items.Add(n);
            }

            if (!string.IsNullOrEmpty(initialCamera) && _cameraCombo.Items.Contains(initialCamera))
            {
                _cameraCombo.SelectedItem = initialCamera;
            }
            else
            {
                _cameraCombo.SelectedIndex = 0;
            }
        }

        private void LoadDefaultTimeRange()
        {
            // Default: Most recent date found in shots, or today
            DateTime latestDate = DateTime.Today;
            try
            {
                if (Directory.Exists(_shotsDirectory))
                {
                    var subdirs = Directory.GetDirectories(_shotsDirectory)
                        .Select(Path.GetFileName)
                        .Where(n => Regex.IsMatch(n, @"^\d{4}-\d{2}-\d{2}$"))
                        .OrderByDescending(n => n)
                        .ToList();

                    if (subdirs.Any() && DateTime.TryParseExact(subdirs.First(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
                    {
                        latestDate = d;
                    }
                }
            }
            catch { }

            _dtpStartDate.Value = latestDate;
            _dtpStartTime.Value = latestDate.Date.AddHours(9); // 09:00:00
            _dtpEndDate.Value = latestDate;
            _dtpEndTime.Value = latestDate.Date.AddHours(17);  // 17:00:00
        }

        private void OnPresetChanged(object sender, EventArgs e)
        {
            DateTime now = DateTime.Now;
            switch (_presetCombo.SelectedIndex)
            {
                case 1: // Today
                    SetDateRange(DateTime.Today, DateTime.Today.Date.AddDays(1).AddTicks(-1));
                    break;
                case 2: // Yesterday
                    SetDateRange(DateTime.Today.AddDays(-1), DateTime.Today.AddTicks(-1));
                    break;
                case 3: // Last 1 Hour
                    SetDateRange(now.AddHours(-1), now);
                    break;
                case 4: // Last 6 Hours
                    SetDateRange(now.AddHours(-6), now);
                    break;
                case 5: // Last 24 Hours
                    SetDateRange(now.AddDays(-1), now);
                    break;
                case 6: // Oct 14, 2026
                    SetDateRange(new DateTime(2026, 10, 14, 0, 0, 0), new DateTime(2026, 10, 14, 23, 59, 59));
                    break;
                case 7: // Oct 20, 2026
                    SetDateRange(new DateTime(2026, 10, 20, 0, 0, 0), new DateTime(2026, 10, 20, 23, 59, 59));
                    break;
                case 8: // Oct 23, 2026
                    SetDateRange(new DateTime(2026, 10, 23, 0, 0, 0), new DateTime(2026, 10, 23, 23, 59, 59));
                    break;
                case 9: // Oct 24: 02:12 - 02:34 (User Clip Test)
                    SetDateRange(new DateTime(2026, 10, 24, 2, 12, 0), new DateTime(2026, 10, 24, 2, 34, 0));
                    for (int i = 0; i < _cameraCombo.Items.Count; i++)
                    {
                        if (_cameraCombo.Items[i].ToString().IndexOf("61252", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            _cameraCombo.SelectedIndex = i;
                            break;
                        }
                    }
                    break;
                case 10: // Oct 24, 2026
                    SetDateRange(new DateTime(2026, 10, 24, 0, 0, 0), new DateTime(2026, 10, 24, 23, 59, 59));
                    break;
                case 11: // Oct 25, 2026
                    SetDateRange(new DateTime(2026, 10, 25, 0, 0, 0), new DateTime(2026, 10, 25, 23, 59, 59));
                    break;
                case 12: // Oct 26, 2026
                    SetDateRange(new DateTime(2026, 10, 26, 0, 0, 0), new DateTime(2026, 10, 26, 23, 59, 59));
                    break;
                case 13: // All Available History
                    SetDateRange(new DateTime(2026, 1, 1, 0, 0, 0), now);
                    break;
            }
        }

        private void SetDateRange(DateTime from, DateTime to)
        {
            _dtpStartDate.Value = from.Date;
            _dtpStartTime.Value = from;
            _dtpEndDate.Value = to.Date;
            _dtpEndTime.Value = to;
        }

        private async Task ExecuteFilterAsync()
        {
            DateTime start = _dtpStartDate.Value.Date.Add(_dtpStartTime.Value.TimeOfDay);
            DateTime end = _dtpEndDate.Value.Date.Add(_dtpEndTime.Value.TimeOfDay);

            if (start > end)
            {
                MessageBox.Show(this, "Start Date/Time must be earlier than End Date/Time.", "Invalid Time Range", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            string selectedCamera = _cameraCombo.SelectedIndex > 0 ? _cameraCombo.SelectedItem.ToString() : null;

            _btnFilter.Enabled = false;
            _btnFilter.Text = "⏳ Filtering...";
            _lblSummary.Text = "Scanning frames matching time window...";
            _playTimer.Stop();
            _btnPlay.Text = "▶ Play Sequence";

            try
            {
                var matches = await Task.Run(() => ScanFrames(start, end, selectedCamera));

                _currentResults.Clear();
                _lastRenderedMp4Path = null;
                _currentResults.AddRange(matches);

                _grid.Rows.Clear();
                for (int i = 0; i < _currentResults.Count; i++)
                {
                    var r = _currentResults[i];
                    _grid.Rows.Add(
                        i + 1,
                        r.CameraName,
                        r.DateString,
                        r.TimeString,
                        (r.FileSizeBytes / 1024.0).ToString("F1")
                    );
                }

                _slider.Minimum = 0;
                _slider.Maximum = Math.Max(0, _currentResults.Count - 1);
                _slider.Value = 0;

                long totalBytes = _currentResults.Sum(r => r.FileSizeBytes);
                string camDisplay = string.IsNullOrEmpty(selectedCamera) ? "All Cameras" : selectedCamera;
                _lblSummary.Text = $"Found {_currentResults.Count:N0} frames for {camDisplay} between {start:yyyy-MM-dd HH:mm:ss} and {end:yyyy-MM-dd HH:mm:ss} (Total: {totalBytes / (1024.0 * 1024.0):F2} MB)";

                if (_currentResults.Count > 0)
                {
                    SelectFrameIndex(0);
                }
                else
                {
                    DisposePreviewImage();
                    _lblFrameInfo.Text = "No video frames found in this time range.";
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Filter error: " + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                _lblSummary.Text = "Filter failed.";
            }
            finally
            {
                _btnFilter.Enabled = true;
                _btnFilter.Text = "🔍 Filter Video Frames";
            }
        }

        private List<FrameRecord> ScanFrames(DateTime from, DateTime to, string camera)
        {
            var results = new List<FrameRecord>();
            var sourceRoots = new List<string>();
            if (Directory.Exists(_shotsDirectory)) sourceRoots.Add(_shotsDirectory);
            const string serviceShots = @"C:\Services\CamGrab\shots";
            if (Directory.Exists(serviceShots) && !sourceRoots.Contains(serviceShots)) sourceRoots.Add(serviceShots);

            if (sourceRoots.Count == 0) return results;

            // Target relevant date subdirectories to avoid scanning all 97k files needlessly
            var targetDirs = new List<string>();
            foreach (var root in sourceRoots)
            {
                for (DateTime d = from.Date; d <= to.Date; d = d.AddDays(1))
                {
                    var sub = Path.Combine(root, d.ToString("yyyy-MM-dd"));
                    if (Directory.Exists(sub)) targetDirs.Add(sub);
                }
                targetDirs.Add(root);
            }

            foreach (var dir in targetDirs.Distinct())
            {
                string[] files;
                try
                {
                    files = Directory.GetFiles(dir, "*.jpg", SearchOption.TopDirectoryOnly);
                }
                catch { continue; }

                foreach (var file in files)
                {
                    string fileName = Path.GetFileName(file);

                    // Camera quick filter
                    if (!string.IsNullOrEmpty(camera) && !fileName.StartsWith(camera, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var rec = ParseFrameRecord(file);
                    if (rec.Timestamp >= from && rec.Timestamp <= to)
                    {
                        if (string.IsNullOrEmpty(camera) || string.Equals(rec.CameraName, camera, StringComparison.OrdinalIgnoreCase))
                        {
                            results.Add(rec);
                        }
                    }
                }
            }

            return results.OrderBy(r => r.Timestamp).ToList();
        }

        private static FrameRecord ParseFrameRecord(string filePath)
        {
            var fi = new FileInfo(filePath);
            var fileName = fi.Name;
            DateTime timestamp = fi.CreationTime;
            string cameraName = "camera";

            var fullMatch = Regex.Match(fileName, @"^(.+?)_(\d{8})_(\d{6})(?:_(\d{3}))?", RegexOptions.IgnoreCase);
            if (fullMatch.Success)
            {
                cameraName = fullMatch.Groups[1].Value;
                var dateStr = fullMatch.Groups[2].Value;
                var timeStr = fullMatch.Groups[3].Value;
                var msStr = fullMatch.Groups[4].Success ? fullMatch.Groups[4].Value : "000";

                if (DateTime.TryParseExact(dateStr + timeStr + msStr, "yyyyMMddHHmmssfff", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
                {
                    timestamp = parsed;
                }
            }
            else
            {
                var shortMatch = Regex.Match(fileName, @"^(.+?)_(\d{6})\.jpg$", RegexOptions.IgnoreCase);
                if (shortMatch.Success && DateTime.TryParseExact(shortMatch.Groups[2].Value, "HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var timeParsed))
                {
                    timestamp = fi.CreationTime.Date.Add(timeParsed.TimeOfDay);
                    cameraName = shortMatch.Groups[1].Value;
                }
                else
                {
                    int us = fileName.IndexOf('_');
                    if (us > 0) cameraName = fileName.Substring(0, us);
                }
            }

            return new FrameRecord
            {
                CameraName = cameraName,
                FileName = fileName,
                FilePath = filePath,
                Timestamp = timestamp,
                FileSizeBytes = fi.Exists ? fi.Length : 0
            };
        }

        private void OnGridSelectionChanged(object sender, EventArgs e)
        {
            if (_grid.CurrentRow != null && _grid.CurrentRow.Index >= 0 && _grid.CurrentRow.Index < _currentResults.Count)
            {
                SelectFrameIndex(_grid.CurrentRow.Index, updateGrid: false);
            }
        }

        private void SelectFrameIndex(int index, bool updateGrid = true)
        {
            if (index < 0 || index >= _currentResults.Count) return;
            _currentIndex = index;

            if (updateGrid && _grid.CurrentRow?.Index != index)
            {
                _grid.ClearSelection();
                if (index < _grid.Rows.Count)
                {
                    _grid.Rows[index].Selected = true;
                    _grid.FirstDisplayedScrollingRowIndex = Math.Max(0, index - 3);
                }
            }

            if (_slider.Value != index && index <= _slider.Maximum)
            {
                _slider.Value = index;
            }

            var frame = _currentResults[index];
            LoadPreviewImage(frame.FilePath);

            _lblFrameInfo.Text = $"[{index + 1}/{_currentResults.Count}] {frame.CameraName}  |  {frame.DateString} {frame.TimeString}  |  {(frame.FileSizeBytes / 1024.0):F1} KB  |  {Path.GetFileName(frame.FilePath)}";
        }

        private void LoadPreviewImage(string path)
        {
            try
            {
                DisposePreviewImage();
                if (File.Exists(path))
                {
                    // Open without locking file
                    using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    using (var temp = Image.FromStream(fs))
                    {
                        _previewBox.Image = new Bitmap(temp);
                    }
                }
            }
            catch
            {
                DisposePreviewImage();
            }
        }

        private void DisposePreviewImage()
        {
            if (_previewBox.Image != null)
            {
                var old = _previewBox.Image;
                _previewBox.Image = null;
                old.Dispose();
            }
        }

        // ==================== Sequence Playback ====================
        private void ToggleSequencePlay(object sender, EventArgs e)
        {
            if (_currentResults.Count == 0) return;

            if (_playTimer.Enabled)
            {
                _playTimer.Stop();
                _btnPlay.Text = "▶ Play Sequence";
                _btnPlay.BackColor = Color.FromArgb(2, 132, 199);
            }
            else
            {
                _playTimer.Start();
                _btnPlay.Text = "⏸ Pause Sequence";
                _btnPlay.BackColor = Color.FromArgb(220, 38, 38);
            }
        }

        private void UpdatePlayTimerInterval()
        {
            int fps = 5;
            switch (_fpsCombo.SelectedIndex)
            {
                case 0: fps = 1; break;
                case 1: fps = 2; break;
                case 2: fps = 5; break;
                case 3: fps = 10; break;
                case 4: fps = 20; break;
            }
            _playTimer.Interval = Math.Max(10, 1000 / fps);
        }

        private void OnPlayTimerTick(object sender, EventArgs e)
        {
            if (_currentResults.Count == 0)
            {
                _playTimer.Stop();
                return;
            }

            int next = _currentIndex + 1;
            if (next >= _currentResults.Count)
            {
                next = 0; // loop sequence
            }
            SelectFrameIndex(next);
        }

        // ==================== Action Handlers ====================
        private void OpenCurrentImage()
        {
            if (_currentIndex < 0 || _currentIndex >= _currentResults.Count) return;
            var file = _currentResults[_currentIndex].FilePath;
            if (File.Exists(file))
            {
                try { Process.Start(new ProcessStartInfo(file) { UseShellExecute = true }); } catch { }
            }
        }

        private void ShowCurrentImageInFolder()
        {
            if (_currentIndex < 0 || _currentIndex >= _currentResults.Count) return;
            var file = _currentResults[_currentIndex].FilePath;
            if (File.Exists(file))
            {
                try { Process.Start("explorer.exe", $"/select,\"{file}\""); } catch { }
            }
        }

        private void ExportFilteredFrames()
        {
            if (_currentResults.Count == 0)
            {
                MessageBox.Show(this, "No frames to export. Filter a range first.", "Export", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            using (var fbd = new FolderBrowserDialog { Description = "Select destination folder to export filtered frames" })
            {
                if (fbd.ShowDialog(this) != DialogResult.OK) return;
                string destDir = fbd.SelectedPath;

                int count = 0;
                foreach (var r in _currentResults)
                {
                    try
                    {
                        string destFile = Path.Combine(destDir, r.FileName);
                        File.Copy(r.FilePath, destFile, true);
                        count++;
                    }
                    catch { }
                }

                MessageBox.Show(this, $"Successfully exported {count} frames to:\n{destDir}", "Export Completed", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        private async Task RenderFilteredFramesToMp4Async()
        {
            if (_currentResults.Count == 0)
            {
                MessageBox.Show(this, "No frames to render into video. Filter a range first.", "Render Video", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            string ffmpeg = CameraInventoryForm.ResolveFfmpegPath();
            if (string.IsNullOrEmpty(ffmpeg) || (!File.Exists(ffmpeg) && ffmpeg != "ffmpeg"))
            {
                MessageBox.Show(this, "ffmpeg.exe not found to render video.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            using (var sfd = new SaveFileDialog
            {
                Title = "Save Rendered Video Clip",
                Filter = "MP4 Video (*.mp4)|*.mp4",
                FileName = $"filtered_video_{DateTime.Now:yyyyMMdd_HHmmss}.mp4"
            })
            {
                if (sfd.ShowDialog(this) != DialogResult.OK) return;
                string outputFile = sfd.FileName;

                _btnRenderMp4.Enabled = false;
                _btnRenderMp4.Text = "⏳ Rendering MP4...";

                try
                {
                    double fps = await RenderMp4InternalAsync(outputFile);
                    _lastRenderedMp4Path = outputFile;

                    MessageBox.Show(this, $"Video successfully rendered!\n\nFile: {outputFile}\nFrames: {_currentResults.Count}\nFPS: {fps:F1}", "Render Complete", MessageBoxButtons.OK, MessageBoxIcon.Information);

                    try { Process.Start(new ProcessStartInfo(outputFile) { UseShellExecute = true }); } catch { }
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "Failed to render video: " + ex.Message, "Render Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                finally
                {
                    _btnRenderMp4.Enabled = true;
                    _btnRenderMp4.Text = "🎬 Render Video (MP4)...";
                }
            }
        }

        private async Task PlayFilteredClipInVlcAsync()
        {
            if (_currentResults.Count == 0)
            {
                MessageBox.Show(this, "No frames to play. Filter a time range first.", "Play in VLC", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            string vlcPath = CameraInventoryForm.ResolveVlcPath();
            if (!File.Exists(vlcPath) && vlcPath != "vlc.exe")
            {
                MessageBox.Show(this, "VLC Media Player was not found.\nPlease install VLC from https://www.videolan.org/vlc/", "VLC Not Found", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            string clipPath = _lastRenderedMp4Path;
            if (string.IsNullOrEmpty(clipPath) || !File.Exists(clipPath))
            {
                string clipsDir = Path.Combine(_shotsDirectory, "clips");
                Directory.CreateDirectory(clipsDir);
                string camName = _currentResults[0].CameraName;
                DateTime start = _dtpStartDate.Value.Date.Add(_dtpStartTime.Value.TimeOfDay);
                DateTime end = _dtpEndDate.Value.Date.Add(_dtpEndTime.Value.TimeOfDay);
                clipPath = Path.Combine(clipsDir, $"{camName}_{start:yyyyMMdd_HHmmss}_to_{end:HHmmss}.mp4");

                _btnPlayInVlc.Enabled = false;
                _btnPlayInVlc.Text = "⏳ Compiling Clip...";
                try
                {
                    await RenderMp4InternalAsync(clipPath);
                    _lastRenderedMp4Path = clipPath;
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "Failed to prepare video clip for VLC: " + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
                finally
                {
                    _btnPlayInVlc.Enabled = true;
                    _btnPlayInVlc.Text = "▶ Play Clip in VLC";
                }
            }

            try
            {
                Process.Start(new ProcessStartInfo(vlcPath, $"\"{clipPath}\" --no-video-title-show") { UseShellExecute = false });
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Failed to launch VLC: " + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private async Task<double> RenderMp4InternalAsync(string outputFile)
        {
            string ffmpeg = CameraInventoryForm.ResolveFfmpegPath();
            string tempConcatFile = Path.Combine(Path.GetTempPath(), $"ffmpeg_concat_{Guid.NewGuid():N}.txt");
            var sb = new StringBuilder();

            double fps = 5.0;
            if (_currentResults.Count > 1)
            {
                var totalSpan = _currentResults.Last().Timestamp - _currentResults.First().Timestamp;
                if (totalSpan.TotalSeconds > 0)
                {
                    fps = Math.Max(0.5, Math.Min(30.0, (_currentResults.Count - 1) / totalSpan.TotalSeconds));
                }
            }

            double frameDuration = 1.0 / fps;
            foreach (var r in _currentResults)
            {
                sb.AppendLine($"file '{r.FilePath.Replace("'", @"'\''")}'");
                sb.AppendLine($"duration {frameDuration:F4}");
            }
            sb.AppendLine($"file '{_currentResults.Last().FilePath.Replace("'", @"'\''")}'");

            File.WriteAllText(tempConcatFile, sb.ToString());

            string args = $"-y -f concat -safe 0 -i \"{tempConcatFile}\" -c:v libx264 -preset veryfast -pix_fmt yuv420p \"{outputFile}\"";

            var psi = new ProcessStartInfo(ffmpeg, args)
            {
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            await Task.Run(() =>
            {
                using (var p = Process.Start(psi))
                {
                    string err = p.StandardError.ReadToEnd();
                    p.WaitForExit();
                    if (p.ExitCode != 0)
                    {
                        throw new InvalidOperationException(string.IsNullOrWhiteSpace(err) ? $"FFmpeg exit code {p.ExitCode}" : err);
                    }
                }
                try { File.Delete(tempConcatFile); } catch { }
            });

            return fps;
        }

        private void ShowTrimVideoDialog()
        {
            string clipsDir = Path.Combine(_shotsDirectory, "clips");
            string defaultInput = _lastRenderedMp4Path;
            if (string.IsNullOrEmpty(defaultInput) || !File.Exists(defaultInput))
            {
                if (Directory.Exists(clipsDir))
                {
                    defaultInput = Directory.GetFiles(clipsDir, "*.mp4", SearchOption.AllDirectories)
                        .OrderByDescending(f => new FileInfo(f).LastWriteTime)
                        .FirstOrDefault();
                }
            }

            using (var dlg = new Form
            {
                Text = "✂️ Trim Video Clip by Time Range",
                Width = 590,
                Height = 310,
                StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false,
                BackColor = Color.FromArgb(248, 250, 252)
            })
            {
                var lblHeader = new Label
                {
                    Text = "Select an existing MP4 video and specify start and end time to take only that part of the clip.",
                    Dock = DockStyle.Top,
                    Height = 42,
                    Padding = new Padding(12, 10, 12, 0),
                    Font = new Font("Segoe UI", 9.2f)
                };

                int y = 50;
                var lblFile = new Label { Text = "Source Video:", Location = new Point(14, y), AutoSize = true, Font = new Font("Segoe UI", 9f, FontStyle.Bold) };
                y += 20;

                var txtFile = new TextBox { Text = defaultInput ?? "", Location = new Point(14, y), Width = 440, Font = new Font("Consolas", 8.8f) };
                var btnBrowse = new Button { Text = "📂 Browse...", Location = new Point(462, y - 1), Width = 96, Height = 25, Cursor = Cursors.Hand };
                btnBrowse.Click += (s, e) =>
                {
                    using (var ofd = new OpenFileDialog
                    {
                        Title = "Select MP4 Video to Trim",
                        Filter = "MP4 Video (*.mp4)|*.mp4|All Videos (*.*)|*.*",
                        InitialDirectory = Directory.Exists(clipsDir) ? clipsDir : _shotsDirectory
                    })
                    {
                        if (ofd.ShowDialog(dlg) == DialogResult.OK) txtFile.Text = ofd.FileName;
                    }
                };
                y += 34;

                var lblStart = new Label { Text = "Start Time (e.g. 00:01:00 or 10):", Location = new Point(14, y), AutoSize = true, Font = new Font("Segoe UI", 9f, FontStyle.Bold) };
                var lblEnd = new Label { Text = "End Time (e.g. 00:02:30 or 45):", Location = new Point(285, y), AutoSize = true, Font = new Font("Segoe UI", 9f, FontStyle.Bold) };
                y += 20;

                var txtStart = new TextBox { Text = "00:00:00", Location = new Point(14, y), Width = 230, Font = new Font("Consolas", 10f) };
                var txtEnd = new TextBox { Text = "00:01:00", Location = new Point(285, y), Width = 230, Font = new Font("Consolas", 10f) };
                y += 38;

                var lblStatus = new Label { Text = "", Location = new Point(14, y), AutoSize = true, ForeColor = Color.FromArgb(2, 132, 199) };
                y += 24;

                var btnTrim = new Button
                {
                    Text = "✂️ Trim & Save Clip",
                    Location = new Point(14, y),
                    Width = 175,
                    Height = 32,
                    BackColor = Color.FromArgb(14, 165, 233),
                    ForeColor = Color.White,
                    FlatStyle = FlatStyle.Flat,
                    Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                    Cursor = Cursors.Hand
                };
                btnTrim.FlatAppearance.BorderSize = 0;

                var btnTrimPlay = new Button
                {
                    Text = "▶ Trim & Play in VLC",
                    Location = new Point(200, y),
                    Width = 175,
                    Height = 32,
                    BackColor = Color.FromArgb(16, 185, 129),
                    ForeColor = Color.White,
                    FlatStyle = FlatStyle.Flat,
                    Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                    Cursor = Cursors.Hand
                };
                btnTrimPlay.FlatAppearance.BorderSize = 0;

                var btnClose = new Button
                {
                    Text = "Close",
                    DialogResult = DialogResult.Cancel,
                    Location = new Point(462, y),
                    Width = 96,
                    Height = 32
                };

                async Task DoTrimAsync(bool playInVlc)
                {
                    string input = txtFile.Text.Trim();
                    if (!File.Exists(input))
                    {
                        MessageBox.Show(dlg, "Please choose a valid existing MP4 video file.", "File Not Found", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }

                    string start = txtStart.Text.Trim();
                    string end = txtEnd.Text.Trim();
                    if (string.IsNullOrEmpty(start) || string.IsNullOrEmpty(end))
                    {
                        MessageBox.Show(dlg, "Please enter both Start Time and End Time.", "Invalid Range", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }

                    string dir = Path.GetDirectoryName(Path.GetFullPath(input)) ?? "";
                    string baseName = Path.GetFileNameWithoutExtension(input);
                    string cleanStart = start.Replace(":", "-");
                    string cleanEnd = end.Replace(":", "-");
                    string output = Path.Combine(dir, $"{baseName}_trimmed_{cleanStart}_to_{cleanEnd}.mp4");

                    btnTrim.Enabled = false;
                    btnTrimPlay.Enabled = false;
                    lblStatus.Text = "⏳ Trimming video clip with FFmpeg...";

                    try
                    {
                        string ffmpeg = CameraInventoryForm.ResolveFfmpegPath();
                        string args = $"-y -ss {start} -to {end} -i \"{input}\" -c:v libx264 -preset veryfast -pix_fmt yuv420p -c:a aac \"{output}\"";

                        var psi = new ProcessStartInfo(ffmpeg, args)
                        {
                            RedirectStandardError = true,
                            UseShellExecute = false,
                            CreateNoWindow = true
                        };

                        await Task.Run(() =>
                        {
                            using (var p = Process.Start(psi))
                            {
                                string err = p.StandardError.ReadToEnd();
                                p.WaitForExit();
                                if (p.ExitCode != 0)
                                {
                                    throw new InvalidOperationException(string.IsNullOrWhiteSpace(err) ? $"FFmpeg error {p.ExitCode}" : err);
                                }
                            }
                        });

                        var fi = new FileInfo(output);
                        lblStatus.Text = $"Trimmed successfully: {fi.Name} ({fi.Length / (1024.0 * 1024.0):F2} MB)";

                        if (playInVlc)
                        {
                            string vlc = CameraInventoryForm.ResolveVlcPath();
                            try { Process.Start(new ProcessStartInfo(vlc, $"\"{output}\" --no-video-title-show") { UseShellExecute = false }); } catch { }
                        }
                        else
                        {
                            var res = MessageBox.Show(dlg, $"Trimmed clip created successfully!\n\nFile: {output}\nSize: {fi.Length / (1024.0 * 1024.0):F2} MB\n\nWould you like to open it now in VLC?", "Trim Complete", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
                            if (res == DialogResult.Yes)
                            {
                                string vlc = CameraInventoryForm.ResolveVlcPath();
                                try { Process.Start(new ProcessStartInfo(vlc, $"\"{output}\" --no-video-title-show") { UseShellExecute = false }); } catch { }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        lblStatus.Text = "Trim failed.";
                        MessageBox.Show(dlg, "Failed to trim video: " + ex.Message, "Trim Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                    finally
                    {
                        btnTrim.Enabled = true;
                        btnTrimPlay.Enabled = true;
                    }
                }

                btnTrim.Click += async (s, e) => await DoTrimAsync(false);
                btnTrimPlay.Click += async (s, e) => await DoTrimAsync(true);

                dlg.Controls.Add(lblHeader);
                dlg.Controls.Add(lblFile);
                dlg.Controls.Add(txtFile);
                dlg.Controls.Add(btnBrowse);
                dlg.Controls.Add(lblStart);
                dlg.Controls.Add(lblEnd);
                dlg.Controls.Add(txtStart);
                dlg.Controls.Add(txtEnd);
                dlg.Controls.Add(lblStatus);
                dlg.Controls.Add(btnTrim);
                dlg.Controls.Add(btnTrimPlay);
                dlg.Controls.Add(btnClose);

                dlg.ShowDialog(this);
            }
        }
    }
}
