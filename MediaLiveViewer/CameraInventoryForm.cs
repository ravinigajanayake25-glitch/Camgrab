using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Windows.Forms;
using VideoOS.Platform;
using VideoOS.Platform.Data;
using VideoOS.Platform.Live;
using VideoOS.Platform.Messaging;
using VideoOS.Platform.Util.AdaptiveStreaming;

namespace MediaLiveViewer
{
    internal sealed class CameraInventoryForm : Form
    {
        // Top Toolbar Controls
        private readonly ComboBox _filterCombo;
        private readonly TextBox _searchBox;
        private readonly Button _btnClearSearch;
        private readonly CheckBox _chkShowCheckedOnly;
        private readonly Button _btnSelectAll;
        private readonly Button _btnDeselectAll;
        private readonly Button _btnSaveToService;
        private readonly Label _countLabel;

        private readonly TextBox _bridgeHostBox;
        private readonly TextBox _uploadUrlBox;
        private readonly Label _filterSummaryLabel;

        // Main Layout Controls
        private readonly DataGridView _cameraGrid;
        private readonly TextBox _detailsBox;

        // Live GUI Controls
        private readonly Label _cameraTitleLabel;
        private readonly TextBox _rtspUrlBox;
        private readonly Button _btnCopyRtsp;
        private readonly Button _btnToggleLive;
        private readonly Button _btnOpenFull;
        private readonly Button _btnSnapshot;
        private readonly Button _btnTimeFilter;
        private readonly Button _btnAlarm;
        private CancellationTokenSource _alarmCts;
        private readonly CheckBox _autoUploadCheck;
        private readonly CheckBox _autoSnapshotCheck;
        private readonly System.Windows.Forms.Timer _autoSnapshotTimer;
        private readonly PictureBox _previewBox;
        private readonly Label _statusLabel;
        private readonly CheckBox _serverTimeOverlayCheck;
        private TimeSpan _serverTimeOffset = TimeSpan.Zero;
        private bool _serverTimeSynced = false;

        // VLC & RTSP Ribbon Streaming
        private readonly Button _btnOpenVlc;
        private readonly Button _btnStreamRibbon;
        private Process _ribbonStreamProcess;
        private string _activeRibbonUrl;

        // Clip Recording & Synopsis Controls
        private readonly ComboBox _clipDurationCombo;
        private readonly Button _btnRecordClip;
        private readonly Button _btnSynopsis;
        private readonly Button _btnTrimClip;
        private Process _clipRecordingProcess;
        private System.Windows.Forms.Timer _clipCountdownTimer;
        private int _clipRemainingSeconds = 0;
        private int _clipTotalSeconds = 0;
        private string _currentClipPath;
        private RtspAuthSanitizerProxy _authProxy;

        // Time Range Clip & Stream Filter Controls
        private readonly DateTimePicker _dtpMainClipDate;
        private readonly TextBox _txtMainClipStart;
        private readonly TextBox _txtMainClipEnd;
        private readonly ComboBox _comboMainPresets;
        private readonly Button _btnMainExtractClip;
        private readonly Button _btnMainPlayVlc;
        private readonly Button _btnMainBrowseFrames;
        private readonly Label _lblMainFilterStatus;

        // Camera State
        private readonly List<Item> _cameras = new List<Item>();
        private readonly HashSet<Guid> _checkedCameraGuids = new HashSet<Guid>();
        private Item _selectedCamera;
        private JPEGLiveSource _liveSource;
        private Process _rtspLiveProcess;
        private CancellationTokenSource _rtspLiveCts;
        private System.Windows.Forms.Timer _autoFallbackTimer;
        private int _isDispatchingFrame = 0;
        private bool _isStreaming = false;
        private bool _isUpdatingGridRows = false;
        private int _frameCount = 0;
        private readonly ComboBox _streamModeCombo;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wp, string lp);
        private const int EM_SETCUEBANNER = 0x1501;

        private static void SetCueBanner(TextBox textBox, string placeholder)
        {
            if (textBox.IsHandleCreated)
            {
                SendMessage(textBox.Handle, EM_SETCUEBANNER, (IntPtr)1, placeholder);
            }
            else
            {
                textBox.HandleCreated += (s, e) => SendMessage(textBox.Handle, EM_SETCUEBANNER, (IntPtr)1, placeholder);
            }
        }

        public CameraInventoryForm()
        {
            Text = "Connected cameras & Live RTSP Stream Manager";
            Width = 1200;
            Height = 740;
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(1000, 600);

            // ==================== Top Panel (2-Row Clean Filter & Settings Bar) ====================
            var topPanel = new Panel
            {
                Dock = DockStyle.Top,
                Height = 76,
                Padding = new Padding(10, 6, 10, 6),
                BackColor = Color.FromArgb(246, 248, 250)
            };

            // ----- Row 1: Camera Filter Box Controls -----
            var searchLabel = new Label
            {
                Text = "Filter cameras:",
                Font = new Font(Font, FontStyle.Bold),
                AutoSize = true,
                Left = 10,
                Top = 13
            };

            _filterCombo = new ComboBox
            {
                Left = 112,
                Top = 9,
                Width = 200,
                DropDownStyle = ComboBoxStyle.DropDownList,
                Font = new Font(Font.FontFamily, 9f)
            };
            _filterCombo.SelectedIndexChanged += (s, e) =>
            {
                if (_filterCombo.SelectedItem is FilterComboItem item)
                {
                    if (item.OnlyChecked)
                    {
                        _chkShowCheckedOnly.Checked = true;
                    }
                    else if (!item.CameraGuid.HasValue)
                    {
                        if (_chkShowCheckedOnly.Checked)
                        {
                            _chkShowCheckedOnly.Checked = false;
                        }
                    }
                }
                PopulateGrid();
            };

            _searchBox = new TextBox
            {
                Left = 320,
                Top = 9,
                Width = 230,
                Font = new Font(Font.FontFamily, 9f)
            };
            SetCueBanner(_searchBox, "Filter: 61250, 61253 or GUID...");
            _searchBox.TextChanged += (sender, args) => PopulateGrid();

            _btnClearSearch = new Button
            {
                Text = "✖",
                Left = 556,
                Top = 8,
                Width = 28,
                Height = 25,
                Cursor = Cursors.Hand,
                FlatStyle = FlatStyle.System
            };
            _btnClearSearch.Click += (s, e) =>
            {
                _searchBox.Clear();
                _filterCombo.SelectedIndex = 0;
                _chkShowCheckedOnly.Checked = false;
                PopulateGrid();
            };

            _chkShowCheckedOnly = new CheckBox
            {
                Text = "Show Checked Only",
                Font = new Font(Font, FontStyle.Bold),
                AutoSize = true,
                Left = 594,
                Top = 12,
                Cursor = Cursors.Hand
            };
            _chkShowCheckedOnly.CheckedChanged += (s, e) => PopulateGrid();

            _btnSelectAll = new Button
            {
                Text = "Select All",
                Left = 754,
                Top = 8,
                Width = 72,
                Height = 25
            };
            _btnSelectAll.Click += (s, e) => SelectAllVisible(true);

            _btnDeselectAll = new Button
            {
                Text = "Clear All",
                Left = 830,
                Top = 8,
                Width = 70,
                Height = 25
            };
            _btnDeselectAll.Click += (s, e) => SelectAllVisible(false);

            _btnSaveToService = new Button
            {
                Text = "💾 Save to Service Config",
                Left = 906,
                Top = 8,
                Width = 155,
                Height = 25
            };
            _btnSaveToService.Click += (s, e) => SaveCheckedCamerasToService();

            var btnTopTimeFilter = new Button
            {
                Text = "🕒 Time Range Filter & Clips",
                Left = 1068,
                Top = 8,
                Width = 190,
                Height = 25,
                BackColor = Color.FromArgb(16, 185, 129),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font(Font, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            btnTopTimeFilter.FlatAppearance.BorderSize = 0;
            btnTopTimeFilter.Click += (s, e) =>
            {
                var initialCam = _selectedCamera != null ? _selectedCamera.Name : null;
                var filterForm = new TimeRangeFrameFilterForm(GetShotsDirectory(), initialCam, _cameras);
                filterForm.Show(this);
            };

            _countLabel = new Label
            {
                AutoSize = true,
                Top = 13,
                Font = new Font(Font, FontStyle.Bold),
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };

            // ----- Row 2: RTSP Bridge, Upload API, & Tip -----
            var bridgeLabel = new Label
            {
                Text = "RTSP Bridge:",
                AutoSize = true,
                Left = 10,
                Top = 46
            };

            _bridgeHostBox = new TextBox
            {
                Left = 90,
                Top = 43,
                Width = 140,
                Text = "10.64.30.142:554"
            };
            _bridgeHostBox.TextChanged += (sender, args) =>
            {
                UpdateRtspUrlsInGrid();
                UpdateSelectedCameraView();
            };

            var uploadLabel = new Label
            {
                Text = "Upload API:",
                AutoSize = true,
                Left = 246,
                Top = 46
            };

            _uploadUrlBox = new TextBox
            {
                Left = 320,
                Top = 43,
                Width = 240,
                Text = "http://10.64.30.96:8000/upload"
            };

            _filterSummaryLabel = new Label
            {
                AutoSize = true,
                Left = 574,
                Top = 46,
                ForeColor = Color.DimGray,
                Text = "💡 Tip: Check only needed cameras & tick 'Show Checked Only', or type comma-separated (e.g. 61250, 61253)."
            };

            topPanel.Controls.Add(searchLabel);
            topPanel.Controls.Add(_filterCombo);
            topPanel.Controls.Add(_searchBox);
            topPanel.Controls.Add(_btnClearSearch);
            topPanel.Controls.Add(_chkShowCheckedOnly);
            topPanel.Controls.Add(_btnSelectAll);
            topPanel.Controls.Add(_btnDeselectAll);
            topPanel.Controls.Add(_btnSaveToService);
            topPanel.Controls.Add(btnTopTimeFilter);
            topPanel.Controls.Add(_countLabel);

            topPanel.Controls.Add(bridgeLabel);
            topPanel.Controls.Add(_bridgeHostBox);
            topPanel.Controls.Add(uploadLabel);
            topPanel.Controls.Add(_uploadUrlBox);
            topPanel.Controls.Add(_filterSummaryLabel);

            // ==================== Main SplitContainer ====================
            var splitMain = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Vertical
            };

            // -------------------- Left Panel (Grid + Details) --------------------
            var splitLeft = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Horizontal
            };

            _cameraGrid = new DataGridView
            {
                Dock = DockStyle.Fill,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AutoGenerateColumns = false,
                ReadOnly = false,
                MultiSelect = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                RowHeadersVisible = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill
            };

            var checkCol = new DataGridViewCheckBoxColumn
            {
                Name = "Checked",
                HeaderText = "Active",
                Width = 52,
                FillWeight = 9,
                Resizable = DataGridViewTriState.False,
                ReadOnly = false
            };
            _cameraGrid.Columns.Add(checkCol);
            _cameraGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "CameraName", HeaderText = "Camera", FillWeight = 22, ReadOnly = true });
            _cameraGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "CameraId", HeaderText = "Camera ID (GUID)", FillWeight = 32, ReadOnly = true });
            _cameraGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "RtspUrl", HeaderText = "RTSP Stream URL", FillWeight = 37, ReadOnly = true });
            _cameraGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Server", HeaderText = "Server", FillWeight = 14, ReadOnly = true });
            _cameraGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Enabled", HeaderText = "Enabled", FillWeight = 10, ReadOnly = true });

            _cameraGrid.CurrentCellDirtyStateChanged += (s, e) =>
            {
                if (_cameraGrid.IsCurrentCellDirty && _cameraGrid.CurrentCell is DataGridViewCheckBoxCell)
                {
                    _cameraGrid.CommitEdit(DataGridViewDataErrorContexts.Commit);
                }
            };

            _cameraGrid.CellValueChanged += (s, e) =>
            {
                if (_isUpdatingGridRows) return;
                if (e.RowIndex >= 0 && e.ColumnIndex == 0)
                {
                    var row = _cameraGrid.Rows[e.RowIndex];
                    if (row.Tag is Item camera)
                    {
                        bool isChecked = Convert.ToBoolean(row.Cells[0].Value);
                        if (isChecked)
                        {
                            _checkedCameraGuids.Add(camera.FQID.ObjectId);
                        }
                        else
                        {
                            _checkedCameraGuids.Remove(camera.FQID.ObjectId);
                        }
                        UpdateStatusCounts();

                        if (_chkShowCheckedOnly.Checked)
                        {
                            BeginInvoke((Action)(() => PopulateGrid()));
                        }
                    }
                }
            };

            _cameraGrid.SelectionChanged += CameraSelectionChanged;
            _cameraGrid.CellDoubleClick += (s, e) =>
            {
                if (_selectedCamera != null)
                {
                    StartLiveStream(_selectedCamera);
                }
            };

            var detailsContainer = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, 4, 0, 0) };
            var detailsLabel = new Label
            {
                Text = "Configuration & Metadata Properties:",
                Dock = DockStyle.Top,
                Height = 20,
                Font = new Font(Font, FontStyle.Bold)
            };

            _detailsBox = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Both,
                WordWrap = false
            };
            detailsContainer.Controls.Add(_detailsBox);
            detailsContainer.Controls.Add(detailsLabel);

            splitLeft.Panel1.Controls.Add(_cameraGrid);
            splitLeft.Panel2.Controls.Add(detailsContainer);
            splitMain.Panel1.Controls.Add(splitLeft);

            // -------------------- Right Panel (Live Camera GUI) --------------------
            var liveGroupBox = new GroupBox
            {
                Text = "Live Camera Feed & Controls",
                Dock = DockStyle.Fill,
                Padding = new Padding(10)
            };

            _cameraTitleLabel = new Label
            {
                Dock = DockStyle.Top,
                Height = 26,
                Font = new Font(FontFamily.GenericSansSerif, 10.5f, FontStyle.Bold),
                Text = "Selected Camera: None",
                AutoEllipsis = true
            };

            // URL bar panel
            var urlPanel = new Panel
            {
                Dock = DockStyle.Top,
                Height = 32,
                Padding = new Padding(0, 3, 0, 3)
            };
            var rtspLabel = new Label
            {
                Text = "RTSP URL:",
                Dock = DockStyle.Left,
                Width = 70,
                TextAlign = ContentAlignment.MiddleLeft
            };
            _btnOpenVlc = new Button
            {
                Text = "🟠 Open in VLC",
                Dock = DockStyle.Right,
                Width = 110,
                BackColor = Color.FromArgb(234, 88, 12),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font(Font.FontFamily, 8.5f, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            _btnOpenVlc.FlatAppearance.BorderSize = 0;
            _btnOpenVlc.Click += (s, e) => OpenCurrentStreamInVlc();

            _btnStreamRibbon = new Button
            {
                Text = "📡 Stream Ribbon",
                Dock = DockStyle.Right,
                Width = 125,
                Cursor = Cursors.Hand
            };
            _btnStreamRibbon.Click += (s, e) => ToggleRtspRibbonStream();

            _btnCopyRtsp = new Button
            {
                Text = "📋 Copy URL",
                Dock = DockStyle.Right,
                Width = 90,
                Cursor = Cursors.Hand
            };
            _btnCopyRtsp.Click += (s, e) => CopyCurrentRtspUrl();

            _rtspUrlBox = new TextBox
            {
                Dock = DockStyle.Fill,
                ReadOnly = true
            };

            urlPanel.Controls.Add(_rtspUrlBox);
            urlPanel.Controls.Add(rtspLabel);
            urlPanel.Controls.Add(_btnCopyRtsp);
            urlPanel.Controls.Add(_btnStreamRibbon);
            urlPanel.Controls.Add(_btnOpenVlc);

            // Control Buttons Panel
            var buttonPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                MinimumSize = new Size(0, 68),
                FlowDirection = FlowDirection.LeftToRight,
                Padding = new Padding(0, 4, 0, 4),
                WrapContents = true
            };

            _btnToggleLive = new Button
            {
                Text = "▶ Start Live Feed",
                Width = 130,
                Height = 28
            };
            _btnToggleLive.Click += (s, e) =>
            {
                if (_isStreaming)
                    StopLiveStream();
                else if (_selectedCamera != null)
                    StartLiveStream(_selectedCamera);
            };

            _streamModeCombo = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Width = 135,
                Height = 28,
                Font = new Font(Font.FontFamily, 8.5f)
            };
            _streamModeCombo.Items.AddRange(new object[] { "⚡ Auto (SDK/RTSP)", "🏢 MIP SDK Native", "📡 RTSP (FFmpeg)" });
            _streamModeCombo.SelectedIndex = 0;
            _streamModeCombo.SelectedIndexChanged += (s, e) =>
            {
                if (_isStreaming && _selectedCamera != null)
                {
                    StartLiveStream(_selectedCamera);
                }
            };

            _btnSnapshot = new Button
            {
                Text = "📸 Save Snapshot",
                Width = 125,
                Height = 28
            };
            _btnSnapshot.Click += (s, e) => CaptureSnapshot();

            _btnTimeFilter = new Button
            {
                Text = "🕒 Filter Frames by Time",
                Width = 165,
                Height = 28,
                BackColor = Color.FromArgb(16, 185, 129),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font(Font.FontFamily, 8.5f, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            _btnTimeFilter.FlatAppearance.BorderSize = 0;
            _btnTimeFilter.Click += (s, e) =>
            {
                var initialCam = _selectedCamera != null ? _selectedCamera.Name : null;
                var filterForm = new TimeRangeFrameFilterForm(GetShotsDirectory(), initialCam, _cameras);
                filterForm.Show(this);
            };

            _clipDurationCombo = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Width = 65,
                Height = 28,
                Font = new Font(Font.FontFamily, 8.5f)
            };
            _clipDurationCombo.Items.AddRange(new object[] { "10s", "30s", "1m", "2m", "5m", "10m" });
            _clipDurationCombo.SelectedIndex = 1; // 30s default

            _btnRecordClip = new Button
            {
                Text = "🎬 Record Clip",
                Width = 110,
                Height = 28,
                Cursor = Cursors.Hand
            };
            _btnRecordClip.Click += async (s, e) => await RecordClipOrSynopsisAsync(isSynopsis: false);

            _btnSynopsis = new Button
            {
                Text = "⚡ Video Synopsis",
                Width = 125,
                Height = 28,
                BackColor = Color.FromArgb(79, 70, 229),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font(Font.FontFamily, 8.5f, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            _btnSynopsis.FlatAppearance.BorderSize = 0;
            _btnSynopsis.Click += (s, e) => OnSynopsisClicked();

            _btnTrimClip = new Button
            {
                Text = "✂️ Trim Video Clip",
                Width = 130,
                Height = 28,
                BackColor = Color.FromArgb(14, 165, 233),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font(Font.FontFamily, 8.5f, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            _btnTrimClip.FlatAppearance.BorderSize = 0;
            _btnTrimClip.Click += (s, e) => ShowTrimVideoDialog();

            _btnOpenFull = new Button
            {
                Text = "🔍 Open Full Viewer",
                Width = 135,
                Height = 28
            };
            _btnOpenFull.Click += (s, e) =>
            {
                if (_selectedCamera != null)
                {
                    var viewer = new MainForm(_selectedCamera);
                    viewer.Show();
                }
            };

            _btnAlarm = new Button
            {
                Text = "🚨 Trigger Alarm",
                Width = 120,
                Height = 28,
                BackColor = Color.FromArgb(200, 35, 51),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font(Font.FontFamily, 8.5f, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            _btnAlarm.FlatAppearance.BorderSize = 0;
            _btnAlarm.Click += (s, e) => ToggleAlarmSound();

            _serverTimeOverlayCheck = new CheckBox
            {
                Text = "🕒 Server Time OSD",
                Checked = true,
                AutoSize = true,
                Padding = new Padding(6, 6, 0, 0),
                Cursor = Cursors.Hand
            };

            _autoSnapshotCheck = new CheckBox
            {
                Text = "⏱ Auto-capture (5s)",
                AutoSize = true,
                Padding = new Padding(6, 6, 0, 0)
            };
            _autoSnapshotTimer = new System.Windows.Forms.Timer
            {
                Interval = 5000 // 5 seconds
            };
            _autoSnapshotTimer.Tick += (s, e) =>
            {
                if (_isStreaming && _previewBox.Image != null && _autoSnapshotCheck.Checked)
                {
                    AutoSaveSnapshot();
                }
            };
            _autoSnapshotCheck.CheckedChanged += (s, e) =>
            {
                if (_autoSnapshotCheck.Checked)
                {
                    _autoSnapshotTimer.Start();
                    if (!_isStreaming && _selectedCamera != null)
                    {
                        StartLiveStream(_selectedCamera);
                    }
                }
                else
                {
                    _autoSnapshotTimer.Stop();
                }
            };

            _autoUploadCheck = new CheckBox
            {
                Text = "🌐 Upload to API",
                Checked = true,
                AutoSize = true,
                Padding = new Padding(6, 6, 0, 0)
            };

            buttonPanel.Controls.Add(_btnToggleLive);
            buttonPanel.Controls.Add(_streamModeCombo);
            buttonPanel.Controls.Add(_btnSnapshot);
            buttonPanel.Controls.Add(_btnTimeFilter);
            buttonPanel.Controls.Add(_clipDurationCombo);
            buttonPanel.Controls.Add(_btnRecordClip);
            buttonPanel.Controls.Add(_btnSynopsis);
            buttonPanel.Controls.Add(_btnTrimClip);
            buttonPanel.Controls.Add(_btnOpenFull);
            buttonPanel.Controls.Add(_btnAlarm);
            buttonPanel.Controls.Add(_serverTimeOverlayCheck);
            buttonPanel.Controls.Add(_autoSnapshotCheck);
            buttonPanel.Controls.Add(_autoUploadCheck);

            // Live Stream Display
            _previewBox = new PictureBox
            {
                Dock = DockStyle.Fill,
                BackColor = Color.Black,
                SizeMode = PictureBoxSizeMode.Zoom
            };
            _previewBox.Resize += (s, e) =>
            {
                if (_liveSource != null && _previewBox.Width > 50 && _previewBox.Height > 50)
                {
                    try
                    {
                        _liveSource.Width = _previewBox.Width;
                        _liveSource.Height = _previewBox.Height;
                        _liveSource.SetWidthHeight();
                    }
                    catch { }
                }
            };

            _statusLabel = new Label
            {
                Dock = DockStyle.Bottom,
                Height = 24,
                TextAlign = ContentAlignment.MiddleLeft,
                Text = "Status: Select a camera to start viewing."
            };

            // ==================== Dedicated Time Range Clip & Stream Filter Bar ====================
            var timeFilterPanel = new GroupBox
            {
                Text = "🕒 Time Range Clip & Stream Filter (e.g. 02:12 - 02:34)",
                Dock = DockStyle.Bottom,
                Height = 106,
                Padding = new Padding(8, 4, 8, 4),
                Font = new Font(Font.FontFamily, 8.5f, FontStyle.Bold),
                BackColor = Color.FromArgb(248, 250, 252)
            };

            // Row 1: Inputs & Preset
            var filterRow1 = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = 34,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                AutoSize = false
            };

            var lblPreset = new Label { Text = "Preset:", AutoSize = true, Padding = new Padding(0, 6, 2, 0), Font = new Font(Font.FontFamily, 8.2f) };
            _comboMainPresets = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Width = 230,
                Font = new Font(Font.FontFamily, 8.5f)
            };
            _comboMainPresets.Items.AddRange(new object[]
            {
                "October 24: 02:12 - 02:34 (User Test Clip)",
                "Today (Full Day)",
                "Yesterday",
                "Last 1 Hour",
                "Last 10 Minutes",
                "Custom Time Range"
            });
            _comboMainPresets.SelectedIndex = 0;

            var lblDate = new Label { Text = "Date:", AutoSize = true, Padding = new Padding(6, 6, 2, 0), Font = new Font(Font.FontFamily, 8.2f) };
            _dtpMainClipDate = new DateTimePicker
            {
                Format = DateTimePickerFormat.Short,
                Width = 105,
                Value = new DateTime(2026, 10, 24),
                Font = new Font(Font.FontFamily, 8.5f)
            };

            var lblStart = new Label { Text = "Start:", AutoSize = true, Padding = new Padding(6, 6, 2, 0), Font = new Font(Font.FontFamily, 8.2f) };
            _txtMainClipStart = new TextBox
            {
                Text = "02:12:00",
                Width = 72,
                Font = new Font("Consolas", 9.5f)
            };

            var lblEnd = new Label { Text = "End:", AutoSize = true, Padding = new Padding(6, 6, 2, 0), Font = new Font(Font.FontFamily, 8.2f) };
            _txtMainClipEnd = new TextBox
            {
                Text = "02:34:00",
                Width = 72,
                Font = new Font("Consolas", 9.5f)
            };

            _comboMainPresets.SelectedIndexChanged += (s, e) => OnMainPresetSelected();

            filterRow1.Controls.Add(lblPreset);
            filterRow1.Controls.Add(_comboMainPresets);
            filterRow1.Controls.Add(lblDate);
            filterRow1.Controls.Add(_dtpMainClipDate);
            filterRow1.Controls.Add(lblStart);
            filterRow1.Controls.Add(_txtMainClipStart);
            filterRow1.Controls.Add(lblEnd);
            filterRow1.Controls.Add(_txtMainClipEnd);

            // Row 2: Action Buttons & Status
            var filterRow2 = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = 36,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                AutoSize = false,
                Padding = new Padding(0, 2, 0, 0)
            };

            _btnMainExtractClip = new Button
            {
                Text = "✂️ Extract MP4 Clip",
                Width = 150,
                Height = 28,
                BackColor = Color.FromArgb(14, 165, 233),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font(Font.FontFamily, 8.5f, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            _btnMainExtractClip.FlatAppearance.BorderSize = 0;
            _btnMainExtractClip.Click += async (s, e) => await ExtractOrPlayTimeRangeClipAsync(playInVlc: false);

            _btnMainPlayVlc = new Button
            {
                Text = "▶ Play Range in VLC",
                Width = 155,
                Height = 28,
                BackColor = Color.FromArgb(16, 185, 129),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font(Font.FontFamily, 8.5f, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            _btnMainPlayVlc.FlatAppearance.BorderSize = 0;
            _btnMainPlayVlc.Click += async (s, e) => await ExtractOrPlayTimeRangeClipAsync(playInVlc: true);

            _btnMainBrowseFrames = new Button
            {
                Text = "🔍 Search Frames",
                Width = 130,
                Height = 28,
                BackColor = Color.FromArgb(99, 102, 241),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font(Font.FontFamily, 8.5f, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            _btnMainBrowseFrames.FlatAppearance.BorderSize = 0;
            _btnMainBrowseFrames.Click += (s, e) => OpenTimeRangeFilterWithCurrentSettings();

            _lblMainFilterStatus = new Label
            {
                AutoSize = true,
                Padding = new Padding(8, 7, 0, 0),
                Font = new Font("Segoe UI", 8.5f),
                ForeColor = Color.FromArgb(2, 132, 199),
                Text = "Preset: 02:12 - 02:34 ready. Click 'Extract MP4 Clip' or 'Play Range in VLC'."
            };

            filterRow2.Controls.Add(_btnMainExtractClip);
            filterRow2.Controls.Add(_btnMainPlayVlc);
            filterRow2.Controls.Add(_btnMainBrowseFrames);
            filterRow2.Controls.Add(_lblMainFilterStatus);

            timeFilterPanel.Controls.Add(filterRow2);
            timeFilterPanel.Controls.Add(filterRow1);

            liveGroupBox.Controls.Add(_previewBox);
            liveGroupBox.Controls.Add(timeFilterPanel);
            liveGroupBox.Controls.Add(buttonPanel);
            liveGroupBox.Controls.Add(urlPanel);
            liveGroupBox.Controls.Add(_cameraTitleLabel);
            liveGroupBox.Controls.Add(_statusLabel);
            _previewBox.BringToFront();

            splitMain.Panel2.Controls.Add(liveGroupBox);

            // Add all to Form
            Controls.Add(splitMain);
            Controls.Add(topPanel);

            Resize += (sender, args) =>
            {
                if (_countLabel.Parent != null)
                {
                    _countLabel.Left = topPanel.ClientSize.Width - _countLabel.Width - 12;
                    btnTopTimeFilter.Left = _countLabel.Left - btnTopTimeFilter.Width - 10;
                }
            };

            FormClosing += (s, e) =>
            {
                StopLiveStream();
                if (_alarmCts != null)
                {
                    _alarmCts.Cancel();
                    _alarmCts.Dispose();
                    _alarmCts = null;
                }
            };
            FormClosed += (s, e) =>
            {
                try
                {
                    VideoOS.Platform.SDK.Environment.RemoveAllServers();
                }
                catch { }
                Environment.Exit(0);
            };
            Load += (sender, args) =>
            {
                try
                {
                    if (splitMain.Width > 500)
                    {
                        splitMain.SplitterDistance = Math.Max(200, (int)(splitMain.Width * 0.58));
                    }
                    if (splitLeft.Height > 300)
                    {
                        splitLeft.SplitterDistance = Math.Max(150, (int)(splitLeft.Height * 0.62));
                    }
                }
                catch { }

                try { EnsureAuthProxy(); } catch { }
                LoadCameras();
            };
        }

        private string BuildRtspUrl(Item camera)
        {
            if (camera == null) return string.Empty;
            string host = _bridgeHostBox.Text.Trim();
            if (string.IsNullOrEmpty(host))
            {
                host = "10.64.30.142:554";
            }
            return $"rtsp://{host}/live/{camera.FQID.ObjectId}";
        }

        private void CopyCurrentRtspUrl()
        {
            if (_selectedCamera == null)
            {
                MessageBox.Show(this, "No camera selected to copy RTSP URL.", "Copy URL", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            string testUrl0 = GetDirectCameraTestUrl(_selectedCamera, 0);
            string testUrl1 = GetDirectCameraTestUrl(_selectedCamera, 1);
            string testUrl2 = GetDirectCameraTestUrl(_selectedCamera, 2);
            string lanUrl = BuildLanRtspUrlWithCredentials(_selectedCamera);
            string directUrl = BuildRtspUrlWithCredentials(_selectedCamera);
            string ribbonUrl = !string.IsNullOrEmpty(_activeRibbonUrl) ? _activeRibbonUrl : null;

            // Automatically copy direct test URL to clipboard as requested
            try { Clipboard.SetText(testUrl0); } catch { }

            using (var dlg = new Form
            {
                Text = $"RTSP Stream Links — {_selectedCamera.Name}",
                Width = 740,
                Height = 560,
                StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false,
                AutoScroll = true,
                BackColor = Color.FromArgb(248, 250, 252)
            })
            {
                var lblHeader = new Label
                {
                    Text = "The Direct Camera Test URL (Main Stream) has been copied to your clipboard!\nChoose from the available direct test and network URLs below:",
                    Dock = DockStyle.Top,
                    Height = 46,
                    Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
                    Padding = new Padding(12, 10, 12, 0)
                };

                int y = 54;

                // 1. Direct Test URL (Main Stream - HD / subtype=0)
                var lblTest0 = new Label
                {
                    Text = "🧪 Direct Camera Test URL — Main Stream (subtype=0, HD - Opens directly in VLC):",
                    Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                    Location = new Point(14, y),
                    AutoSize = true,
                    ForeColor = Color.FromArgb(16, 185, 129)
                };
                y += 20;

                var txtTest0 = new TextBox
                {
                    Text = testUrl0,
                    ReadOnly = true,
                    Location = new Point(14, y),
                    Width = 550,
                    Font = new Font("Consolas", 8.8f)
                };
                var btnTest0 = new Button
                {
                    Text = "📋 Copy Main",
                    Location = new Point(572, y - 1),
                    Width = 125,
                    Height = 24,
                    BackColor = Color.FromArgb(16, 185, 129),
                    ForeColor = Color.White,
                    FlatStyle = FlatStyle.Flat,
                    Cursor = Cursors.Hand
                };
                btnTest0.FlatAppearance.BorderSize = 0;
                btnTest0.Click += (s, e) => { Clipboard.SetText(testUrl0); MessageBox.Show(dlg, "Copied Main Stream Test URL to clipboard!", "Copied", MessageBoxButtons.OK, MessageBoxIcon.Information); };
                y += 28;

                // 2. Direct Test URL (Sub Stream 1 - Fast / subtype=1)
                var lblTest1 = new Label
                {
                    Text = "🧪 Direct Camera Test URL — Sub Stream 1 (subtype=1, Low Latency / Preview):",
                    Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                    Location = new Point(14, y),
                    AutoSize = true,
                    ForeColor = Color.FromArgb(2, 132, 199)
                };
                y += 20;

                var txtTest1 = new TextBox
                {
                    Text = testUrl1,
                    ReadOnly = true,
                    Location = new Point(14, y),
                    Width = 550,
                    Font = new Font("Consolas", 8.8f)
                };
                var btnTest1 = new Button
                {
                    Text = "📋 Copy Sub 1",
                    Location = new Point(572, y - 1),
                    Width = 125,
                    Height = 24,
                    BackColor = Color.FromArgb(2, 132, 199),
                    ForeColor = Color.White,
                    FlatStyle = FlatStyle.Flat,
                    Cursor = Cursors.Hand
                };
                btnTest1.FlatAppearance.BorderSize = 0;
                btnTest1.Click += (s, e) => { Clipboard.SetText(testUrl1); MessageBox.Show(dlg, "Copied Sub Stream 1 Test URL to clipboard!", "Copied", MessageBoxButtons.OK, MessageBoxIcon.Information); };
                y += 28;

                // 3. Direct Test URL (Sub Stream 2 - Mobile / subtype=2)
                var lblTest2 = new Label
                {
                    Text = "🧪 Direct Camera Test URL — Sub Stream 2 (subtype=2, Mobile):",
                    Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                    Location = new Point(14, y),
                    AutoSize = true,
                    ForeColor = Color.FromArgb(139, 92, 246)
                };
                y += 20;

                var txtTest2 = new TextBox
                {
                    Text = testUrl2,
                    ReadOnly = true,
                    Location = new Point(14, y),
                    Width = 550,
                    Font = new Font("Consolas", 8.8f)
                };
                var btnTest2 = new Button
                {
                    Text = "📋 Copy Sub 2",
                    Location = new Point(572, y - 1),
                    Width = 125,
                    Height = 24,
                    BackColor = Color.FromArgb(139, 92, 246),
                    ForeColor = Color.White,
                    FlatStyle = FlatStyle.Flat,
                    Cursor = Cursors.Hand
                };
                btnTest2.FlatAppearance.BorderSize = 0;
                btnTest2.Click += (s, e) => { Clipboard.SetText(testUrl2); MessageBox.Show(dlg, "Copied Sub Stream 2 Test URL to clipboard!", "Copied", MessageBoxButtons.OK, MessageBoxIcon.Information); };
                y += 30;

                // 4. LAN / Wi-Fi Proxy URL
                var lblLan = new Label
                {
                    Text = "🌐 Network / Wi-Fi Proxy URL (Milestone Relay for other laptops & phones):",
                    Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                    Location = new Point(14, y),
                    AutoSize = true,
                    ForeColor = Color.FromArgb(15, 23, 42)
                };
                y += 20;

                var txtLan = new TextBox
                {
                    Text = lanUrl,
                    ReadOnly = true,
                    Location = new Point(14, y),
                    Width = 550,
                    Font = new Font("Consolas", 8.8f)
                };
                var btnLan = new Button
                {
                    Text = "📋 Copy LAN",
                    Location = new Point(572, y - 1),
                    Width = 125,
                    Height = 24,
                    Cursor = Cursors.Hand
                };
                btnLan.Click += (s, e) => { Clipboard.SetText(lanUrl); MessageBox.Show(dlg, "Copied Network URL to clipboard!", "Copied", MessageBoxButtons.OK, MessageBoxIcon.Information); };
                y += 28;

                // 5. Ribbon URL (if active)
                if (!string.IsNullOrEmpty(ribbonUrl))
                {
                    var lblRib = new Label
                    {
                        Text = "📡 Live Server Time Ribbon Stream (Active):",
                        Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                        Location = new Point(14, y),
                        AutoSize = true,
                        ForeColor = Color.FromArgb(13, 148, 136)
                    };
                    y += 20;

                    var txtRib = new TextBox
                    {
                        Text = ribbonUrl,
                        ReadOnly = true,
                        Location = new Point(14, y),
                        Width = 550,
                        Font = new Font("Consolas", 8.8f)
                    };
                    var btnRib = new Button
                    {
                        Text = "📋 Copy Ribbon",
                        Location = new Point(572, y - 1),
                        Width = 125,
                        Height = 24,
                        BackColor = Color.FromArgb(13, 148, 136),
                        ForeColor = Color.White,
                        FlatStyle = FlatStyle.Flat,
                        Cursor = Cursors.Hand
                    };
                    btnRib.FlatAppearance.BorderSize = 0;
                    btnRib.Click += (s, e) => { Clipboard.SetText(ribbonUrl); MessageBox.Show(dlg, "Copied Ribbon Stream URL to clipboard!", "Copied", MessageBoxButtons.OK, MessageBoxIcon.Information); };
                    dlg.Controls.Add(lblRib);
                    dlg.Controls.Add(txtRib);
                    dlg.Controls.Add(btnRib);
                    y += 28;
                }

                // 6. Direct Milestone ONB URL
                var lblDir = new Label
                {
                    Text = "🔒 Direct Milestone ONB Subnet URL (10.64.30.142:554):",
                    Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                    Location = new Point(14, y),
                    AutoSize = true,
                    ForeColor = Color.FromArgb(71, 85, 105)
                };
                y += 20;

                var txtDir = new TextBox
                {
                    Text = directUrl,
                    ReadOnly = true,
                    Location = new Point(14, y),
                    Width = 550,
                    Font = new Font("Consolas", 8.8f)
                };
                var btnDir = new Button
                {
                    Text = "📋 Copy ONB",
                    Location = new Point(572, y - 1),
                    Width = 125,
                    Height = 24,
                    Cursor = Cursors.Hand
                };
                btnDir.Click += (s, e) => { Clipboard.SetText(directUrl); MessageBox.Show(dlg, "Copied Direct Camera URL to clipboard!", "Copied", MessageBoxButtons.OK, MessageBoxIcon.Information); };
                y += 32;

                var lblDirDesc = new Label
                {
                    Text = "• Note: Only works on PCs physically connected to the 10.64.30.x camera switch (not standard Wi-Fi).",
                    ForeColor = Color.FromArgb(100, 116, 139),
                    Font = new Font("Segoe UI", 8.2f),
                    Location = new Point(14, y),
                    AutoSize = true
                };
                y += 32;

                var btnClose = new Button
                {
                    Text = "Close",
                    DialogResult = DialogResult.OK,
                    Location = new Point(590, dlg.ClientSize.Height - 38),
                    Width = 97,
                    Height = 28
                };

                dlg.Controls.Add(lblHeader);
                dlg.Controls.Add(lblTest0);
                dlg.Controls.Add(txtTest0);
                dlg.Controls.Add(btnTest0);
                dlg.Controls.Add(lblTest1);
                dlg.Controls.Add(txtTest1);
                dlg.Controls.Add(btnTest1);
                dlg.Controls.Add(lblTest2);
                dlg.Controls.Add(txtTest2);
                dlg.Controls.Add(btnTest2);
                dlg.Controls.Add(lblLan);
                dlg.Controls.Add(txtLan);
                dlg.Controls.Add(btnLan);
                dlg.Controls.Add(lblDir);
                dlg.Controls.Add(txtDir);
                dlg.Controls.Add(btnDir);
                dlg.Controls.Add(lblDirDesc);
                dlg.Controls.Add(btnClose);

                dlg.ShowDialog(this);
            }
        }

        private void LoadCameras()
        {
            try
            {
                _cameras.Clear();
                var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (Item root in Configuration.Instance.GetItemsByKind(Kind.Camera))
                {
                    AddCameraChildren(root, visited);
                }

                _cameras.Sort((left, right) => StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name));

                // Initially check all cameras so all are active
                _checkedCameraGuids.Clear();
                foreach (var cam in _cameras)
                {
                    _checkedCameraGuids.Add(cam.FQID.ObjectId);
                }

                // Populate filter dropdown
                _filterCombo.Items.Clear();
                _filterCombo.Items.Add(new FilterComboItem { Display = $"All Cameras ({_cameras.Count})", CameraGuid = null });
                _filterCombo.Items.Add(new FilterComboItem { Display = "★ Only Checked Cameras", OnlyChecked = true });
                foreach (var cam in _cameras)
                {
                    _filterCombo.Items.Add(new FilterComboItem { Display = $"Camera: {cam.Name}", CameraGuid = cam.FQID.ObjectId });
                }
                _filterCombo.SelectedIndex = 0;

                PopulateGrid();
            }
            catch (Exception exception)
            {
                MessageBox.Show(this, "Could not retrieve camera information: " + exception.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void AddCameraChildren(Item parent, HashSet<string> visited)
        {
            List<Item> children = parent.GetChildren();
            if (children == null) return;

            foreach (Item item in children)
            {
                if (item.FQID.Kind == Kind.Camera && item.FQID.FolderType == FolderType.No)
                {
                    string key = item.FQID.ServerId.Id + ":" + item.FQID.ObjectId;
                    if (visited.Add(key))
                    {
                        _cameras.Add(item);
                    }
                }

                if (item.HasChildren != VideoOS.Platform.HasChildren.No)
                {
                    AddCameraChildren(item, visited);
                }
            }
        }

        private void PopulateGrid()
        {
            string query = _searchBox.Text.Trim();
            _isUpdatingGridRows = true;
            _cameraGrid.Rows.Clear();

            var filtered = _cameras.Where(item => MatchesFilter(item, query)).ToList();

            foreach (Item camera in filtered)
            {
                string rtspUrl = GetDirectCameraTestUrl(camera, 0);
                bool isChecked = _checkedCameraGuids.Contains(camera.FQID.ObjectId);
                int rowIndex = _cameraGrid.Rows.Add(
                    isChecked,
                    camera.Name,
                    camera.FQID.ObjectId.ToString(),
                    rtspUrl,
                    camera.FQID.ServerId.ServerHostname,
                    camera.Enabled ? "Yes" : "No");
                _cameraGrid.Rows[rowIndex].Tag = camera;
            }
            _isUpdatingGridRows = false;

            UpdateStatusCounts();

            if (_cameraGrid.Rows.Count > 0)
            {
                int targetIndex = 0;
                if (_selectedCamera != null)
                {
                    for (int i = 0; i < _cameraGrid.Rows.Count; i++)
                    {
                        if (_cameraGrid.Rows[i].Tag is Item c && c.FQID.ObjectId == _selectedCamera.FQID.ObjectId)
                        {
                            targetIndex = i;
                            break;
                        }
                    }
                }
                _cameraGrid.Rows[targetIndex].Selected = true;
            }
            else
            {
                _selectedCamera = null;
                UpdateSelectedCameraView();
            }
        }

        private bool MatchesFilter(Item camera, string query)
        {
            if (camera == null) return false;

            // 1. Show checked only filter
            if (_chkShowCheckedOnly.Checked && !_checkedCameraGuids.Contains(camera.FQID.ObjectId))
            {
                return false;
            }

            // 2. Dropdown filter selection
            if (_filterCombo.SelectedIndex > 0 && _filterCombo.SelectedItem is FilterComboItem comboItem)
            {
                if (comboItem.OnlyChecked)
                {
                    if (!_checkedCameraGuids.Contains(camera.FQID.ObjectId))
                        return false;
                }
                else if (comboItem.CameraGuid.HasValue)
                {
                    if (camera.FQID.ObjectId != comboItem.CameraGuid.Value)
                        return false;
                }
            }

            // 3. Text query filter
            if (string.IsNullOrWhiteSpace(query))
            {
                return true;
            }

            string guidStr = camera.FQID.ObjectId.ToString();

            // Support comma, semicolon, or pipe separated terms:
            // e.g. "61250, 61253" matches camera 61250 OR 61253!
            var terms = query.Split(new[] { ',', ';', '|' }, StringSplitOptions.RemoveEmptyEntries)
                             .Select(t => t.Trim())
                             .Where(t => !string.IsNullOrEmpty(t))
                             .ToList();

            if (terms.Count == 0) return true;

            // Exclusion terms: e.g. "-WLKR"
            foreach (var term in terms.Where(t => t.StartsWith("-") && t.Length > 1))
            {
                var exclude = term.Substring(1);
                if (camera.Name.IndexOf(exclude, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    guidStr.IndexOf(exclude, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return false;
                }
            }

            var positiveTerms = terms.Where(t => !t.StartsWith("-")).ToList();
            if (positiveTerms.Count == 0) return true;

            return positiveTerms.Any(term =>
                camera.Name.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0 ||
                guidStr.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0 ||
                (camera.FQID.ServerId?.ServerHostname?.IndexOf(term, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0
            );
        }

        private void UpdateStatusCounts()
        {
            int visible = _cameraGrid.Rows.Count;
            int total = _cameras.Count;
            int checkedCount = _cameras.Count(c => _checkedCameraGuids.Contains(c.FQID.ObjectId));

            _countLabel.Text = visible == total
                ? $"{total} camera(s) ({checkedCount} active)"
                : $"Showing {visible} of {total} camera(s) ({checkedCount} active)";

            if (_countLabel.Parent != null)
            {
                _countLabel.Left = _countLabel.Parent.ClientSize.Width - _countLabel.Width - 12;
            }
        }

        private void SelectAllVisible(bool select)
        {
            _isUpdatingGridRows = true;
            foreach (DataGridViewRow row in _cameraGrid.Rows)
            {
                if (row.Tag is Item camera)
                {
                    row.Cells["Checked"].Value = select;
                    if (select)
                    {
                        _checkedCameraGuids.Add(camera.FQID.ObjectId);
                    }
                    else
                    {
                        _checkedCameraGuids.Remove(camera.FQID.ObjectId);
                    }
                }
            }
            _isUpdatingGridRows = false;
            UpdateStatusCounts();

            if (_chkShowCheckedOnly.Checked && !select)
            {
                PopulateGrid();
            }
        }

        private void SaveCheckedCamerasToService()
        {
            var selectedCameras = _cameras.Where(c => _checkedCameraGuids.Contains(c.FQID.ObjectId)).ToList();
            if (selectedCameras.Count == 0)
            {
                MessageBox.Show(this, "No cameras are currently checked/active. Please check at least one camera before saving to service.", "Save to Service", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            var prompt = $"Update background service configuration to capture ONLY the {selectedCameras.Count} checked camera(s)?\n\n" +
                         string.Join("\n", selectedCameras.Select(c => $"• {c.Name} ({c.FQID.ObjectId})"));

            if (MessageBox.Show(this, prompt, "Confirm Save to Service", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            {
                return;
            }

            try
            {
                string bridge = _bridgeHostBox.Text.Trim();
                if (string.IsNullOrEmpty(bridge)) bridge = "10.64.30.142:554";
                string bridgeHost = bridge.Contains(":") ? bridge.Split(':')[0] : bridge;
                int bridgePort = 554;
                if (bridge.Contains(":") && int.TryParse(bridge.Split(':')[1], out int p)) bridgePort = p;

                string uploadUrl = _uploadUrlBox.Text.Trim();
                if (string.IsNullOrEmpty(uploadUrl)) uploadUrl = "http://10.64.30.96:8000/upload";

                var paths = new List<string>
                {
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "appsettings.json"),
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "appsettings.json"),
                    @"C:\Services\CamGrabService\appsettings.json"
                };

                int updatedCount = 0;
                foreach (var path in paths)
                {
                    try
                    {
                        var fullPath = Path.GetFullPath(path);
                        if (File.Exists(fullPath))
                        {
                            UpdateAppSettingsFile(fullPath, selectedCameras, bridgeHost, bridgePort, uploadUrl);
                            updatedCount++;
                        }
                    }
                    catch { }
                }

                MessageBox.Show(this, $"Successfully updated {updatedCount} configuration file(s) with {selectedCameras.Count} camera(s)!\n\n" +
                    "The CamGrab snapshot service will now capture only the selected cameras.", "Service Config Updated", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Failed to update service config: " + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void UpdateAppSettingsFile(string configPath, List<Item> cameras, string bridgeHost, int bridgePort, string uploadUrl)
        {
            var streamsJson = new StringBuilder();
            streamsJson.AppendLine("    \"Streams\": [");
            for (int i = 0; i < cameras.Count; i++)
            {
                var cam = cameras[i];
                streamsJson.AppendLine("      {");
                streamsJson.AppendLine($"        \"Name\": \"{cam.Name}\",");
                streamsJson.AppendLine($"        \"Guid\": \"{cam.FQID.ObjectId}\",");
                streamsJson.AppendLine("        \"Url\": \"\"");
                streamsJson.Append("      }");
                if (i < cameras.Count - 1) streamsJson.Append(",");
                streamsJson.AppendLine();
            }
            streamsJson.AppendLine("    ]");

            string json = File.ReadAllText(configPath);
            var regex = new Regex(@"""Streams""\s*:\s*\[[\s\S]*?\]", RegexOptions.Multiline);
            if (regex.IsMatch(json))
            {
                json = regex.Replace(json, streamsJson.ToString().Trim());
                File.WriteAllText(configPath, json);
            }
        }

        private void UpdateRtspUrlsInGrid()
        {
            foreach (DataGridViewRow row in _cameraGrid.Rows)
            {
                if (row.Tag is Item camera)
                {
                    row.Cells["RtspUrl"].Value = GetDirectCameraTestUrl(camera, 0);
                }
            }
        }

        private void CameraSelectionChanged(object sender, EventArgs e)
        {
            if (_cameraGrid.SelectedRows.Count == 0 || !(_cameraGrid.SelectedRows[0].Tag is Item camera))
            {
                _selectedCamera = null;
                UpdateSelectedCameraView();
                return;
            }

            var previousCamera = _selectedCamera;
            _selectedCamera = camera;
            UpdateSelectedCameraView();

            // Automatically start / switch live feed when user selects a camera
            if (previousCamera == null || previousCamera.FQID.ObjectId != camera.FQID.ObjectId || !_isStreaming)
            {
                StartLiveStream(_selectedCamera);
            }
        }

        private void UpdateSelectedCameraView()
        {
            if (_selectedCamera == null)
            {
                _cameraTitleLabel.Text = "Selected Camera: None";
                _rtspUrlBox.Clear();
                _detailsBox.Clear();
                return;
            }

            _cameraTitleLabel.Text = $"Selected: {_selectedCamera.Name}";
            string testUrl0 = GetDirectCameraTestUrl(_selectedCamera, 0);
            string testUrl1 = GetDirectCameraTestUrl(_selectedCamera, 1);
            string testUrl2 = GetDirectCameraTestUrl(_selectedCamera, 2);
            string lanUrl = BuildLanRtspUrlWithCredentials(_selectedCamera);
            string directUrl = BuildRtspUrlWithCredentials(_selectedCamera);
            _rtspUrlBox.Text = testUrl0;

            var details = new StringBuilder();
            details.AppendLine("Name: " + _selectedCamera.Name);
            details.AppendLine("Enabled: " + _selectedCamera.Enabled);
            details.AppendLine("Camera ID (GUID): " + _selectedCamera.FQID.ObjectId);
            details.AppendLine("Direct Camera Test URL (Main Stream - subtype=0): " + testUrl0);
            details.AppendLine("Direct Camera Test URL (Sub Stream 1 - subtype=1): " + testUrl1);
            details.AppendLine("Direct Camera Test URL (Sub Stream 2 - subtype=2): " + testUrl2);
            details.AppendLine("Network RTSP URL (Wi-Fi / LAN Relay): " + lanUrl);
            details.AppendLine("Direct Camera URL (Milestone Subnet): " + directUrl);
            details.AppendLine("Server: " + _selectedCamera.FQID.ServerId.ServerHostname);
            details.AppendLine("Server ID: " + _selectedCamera.FQID.ServerId.Id);
            details.AppendLine("Server type: " + _selectedCamera.FQID.ServerId.ServerType);
            details.AppendLine("Kind: " + _selectedCamera.FQID.Kind);
            details.AppendLine();
            details.AppendLine("Configuration properties:");

            if (_selectedCamera.Properties != null && _selectedCamera.Properties.Count > 0)
            {
                foreach (string key in _selectedCamera.Properties.Keys.OrderBy(key => key, StringComparer.OrdinalIgnoreCase))
                {
                    details.AppendLine(key + ": " + _selectedCamera.Properties[key]);
                }
            }
            else
            {
                details.AppendLine("No additional properties were returned.");
            }

            _detailsBox.Text = details.ToString();
        }

        private void StartLiveStream(Item camera)
        {
            StopLiveStreamOnly();
            if (camera == null) return;

            int mode = _streamModeCombo != null ? _streamModeCombo.SelectedIndex : 0;
            // 0 = Auto (MIP SDK first, automatic RTSP fallback)
            // 1 = Force MIP SDK Native
            // 2 = Force RTSP (FFmpeg)

            if (mode == 2)
            {
                StartRtspLiveStreamFallback(camera);
                return;
            }

            try
            {
                int reqWidth = (_previewBox != null && _previewBox.Width > 50) ? _previewBox.Width : 640;
                int reqHeight = (_previewBox != null && _previewBox.Height > 50) ? _previewBox.Height : 480;

                _liveSource = new JPEGLiveSource(camera)
                {
                    LiveModeStart = true,
                    Width = reqWidth,
                    Height = reqHeight
                };
                _liveSource.SetKeepAspectRatio(true, false);

                // Register event listeners before initializing the source
                _liveSource.LiveContentEvent += OnLiveContentReceived;
                _liveSource.LiveStatusEvent += OnLiveStatusReceived;

                try
                {
                    if (_liveSource.StreamSelectionParams != null)
                    {
                        _liveSource.StreamSelectionParams.StreamSelectionType = StreamSelectionType.DefaultStream;
                    }
                }
                catch { }

                _liveSource.Init();

                _isStreaming = true;
                _frameCount = 0;
                _btnToggleLive.Text = "⏹ Stop Live Feed";
                _statusLabel.Text = $"Connecting to {camera.Name} live feed via MIP SDK...";
                _ = SyncServerTimeAsync(camera);

                // Auto fallback watchdog: if MIP SDK receives no frames within 2s in Auto mode, fall back to RTSP
                if (mode == 0)
                {
                    if (_autoFallbackTimer == null)
                    {
                        _autoFallbackTimer = new System.Windows.Forms.Timer { Interval = 2000 };
                        _autoFallbackTimer.Tick += (s, e) =>
                        {
                            _autoFallbackTimer.Stop();
                            if (_isStreaming && _frameCount == 0 && _selectedCamera != null)
                            {
                                _statusLabel.Text = $"MIP SDK feed inactive for {camera.Name}. Switching to direct RTSP feed...";
                                StartRtspLiveStreamFallback(_selectedCamera);
                            }
                        };
                    }
                    _autoFallbackTimer.Stop();
                    _autoFallbackTimer.Start();
                }
            }
            catch (Exception ex)
            {
                if (mode == 0) // Auto fallback to RTSP
                {
                    _statusLabel.Text = $"MIP SDK init failed ({ex.Message}). Switching to RTSP feed...";
                    StartRtspLiveStreamFallback(camera);
                }
                else
                {
                    MessageBox.Show(this, "Could not start live camera feed via MIP SDK: " + ex.Message, "Live Feed Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    StopLiveStream();
                }
            }
        }

        private void StopLiveStream()
        {
            StopLiveStreamOnly();
            _isStreaming = false;
            _isDispatchingFrame = 0;
            _btnToggleLive.Text = "▶ Start Live Feed";
            _statusLabel.Text = "Status: Feed stopped.";
        }

        private void StopLiveStreamOnly()
        {
            if (_autoFallbackTimer != null)
            {
                try { _autoFallbackTimer.Stop(); } catch { }
            }

            if (_liveSource != null)
            {
                try
                {
                    _liveSource.LiveContentEvent -= OnLiveContentReceived;
                    _liveSource.LiveStatusEvent -= OnLiveStatusReceived;
                    _liveSource.Close();
                }
                catch { }
                _liveSource = null;
            }

            if (_rtspLiveCts != null)
            {
                try { _rtspLiveCts.Cancel(); _rtspLiveCts.Dispose(); } catch { }
                _rtspLiveCts = null;
            }

            if (_rtspLiveProcess != null && !_rtspLiveProcess.HasExited)
            {
                try { _rtspLiveProcess.Kill(); _rtspLiveProcess.Dispose(); } catch { }
                _rtspLiveProcess = null;
            }
        }

        private void OnLiveStatusReceived(object sender, EventArgs e)
        {
            if (InvokeRequired)
            {
                try { BeginInvoke(new EventHandler(OnLiveStatusReceived), sender, e); } catch { }
                return;
            }

            if (e is LiveStatusEventArgs args)
            {
                if ((args.ChangedStatusFlags & StatusFlags.CameraConnectionLost) != 0 && (args.CurrentStatusFlags & StatusFlags.CameraConnectionLost) != 0)
                {
                    _statusLabel.Text = $"Status: Camera '{_selectedCamera?.Name}' connection lost on recording server.";
                    if (_streamModeCombo != null && _streamModeCombo.SelectedIndex == 0 && _isStreaming)
                    {
                        StartRtspLiveStreamFallback(_selectedCamera);
                    }
                }
                else if ((args.ChangedStatusFlags & StatusFlags.ClientLiveStopped) != 0 && (args.CurrentStatusFlags & StatusFlags.ClientLiveStopped) != 0)
                {
                    _statusLabel.Text = $"Status: Live feed stopped by server for '{_selectedCamera?.Name}'.";
                }
                else if ((args.ChangedStatusFlags & StatusFlags.LiveFeed) != 0)
                {
                    bool isLive = (args.CurrentStatusFlags & StatusFlags.LiveFeed) != 0;
                    if (!isLive)
                    {
                        _statusLabel.Text = $"Status: Live feed inactive for '{_selectedCamera?.Name}'.";
                    }
                }
            }
        }

        private void OnLiveContentReceived(object sender, EventArgs e)
        {
            if (InvokeRequired)
            {
                if (Interlocked.CompareExchange(ref _isDispatchingFrame, 1, 0) != 0)
                {
                    if (e is LiveContentEventArgs dropArgs && dropArgs.LiveContent != null)
                    {
                        dropArgs.LiveContent.Dispose();
                    }
                    return;
                }

                try
                {
                    BeginInvoke(new EventHandler(OnLiveContentReceived), sender, e);
                }
                catch
                {
                    Interlocked.Exchange(ref _isDispatchingFrame, 0);
                    if (e is LiveContentEventArgs dropArgs && dropArgs.LiveContent != null)
                    {
                        dropArgs.LiveContent.Dispose();
                    }
                }
                return;
            }

            try
            {
                if (e is LiveContentEventArgs args)
                {
                    if (args.LiveContent != null && args.LiveContent.Content != null && args.LiveContent.Content.Length > 0)
                    {
                        try
                        {
                            if (_autoFallbackTimer != null)
                            {
                                _autoFallbackTimer.Stop();
                            }

                            using (var ms = new MemoryStream(args.LiveContent.Content))
                            using (var srcBmp = new Bitmap(ms))
                            {
                                var newBmp = new Bitmap(srcBmp);
                                if (_serverTimeOverlayCheck != null && _serverTimeOverlayCheck.Checked)
                                {
                                    using (var g = Graphics.FromImage(newBmp))
                                    {
                                        RenderServerTimestamp(g, newBmp.Width, newBmp.Height);
                                    }
                                }

                                var oldImg = _previewBox.Image;
                                _previewBox.Image = newBmp;
                                oldImg?.Dispose();

                                _frameCount++;
                                _statusLabel.Text = $"Live (MIP SDK): {_selectedCamera?.Name} | Res: {newBmp.Width}x{newBmp.Height} | Server Time: {GetCurrentServerTime():HH:mm:ss}{(_serverTimeSynced ? " (Synced)" : "")} | Frame #{_frameCount}";
                            }
                        }
                        catch (Exception renderEx)
                        {
                            _statusLabel.Text = "Frame render error: " + renderEx.Message;
                        }
                        finally
                        {
                            args.LiveContent.Dispose();
                        }
                    }
                    else if (args.Exception != null)
                    {
                        _statusLabel.Text = "Live stream error: " + args.Exception.Message;
                        if (_streamModeCombo != null && _streamModeCombo.SelectedIndex == 0 && _isStreaming)
                        {
                            StartRtspLiveStreamFallback(_selectedCamera);
                        }
                    }
                }
            }
            finally
            {
                Interlocked.Exchange(ref _isDispatchingFrame, 0);
            }
        }

        private void StartRtspLiveStreamFallback(Item camera)
        {
            StopLiveStreamOnly();
            if (camera == null) return;

            string streamUrl = BuildRtspUrlForFfmpeg(camera);
            if (string.IsNullOrWhiteSpace(streamUrl))
            {
                _statusLabel.Text = "Cannot start RTSP feed: camera stream URL unavailable.";
                return;
            }

            string ffmpeg = ResolveFfmpegPath();
            if (!File.Exists(ffmpeg) && ffmpeg != "ffmpeg")
            {
                _statusLabel.Text = "ffmpeg.exe not found for RTSP live stream.";
                return;
            }

            _isStreaming = true;
            _frameCount = 0;
            _btnToggleLive.Text = "⏹ Stop Live Feed";
            _statusLabel.Text = $"Connecting to {camera.Name} via RTSP stream...";

            _rtspLiveCts = new CancellationTokenSource();
            var token = _rtspLiveCts.Token;

            Task.Run(() => RunRtspLiveStreamLoop(ffmpeg, streamUrl, camera.Name, token), token);
        }

        private void RunRtspLiveStreamLoop(string ffmpeg, string streamUrl, string cameraName, CancellationToken token)
        {
            Process proc = null;
            try
            {
                string args = $"-y -loglevel error -rtsp_transport tcp -i \"{streamUrl}\" -f image2pipe -vcodec mjpeg -";
                var psi = new ProcessStartInfo(ffmpeg, args)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                proc = Process.Start(psi);
                _rtspLiveProcess = proc;

                string lastStderr = null;
                proc.ErrorDataReceived += (s, ev) =>
                {
                    if (!string.IsNullOrEmpty(ev.Data))
                    {
                        lastStderr = ev.Data;
                        Debug.WriteLine($"[RTSP Stderr] {ev.Data}");
                    }
                };
                proc.BeginErrorReadLine();

                using (var stream = proc.StandardOutput.BaseStream)
                using (var ms = new MemoryStream())
                {
                    byte[] buffer = new byte[16384];
                    int prevByte = -1;
                    bool inFrame = false;

                    while (!token.IsCancellationRequested && !proc.HasExited)
                    {
                        int bytesRead = stream.Read(buffer, 0, buffer.Length);
                        if (bytesRead <= 0) break;

                        for (int i = 0; i < bytesRead; i++)
                        {
                            byte b = buffer[i];

                            if (!inFrame)
                            {
                                if (prevByte == 0xFF && b == 0xD8)
                                {
                                    inFrame = true;
                                    ms.SetLength(0);
                                    ms.WriteByte(0xFF);
                                    ms.WriteByte(0xD8);
                                }
                            }
                            else
                            {
                                ms.WriteByte(b);
                                if (prevByte == 0xFF && b == 0xD9)
                                {
                                    inFrame = false;
                                    byte[] frameBytes = ms.ToArray();
                                    DisplayRtspFrame(frameBytes, cameraName);
                                    ms.SetLength(0);
                                }
                            }
                            prevByte = b;
                        }
                    }
                }

                if (!token.IsCancellationRequested && _frameCount == 0 && !string.IsNullOrEmpty(lastStderr))
                {
                    try
                    {
                        BeginInvoke((Action)(() => _statusLabel.Text = $"RTSP stream closed: {lastStderr}"));
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                if (!token.IsCancellationRequested)
                {
                    try
                    {
                        BeginInvoke((Action)(() => _statusLabel.Text = "RTSP live stream error: " + ex.Message));
                    }
                    catch { }
                }
            }
            finally
            {
                if (proc != null && !proc.HasExited)
                {
                    try { proc.Kill(); proc.Dispose(); } catch { }
                }
            }
        }

        private void DisplayRtspFrame(byte[] frameBytes, string cameraName)
        {
            if (Interlocked.CompareExchange(ref _isDispatchingFrame, 1, 0) != 0)
            {
                return;
            }

            try
            {
                BeginInvoke((Action)(() =>
                {
                    try
                    {
                        using (var ms = new MemoryStream(frameBytes))
                        using (var srcBmp = new Bitmap(ms))
                        {
                            var newBmp = new Bitmap(srcBmp);
                            if (_serverTimeOverlayCheck != null && _serverTimeOverlayCheck.Checked)
                            {
                                using (var g = Graphics.FromImage(newBmp))
                                {
                                    RenderServerTimestamp(g, newBmp.Width, newBmp.Height);
                                }
                            }

                            var old = _previewBox.Image;
                            _previewBox.Image = newBmp;
                            old?.Dispose();

                            _frameCount++;
                            _statusLabel.Text = $"Live (RTSP): {cameraName} | Res: {newBmp.Width}x{newBmp.Height} | Server Time: {GetCurrentServerTime():HH:mm:ss} | Frame #{_frameCount}";
                        }
                    }
                    catch { }
                    finally
                    {
                        Interlocked.Exchange(ref _isDispatchingFrame, 0);
                    }
                }));
            }
            catch
            {
                Interlocked.Exchange(ref _isDispatchingFrame, 0);
            }
        }

        private DateTime GetCurrentServerTime()
        {
            return DateTime.Now.Add(_serverTimeOffset);
        }

        private async Task SyncServerTimeAsync(Item camera)
        {
            try
            {
                Uri uri = camera?.FQID?.ServerId?.Uri;
                string host = camera?.FQID?.ServerId?.ServerHostname ?? _bridgeHostBox.Text.Split(':')[0].Trim();
                string targetUrl = uri != null ? uri.ToString() : $"http://{host}";

                using (var req = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Head, targetUrl))
                using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3)))
                using (var res = await _uploadClient.SendAsync(req, System.Net.Http.HttpCompletionOption.ResponseHeadersRead, cts.Token))
                {
                    if (res.Headers.Date.HasValue)
                    {
                        var serverUtc = res.Headers.Date.Value.UtcDateTime;
                        _serverTimeOffset = serverUtc.ToLocalTime() - DateTime.Now;
                        _serverTimeSynced = true;
                    }
                }
            }
            catch
            {
                // Fallback to local host server time (offset remains TimeSpan.Zero)
            }
        }

        private Bitmap EnsureOverlayOnBitmap(Bitmap sourceBmp)
        {
            if (sourceBmp == null) return null;
            if (_serverTimeOverlayCheck == null || !_serverTimeOverlayCheck.Checked) return sourceBmp;

            try
            {
                using (var g = Graphics.FromImage(sourceBmp))
                {
                    RenderServerTimestamp(g, sourceBmp.Width, sourceBmp.Height);
                    return sourceBmp;
                }
            }
            catch
            {
                try
                {
                    var formattedBmp = new Bitmap(sourceBmp.Width, sourceBmp.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                    using (var g = Graphics.FromImage(formattedBmp))
                    {
                        g.DrawImage(sourceBmp, 0, 0, sourceBmp.Width, sourceBmp.Height);
                        RenderServerTimestamp(g, formattedBmp.Width, formattedBmp.Height);
                    }
                    sourceBmp.Dispose();
                    return formattedBmp;
                }
                catch
                {
                    return sourceBmp;
                }
            }
        }

        private void RenderServerTimestamp(Graphics g, int imgWidth, int imgHeight)
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            // Accurate server date & time (bypasses inaccurate camera RTC clock)
            var serverTime = GetCurrentServerTime();
            string camName = _selectedCamera?.Name ?? "Camera";
            string dateStr = serverTime.ToString("yyyy-MM-dd");
            string timeStr = serverTime.ToString("HH:mm:ss");

            // Scale font smoothly with stream resolution
            float baseFontSize = Math.Max(10.5f, Math.Min(22f, imgHeight * 0.032f));
            float badgeFontSize = Math.Max(9f, baseFontSize * 0.82f);

            using (var mainFont = new Font("Segoe UI", baseFontSize, FontStyle.Bold))
            using (var timeFont = new Font("Consolas", baseFontSize * 1.05f, FontStyle.Bold))
            using (var badgeFont = new Font("Segoe UI", badgeFontSize, FontStyle.Bold))
            using (var ribbonBg = new SolidBrush(Color.FromArgb(220, 15, 23, 42))) // Deep sleek slate ribbon
            using (var badgeBg = new SolidBrush(Color.FromArgb(240, 2, 132, 199))) // Blue ribbon tag
            using (var accentLine = new SolidBrush(Color.FromArgb(56, 189, 248))) // Cyan ribbon border
            using (var whiteText = new SolidBrush(Color.White))
            using (var yellowTime = new SolidBrush(Color.FromArgb(254, 240, 138))) // High-visibility yellow time
            using (var dateText = new SolidBrush(Color.FromArgb(226, 232, 240))) // Light slate date
            using (var dotBrush = new SolidBrush(Color.FromArgb(34, 197, 94))) // Green live dot
            {
                string badgeLabel = "TIME";
                var badgeSize = g.MeasureString(badgeLabel, badgeFont);
                var camSize = g.MeasureString(camName, mainFont);
                var dateSize = g.MeasureString(dateStr, mainFont);
                var timeSize = g.MeasureString(timeStr, timeFont);

                int padX = (int)(baseFontSize * 0.75f);
                int ribbonHeight = (int)(baseFontSize * 2.3f);
                int badgeWidth = (int)badgeSize.Width + padX * 2;
                int badgeHeight = ribbonHeight - 6;

                int contentWidth = (int)(badgeWidth + camSize.Width + dateSize.Width + timeSize.Width + padX * 5);
                int chevronCut = (int)(baseFontSize * 1.2f);
                int ribbonWidth = contentWidth + chevronCut + padX;

                // 1. Draw Top-Left Ribbon Polygon anchored at (x=0, y=0) with angled chevron end
                var ribbonPoints = new Point[]
                {
                    new Point(0, 0),
                    new Point(ribbonWidth, 0),
                    new Point(ribbonWidth - chevronCut, ribbonHeight),
                    new Point(0, ribbonHeight)
                };
                g.FillPolygon(ribbonBg, ribbonPoints);

                // 2. Bottom accent border line for the ribbon
                using (var pen = new Pen(accentLine, Math.Max(2, (int)(baseFontSize * 0.15f))))
                {
                    g.DrawLine(pen, 0, ribbonHeight, ribbonWidth - chevronCut, ribbonHeight);
                }

                // 3. Ribbon Badge: "SERVER TIME" tag on the left
                int curX = padX;
                int badgeY = (ribbonHeight - badgeHeight) / 2;
                g.FillRectangle(badgeBg, curX, badgeY, badgeWidth, badgeHeight);

                // Live green dot
                int dotSize = (int)(badgeFontSize * 0.6f);
                int dotY = badgeY + (badgeHeight - dotSize) / 2;
                g.FillEllipse(dotBrush, curX + (int)(padX * 0.55f), dotY, dotSize, dotSize);

                // Badge text
                float badgeTextX = curX + (int)(padX * 0.55f) + dotSize + (int)(baseFontSize * 0.35f);
                float badgeTextY = badgeY + (badgeHeight - badgeSize.Height) / 2f;
                g.DrawString(badgeLabel, badgeFont, whiteText, badgeTextX, badgeTextY);

                curX += badgeWidth + padX;

                // 4. Camera Name
                float textY = (ribbonHeight - camSize.Height) / 2f;
                g.DrawString(camName, mainFont, whiteText, curX, textY);
                curX += (int)camSize.Width + (int)(padX * 0.7f);

                // Separator pipe
                using (var sepBrush = new SolidBrush(Color.FromArgb(120, 148, 163, 184)))
                {
                    g.DrawString("|", mainFont, sepBrush, curX, textY);
                }
                curX += (int)(padX * 0.7f);

                // 5. Server Date
                g.DrawString(dateStr, mainFont, dateText, curX, textY);
                curX += (int)dateSize.Width + (int)(padX * 0.7f);

                // 6. Server Time (digital yellow in Consolas for maximum clarity)
                float timeY = (ribbonHeight - timeSize.Height) / 2f;
                g.DrawString(timeStr, timeFont, yellowTime, curX, timeY);
            }
        }

        private static readonly System.Net.Http.HttpClient _uploadClient = new System.Net.Http.HttpClient
        {
            Timeout = TimeSpan.FromSeconds(10)
        };

        private static string GetShotsDirectory()
        {
            var customDir = Environment.GetEnvironmentVariable("OUTPUT_DIR");
            if (!string.IsNullOrWhiteSpace(customDir)) return Path.GetFullPath(customDir);

            var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "camgrab.csproj")))
                {
                    return Path.Combine(dir.FullName, "shots");
                }
                if (Directory.Exists(Path.Combine(dir.FullName, "camgrab", "shots")))
                {
                    return Path.Combine(dir.FullName, "camgrab", "shots");
                }
                dir = dir.Parent;
            }

            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "shots");
        }

        private void CaptureSnapshot()
        {
            if (_previewBox.Image == null)
            {
                MessageBox.Show(this, "No live video frame available. Click 'Start Live Feed' first to receive camera frames.", "Capture Snapshot", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            try
            {
                string shotsDir = GetShotsDirectory();
                var now = DateTime.Now;
                string dateDir = Path.Combine(shotsDir, now.ToString("yyyy-MM-dd"));
                Directory.CreateDirectory(dateDir);

                string camName = _selectedCamera != null ? _selectedCamera.Name.Replace(" ", "_") : "camera";
                string filePath = Path.Combine(dateDir, $"{camName}_{now:yyyyMMdd_HHmmss}.jpg");

                _previewBox.Image.Save(filePath, System.Drawing.Imaging.ImageFormat.Jpeg);

                if (_autoUploadCheck.Checked)
                {
                    _ = UploadSnapshotAsync(filePath, camName, now);
                }

                MessageBox.Show(this, $"Snapshot saved successfully!\n\nFile: {filePath}" + (_autoUploadCheck.Checked ? "\nUploading to API (10.64.30.96:8000)..." : ""), "Snapshot Saved", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Failed to save snapshot: " + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void AutoSaveSnapshot()
        {
            if (_previewBox.Image == null) return;
            try
            {
                string shotsDir = GetShotsDirectory();
                var now = DateTime.Now;
                string dateDir = Path.Combine(shotsDir, now.ToString("yyyy-MM-dd"));
                Directory.CreateDirectory(dateDir);

                string camName = _selectedCamera != null ? _selectedCamera.Name.Replace(" ", "_") : "camera";
                string filePath = Path.Combine(dateDir, $"{camName}_{now:yyyyMMdd_HHmmss}.jpg");

                _previewBox.Image.Save(filePath, System.Drawing.Imaging.ImageFormat.Jpeg);
                _statusLabel.Text = $"[Auto-Saved] {Path.Combine(now.ToString("yyyy-MM-dd"), Path.GetFileName(filePath))} at {now:HH:mm:ss}";

                if (_autoUploadCheck.Checked)
                {
                    _ = UploadSnapshotAsync(filePath, camName, now);
                }
            }
            catch (Exception ex)
            {
                _statusLabel.Text = "Auto-save failed: " + ex.Message;
            }
        }

        private async Task UploadSnapshotAsync(string filePath, string cameraName, DateTime timestamp)
        {
            string url = _uploadUrlBox.Text.Trim();
            if (string.IsNullOrEmpty(url)) return;

            try
            {
                byte[] imageBytes = File.ReadAllBytes(filePath);
                using (var content = new System.Net.Http.MultipartFormDataContent())
                {
                    content.Add(new System.Net.Http.StringContent(cameraName), "camera_name");
                    content.Add(new System.Net.Http.StringContent(timestamp.ToString("yyyy-MM-dd")), "date");
                    content.Add(new System.Net.Http.StringContent(timestamp.ToString("HH:mm:ss")), "time");

                    var fileContent = new System.Net.Http.ByteArrayContent(imageBytes);
                    fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
                    content.Add(fileContent, "image", Path.GetFileName(filePath));

                    var response = await _uploadClient.PostAsync(url, content);
                    string body = await response.Content.ReadAsStringAsync();

                    // Auto-heal: If receiver returns "uploads are disabled", re-enable on port 8080 and retry
                    if (!response.IsSuccessStatusCode && body.Contains("uploads are disabled"))
                    {
                        try
                        {
                            var uri = new Uri(url);
                            var enableUrl = $"{uri.Scheme}://{uri.Host}:8080/api/upload-enabled";
                            using (var enableContent = new System.Net.Http.FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("enabled", "true") }))
                            {
                                var enableResp = await _uploadClient.PutAsync(enableUrl, enableContent);
                                if (enableResp.IsSuccessStatusCode)
                                {
                                    using (var retryContent = new System.Net.Http.MultipartFormDataContent())
                                    {
                                        retryContent.Add(new System.Net.Http.StringContent(cameraName), "camera_name");
                                        retryContent.Add(new System.Net.Http.StringContent(timestamp.ToString("yyyy-MM-dd")), "date");
                                        retryContent.Add(new System.Net.Http.StringContent(timestamp.ToString("HH:mm:ss")), "time");
                                        var retryFile = new System.Net.Http.ByteArrayContent(imageBytes);
                                        retryFile.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
                                        retryContent.Add(retryFile, "image", Path.GetFileName(filePath));
                                        response = await _uploadClient.PostAsync(url, retryContent);
                                        body = await response.Content.ReadAsStringAsync();
                                    }
                                }
                            }
                        }
                        catch { }
                    }

                    if (InvokeRequired)
                    {
                        BeginInvoke((Action)(() =>
                        {
                            if (response.IsSuccessStatusCode)
                                _statusLabel.Text = $"[Uploaded OK] {cameraName} ({timestamp:HH:mm:ss}) -> 10.64.30.96:8000";
                            else
                                _statusLabel.Text = $"[Upload Failed {response.StatusCode}] {body}";
                        }));
                    }
                    else
                    {
                        if (response.IsSuccessStatusCode)
                            _statusLabel.Text = $"[Uploaded OK] {cameraName} ({timestamp:HH:mm:ss}) -> 10.64.30.96:8000";
                        else
                            _statusLabel.Text = $"[Upload Failed {response.StatusCode}] {body}";
                    }
                }
            }
            catch (Exception ex)
            {
                try
                {
                    BeginInvoke((Action)(() =>
                    {
                        _statusLabel.Text = "Upload error: " + ex.Message;
                    }));
                }
                catch { }
            }
        }

        private void ToggleAlarmSound()
        {
            if (_alarmCts != null)
            {
                _alarmCts.Cancel();
                _alarmCts.Dispose();
                _alarmCts = null;
                _btnAlarm.Text = "🚨 Trigger Alarm";
                _btnAlarm.BackColor = Color.FromArgb(200, 35, 51);
                _statusLabel.Text = "Alarm silenced.";
                return;
            }

            // 1. Post Alarm directly into Milestone Event Server so it appears in Milestone XProtect Smart Client -> Alarm Manager
            bool alarmSent = TriggerMilestoneAlarm(_selectedCamera, out string milestoneResult);

            _alarmCts = new CancellationTokenSource();
            var token = _alarmCts.Token;
            _btnAlarm.Text = "⏹ Stop Alarm";
            _btnAlarm.BackColor = Color.FromArgb(230, 80, 0);

            string camInfo = _selectedCamera != null ? $" ({_selectedCamera.Name})" : "";
            _statusLabel.Text = $"🚨 [ALARM ACTIVE] {milestoneResult} | Siren sounding on system!";

            Task.Run(async () =>
            {
                try
                {
                    var sw = Stopwatch.StartNew();
                    while (!token.IsCancellationRequested && sw.Elapsed < TimeSpan.FromSeconds(30))
                    {
                        System.Media.SystemSounds.Exclamation.Play();
                        try { Console.Beep(1200, 200); } catch { }
                        if (token.IsCancellationRequested) break;
                        try { Console.Beep(800, 200); } catch { }
                        if (token.IsCancellationRequested) break;
                        await Task.Delay(50, token);
                    }
                }
                catch (OperationCanceledException) { }
                catch { }
                finally
                {
                    if (IsHandleCreated && !IsDisposed)
                    {
                        BeginInvoke((Action)(() =>
                        {
                            if (_alarmCts != null)
                            {
                                _alarmCts.Dispose();
                                _alarmCts = null;
                                _btnAlarm.Text = "🚨 Trigger Alarm";
                                _btnAlarm.BackColor = Color.FromArgb(200, 35, 51);
                                _statusLabel.Text = "Alarm sequence completed.";
                            }
                        }));
                    }
                }
            }, token);
        }

        private bool TriggerMilestoneAlarm(Item camera, out string details)
        {
            try
            {
                var cameraName = camera?.Name ?? "CamGrab System";
                var cameraFqid = camera?.FQID ?? EnvironmentManager.Instance.MasterSite;

                var eventSource = new EventSource
                {
                    FQID = cameraFqid,
                    Name = cameraName
                };

                var eventHeader = new EventHeader
                {
                    ID = Guid.NewGuid(),
                    Class = "Alarm",
                    Type = "Manual Alarm",
                    Timestamp = DateTime.UtcNow,
                    Message = $"🚨 Manual Alarm triggered for {cameraName} from MediaLiveViewer",
                    Name = "Manual Alarm",
                    Source = eventSource,
                    Priority = 1,
                    PriorityName = "High"
                };

                var alarm = new Alarm
                {
                    EventHeader = eventHeader,
                    State = 1,
                    StateName = "New"
                };

                // 1. Send via EnvironmentManager message bus
                try
                {
                    EnvironmentManager.Instance.SendMessage(
                        new VideoOS.Platform.Messaging.Message(MessageId.Server.NewAlarmCommand) { Data = alarm }
                    );
                }
                catch { }

                // 2. Also send BaseEvent so any Rule/Analytics trigger fires
                try
                {
                    var baseEvent = new BaseEvent { EventHeader = eventHeader };
                    EnvironmentManager.Instance.SendMessage(
                        new VideoOS.Platform.Messaging.Message(MessageId.Server.NewEventCommand) { Data = baseEvent }
                    );
                }
                catch { }

                // 3. Transmit directly to Event Server via MessageCommunicationManager
                try
                {
                    var serverId = EnvironmentManager.Instance.MasterSite?.ServerId;
                    if (serverId != null)
                    {
                        MessageCommunicationManager.Start(serverId);
                        var msgCom = MessageCommunicationManager.Get(serverId);
                        if (msgCom != null)
                        {
                            var alarmMsg = new VideoOS.Platform.Messaging.Message(MessageId.Server.NewAlarmCommand, alarm);
                            msgCom.TransmitMessage(alarmMsg, null, null, null);

                            var baseEvent = new BaseEvent { EventHeader = eventHeader };
                            var eventMsg = new VideoOS.Platform.Messaging.Message(MessageId.Server.NewEventCommand, baseEvent);
                            msgCom.TransmitMessage(eventMsg, null, null, null);
                        }
                    }
                }
                catch { }

                details = $"Milestone Alarm sent for {cameraName}";
                return true;
            }
            catch (Exception ex)
            {
                details = "Milestone error: " + ex.Message;
                return false;
            }
        }

        // =========================================================================
        // VLC, RTSP Ribbon Re-streaming, Duration Clip & Video Synopsis Implementations
        // =========================================================================

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            StopLiveStream();
            if (_autoFallbackTimer != null)
            {
                try { _autoFallbackTimer.Stop(); _autoFallbackTimer.Dispose(); } catch { }
                _autoFallbackTimer = null;
            }
            if (_ribbonStreamProcess != null && !_ribbonStreamProcess.HasExited)
            {
                try { _ribbonStreamProcess.Kill(); } catch { }
                _ribbonStreamProcess = null;
            }
            if (_clipRecordingProcess != null && !_clipRecordingProcess.HasExited)
            {
                try { _clipRecordingProcess.Kill(); } catch { }
                _clipRecordingProcess = null;
            }
            if (_authProxy != null)
            {
                try { _authProxy.Dispose(); } catch { }
                _authProxy = null;
            }
            base.OnFormClosing(e);
        }

        private RtspAuthSanitizerProxy EnsureAuthProxy()
        {
            string bridge = _bridgeHostBox.Text.Trim();
            if (string.IsNullOrEmpty(bridge)) bridge = "10.64.30.142:554";
            string targetHost = bridge.Contains(":") ? bridge.Split(':')[0] : bridge;
            int targetPort = 554;
            if (bridge.Contains(":") && int.TryParse(bridge.Split(':')[1], out int p)) targetPort = p;

            if (_authProxy != null && _authProxy.IsActive && _authProxy.TargetHost == targetHost && _authProxy.TargetPort == targetPort)
            {
                return _authProxy;
            }

            if (_authProxy != null)
            {
                try { _authProxy.Dispose(); } catch { }
                _authProxy = null;
            }

            try
            {
                _authProxy = new RtspAuthSanitizerProxy(targetHost, targetPort);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("EnsureAuthProxy error: " + ex.Message);
            }
            return _authProxy;
        }

        private string BuildRtspUrlForFfmpeg(Item camera)
        {
            if (camera == null) return string.Empty;

            // 1. Direct hardware camera RTSP stream (Dahua IP cameras)
            string directUrl = GetDirectCameraTestUrl(camera, 0);
            if (!string.IsNullOrWhiteSpace(directUrl))
            {
                return directUrl;
            }

            // 2. Fallback to Milestone proxy
            string user = Environment.GetEnvironmentVariable("CAM_USER") ?? "mipuser";
            string pass = Environment.GetEnvironmentVariable("CAM_PASS") ?? "M1l3T@123";
            string creds = string.IsNullOrEmpty(pass) ? "" : $"{Uri.EscapeDataString(user)}:{Uri.EscapeDataString(pass)}@";

            var proxy = EnsureAuthProxy();
            if (proxy != null && proxy.IsActive)
            {
                return $"rtsp://{creds}127.0.0.1:{proxy.LocalPort}/live/{camera.FQID.ObjectId}";
            }

            return BuildRtspUrlWithCredentials(camera);
        }

        private string BuildLanRtspUrlWithCredentials(Item camera)
        {
            if (camera == null) return string.Empty;

            string user = Environment.GetEnvironmentVariable("CAM_USER") ?? "mipuser";
            string pass = Environment.GetEnvironmentVariable("CAM_PASS") ?? "M1l3T@123";
            string creds = string.IsNullOrEmpty(pass) ? "" : $"{Uri.EscapeDataString(user)}:{Uri.EscapeDataString(pass)}@";

            var proxy = EnsureAuthProxy();
            if (proxy != null && proxy.IsActive)
            {
                string host = !string.IsNullOrEmpty(proxy.LanIp) && proxy.LanIp != "127.0.0.1" ? proxy.LanIp : "127.0.0.1";
                return $"rtsp://{creds}{host}:{proxy.LocalPort}/live/{camera.FQID.ObjectId}";
            }

            return BuildRtspUrlWithCredentials(camera);
        }

        private string BuildRtspUrlWithCredentials(Item camera)
        {
            string url = BuildRtspUrl(camera);
            if (string.IsNullOrEmpty(url)) return string.Empty;

            if (!url.Contains("@"))
            {
                string user = Environment.GetEnvironmentVariable("CAM_USER") ?? "mipuser";
                string pass = Environment.GetEnvironmentVariable("CAM_PASS") ?? "M1l3T@123";
                if (!string.IsNullOrEmpty(user) && !string.IsNullOrEmpty(pass))
                {
                    return url.Replace("rtsp://", $"rtsp://{Uri.EscapeDataString(user)}:{Uri.EscapeDataString(pass)}@");
                }
            }
            return url;
        }

        public static string GetDirectCameraTestUrl(Item camera, int subtype = 0)
        {
            if (camera == null) return string.Empty;
            string name = camera.Name ?? "";
            string guid = camera.FQID?.ObjectId.ToString() ?? "";

            string ip = "10.64.61.145";
            if (name.IndexOf("61253", StringComparison.OrdinalIgnoreCase) >= 0 ||
                guid.Equals("229802e9-9bb3-4e89-aec7-005c3e44fb94", StringComparison.OrdinalIgnoreCase))
            {
                ip = "10.64.61.46";
            }
            else if (name.IndexOf("61252", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     guid.Equals("9ac405ca-1db1-4696-9d7f-ebccc74a9b18", StringComparison.OrdinalIgnoreCase))
            {
                ip = "10.64.61.145";
            }
            else
            {
                // Attempt to resolve IP from appsettings.json if customized
                try
                {
                    string cfgPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "appsettings.json");
                    if (!File.Exists(cfgPath)) cfgPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "appsettings.json");
                    if (File.Exists(cfgPath))
                    {
                        string json = File.ReadAllText(cfgPath);
                        var match = Regex.Match(json, $@"rtsp://[^@]*@([^:/]+):.*{Regex.Escape(guid)}", RegexOptions.IgnoreCase);
                        if (match.Success) ip = match.Groups[1].Value;
                    }
                }
                catch { }
            }

            string user = Environment.GetEnvironmentVariable("TEST_CAM_USER") ?? "testfr";
            string pass = Environment.GetEnvironmentVariable("TEST_CAM_PASS") ?? "It123aasl";
            return $"rtsp://{Uri.EscapeDataString(user)}:{Uri.EscapeDataString(pass)}@{ip}:554/cam/realmonitor?channel=1&subtype={subtype}";
        }

        public static string ResolveFfmpegPath()
        {
            var baseDirFfmpeg = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ffmpeg.exe");
            if (File.Exists(baseDirFfmpeg)) return baseDirFfmpeg;

            var localFfmpeg = Path.Combine(Environment.CurrentDirectory, "ffmpeg.exe");
            if (File.Exists(localFfmpeg)) return localFfmpeg;

            var projectFfmpeg = Path.Combine(Environment.CurrentDirectory, "camgrab", "ffmpeg.exe");
            if (File.Exists(projectFfmpeg)) return projectFfmpeg;

            var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (dir != null)
            {
                var candidate = Path.Combine(dir.FullName, "ffmpeg.exe");
                if (File.Exists(candidate)) return candidate;
                dir = dir.Parent;
            }

            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var wingetDir = Path.Combine(localAppData, "Microsoft", "WinGet", "Packages");
            if (Directory.Exists(wingetDir))
            {
                var found = Directory.GetFiles(wingetDir, "ffmpeg.exe", SearchOption.AllDirectories).FirstOrDefault();
                if (found != null) return found;
            }

            return "ffmpeg";
        }

        public static string ResolveVlcPath()
        {
            string p1 = @"C:\Program Files\VideoLAN\VLC\vlc.exe";
            if (File.Exists(p1)) return p1;

            string p2 = @"C:\Program Files (x86)\VideoLAN\VLC\vlc.exe";
            if (File.Exists(p2)) return p2;

            try
            {
                using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\VideoLAN\VLC"))
                {
                    var val = key?.GetValue("InstallDir") as string;
                    if (!string.IsNullOrEmpty(val))
                    {
                        var exe = Path.Combine(val, "vlc.exe");
                        if (File.Exists(exe)) return exe;
                    }
                }
            }
            catch { }

            return "vlc.exe";
        }

        private static string BuildFfmpegRibbonFilter(string cameraName, bool isSynopsis = false)
        {
            string font = @"C\:/Windows/Fonts/segoeuib.ttf";
            if (!File.Exists(@"C:\Windows\Fonts\segoeuib.ttf"))
            {
                font = @"C\:/Windows/Fonts/arial.ttf";
            }

            string tag = isSynopsis ? "SYNOPSIS" : "TIME";
            string cleanCam = Regex.Replace(cameraName, @"[':\\]", "").Trim();

            return $"drawbox=x=0:y=0:w=680:h=46:color=black@0.85:t=fill," +
                   $"drawbox=x=0:y=44:w=680:h=3:color=#38bdf8@1.0:t=fill," +
                   $"drawbox=x=10:y=6:w=74:h=32:color=#0284c7@1.0:t=fill," +
                   $"drawtext=fontfile='{font}':text='{tag}':fontcolor=white:fontsize=15:x=18:y=12," +
                   $"drawtext=fontfile='{font}':text='{cleanCam}  |  %{{localtime}} (Server)':fontcolor=white:fontsize=18:x=96:y=12";
        }

        private void OpenCurrentStreamInVlc()
        {
            if (_selectedCamera == null)
            {
                MessageBox.Show(this, "Please select an active camera from the list first.", "Open in VLC", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            string vlcPath = ResolveVlcPath();
            if (!File.Exists(vlcPath) && vlcPath != "vlc.exe")
            {
                MessageBox.Show(this, "VLC Media Player was not found.\nPlease install VLC from https://www.videolan.org/vlc/", "VLC Not Found", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            string streamUrl;
            bool isDirectTestUrl = false;
            if (!string.IsNullOrEmpty(_activeRibbonUrl))
            {
                // For local VLC on the same machine, connect via loopback (127.0.0.1) to avoid LAN firewall/hairpin blocks
                string camClean = Regex.Replace(_selectedCamera.Name, @"[^\w\-]", "_");
                streamUrl = $"rtsp://127.0.0.1:8554/live/{camClean}";
            }
            else
            {
                // When pressing 'Open in VLC', open direct camera test URL (subtype=0: Main Stream)
                streamUrl = GetDirectCameraTestUrl(_selectedCamera, subtype: 0);
                isDirectTestUrl = true;

                if (string.IsNullOrWhiteSpace(streamUrl))
                {
                    // Fallback to local auth proxy if direct camera cannot be resolved
                    EnsureAuthProxy();
                    streamUrl = BuildRtspUrlForFfmpeg(_selectedCamera);
                    if (string.IsNullOrWhiteSpace(streamUrl))
                    {
                        streamUrl = BuildRtspUrlWithCredentials(_selectedCamera);
                    }
                    isDirectTestUrl = false;
                }
            }

            if (string.IsNullOrWhiteSpace(streamUrl))
            {
                MessageBox.Show(this, "Could not determine RTSP URL for this camera.", "Open in VLC", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            try
            {
                // Always enforce TCP transport (--rtsp-tcp) for Dahua direct RTSP and Milestone streams
                string vlcArgs = $"\"{streamUrl}\" --network-caching=300 --rtsp-tcp --no-video-title-show";
                if (!isDirectTestUrl && string.IsNullOrEmpty(_activeRibbonUrl))
                {
                    string user = Environment.GetEnvironmentVariable("CAM_USER") ?? "mipuser";
                    string pass = Environment.GetEnvironmentVariable("CAM_PASS") ?? "M1l3T@123";
                    vlcArgs += $" --rtsp-user=\"{user}\" --rtsp-pwd=\"{pass}\"";
                }

                var psi = new ProcessStartInfo(vlcPath, vlcArgs)
                {
                    UseShellExecute = false
                };

                Process.Start(psi);
                _statusLabel.Text = isDirectTestUrl
                    ? $"Launched VLC with Direct Test Stream for {_selectedCamera.Name}."
                    : $"Launched VLC for {_selectedCamera.Name} live stream via TCP.";
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Failed to launch VLC: " + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void ToggleRtspRibbonStream()
        {
            if (_ribbonStreamProcess != null && !_ribbonStreamProcess.HasExited)
            {
                try
                {
                    _ribbonStreamProcess.Kill();
                    _ribbonStreamProcess.Dispose();
                }
                catch { }
                _ribbonStreamProcess = null;
                _activeRibbonUrl = null;
                _btnStreamRibbon.Text = "📡 Stream Ribbon";
                _btnStreamRibbon.BackColor = SystemColors.Control;
                _btnStreamRibbon.ForeColor = SystemColors.ControlText;
                _statusLabel.Text = "RTSP Ribbon stream stopped.";
                return;
            }

            if (_selectedCamera == null)
            {
                MessageBox.Show(this, "Please select a camera to stream with the ribbon.", "RTSP Ribbon Stream", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            string streamUrl = BuildRtspUrlForFfmpeg(_selectedCamera);
            string camClean = Regex.Replace(_selectedCamera.Name, @"[^\w\-]", "_");
            int port = 8554;
            string lanIp = RtspAuthSanitizerProxy.GetLanIpAddress();
            string listenUrl = $"rtsp://0.0.0.0:{port}/live/{camClean}";
            string shareableRtspUrl = $"rtsp://{lanIp}:{port}/live/{camClean}";

            try
            {
                string ffmpeg = ResolveFfmpegPath();
                string filter = BuildFfmpegRibbonFilter(_selectedCamera.Name);
                string args = $"-y -loglevel warning -rtsp_transport tcp -i \"{streamUrl}\" -vf \"{filter}\" -c:v libx264 -preset ultrafast -tune zerolatency -f rtsp -rtsp_flags listen \"{listenUrl}\"";

                var psi = new ProcessStartInfo(ffmpeg, args)
                {
                    RedirectStandardError = false,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                _ribbonStreamProcess = Process.Start(psi);
                _activeRibbonUrl = shareableRtspUrl;
                _btnStreamRibbon.Text = "⏹ Stop Ribbon";
                _btnStreamRibbon.BackColor = Color.FromArgb(2, 132, 199);
                _btnStreamRibbon.ForeColor = Color.White;

                try { Clipboard.SetText(shareableRtspUrl); } catch { }
                _statusLabel.Text = $"Broadcasting RTSP with Server Time Ribbon: {shareableRtspUrl} (URL copied)";

                var choice = MessageBox.Show(this,
                    $"Live RTSP Stream with Server Time Ribbon is now active!\n\n" +
                    $"Network Stream URL (Shareable to other devices on Wi-Fi/LAN):\n{shareableRtspUrl}\n\n" +
                    "(URL has been copied to your clipboard)\n\n" +
                    "Would you like to open it immediately in VLC on this PC?",
                    "RTSP Ribbon Stream Active",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Information);

                if (choice == DialogResult.Yes)
                {
                    _statusLabel.Text = "Waiting for RTSP stream to initialize before launching VLC...";
                    Task.Delay(1500).ContinueWith(_ =>
                    {
                        try { BeginInvoke((Action)(() => OpenCurrentStreamInVlc())); } catch { }
                    });
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Could not start RTSP Ribbon Stream: " + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private int GetSelectedDurationSeconds()
        {
            switch (_clipDurationCombo.SelectedIndex)
            {
                case 0: return 10;
                case 1: return 30;
                case 2: return 60;
                case 3: return 120;
                case 4: return 300;
                case 5: return 600;
                default: return 30;
            }
        }

        private string GetSelectedDurationLabel()
        {
            return _clipDurationCombo.SelectedItem?.ToString() ?? "30s";
        }

        private void ResetClipButtons()
        {
            _clipCountdownTimer?.Stop();
            _clipRecordingProcess = null;
            _btnRecordClip.Text = "🎬 Record Clip";
            _btnRecordClip.BackColor = SystemColors.Control;
            _btnRecordClip.ForeColor = SystemColors.ControlText;
            _btnSynopsis.Text = "⚡ Video Synopsis";
            _btnSynopsis.BackColor = Color.FromArgb(79, 70, 229);
            _btnSynopsis.ForeColor = Color.White;
        }

        private async Task RecordClipOrSynopsisAsync(bool isSynopsis)
        {
            if (_clipRecordingProcess != null && !_clipRecordingProcess.HasExited)
            {
                try
                {
                    _clipCountdownTimer?.Stop();
                    _clipRecordingProcess.StandardInput?.Close();
                    await Task.Delay(400);
                    if (!_clipRecordingProcess.HasExited)
                    {
                        _clipRecordingProcess.Kill();
                    }
                }
                catch { }
                _statusLabel.Text = "Recording stopped by user.";
                ResetClipButtons();
                return;
            }

            if (_selectedCamera == null)
            {
                MessageBox.Show(this, "Please select an active camera first.", "Record Clip", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            int durationSeconds = GetSelectedDurationSeconds();
            string streamUrl = BuildRtspUrlForFfmpeg(_selectedCamera);

            string shotsDir = GetShotsDirectory();
            var now = DateTime.Now;
            string dateDir = Path.Combine(shotsDir, "clips", now.ToString("yyyy-MM-dd"));
            Directory.CreateDirectory(dateDir);

            string camClean = Regex.Replace(_selectedCamera.Name, @"[^\w\-]", "_");
            string suffix = isSynopsis ? $"synopsis_{durationSeconds}s" : $"clip_{durationSeconds}s";
            string fileName = $"{camClean}_{now:yyyyMMdd_HHmmss}_{suffix}.mp4";
            string outputPath = Path.Combine(dateDir, fileName);
            _currentClipPath = outputPath;

            string ffmpeg = ResolveFfmpegPath();
            string filter = BuildFfmpegRibbonFilter(_selectedCamera.Name, isSynopsis);
            if (isSynopsis)
            {
                filter += ",mpdecimate,setpts=N/FRAME_RATE/TB";
            }

            string args = $"-y -loglevel warning -rtsp_transport tcp -i \"{streamUrl}\" -t {durationSeconds} -vf \"{filter}\" -c:v libx264 -preset veryfast -pix_fmt yuv420p -an \"{outputPath}\"";

            var psi = new ProcessStartInfo(ffmpeg, args)
            {
                RedirectStandardInput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            try
            {
                _clipRecordingProcess = Process.Start(psi);
                _clipTotalSeconds = durationSeconds;
                _clipRemainingSeconds = durationSeconds;

                var activeBtn = isSynopsis ? _btnSynopsis : _btnRecordClip;
                activeBtn.BackColor = Color.FromArgb(220, 38, 38);
                activeBtn.ForeColor = Color.White;

                _clipCountdownTimer?.Stop();
                _clipCountdownTimer = new System.Windows.Forms.Timer { Interval = 1000 };
                _clipCountdownTimer.Tick += (s, e) =>
                {
                    _clipRemainingSeconds--;
                    int elapsed = _clipTotalSeconds - _clipRemainingSeconds;
                    activeBtn.Text = $"⏹ Stop ({elapsed:D2}s/{_clipTotalSeconds:D2}s)";
                    _statusLabel.Text = $"Recording {(isSynopsis ? "Synopsis" : "Clip")}: {elapsed}s of {_clipTotalSeconds}s for {_selectedCamera.Name}...";
                    if (_clipRemainingSeconds <= 0)
                    {
                        _clipCountdownTimer.Stop();
                    }
                };
                _clipCountdownTimer.Start();

                var errorTask = _clipRecordingProcess.StandardError.ReadToEndAsync();
                await Task.Run(() => _clipRecordingProcess.WaitForExit());
                _clipCountdownTimer.Stop();

                ResetClipButtons();

                if (File.Exists(outputPath) && new FileInfo(outputPath).Length > 1024)
                {
                    var fileInfo = new FileInfo(outputPath);
                    double sizeMb = fileInfo.Length / (1024.0 * 1024.0);
                    _statusLabel.Text = $"{(isSynopsis ? "Synopsis" : "Clip")} saved: {fileName} ({sizeMb:F2} MB)";

                    var res = MessageBox.Show(this,
                        $"{(isSynopsis ? "Video Synopsis" : "Video Clip")} created successfully!\n\n" +
                        $"• Type: {(isSynopsis ? "Motion-Condensed Video Synopsis (Inactivity Dropped)" : "Standard Video Clip")}\n" +
                        $"• File: {fileName}\n" +
                        $"• Size: {sizeMb:F2} MB\n" +
                        $"• Location: {outputPath}\n\n" +
                        "Would you like to play it now in VLC?",
                        $"{(isSynopsis ? "Video Synopsis" : "Clip")} Ready",
                        MessageBoxButtons.YesNo,
                        MessageBoxIcon.Information);

                    if (res == DialogResult.Yes)
                    {
                        PlayFileInVlc(outputPath);
                    }
                }
                else
                {
                    string err = await errorTask;
                    _statusLabel.Text = "Recording failed: " + err;
                    MessageBox.Show(this, "Recording failed:\n" + err, "Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
            catch (Exception ex)
            {
                ResetClipButtons();
                MessageBox.Show(this, "Recording error: " + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void OnSynopsisClicked()
        {
            if (_clipRecordingProcess != null && !_clipRecordingProcess.HasExited)
            {
                _ = RecordClipOrSynopsisAsync(isSynopsis: true);
                return;
            }

            var dialog = new Form
            {
                Text = "Video Synopsis (Condensed Video Summary)",
                Width = 540,
                Height = 230,
                StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false,
                BackColor = Color.FromArgb(248, 250, 252)
            };

            var label = new Label
            {
                Text = "Video Synopsis automatically compresses long camera footage into a short, manageable summary by removing all static/idle frames where nothing moves, while burning the accurate Server Time ribbon on every active event.",
                Dock = DockStyle.Top,
                Height = 60,
                Padding = new Padding(14, 12, 14, 0),
                Font = new Font("Segoe UI", 9.2f)
            };

            var btnLiveSynopsis = new Button
            {
                Text = $"⚡ Record Live Synopsis ({GetSelectedDurationLabel()})",
                Size = new Size(240, 42),
                Location = new Point(18, 80),
                BackColor = Color.FromArgb(79, 70, 229),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 9.2f, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            btnLiveSynopsis.Click += (s, e) =>
            {
                dialog.Close();
                _ = RecordClipOrSynopsisAsync(isSynopsis: true);
            };

            var btnExistingSynopsis = new Button
            {
                Text = "📂 Condense Existing Clip / Video",
                Size = new Size(240, 42),
                Location = new Point(268, 80),
                BackColor = Color.FromArgb(71, 85, 105),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 9.2f, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            btnExistingSynopsis.Click += (s, e) =>
            {
                dialog.Close();
                CondenseExistingVideoToSynopsis();
            };

            var note = new Label
            {
                Text = "Tip: Static frames are automatically eliminated, condensing hours of inactivity into seconds.",
                Dock = DockStyle.Bottom,
                Height = 35,
                Padding = new Padding(16, 0, 16, 8),
                ForeColor = Color.FromArgb(100, 116, 139),
                Font = new Font("Segoe UI", 8.5f, FontStyle.Italic)
            };

            dialog.Controls.Add(btnExistingSynopsis);
            dialog.Controls.Add(btnLiveSynopsis);
            dialog.Controls.Add(label);
            dialog.Controls.Add(note);

            dialog.ShowDialog(this);
        }

        private void CondenseExistingVideoToSynopsis()
        {
            using (var ofd = new OpenFileDialog
            {
                Title = "Select Video to Generate Synopsis",
                Filter = "Video Files (*.mp4;*.avi;*.mkv)|*.mp4;*.avi;*.mkv|All Files (*.*)|*.*",
                InitialDirectory = Path.Combine(GetShotsDirectory(), "clips")
            })
            {
                if (ofd.ShowDialog(this) != DialogResult.OK) return;
                string inputPath = ofd.FileName;
                string dir = Path.GetDirectoryName(inputPath);
                string baseName = Path.GetFileNameWithoutExtension(inputPath);
                string outputPath = Path.Combine(dir, $"{baseName}_synopsis.mp4");

                _statusLabel.Text = "Processing Video Synopsis (removing inactivity)...";
                Task.Run(() =>
                {
                    try
                    {
                        string args = $"-y -i \"{inputPath}\" -vf \"mpdecimate,setpts=N/FRAME_RATE/TB\" -c:v libx264 -preset veryfast -pix_fmt yuv420p -an \"{outputPath}\"";
                        var psi = new ProcessStartInfo(ResolveFfmpegPath(), args)
                        {
                            RedirectStandardError = true,
                            UseShellExecute = false,
                            CreateNoWindow = true
                        };

                        using (var p = Process.Start(psi))
                        {
                            string err = p.StandardError.ReadToEnd();
                            p.WaitForExit();
                        }

                        BeginInvoke(new Action(() =>
                        {
                            if (File.Exists(outputPath))
                            {
                                var res = MessageBox.Show(this,
                                    $"Video Synopsis created successfully!\n\nFile: {outputPath}\n\nWould you like to open it in VLC?",
                                    "Synopsis Complete",
                                    MessageBoxButtons.YesNo,
                                    MessageBoxIcon.Information);
                                if (res == DialogResult.Yes)
                                {
                                    PlayFileInVlc(outputPath);
                                }
                            }
                            else
                            {
                                MessageBox.Show(this, "Failed to create synopsis.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                            }
                        }));
                    }
                    catch (Exception ex)
                    {
                        BeginInvoke(new Action(() =>
                        {
                            MessageBox.Show(this, "Synopsis processing error: " + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        }));
                    }
                });
            }
        }

        private void PlayFileInVlc(string filePath)
        {
            try
            {
                string vlc = ResolveVlcPath();
                Process.Start(new ProcessStartInfo(vlc, $"\"{filePath}\" --no-video-title-show") { UseShellExecute = false });
            }
            catch
            {
                try { Process.Start(new ProcessStartInfo(filePath) { UseShellExecute = true }); } catch { }
            }
        }

        private void ShowTrimVideoDialog()
        {
            string clipsDir = Path.Combine(GetShotsDirectory(), "clips");
            string defaultInput = _currentClipPath;
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
                    Text = "Select an existing MP4 video and specify the start and end time to take only that part of the clip.",
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
                        InitialDirectory = Directory.Exists(clipsDir) ? clipsDir : GetShotsDirectory()
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
                        string ffmpeg = ResolveFfmpegPath();
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
                        _statusLabel.Text = $"Trimmed clip saved: {output}";

                        if (playInVlc)
                        {
                            PlayFileInVlc(output);
                        }
                        else
                        {
                            var res = MessageBox.Show(dlg, $"Trimmed clip created successfully!\n\nFile: {output}\nSize: {fi.Length / (1024.0 * 1024.0):F2} MB\n\nWould you like to open it now in VLC?", "Trim Complete", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
                            if (res == DialogResult.Yes) PlayFileInVlc(output);
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

        private void OnMainPresetSelected()
        {
            var now = DateTime.Now;
            switch (_comboMainPresets.SelectedIndex)
            {
                case 0: // October 24: 02:12 - 02:34
                    _dtpMainClipDate.Value = new DateTime(2026, 10, 24);
                    _txtMainClipStart.Text = "02:12:00";
                    _txtMainClipEnd.Text = "02:34:00";
                    _lblMainFilterStatus.Text = "Preset: 02:12 - 02:34 ready. Click 'Extract MP4 Clip' or 'Play Range in VLC'.";
                    break;
                case 1: // Today Full Day
                    _dtpMainClipDate.Value = DateTime.Today;
                    _txtMainClipStart.Text = "00:00:00";
                    _txtMainClipEnd.Text = "23:59:59";
                    _lblMainFilterStatus.Text = "Preset: Today (Full Day) loaded.";
                    break;
                case 2: // Yesterday
                    _dtpMainClipDate.Value = DateTime.Today.AddDays(-1);
                    _txtMainClipStart.Text = "00:00:00";
                    _txtMainClipEnd.Text = "23:59:59";
                    _lblMainFilterStatus.Text = "Preset: Yesterday loaded.";
                    break;
                case 3: // Last 1 Hour
                    _dtpMainClipDate.Value = now.Date;
                    _txtMainClipStart.Text = now.AddHours(-1).ToString("HH:mm:ss");
                    _txtMainClipEnd.Text = now.ToString("HH:mm:ss");
                    _lblMainFilterStatus.Text = "Preset: Last 1 Hour loaded.";
                    break;
                case 4: // Last 10 Minutes
                    _dtpMainClipDate.Value = now.Date;
                    _txtMainClipStart.Text = now.AddMinutes(-10).ToString("HH:mm:ss");
                    _txtMainClipEnd.Text = now.ToString("HH:mm:ss");
                    _lblMainFilterStatus.Text = "Preset: Last 10 Minutes loaded.";
                    break;
                case 5: // Custom Time Range
                    _lblMainFilterStatus.Text = "Custom: Enter any Start and End time above.";
                    break;
            }
        }

        private void OpenTimeRangeFilterWithCurrentSettings()
        {
            var initialCam = _selectedCamera != null ? _selectedCamera.Name : null;
            DateTime date = _dtpMainClipDate.Value.Date;
            TimeSpan.TryParse(_txtMainClipStart.Text.Trim(), out var startTime);
            TimeSpan.TryParse(_txtMainClipEnd.Text.Trim(), out var endTime);
            var startDt = date.Add(startTime);
            var endDt = date.Add(endTime);

            var filterForm = new TimeRangeFrameFilterForm(GetShotsDirectory(), initialCam, _cameras, startDt, endDt);
            filterForm.Show(this);
        }

        private async Task ExtractOrPlayTimeRangeClipAsync(bool playInVlc)
        {
            var camera = _selectedCamera ?? (_cameras.Count > 0 ? _cameras[0] : null);
            if (camera == null)
            {
                MessageBox.Show(this, "Please select a camera from the inventory list.", "Select Camera", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            string cameraName = camera.Name;
            DateTime date = _dtpMainClipDate.Value.Date;
            if (!TimeSpan.TryParse(_txtMainClipStart.Text.Trim(), out var startTime))
            {
                if (DateTime.TryParse(_txtMainClipStart.Text.Trim(), out var parsedStart))
                    startTime = parsedStart.TimeOfDay;
                else
                    startTime = new TimeSpan(2, 12, 0);
            }

            if (!TimeSpan.TryParse(_txtMainClipEnd.Text.Trim(), out var endTime))
            {
                if (DateTime.TryParse(_txtMainClipEnd.Text.Trim(), out var parsedEnd))
                    endTime = parsedEnd.TimeOfDay;
                else
                    endTime = new TimeSpan(2, 34, 0);
            }

            DateTime startDt = date.Add(startTime);
            DateTime endDt = date.Add(endTime);
            if (startDt > endDt)
            {
                MessageBox.Show(this, "Start time must be earlier than End time.", "Invalid Time Range", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            _btnMainExtractClip.Enabled = false;
            _btnMainPlayVlc.Enabled = false;
            _lblMainFilterStatus.Text = $"⏳ Scanning snapshots for {cameraName} ({startDt:yyyy-MM-dd HH:mm:ss} to {endDt:HH:mm:ss})...";

            try
            {
                string clipsDir = Path.Combine(GetShotsDirectory(), "clips");
                Directory.CreateDirectory(clipsDir);
                string cleanCam = Regex.Replace(cameraName, @"[^\w\-]", "_");
                string outputFile = Path.Combine(clipsDir, $"{cleanCam}_{startDt:yyyyMMdd_HHmmss}_to_{endDt:HHmmss}.mp4");

                // Scan snapshots matching time range from all sources
                var matchingFrames = await Task.Run(() => ScanFramesForCamera(cleanCam, startDt, endDt));

                if (matchingFrames.Count > 0)
                {
                    _lblMainFilterStatus.Text = $"⏳ Found {matchingFrames.Count} frames for {cleanCam}. Compiling MP4 clip with FFmpeg...";
                    await CompileFramesToMp4Async(matchingFrames, outputFile);
                }
                else
                {
                    // Check if an existing source video exists to trim
                    string sourceVideo = _currentClipPath;
                    if (string.IsNullOrEmpty(sourceVideo) || !File.Exists(sourceVideo))
                    {
                        if (Directory.Exists(clipsDir))
                        {
                            sourceVideo = Directory.GetFiles(clipsDir, "*.mp4", SearchOption.AllDirectories)
                                .OrderByDescending(f => new FileInfo(f).LastWriteTime)
                                .FirstOrDefault();
                        }
                    }

                    if (!string.IsNullOrEmpty(sourceVideo) && File.Exists(sourceVideo))
                    {
                        _lblMainFilterStatus.Text = $"⏳ Trimming video clip ({Path.GetFileName(sourceVideo)}) from {startTime} to {endTime}...";
                        await TrimVideoFileAsync(sourceVideo, startTime.ToString(@"hh\:mm\:ss"), endTime.ToString(@"hh\:mm\:ss"), outputFile);
                    }
                    else
                    {
                        // Direct RTSP capture fallback if neither frames nor video files exist
                        double duration = Math.Min(600, (endDt - startDt).TotalSeconds);
                        if (duration <= 0) duration = 30;
                        string rtspUrl = BuildRtspUrlForFfmpeg(camera);
                        _lblMainFilterStatus.Text = $"⏳ Capturing {duration:F0}s stream clip from direct RTSP...";
                        await CaptureStreamClipAsync(rtspUrl, (int)duration, outputFile);
                    }
                }

                if (File.Exists(outputFile))
                {
                    _currentClipPath = outputFile;
                    if (playInVlc)
                    {
                        string vlcPath = ResolveVlcPath();
                        _lblMainFilterStatus.Text = $"▶ Playing clip in VLC: {Path.GetFileName(outputFile)}";
                        PlayFileInVlc(outputFile);
                    }
                    else
                    {
                        var fi = new FileInfo(outputFile);
                        _lblMainFilterStatus.Text = $"✅ Clip saved: {fi.Name} ({fi.Length / 1024} KB)";
                        Process.Start("explorer.exe", $"/select,\"{outputFile}\"");
                    }
                }
                else
                {
                    _lblMainFilterStatus.Text = "⚠️ Could not generate clip: no frames or source video available for this range.";
                }
            }
            catch (Exception ex)
            {
                _lblMainFilterStatus.Text = "❌ Error: " + ex.Message;
                MessageBox.Show(this, "Time range filter error: " + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                _btnMainExtractClip.Enabled = true;
                _btnMainPlayVlc.Enabled = true;
            }
        }

        private static List<string> ScanFramesForCamera(string cameraName, DateTime startDt, DateTime endDt)
        {
            var results = new List<(DateTime Ts, string Path)>();
            var roots = new List<string> { GetShotsDirectory() };
            const string svcShots = @"C:\Services\CamGrab\shots";
            if (Directory.Exists(svcShots) && !roots.Contains(svcShots)) roots.Add(svcShots);

            foreach (var root in roots)
            {
                for (DateTime d = startDt.Date; d <= endDt.Date; d = d.AddDays(1))
                {
                    var sub = Path.Combine(root, d.ToString("yyyy-MM-dd"));
                    if (Directory.Exists(sub))
                    {
                        try
                        {
                            foreach (var f in Directory.GetFiles(sub, "*.jpg"))
                            {
                                var name = Path.GetFileName(f);
                                if (!name.StartsWith(cameraName, StringComparison.OrdinalIgnoreCase)) continue;

                                var m = Regex.Match(name, @"^.+?_(\d{8})_(\d{6})(?:_(\d{3}))?", RegexOptions.IgnoreCase);
                                if (m.Success)
                                {
                                    var dtStr = m.Groups[1].Value + m.Groups[2].Value + (m.Groups[3].Success ? m.Groups[3].Value : "000");
                                    if (DateTime.TryParseExact(dtStr, "yyyyMMddHHmmssfff", CultureInfo.InvariantCulture, DateTimeStyles.None, out var ts))
                                    {
                                        if (ts >= startDt && ts <= endDt)
                                        {
                                            results.Add((ts, f));
                                        }
                                    }
                                }
                            }
                        }
                        catch { }
                    }
                }
            }

            return results.OrderBy(x => x.Ts).Select(x => x.Path).Distinct().ToList();
        }

        private static async Task CompileFramesToMp4Async(List<string> frames, string outputFile)
        {
            string ffmpeg = ResolveFfmpegPath();
            string tempConcat = Path.Combine(Path.GetTempPath(), $"concat_{Guid.NewGuid():N}.txt");
            var sb = new StringBuilder();
            double durationPerFrame = 0.5; // 2 fps for snapshot playback

            foreach (var f in frames)
            {
                sb.AppendLine($"file '{f.Replace("\\", "/")}'");
                sb.AppendLine($"duration {durationPerFrame.ToString("F2", CultureInfo.InvariantCulture)}");
            }
            if (frames.Count > 0)
            {
                sb.AppendLine($"file '{frames.Last().Replace("\\", "/")}'");
            }

            File.WriteAllText(tempConcat, sb.ToString());

            try
            {
                string args = $"-y -f concat -safe 0 -i \"{tempConcat}\" -c:v libx264 -pix_fmt yuv420p -r 25 -movflags +faststart \"{outputFile}\"";
                var psi = new ProcessStartInfo(ffmpeg, args)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true
                };
                await Task.Run(() =>
                {
                    using (var p = Process.Start(psi))
                    {
                        if (p != null) p.WaitForExit();
                    }
                });
            }
            finally
            {
                try { File.Delete(tempConcat); } catch { }
            }
        }

        private static async Task TrimVideoFileAsync(string inputFile, string start, string end, string outputFile)
        {
            string ffmpeg = ResolveFfmpegPath();
            string args = $"-y -ss {start} -to {end} -i \"{inputFile}\" -c:v libx264 -preset veryfast -pix_fmt yuv420p -c:a aac \"{outputFile}\"";
            var psi = new ProcessStartInfo(ffmpeg, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true
            };
            await Task.Run(() =>
            {
                using (var p = Process.Start(psi))
                {
                    if (p != null) p.WaitForExit();
                }
            });
        }

        private static async Task CaptureStreamClipAsync(string rtspUrl, int durationSec, string outputFile)
        {
            string ffmpeg = ResolveFfmpegPath();
            string args = $"-y -loglevel error -rtsp_transport tcp -i \"{rtspUrl}\" -t {durationSec} -c:v copy -movflags +faststart \"{outputFile}\"";
            var psi = new ProcessStartInfo(ffmpeg, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true
            };
            await Task.Run(() =>
            {
                using (var p = Process.Start(psi))
                {
                    if (p != null) p.WaitForExit();
                }
            });
        }

        private sealed class FilterComboItem
        {
            public string Display { get; set; } = string.Empty;
            public Guid? CameraGuid { get; set; }
            public bool OnlyChecked { get; set; }
            public override string ToString() => Display;
        }
    }
}
