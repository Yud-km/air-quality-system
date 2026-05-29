using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.IO.Ports;
using System.Text;
using System.Windows.Forms;
using System.Windows.Forms.DataVisualization.Charting;

namespace AirQualityMonitor
{
    public partial class Form1 : Form
    {
        // ── Serial port ──────────────────────────────────────────────────────
        private SerialPort serialPort;
        private string receiveBuffer = "";

        // ── Sensor data ──────────────────────────────────────────────────────
        private double pm25Value    = 0;
        private double co2Value     = 0;
        private double tempValue    = 0;
        private double humValue     = 0;
        private bool   fanState     = false;

        // ── Demo / chart ─────────────────────────────────────────────────────
        private Timer demoTimer;
        private Timer fanAnimTimer;
        private bool  isDemoMode   = false;
        private bool  fanAnimating = false;
        private int   fanAngle     = 0;
        private Random rng         = new Random();

        // ── Chart data ───────────────────────────────────────────────────────
        private const int MAX_CHART_POINTS = 60;
        private List<double> pm25History  = new List<double>();
        private List<double> co2History   = new List<double>();
        private List<double> tempHistory  = new List<double>();

        // ── Colors ───────────────────────────────────────────────────────────
        private static readonly Color SidebarBg    = Color.FromArgb(22, 27, 34);
        private static readonly Color SidebarCard  = Color.FromArgb(33, 38, 45);
        private static readonly Color AccentGreen  = Color.FromArgb(35, 197, 132);
        private static readonly Color AccentBlue   = Color.FromArgb(57, 142, 214);
        private static readonly Color AccentOrange = Color.FromArgb(230, 162, 60);
        private static readonly Color AccentRed    = Color.FromArgb(220, 75, 75);
        private static readonly Color MainBg       = Color.FromArgb(240, 243, 248);
        private static readonly Color PanelBg      = Color.White;
        private static readonly Color TextDim      = Color.FromArgb(130, 150, 180);
        private static readonly Color TextWhite    = Color.FromArgb(230, 235, 245);
        private static readonly Color HeaderBg     = Color.FromArgb(15, 20, 28);

        // ── Log data ─────────────────────────────────────────────────────────
        private DataGridView dgvLog;

        // ── UI Controls ──────────────────────────────────────────────────────
        private Label lblPm25Value, lblCo2Value, lblTempValue, lblHumValue;
        private Label lblPm25Status, lblCo2Status, lblTempStatus, lblHumStatus;
        private Label lblConnStatus, lblAirQuality, lblSafetyLabel;
        private Panel pnlSafetyIndicator;
        private ComboBox cboCom, cboBaud;
        private Button btnRefreshCom, btnConnect, btnDemo;
        private Button btnFanToggle, btnClearLog, btnExportHtml;
        private RadioButton rdoManual, rdoAuto;
        private TrackBar trkFanSpeed;
        private Label lblFanSpeed;
        private NumericUpDown nudThreshold;
        private Chart chartMain;
        private Panel pnlFanIcon;
        private Label lblFanIndicator;

        // =====================================================================
        public Form1()
        {
            InitializeMyForm();
            PopulateComPorts();
            SetupTimers();
        }

        // =====================================================================
        //  UI BUILDER
        // =====================================================================
        private void InitializeMyForm()
        {
            this.Text            = "Giám sát chất lượng không khí – ATmega16 | MQ135 | DHT11 | Fan";
            this.Size            = new Size(1280, 780);
            this.MinimumSize     = new Size(1100, 700);
            this.BackColor       = MainBg;
            this.Font            = new Font("Segoe UI", 9f);
            this.StartPosition   = FormStartPosition.CenterScreen;
            this.FormClosing    += (s, e) => CleanUp();

            // ── Root split: sidebar | main ─────────────────────────────────
            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                BackColor = MainBg,
                Padding = Padding.Empty,
                Margin  = Padding.Empty
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 272));
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            this.Controls.Add(root);

            root.Controls.Add(BuildSidebar(), 0, 0);
            root.Controls.Add(BuildMainArea(), 1, 0);
        }

        // ── SIDEBAR ──────────────────────────────────────────────────────────
        private Panel BuildSidebar()
        {
            var sidebar = new Panel
            {
                Dock      = DockStyle.Fill,
                BackColor = SidebarBg,
                Padding   = new Padding(0)
            };

            // Header band
            var header = new Panel
            {
                Dock      = DockStyle.Top,
                Height    = 64,
                BackColor = HeaderBg
            };
            var lblTitle = new Label
            {
                Text      = "AIR QUALITY MONITOR",
                Font      = new Font("Segoe UI", 11f, FontStyle.Bold),
                ForeColor = TextWhite,
                AutoSize  = false,
                Dock      = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter
            };
            header.Controls.Add(lblTitle);
            sidebar.Controls.Add(header);

            // Subtitle chip
            var chip = new Panel
            {
                Dock      = DockStyle.Top,
                Height    = 32,
                BackColor = SidebarBg,
                Padding   = new Padding(12, 4, 12, 4)
            };
            var chipLbl = new Label
            {
                Text      = "ATmega16 + MQ135 + DHT11 + Fan PWM",
                Font      = new Font("Segoe UI", 7.5f),
                ForeColor = TextDim,
                AutoSize  = false,
                Dock      = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter
            };
            chip.Controls.Add(chipLbl);
            sidebar.Controls.Add(chip);

            // Connection status
            var connPanel = new FlowLayoutPanel
            {
                Dock          = DockStyle.Top,
                Height        = 28,
                BackColor     = SidebarBg,
                FlowDirection = FlowDirection.RightToLeft,
                Padding       = new Padding(8, 4, 8, 0)
            };
            lblConnStatus = new Label
            {
                Text      = "● Chưa kết nối",
                Font      = new Font("Segoe UI", 8.5f, FontStyle.Bold),
                ForeColor = AccentRed,
                AutoSize  = true
            };
            connPanel.Controls.Add(lblConnStatus);
            sidebar.Controls.Add(connPanel);

            // ── 4 sensor cards ───────────────────────────────────────────
            var sensors = new Panel
            {
                Dock      = DockStyle.Top,
                Height    = 4 * 80,
                BackColor = SidebarBg,
                Padding   = new Padding(12, 6, 12, 0)
            };

            (lblPm25Value,  lblPm25Status)  = AddSensorCard(sensors, 0,   "PM2.5",        "0,0", "μg/m³", AccentBlue);
            (lblCo2Value,   lblCo2Status)   = AddSensorCard(sensors, 80,  "CO₂ (MQ135)",  "0,0", "ppm",   AccentGreen);
            (lblTempValue,  lblTempStatus)  = AddSensorCard(sensors, 160, "Nhiệt độ",     "0,0", "°C",    AccentOrange);
            (lblHumValue,   lblHumStatus)   = AddSensorCard(sensors, 240, "Độ ẩm",        "0,0", "%",     AccentBlue);

            sidebar.Controls.Add(sensors);

            // ── UART section ─────────────────────────────────────────────
            var uartPanel = BuildUartPanel();
            sidebar.Controls.Add(uartPanel);

            // ── Demo button ──────────────────────────────────────────────
            btnDemo = new Button
            {
                Text      = "▶  Chế độ Demo",
                Dock      = DockStyle.Bottom,
                Height    = 38,
                FlatStyle = FlatStyle.Flat,
                BackColor = SidebarCard,
                ForeColor = TextDim,
                Font      = new Font("Segoe UI", 9f),
                Cursor    = Cursors.Hand
            };
            btnDemo.FlatAppearance.BorderColor = Color.FromArgb(55, 65, 80);
            btnDemo.FlatAppearance.BorderSize  = 1;
            btnDemo.Margin  = new Padding(12, 0, 12, 8);
            btnDemo.Click  += BtnDemo_Click;
            sidebar.Controls.Add(btnDemo);

            return sidebar;
        }

        private (Label valLbl, Label statusLbl) AddSensorCard(Panel parent, int top,
            string name, string initialVal, string unit, Color accent)
        {
            var card = new Panel
            {
                Location  = new Point(0, top),
                Size      = new Size(parent.Width, 76),
                BackColor = SidebarCard,
                Margin    = new Padding(0, 0, 0, 4),
                Padding   = new Padding(14, 8, 14, 8)
            };
            card.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;

            // Draw left accent strip
            card.Paint += (s, e) =>
            {
                using (var b = new SolidBrush(accent))
                    e.Graphics.FillRectangle(b, 0, 0, 3, card.Height);
            };

            var lblName = new Label
            {
                Text      = name,
                Font      = new Font("Segoe UI", 8.5f),
                ForeColor = TextDim,
                Location  = new Point(14, 8),
                AutoSize  = true
            };

            var statusLbl = new Label
            {
                Text      = "Chờ...",
                Font      = new Font("Segoe UI", 8f),
                ForeColor = AccentOrange,
                Location  = new Point(0, 8),
                AutoSize  = true,
                Anchor    = AnchorStyles.Right | AnchorStyles.Top
            };
            statusLbl.Left = 248 - statusLbl.Width;

            var valLbl = new Label
            {
                Text      = initialVal,
                Font      = new Font("Segoe UI", 24f, FontStyle.Bold),
                ForeColor = Color.White,
                Location  = new Point(14, 28),
                AutoSize  = true
            };

            var unitLbl = new Label
            {
                Text      = unit,
                Font      = new Font("Segoe UI", 9f),
                ForeColor = TextDim,
                Location  = new Point(14, 55),
                AutoSize  = true
            };

            card.Controls.AddRange(new Control[] { lblName, statusLbl, valLbl, unitLbl });
            parent.Controls.Add(card);

            // Position status label after card is added to handle resize
            card.Resize += (s, e) => { statusLbl.Left = card.Width - statusLbl.Width - 14; };

            return (valLbl, statusLbl);
        }

        private Panel BuildUartPanel()
        {
            var p = new Panel
            {
                Dock      = DockStyle.Top,
                Height    = 200,
                BackColor = SidebarBg,
                Padding   = new Padding(12, 8, 12, 0)
            };

            var sectionLbl = MakeSectionLabel("Kết nối UART");
            sectionLbl.Location = new Point(12, 8);
            p.Controls.Add(sectionLbl);

            // COM label + combo
            AddLabel(p, "Cổng COM:", 12, 32);
            cboCom = new ComboBox
            {
                Location     = new Point(12, 50),
                Size         = new Size(220, 26),
                DropDownStyle= ComboBoxStyle.DropDownList,
                BackColor    = SidebarCard,
                ForeColor    = TextWhite,
                FlatStyle    = FlatStyle.Flat
            };
            p.Controls.Add(cboCom);

            AddLabel(p, "Baud rate:", 12, 82);
            cboBaud = new ComboBox
            {
                Location     = new Point(12, 100),
                Size         = new Size(220, 26),
                DropDownStyle= ComboBoxStyle.DropDownList,
                BackColor    = SidebarCard,
                ForeColor    = TextWhite,
                FlatStyle    = FlatStyle.Flat
            };
            cboBaud.Items.AddRange(new object[] { "4800","9600","19200","38400","57600","115200" });
            cboBaud.SelectedItem = "9600";
            p.Controls.Add(cboBaud);

            btnRefreshCom = MakeSideButton("↺  Làm mới cổng", 12, 132, SidebarCard);
            btnRefreshCom.Width = 220;
            btnRefreshCom.Click += (s,e) => PopulateComPorts();
            p.Controls.Add(btnRefreshCom);

            btnConnect = new Button
            {
                Text      = "Kết nối",
                Location  = new Point(12, 162),
                Size      = new Size(220, 36),
                FlatStyle = FlatStyle.Flat,
                BackColor = AccentGreen,
                ForeColor = Color.White,
                Font      = new Font("Segoe UI", 10f, FontStyle.Bold),
                Cursor    = Cursors.Hand
            };
            btnConnect.FlatAppearance.BorderSize = 0;
            btnConnect.Click += BtnConnect_Click;
            p.Controls.Add(btnConnect);

            return p;
        }

        // ── MAIN AREA ────────────────────────────────────────────────────────
        private Panel BuildMainArea()
        {
            var main = new Panel
            {
                Dock      = DockStyle.Fill,
                BackColor = MainBg,
                Padding   = new Padding(12)
            };

            // Top status bar
            var statusBar = BuildStatusBar();
            statusBar.Dock = DockStyle.Top;
            main.Controls.Add(statusBar);

            // Chart panel
            var chartPanel = BuildChartPanel();
            chartPanel.Dock = DockStyle.Top;
            main.Controls.Add(chartPanel);

            // Fan control
            var fanPanel = BuildFanPanel();
            fanPanel.Dock = DockStyle.Top;
            main.Controls.Add(fanPanel);

            // Log panel (fills remaining space)
            var logPanel = BuildLogPanel();
            logPanel.Dock = DockStyle.Fill;
            main.Controls.Add(logPanel);

            return main;
        }

        private Panel BuildStatusBar()
        {
            var bar = new Panel
            {
                Height    = 44,
                BackColor = PanelBg,
                Margin    = new Padding(0, 0, 0, 8),
                Padding   = new Padding(16, 0, 16, 0)
            };
            bar.Paint += PanelRoundedBorder;

            lblAirQuality = new Label
            {
                Text      = "✓  Chất lượng không khí ổn định",
                Font      = new Font("Segoe UI", 10f, FontStyle.Bold),
                ForeColor = AccentGreen,
                AutoSize  = true,
                Location  = new Point(12, 12)
            };
            bar.Controls.Add(lblAirQuality);

            // Safety indicator (right side)
            var row = new FlowLayoutPanel
            {
                Location      = new Point(0, 8),
                Height        = 28,
                AutoSize      = true,
                FlowDirection = FlowDirection.RightToLeft,
                Anchor        = AnchorStyles.Right | AnchorStyles.Top,
                BackColor     = Color.Transparent
            };

            lblSafetyLabel = new Label
            {
                Text      = "Trạng thái: AN TOÀN",
                Font      = new Font("Segoe UI", 9f, FontStyle.Bold),
                ForeColor = Color.FromArgb(40, 60, 40),
                AutoSize  = true,
                Margin    = new Padding(0, 4, 4, 0)
            };

            pnlSafetyIndicator = new Panel
            {
                Size      = new Size(14, 14),
                BackColor = AccentGreen,
                Margin    = new Padding(0, 7, 4, 0)
            };
            pnlSafetyIndicator.Paint += (s, e) =>
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using (var b = new SolidBrush(pnlSafetyIndicator.BackColor))
                    e.Graphics.FillEllipse(b, 0, 0, 13, 13);
            };
            pnlSafetyIndicator.BackColor = AccentGreen;

            row.Controls.Add(lblSafetyLabel);
            row.Controls.Add(pnlSafetyIndicator);
            bar.Controls.Add(row);

            bar.Resize += (s, e) => { row.Left = bar.Width - row.Width - 12; };

            return bar;
        }

        private Panel BuildChartPanel()
        {
            var outer = new Panel
            {
                Height    = 240,
                BackColor = PanelBg,
                Padding   = new Padding(10),
                Margin    = new Padding(0, 0, 0, 8)
            };
            outer.Paint += PanelRoundedBorder;

            chartMain = new Chart
            {
                Dock      = DockStyle.Fill,
                BackColor = PanelBg
            };

            var area = new ChartArea("Main")
            {
                BackColor     = Color.Transparent,
                BorderColor   = Color.FromArgb(220, 225, 235),
                BorderWidth   = 1,
                BorderDashStyle = ChartDashStyle.Solid
            };
            area.AxisX.LabelStyle.ForeColor  = Color.Gray;
            area.AxisX.LineColor             = Color.FromArgb(210, 215, 225);
            area.AxisX.MajorGrid.LineColor   = Color.FromArgb(235, 238, 245);
            area.AxisX.MajorGrid.LineDashStyle = ChartDashStyle.Dot;
            area.AxisX.Title                 = "Thời gian (điểm lấy mẫu)";
            area.AxisX.TitleForeColor        = Color.Gray;
            area.AxisY.LabelStyle.ForeColor  = Color.Gray;
            area.AxisY.LineColor             = Color.FromArgb(210, 215, 225);
            area.AxisY.MajorGrid.LineColor   = Color.FromArgb(235, 238, 245);
            area.AxisY.MajorGrid.LineDashStyle = ChartDashStyle.Dot;
            area.AxisY.Minimum               = 0;
            area.AxisY.Maximum               = 100;
            chartMain.ChartAreas.Add(area);

            chartMain.Legends.Clear();
            var leg = new Legend
            {
                Docking   = Docking.Bottom,
                Alignment = StringAlignment.Center,
                BackColor = Color.Transparent,
                BorderColor = Color.Transparent,
                Font      = new Font("Segoe UI", 8f)
            };
            chartMain.Legends.Add(leg);

            // Series: PM2.5
            var sPm = new Series("PM2.5 (μg/m³)")
            {
                ChartType       = SeriesChartType.SplineArea,
                Color           = Color.FromArgb(100, AccentBlue),
                BorderColor     = AccentBlue,
                BorderWidth     = 2,
                ChartArea       = "Main",
                IsVisibleInLegend= true,
                LegendText      = "PM2.5 (μg/m³)"
            };

            // Series: CO2
            var sCo2 = new Series("CO₂ (ppm)")
            {
                ChartType       = SeriesChartType.SplineArea,
                Color           = Color.FromArgb(80, AccentGreen),
                BorderColor     = AccentGreen,
                BorderWidth     = 2,
                ChartArea       = "Main",
                IsVisibleInLegend= true,
                LegendText      = "CO₂ (ppm)"
            };

            // Series: Temperature
            var sTemp = new Series("Nhiệt độ (°C)")
            {
                ChartType       = SeriesChartType.Spline,
                Color           = AccentOrange,
                BorderWidth     = 2,
                ChartArea       = "Main",
                IsVisibleInLegend= true,
                LegendText      = "Nhiệt độ (°C)"
            };

            chartMain.Series.Add(sPm);
            chartMain.Series.Add(sCo2);
            chartMain.Series.Add(sTemp);

            // Placeholder label
            var placeholder = new Label
            {
                Text      = "Nhấn ▶ Chế độ Demo để xem biểu đồ thời gian thực",
                Font      = new Font("Segoe UI", 10f),
                ForeColor = Color.FromArgb(180, 185, 200),
                AutoSize  = false,
                Dock      = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
                BackColor = Color.Transparent,
                Name      = "chartPlaceholder"
            };
            outer.Controls.Add(placeholder);
            outer.Controls.Add(chartMain);
            chartMain.Visible = false;

            return outer;
        }


        private Panel BuildFanPanel()
        {
            var outer = new Panel
            {
                Height = 152,
                BackColor = PanelBg,
                Margin = new Padding(0, 0, 0, 8),
                Padding = new Padding(12)
            };
            outer.Paint += PanelRoundedBorder;

            var titleLbl = new Label
            {
                Text = "Điều khiển quạt – Fan PWM",
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
                ForeColor = Color.FromArgb(50, 60, 80),
                AutoSize = true,
                Location = new Point(12, 12)
            };
            outer.Controls.Add(titleLbl);

            // Fan icon
            pnlFanIcon = new Panel
            {
                Location = new Point(12, 34),
                Size = new Size(90, 90),
                BackColor = Color.FromArgb(235, 240, 248)
            };
            pnlFanIcon.Paint += PaintFan;
            outer.Controls.Add(pnlFanIcon);

            // Fan state label
            lblFanIndicator = new Label
            {
                Text = "● TẮT",
                Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
                ForeColor = Color.Gray,
                AutoSize = true,
                Location = new Point(22, 127)
            };
            outer.Controls.Add(lblFanIndicator);

            // Mode radios
            rdoManual = new RadioButton
            {
                Text = "Thủ công",
                Location = new Point(118, 38),
                AutoSize = true,
                Checked = true,
                Font = new Font("Segoe UI", 9f),
                ForeColor = Color.FromArgb(50, 60, 80)
            };
            rdoAuto = new RadioButton
            {
                Text = "Tự động (PM2.5)",
                Location = new Point(118, 62),
                AutoSize = true,
                Font = new Font("Segoe UI", 9f),
                ForeColor = Color.FromArgb(50, 60, 80)
            };
            // keep enable/disable behavior
            rdoManual.CheckedChanged += (s, e) => UpdateFanControls();
            rdoAuto.CheckedChanged += (s, e) => UpdateFanControls();
            // send mode to MCU only on user click to avoid feedback loops
            rdoManual.Click += (s, e) => SendCommand("#mode:0");
            rdoAuto.Click += (s, e) => SendCommand("#mode:1");
            outer.Controls.Add(rdoManual);
            outer.Controls.Add(rdoAuto);

            // Threshold
            AddLabel(outer, "Ngưỡng PM2.5:", 118, 92, Color.FromArgb(80, 90, 110));
            nudThreshold = new NumericUpDown
            {
                Location = new Point(220, 89),
                Size = new Size(60, 24),
                Minimum = 5,
                Maximum = 200,
                Value = 35,
                Font = new Font("Segoe UI", 9f)
            };
            outer.Controls.Add(nudThreshold);
            AddLabel(outer, "μg/m³", 283, 92, Color.Gray);

            // Fan toggle button
            btnFanToggle = new Button
            {
                Text = "▶  BẬT QUẠT",
                Location = new Point(380, 38),
                Size = new Size(150, 36),
                FlatStyle = FlatStyle.Flat,
                BackColor = AccentGreen,
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            btnFanToggle.FlatAppearance.BorderSize = 0;
            btnFanToggle.Click += BtnFanToggle_Click;
            outer.Controls.Add(btnFanToggle);

            // Speed slider
            AddLabel(outer, "Tốc độ:", 380, 86, Color.FromArgb(80, 90, 110));
            trkFanSpeed = new TrackBar
            {
                Location = new Point(430, 82),
                Size = new Size(200, 30),
                Minimum = 0,
                Maximum = 100,
                Value = 50,
                TickFrequency = 10,
                SmallChange = 5,
                LargeChange = 10
            };
            trkFanSpeed.ValueChanged += TrkFanSpeed_ValueChanged;
            outer.Controls.Add(trkFanSpeed);

            lblFanSpeed = new Label
            {
                Text = "50%",
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
                ForeColor = AccentBlue,
                AutoSize = true,
                Location = new Point(635, 88)
            };
            outer.Controls.Add(lblFanSpeed);

            return outer;
        }


        private Panel BuildLogPanel()
        {
            var outer = new Panel
            {
                BackColor = PanelBg,
                Padding   = new Padding(12, 8, 12, 8),
                Margin    = new Padding(0)
            };
            outer.Paint += PanelRoundedBorder;

            var titleLbl = new Label
            {
                Text      = "Nhật ký dữ liệu",
                Font      = new Font("Segoe UI", 9.5f, FontStyle.Bold),
                ForeColor = Color.FromArgb(50, 60, 80),
                AutoSize  = true,
                Location  = new Point(12, 10)
            };
            outer.Controls.Add(titleLbl);

            // Grid
            dgvLog = new DataGridView
            {
                Location              = new Point(12, 32),
                Size                  = new Size(outer.Width - 24, outer.Height - 70),
                AllowUserToAddRows    = false,
                AllowUserToDeleteRows = false,
                ReadOnly              = true,
                RowHeadersVisible     = false,
                AutoSizeColumnsMode   = DataGridViewAutoSizeColumnsMode.Fill,
                BorderStyle           = BorderStyle.None,
                BackgroundColor       = PanelBg,
                GridColor             = Color.FromArgb(230, 235, 245),
                SelectionMode         = DataGridViewSelectionMode.FullRowSelect,
                Font                  = new Font("Segoe UI", 8.5f),
                Anchor                = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom
            };
            dgvLog.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(245, 248, 255);
            dgvLog.ColumnHeadersDefaultCellStyle.ForeColor = Color.FromArgb(60, 80, 120);
            dgvLog.ColumnHeadersDefaultCellStyle.Font      = new Font("Segoe UI", 8.5f, FontStyle.Bold);
            dgvLog.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
            dgvLog.EnableHeadersVisualStyles = false;
            dgvLog.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(248, 250, 255);

            dgvLog.Columns.Add("colTime", "Thời gian");
            dgvLog.Columns.Add("colPm",   "PM2.5 (μg/m³)");
            dgvLog.Columns.Add("colCo",   "CO₂ (ppm)");
            dgvLog.Columns.Add("colTemp", "Nhiệt độ (°C)");
            dgvLog.Columns.Add("colHum",  "Độ ẩm (%)");

            outer.Controls.Add(dgvLog);

            // Bottom buttons
            var btnRow = new FlowLayoutPanel
            {
                Height        = 36,
                Dock          = DockStyle.Bottom,
                FlowDirection = FlowDirection.RightToLeft,
                Padding       = new Padding(4, 2, 0, 0),
                BackColor     = Color.Transparent
            };

            btnExportHtml = MakeIconButton("⊕ Xuất HTML", AccentOrange,   Color.White);
            btnClearLog   = MakeIconButton("✕ Xóa log",   Color.FromArgb(230,235,245), Color.FromArgb(80,90,110));

            btnExportHtml.Click += BtnExportHtml_Click;
            btnClearLog.Click   += (s,e) => { dgvLog.Rows.Clear(); };

            btnRow.Controls.Add(btnExportHtml);
            btnRow.Controls.Add(btnClearLog);
            outer.Controls.Add(btnRow);

            outer.Resize += (s,e) =>
            {
                dgvLog.Size = new Size(outer.Width - 24, outer.Height - 74);
            };

            return outer;
        }

        // =====================================================================
        //  HELPERS
        // =====================================================================
        private Label MakeSectionLabel(string text)
        {
            return new Label
            {
                Text      = text,
                Font      = new Font("Segoe UI", 8.5f, FontStyle.Bold),
                ForeColor = TextDim,
                AutoSize  = true
            };
        }

        private void AddLabel(Panel p, string text, int x, int y, Color? color = null)
        {
            p.Controls.Add(new Label
            {
                Text      = text,
                Location  = new Point(x, y),
                AutoSize  = true,
                Font      = new Font("Segoe UI", 8.5f),
                ForeColor = color ?? TextDim
            });
        }

        private Button MakeSideButton(string text, int x, int y, Color bg)
        {
            var b = new Button
            {
                Text      = text,
                Location  = new Point(x, y),
                Size      = new Size(220, 28),
                FlatStyle = FlatStyle.Flat,
                BackColor = bg,
                ForeColor = TextDim,
                Font      = new Font("Segoe UI", 8.5f),
                Cursor    = Cursors.Hand
            };
            b.FlatAppearance.BorderColor = Color.FromArgb(55, 65, 80);
            b.FlatAppearance.BorderSize  = 1;
            return b;
        }

        private Button MakeIconButton(string text, Color bg, Color fg)
        {
            var b = new Button
            {
                Text      = text,
                Size      = new Size(110, 28),
                FlatStyle = FlatStyle.Flat,
                BackColor = bg,
                ForeColor = fg,
                Font      = new Font("Segoe UI", 8.5f),
                Cursor    = Cursors.Hand,
                Margin    = new Padding(4, 0, 0, 0)
            };
            b.FlatAppearance.BorderColor = Color.FromArgb(200, 210, 230);
            b.FlatAppearance.BorderSize  = 1;
            return b;
        }

        private void PanelRoundedBorder(object s, PaintEventArgs e)
        {
            var p = (Panel)s;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var pen = new Pen(Color.FromArgb(220, 226, 238), 1))
            {
                var r = new Rectangle(0, 0, p.Width - 1, p.Height - 1);
                e.Graphics.DrawRectangle(pen, r);
            }
        }

        private void PaintFan(object s, PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var p = (Panel)s;
            int cx = p.Width / 2, cy = p.Height / 2, r = 32;

            g.FillEllipse(Brushes.LightGray, cx - r, cy - r, r * 2, r * 2);

            using (var b = new SolidBrush(fanState ? AccentGreen : Color.Gray))
            {
                for (int i = 0; i < 3; i++)
                {
                    double a = (fanAngle + i * 120) * Math.PI / 180.0;
                    var pts = new PointF[]
                    {
                        new PointF(cx, cy),
                        new PointF(cx + (float)(r * Math.Cos(a - 0.4)),
                                   cy + (float)(r * Math.Sin(a - 0.4))),
                        new PointF(cx + (float)(r * 1.1 * Math.Cos(a)),
                                   cy + (float)(r * 1.1 * Math.Sin(a))),
                        new PointF(cx + (float)(r * Math.Cos(a + 0.4)),
                                   cy + (float)(r * Math.Sin(a + 0.4)))
                    };
                    g.FillPolygon(b, pts);
                }
            }

            using (var b = new SolidBrush(Color.FromArgb(235, 240, 248)))
                g.FillEllipse(b, cx - 8, cy - 8, 16, 16);
        }

        // =====================================================================
        //  SERIAL PORT LOGIC
        // =====================================================================
        private void PopulateComPorts()
        {
            cboCom.Items.Clear();
            foreach (var port in SerialPort.GetPortNames())
                cboCom.Items.Add(port);
            if (cboCom.Items.Count > 0)
                cboCom.SelectedIndex = 0;
        }

        private void BtnConnect_Click(object s, EventArgs e)
        {
            if (serialPort != null && serialPort.IsOpen)
            {
                DisconnectPort();
            }
            else
            {
                ConnectPort();
            }
        }

        private void ConnectPort()
        {
            if (cboCom.SelectedItem == null)
            {
                MessageBox.Show("Vui lòng chọn cổng COM.", "Lỗi", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            try
            {
                serialPort = new SerialPort
                {
                    PortName = cboCom.SelectedItem.ToString(),
                    BaudRate = int.Parse(cboBaud.SelectedItem.ToString()),
                    DataBits = 8,
                    Parity   = Parity.None,
                    StopBits = StopBits.One,
                    Encoding = Encoding.ASCII
                };
                serialPort.DataReceived += SerialPort_DataReceived;
                serialPort.Open();

                btnConnect.Text      = "Ngắt kết nối";
                btnConnect.BackColor = AccentRed;
                lblConnStatus.Text   = $"● Đã kết nối: {cboCom.SelectedItem} @ {cboBaud.SelectedItem}";
                lblConnStatus.ForeColor = AccentGreen;

                // Show chart
                ShowChart(true);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Không thể mở cổng COM:\n" + ex.Message, "Lỗi kết nối",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void DisconnectPort()
        {
            if (serialPort != null)
            {
                serialPort.DataReceived -= SerialPort_DataReceived;
                if (serialPort.IsOpen) serialPort.Close();
                serialPort.Dispose();
                serialPort = null;
            }
            btnConnect.Text      = "Kết nối";
            btnConnect.BackColor = AccentGreen;
            lblConnStatus.Text   = "● Chưa kết nối";
            lblConnStatus.ForeColor = AccentRed;
        }

        // ── Data Received ────────────────────────────────────────────────────
        // Expected UART frame from ATMega16:
        //   PM:35.2,CO:520,T:28.5,H:65.3\r\n
        //   or:  $PM:xx.x;CO:xxx;T:xx.x;H:xx.x#
        //   or:  JSON {"pm":35.2,"co2":520,"t":28.5,"h":65.3}
        private void SerialPort_DataReceived(object s, SerialDataReceivedEventArgs e)
        {
            try
            {
                receiveBuffer += serialPort.ReadExisting();
                int idx;
                while ((idx = receiveBuffer.IndexOf('\n')) >= 0)
                {
                    string line = receiveBuffer.Substring(0, idx).Trim();
                    receiveBuffer = receiveBuffer.Substring(idx + 1);
                    if (line.Length > 0)
                        this.BeginInvoke(new Action(() => ParseFrame(line)));
                }
            }
            catch { /* port closed mid-read */ }
        }

        private void ParseFrame(string raw)
        {
            // Try multiple formats
            // New Format: #data:temp;humi;co2;dust;mode;percent\r\n
            // Old formats still supported:
            //   PM:35.2,CO:520,T:28.5,H:65.3
            //   $PM:xx.x;CO:xxx;T:xx.x;H:xx.x#
            //   JSON {"pm":35.2,"co2":520,"t":28.5,"h":65.3}
            raw = raw.Trim('$', '#', '\r', '\n', ' ');

            double pm = -1, co = -1, t = -1, h = -1;

            // New concise "#data:..." frame
            if (raw.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var payload = raw.Substring(raw.IndexOf(':') + 1);
                    var parts = payload.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);

                    if (parts.Length >= 6)
                    {
                        double tempV = -1, humV = -1, co2V = -1, dustV = -1;
                        int modeV = -1, pct = -1;

                        double.TryParse(parts[0].Trim(), out tempV);   // temp
                        double.TryParse(parts[1].Trim(), out humV);    // hum
                        double.TryParse(parts[2].Trim(), out co2V);    // co2
                        double.TryParse(parts[3].Trim(), out dustV);   // dust -> map to PM2.5
                        int.TryParse(parts[4].Trim(), out modeV);      // mode
                        int.TryParse(parts[5].Trim(), out pct);        // percent

                        t = tempV;
                        h = humV;
                        co = co2V;
                        pm = dustV;

                        // Reflect mode in UI (assumes 1 = auto, 0 = manual)
                        if (modeV >= 0)
                        {
                            if (modeV == 1) rdoAuto.Checked = true;
                            else rdoManual.Checked = true;
                        }

                        // Update fan speed display (clamp to control range)
                        if (pct >= trkFanSpeed.Minimum && pct <= trkFanSpeed.Maximum)
                        {
                            trkFanSpeed.Value = pct;
                            lblFanSpeed.Text = pct + "%";
                        }
                        else if (pct >= 0)
                        {
                            int val = Math.Min(Math.Max(pct, trkFanSpeed.Minimum), trkFanSpeed.Maximum);
                            trkFanSpeed.Value = val;
                            lblFanSpeed.Text = val + "%";
                        }
                    }
                }
                catch
                {
                    // ignore malformed data
                }
            }
            else if (raw.StartsWith("{"))
            {
                // Minimal JSON parse
                pm = ExtractJson(raw, "pm");
                co = ExtractJson(raw, "co2");
                t = ExtractJson(raw, "t");
                h = ExtractJson(raw, "h");
            }
            else
            {
                // Key:Value comma or semicolon separated
                char sep = raw.Contains(",") ? ',' : ';';
                foreach (var tok in raw.Split(new char[] { sep }))
                {
                    var kv = tok.Split(':');
                    if (kv.Length != 2) continue;
                    string k = kv[0].Trim().ToUpperInvariant();
                    if (!double.TryParse(kv[1].Trim(), out double v)) continue;
                    switch (k)
                    {
                        case "PM": case "PM2.5": pm = v; break;
                        case "CO": case "CO2": co = v; break;
                        case "T": case "TEMP": t = v; break;
                        case "H": case "HUM": h = v; break;
                    }
                }
            }

            if (pm < 0 && co < 0 && t < 0 && h < 0) return; // garbage frame

            UpdateSensorUI(pm, co, t, h);
        }

        private double ExtractJson(string json, string key)
        {
            string search = $"\"{key}\"";
            int i = json.IndexOf(search, StringComparison.OrdinalIgnoreCase);
            if (i < 0) return -1;
            i += search.Length;
            while (i < json.Length && (json[i] == ':' || json[i] == ' ')) i++;
            int j = i;
            while (j < json.Length && (char.IsDigit(json[j]) || json[j] == '.' || json[j] == '-')) j++;
            if (j == i) return -1;
            double.TryParse(json.Substring(i, j - i), out double v);
            return v;
        }

        // =====================================================================
        //  SENSOR UI UPDATE
        // =====================================================================
        private void UpdateSensorUI(double pm, double co, double t, double h)
        {
            if (pm >= 0)
            {
                pm25Value = pm;
                lblPm25Value.Text   = pm.ToString("F1").Replace('.', ',');
                lblPm25Status.Text  = GetPm25Status(pm);
                lblPm25Status.ForeColor = GetPm25Color(pm);
            }
            if (co >= 0)
            {
                co2Value = co;
                lblCo2Value.Text    = co.ToString("F0");
                lblCo2Status.Text   = GetCo2Status(co);
                lblCo2Status.ForeColor = GetCo2Color(co);
            }
            if (t >= 0)
            {
                tempValue = t;
                lblTempValue.Text   = t.ToString("F1").Replace('.', ',');
                lblTempStatus.Text  = "OK";
                lblTempStatus.ForeColor = AccentGreen;
            }
            if (h >= 0)
            {
                humValue = h;
                lblHumValue.Text    = h.ToString("F1").Replace('.', ',');
                lblHumStatus.Text   = "OK";
                lblHumStatus.ForeColor = AccentGreen;
            }

            // Add chart data
            AddChartPoint(pm >= 0 ? pm : pm25Value,
                          co >= 0 ? co : co2Value,
                          t  >= 0 ? t  : tempValue);

            // Add log row
            if (pm >= 0 || co >= 0 || t >= 0 || h >= 0)
            {
                AddLogRow(pm >= 0 ? pm : pm25Value,
                          co >= 0 ? co : co2Value,
                          t  >= 0 ? t  : tempValue,
                          h  >= 0 ? h  : humValue);
            }

            // Auto fan
            if (rdoAuto.Checked && pm >= 0)
            {
                bool shouldFan = pm > (double)nudThreshold.Value;
                if (shouldFan != fanState) SetFanState(shouldFan);
            }

            UpdateAirQualityStatus();
        }

        private string GetPm25Status(double v)
        {
            if (v <= 12)  return "Tốt";
            if (v <= 35)  return "Trung bình";
            if (v <= 55)  return "Kém";
            if (v <= 150) return "Xấu";
            return "Nguy hiểm";
        }

        private Color GetPm25Color(double v)
        {
            if (v <= 12)  return AccentGreen;
            if (v <= 35)  return AccentOrange;
            if (v <= 55)  return Color.DarkOrange;
            return AccentRed;
        }

        private string GetCo2Status(double v)
        {
            if (v <= 400)  return "Bình thường";
            if (v <= 1000) return "Trung bình";
            if (v <= 2000) return "Kém";
            return "Nguy hiểm";
        }

        private Color GetCo2Color(double v)
        {
            if (v <= 400)  return AccentGreen;
            if (v <= 1000) return AccentOrange;
            return AccentRed;
        }

        private void UpdateAirQualityStatus()
        {
            bool danger = pm25Value > 55 || co2Value > 2000;
            bool warn   = pm25Value > 35 || co2Value > 1000;

            if (danger)
            {
                lblAirQuality.Text      = "✕  Chất lượng không khí nguy hiểm!";
                lblAirQuality.ForeColor = AccentRed;
                pnlSafetyIndicator.BackColor = AccentRed;
                lblSafetyLabel.Text     = "Trạng thái: NGUY HIỂM";
                lblSafetyLabel.ForeColor = AccentRed;
            }
            else if (warn)
            {
                lblAirQuality.Text      = "⚠  Chất lượng không khí trung bình";
                lblAirQuality.ForeColor = AccentOrange;
                pnlSafetyIndicator.BackColor = AccentOrange;
                lblSafetyLabel.Text     = "Trạng thái: CẢNH BÁO";
                lblSafetyLabel.ForeColor = AccentOrange;
            }
            else
            {
                lblAirQuality.Text      = "✓  Chất lượng không khí ổn định";
                lblAirQuality.ForeColor = AccentGreen;
                pnlSafetyIndicator.BackColor = AccentGreen;
                lblSafetyLabel.Text     = "Trạng thái: AN TOÀN";
                lblSafetyLabel.ForeColor = Color.FromArgb(40, 60, 40);
            }
            pnlSafetyIndicator.Invalidate();
        }

        // =====================================================================
        //  CHART
        // =====================================================================
        private void ShowChart(bool show)
        {
            chartMain.Visible = show;
            var placeholder = chartMain.Parent?.Controls["chartPlaceholder"];
            if (placeholder != null) placeholder.Visible = !show;
        }

        private void AddChartPoint(double pm, double co, double temp)
        {
            pm25History.Add(pm);
            co2History.Add(co);
            tempHistory.Add(temp);

            if (pm25History.Count > MAX_CHART_POINTS)  pm25History.RemoveAt(0);
            if (co2History.Count  > MAX_CHART_POINTS)  co2History.RemoveAt(0);
            if (tempHistory.Count > MAX_CHART_POINTS)  tempHistory.RemoveAt(0);

            var sPm   = chartMain.Series["PM2.5 (μg/m³)"];
            var sCo   = chartMain.Series["CO₂ (ppm)"];
            var sTemp = chartMain.Series["Nhiệt độ (°C)"];

            sPm.Points.Clear();
            sCo.Points.Clear();
            sTemp.Points.Clear();

            for (int i = 0; i < pm25History.Count; i++)
            {
                sPm.Points.AddXY(i, pm25History[i]);
                sCo.Points.AddXY(i, co2History[i]);
                sTemp.Points.AddXY(i, tempHistory[i]);
            }

            // Auto-scale Y
            double max = 100;
            foreach (var v in pm25History) if (v > max) max = v;
            foreach (var v in co2History)  if (v > max) max = v;
            foreach (var v in tempHistory) if (v > max) max = v;
            chartMain.ChartAreas["Main"].AxisY.Maximum = Math.Ceiling(max / 10) * 10 + 10;
        }

        // =====================================================================
        //  LOG
        // =====================================================================
        private void AddLogRow(double pm, double co, double temp, double hum)
        {
            dgvLog.Rows.Insert(0,
                DateTime.Now.ToString("HH:mm:ss"),
                pm.ToString("F1"),
                co.ToString("F0"),
                temp.ToString("F1"),
                hum.ToString("F1"));

            if (dgvLog.Rows.Count > 500)
                dgvLog.Rows.RemoveAt(dgvLog.Rows.Count - 1);
        }

        // =====================================================================
        //  FAN CONTROLS
        // =====================================================================
        private void BtnFanToggle_Click(object s, EventArgs e)
        {
            SetFanState(!fanState);
        }

        private void SetFanState(bool on)
        {
            fanState = on;
            if (on)
            {
                btnFanToggle.Text = "⏸  TẮT QUẠT";
                btnFanToggle.BackColor = AccentRed;
                lblFanIndicator.Text = "● BẬT";
                lblFanIndicator.ForeColor = AccentGreen;
                fanAnimating = true;
            }
            else
            {
                btnFanToggle.Text = "▶  BẬT QUẠT";
                btnFanToggle.BackColor = AccentGreen;
                lblFanIndicator.Text = "● TẮT";
                lblFanIndicator.ForeColor = Color.Gray;
                fanAnimating = false;
            }

            // MCU expects "#fan:yy" where yy is 0..100
            if (on)
                SendCommand($"#fan:{trkFanSpeed.Value}");
            else
                SendCommand("#fan:0");
        }

        private void TrkFanSpeed_ValueChanged(object s, EventArgs e)
        {
            lblFanSpeed.Text = trkFanSpeed.Value + "%";

            // Send new speed to MCU in manual mode (MCU will ignore if it's in auto).
            // Only send when serial is open (SendCommand already checks).
            SendCommand($"#fan:{trkFanSpeed.Value}");
        }

        private void UpdateFanControls()
        {
            bool manual = rdoManual.Checked;
            btnFanToggle.Enabled = manual;
            trkFanSpeed.Enabled  = manual;
            nudThreshold.Enabled = !manual;
        }

        // =====================================================================
        //  SEND COMMAND TO MCU
        // =====================================================================
        private void SendCommand(string cmd)
        {
            if (serialPort != null && serialPort.IsOpen)
            {
                try { serialPort.WriteLine(cmd); }
                catch { /* swallow */ }
            }
        }

        // =====================================================================
        //  DEMO MODE
        // =====================================================================
        private void BtnDemo_Click(object s, EventArgs e)
        {
            isDemoMode = !isDemoMode;
            if (isDemoMode)
            {
                btnDemo.Text      = "⏸  Dừng Demo";
                btnDemo.BackColor = AccentOrange;
                btnDemo.ForeColor = Color.White;
                demoTimer.Start();
                ShowChart(true);
            }
            else
            {
                btnDemo.Text      = "▶  Chế độ Demo";
                btnDemo.BackColor = SidebarCard;
                btnDemo.ForeColor = TextDim;
                demoTimer.Stop();
            }
        }

        private void DemoTimer_Tick(object s, EventArgs e)
        {
            // Simulate realistic sensor values
            pm25Value  = Math.Max(0, pm25Value  + (rng.NextDouble() - 0.45) * 3.0);
            co2Value   = Math.Max(300, co2Value + (rng.NextDouble() - 0.45) * 20.0);
            tempValue  = Math.Max(15,  tempValue + (rng.NextDouble() - 0.48) * 0.5);
            humValue   = Math.Max(20, Math.Min(95, humValue + (rng.NextDouble() - 0.48) * 1.0));

            if (pm25Value  > 80)  pm25Value  = 80;
            if (co2Value   > 2000) co2Value  = 2000;
            if (tempValue  > 45)   tempValue = 45;

            UpdateSensorUI(pm25Value, co2Value, tempValue, humValue);
        }

        // =====================================================================
        //  TIMERS
        // =====================================================================
        private void SetupTimers()
        {
            demoTimer = new Timer { Interval = 1200 };
            demoTimer.Tick += DemoTimer_Tick;

            fanAnimTimer = new Timer { Interval = 40 };
            fanAnimTimer.Tick += (s, e) =>
            {
                if (fanAnimating)
                {
                    int step = (int)(trkFanSpeed.Value * 0.12) + 4;
                    fanAngle = (fanAngle + step) % 360;
                }
                pnlFanIcon.Invalidate();
            };
            fanAnimTimer.Start();
        }

        // =====================================================================
        //  EXPORT
        // =====================================================================
        private class SensorStat
        {
            public string Name;
            public string Unit;
            public string ColumnName;
            public int Decimals;
            public double Min = double.MaxValue;
            public double Max = double.MinValue;
            public int Count = 0;

            public bool HasData
            {
                get { return Count > 0; }
            }

            public void Add(double value)
            {
                if (Count == 0)
                {
                    Min = value;
                    Max = value;
                }
                else
                {
                    if (value < Min) Min = value;
                    if (value > Max) Max = value;
                }
                Count++;
            }
        }

        private bool TryParseCellDouble(DataGridViewRow row, string columnName, out double value)
        {
            value = 0;

            if (row == null || row.Cells[columnName].Value == null)
                return false;

            string raw = row.Cells[columnName].Value.ToString();
            if (string.IsNullOrWhiteSpace(raw))
                return false;

            // Hỗ trợ cả định dạng 12.3 và 12,3 để tránh lỗi khi máy dùng culture khác nhau.
            string normalized = raw.Trim().Replace(',', '.');
            return double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
                || double.TryParse(raw, NumberStyles.Float, CultureInfo.CurrentCulture, out value);
        }

        private string FormatStatValue(SensorStat stat, double value)
        {
            string format = "F" + stat.Decimals;
            return value.ToString(format, CultureInfo.CurrentCulture);
        }

        private string EscapeHtml(object value)
        {
            return System.Net.WebUtility.HtmlEncode(value == null ? "" : value.ToString());
        }

        private string GetReportComment(SensorStat stat)
        {
            if (!stat.HasData)
                return "Chưa có dữ liệu.";

            if (stat.ColumnName == "colPm")
                return $"Dao động từ {FormatStatValue(stat, stat.Min)} đến {FormatStatValue(stat, stat.Max)} {stat.Unit}. Đánh giá: {GetPm25Status(stat.Max)}";

            if (stat.ColumnName == "colCo")
                return $"Dao động từ {FormatStatValue(stat, stat.Min)} đến {FormatStatValue(stat, stat.Max)} {stat.Unit}.Đánh giá: {GetCo2Status(stat.Max)}";

            return $"Dao động từ {FormatStatValue(stat, stat.Min)} đến {FormatStatValue(stat, stat.Max)} {stat.Unit}.";
        }

        private string GetOverallAirQualityComment(SensorStat pmStat, SensorStat co2Stat)
        {
            if (!pmStat.HasData && !co2Stat.HasData)
                return "Chưa đủ dữ liệu để nhận xét chất lượng không khí.";

            double maxPm = pmStat.HasData ? pmStat.Max : 0;
            double maxCo2 = co2Stat.HasData ? co2Stat.Max : 0;

            if (maxPm > 55 || maxCo2 > 2000)
                return "Nhận xét tổng quan: chất lượng không khí có thời điểm NGUY HIỂM (PM2.5 > 55 μg/m³ hoặc CO₂ > 2000 ppm).";

            if (maxPm > 35 || maxCo2 > 1000)
                return "Nhận xét tổng quan: chất lượng không khí có thời điểm ở mức CẢNH BÁO / trung bình (PM2.5 > 35 μg/m³ hoặc CO₂ > 1000 ppm).";

            return "Nhận xét tổng quan: chất lượng không khí ổn định.";
        }

        private void AppendSummaryRow(StringBuilder sb, SensorStat stat)
        {
            string minText = stat.HasData ? FormatStatValue(stat, stat.Min) : "-";
            string maxText = stat.HasData ? FormatStatValue(stat, stat.Max) : "-";
            string comment = GetReportComment(stat);

            sb.AppendLine("<tr>" +
                $"<td>{EscapeHtml(stat.Name)}</td>" +
                $"<td>{EscapeHtml(minText)}</td>" +
                $"<td>{EscapeHtml(maxText)}</td>" +
                $"<td>{EscapeHtml(stat.Unit)}</td>" +
                $"<td>{EscapeHtml(comment)}</td>" +
                "</tr>");
        }

        private void BtnExportHtml_Click(object s, EventArgs e)
        {
            using (var dlg = new SaveFileDialog
            {
                Filter   = "HTML Files|*.html",
                FileName = $"air_quality_{DateTime.Now:yyyyMMdd_HHmmss}.html"
            })
            {
                if (dlg.ShowDialog() != DialogResult.OK) return;

                var stats = new[]
                {
                    new SensorStat { Name = "Nồng độ bụi PM2.5", Unit = "μg/m³", ColumnName = "colPm",   Decimals = 1 },
                    new SensorStat { Name = "CO₂",               Unit = "ppm",   ColumnName = "colCo",   Decimals = 0 },
                    new SensorStat { Name = "Nhiệt độ",          Unit = "°C",    ColumnName = "colTemp", Decimals = 1 },
                    new SensorStat { Name = "Độ ẩm",             Unit = "%",     ColumnName = "colHum",  Decimals = 1 }
                };

                foreach (DataGridViewRow row in dgvLog.Rows)
                {
                    foreach (var stat in stats)
                    {
                        double value;
                        if (TryParseCellDouble(row, stat.ColumnName, out value))
                            stat.Add(value);
                    }
                }

                var sb = new StringBuilder();
                sb.AppendLine("<!DOCTYPE html><html lang='vi'><head><meta charset='UTF-8'>");
                sb.AppendLine("<title>Nhật ký chất lượng không khí</title>");
                sb.AppendLine("<style>");
                sb.AppendLine("body{font-family:Segoe UI,Arial,sans-serif;padding:24px;background:#f5f7fa;color:#1e2329}");
                sb.AppendLine("h1{margin-bottom:4px}h2{margin-top:24px;color:#2c3e50}");
                sb.AppendLine(".meta{color:#607086;margin-top:0}.notice{padding:12px 14px;border-left:4px solid #e6a23c;background:#fff7e6;border-radius:6px;margin:14px 0}");
                sb.AppendLine("table{width:100%;border-collapse:collapse;background:#fff;border-radius:8px;overflow:hidden;box-shadow:0 2px 8px #0001;margin-top:10px}");
                sb.AppendLine("th{background:#2c3e50;color:#fff;padding:10px 14px;text-align:left}");
                sb.AppendLine("td{padding:8px 14px;border-bottom:1px solid #eee}tr:nth-child(even)td{background:#f8faff}");
                sb.AppendLine(".small{font-size:13px;color:#607086}.empty{color:#999;font-style:italic}");
                sb.AppendLine("</style></head><body>");

                sb.AppendLine("<h1>Nhật ký chất lượng không khí</h1>");
                sb.AppendLine($"<p class='meta'>Xuất lúc: {DateTime.Now:dd/MM/yyyy HH:mm:ss}</p>");

                sb.AppendLine($"<div class='notice'>{EscapeHtml(GetOverallAirQualityComment(stats[0], stats[1]))}</div>");

               // sb.AppendLine("<h2>Thống kê min/max và nhận xét</h2>");
                sb.AppendLine("<table><thead><tr><th>Thông số</th><th>Min</th><th>Max</th><th>Đơn vị</th><th>Nhận xét</th></tr></thead><tbody>");
                foreach (var stat in stats)
                    AppendSummaryRow(sb, stat);
                sb.AppendLine("</tbody></table>");

                sb.AppendLine("<p class='small'>Dải đo: PM2.5 ≤ 12: Tốt; ≤ 35: Trung bình; ≤ 55: Kém; ≤ 150: Xấu; > 150: Nguy hiểm. CO₂ ≤ 400: Bình thường; ≤ 1000: Trung bình; ≤ 2000: Kém; > 2000: Nguy hiểm.</p>");

                sb.AppendLine("<h2>Dữ liệu chi tiết</h2>");
                if (dgvLog.Rows.Count == 0)
                {
                    sb.AppendLine("<p class='empty'>Chưa có dữ liệu log.</p>");
                }
                else
                {
                    sb.AppendLine("<table><thead><tr><th>Thời gian</th><th>PM2.5 (μg/m³)</th><th>CO₂ (ppm)</th><th>Nhiệt độ (°C)</th><th>Độ ẩm (%)</th></tr></thead><tbody>");
                    foreach (DataGridViewRow row in dgvLog.Rows)
                    {
                        sb.AppendLine("<tr>" +
                            $"<td>{EscapeHtml(row.Cells["colTime"].Value)}</td>" +
                            $"<td>{EscapeHtml(row.Cells["colPm"].Value)}</td>" +
                            $"<td>{EscapeHtml(row.Cells["colCo"].Value)}</td>" +
                            $"<td>{EscapeHtml(row.Cells["colTemp"].Value)}</td>" +
                            $"<td>{EscapeHtml(row.Cells["colHum"].Value)}</td>" +
                            "</tr>");
                    }
                    sb.AppendLine("</tbody></table>");
                }

                sb.AppendLine("</body></html>");
                File.WriteAllText(dlg.FileName, sb.ToString(), Encoding.UTF8);
                MessageBox.Show("Đã xuất HTML thành công!", "Hoàn thành", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        // =====================================================================
        //  CLEANUP
        // =====================================================================
        private void CleanUp()
        {
            demoTimer?.Stop();
            fanAnimTimer?.Stop();
            DisconnectPort();
        }
    }
}
