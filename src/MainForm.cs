using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace AccessFix
{
    public class BorderPanel : Panel
    {
        public Color BorderColor { get; set; }

        public BorderPanel()
        {
            BorderColor = Color.FromArgb(38, 49, 71);
            this.DoubleBuffered = true;
            this.ResizeRedraw = true;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (Pen p = new Pen(BorderColor, 1f))
            {
                Rectangle r = new Rectangle(0, 0, this.ClientSize.Width - 1, this.ClientSize.Height - 1);
                e.Graphics.DrawRectangle(p, r);
            }
        }
    }

    public class MainForm : Form
    {
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        // Bảng màu Dark Mode sạch sẽ, dễ đọc
        private static readonly Color BgWindow = Color.FromArgb(15, 20, 29);
        private static readonly Color BgCard = Color.FromArgb(23, 30, 44);
        private static readonly Color BgInput = Color.FromArgb(15, 21, 32);
        private static readonly Color BorderSubtle = Color.FromArgb(38, 49, 71);
        private static readonly Color TextPrimary = Color.FromArgb(241, 245, 249);
        private static readonly Color TextSecondary = Color.FromArgb(148, 163, 184);
        private static readonly Color TextMuted = Color.FromArgb(100, 116, 139);
        private static readonly Color AccentGreen = Color.FromArgb(16, 185, 129);
        private static readonly Color AccentGreenHover = Color.FromArgb(5, 150, 105);
        private static readonly Color AccentRed = Color.FromArgb(220, 62, 62);
        private static readonly Color AccentRedHover = Color.FromArgb(185, 45, 45);
        private static readonly Color AccentBlue = Color.FromArgb(59, 130, 246);
        private static readonly Color BtnNeutral = Color.FromArgb(34, 44, 64);
        private static readonly Color BtnNeutralHover = Color.FromArgb(47, 60, 86);

        private const string InputPlaceholder = "Nhập tên miền hoặc dán link web bị chặn (vd: bbc.com, https://medium.com/...)";

        private float dpiScale = 1f;
        private AppConfig config;
        private readonly SplitProxyEngine engine;
        private readonly bool startMinimized;
        private bool allowExit = false;
        private bool suppressUiEvents = false;
        private volatile bool isTestingBatch = false;

        private Icon iconActiveSmall;
        private Icon iconInactiveSmall;
        private Icon iconActiveWindow;
        private Icon iconInactiveWindow;

        // Controls
        private Panel statusDot;
        private Label lblStatusTitle;
        private Label lblStatusMetrics;
        private Label lblLastActivity;
        private Button btnToggleMain;

        private TextBox txtDomainInput;
        private bool isPlaceholderActive = true;
        private Button btnAddDomain;
        private FlowLayoutPanel flowPresets;
        private DataGridView gridDomains;
        private Button btnTestSelected;
        private Button btnTestAll;
        private Button btnRemoveSelected;
        private CheckBox chkSteamGames;

        private CheckBox chkAutoEnableOnLaunch;
        private CheckBox chkMinimizeToTray;
        private CheckBox chkAutoStartWin;
        private CheckBox chkPubgDirect;
        private Label lblFooterInfo;
        private Button btnHideTray;
        private Button btnEmergencyReset;

        private NotifyIcon trayIcon;
        private ContextMenuStrip trayMenu;
        private ToolStripMenuItem trayItemToggle;
        private System.Windows.Forms.Timer statsTimer;

        private readonly Dictionary<string, string> testStatusByDomain =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private int S(int px)
        {
            return (int)Math.Round(px * dpiScale);
        }

        private Padding SP(int l, int t, int r, int b)
        {
            return new Padding(S(l), S(t), S(r), S(b));
        }

        public MainForm(bool minimized)
        {
            startMinimized = minimized;
            config = AppConfig.Load();
            engine = new SplitProxyEngine();

            using (Graphics g = Graphics.FromHwnd(IntPtr.Zero))
            {
                dpiScale = g.DpiX / 96f;
                if (dpiScale < 1f) dpiScale = 1f;
            }

            iconActiveSmall = TrayIconHelper.CreateStatusIcon(true, S(32));
            iconInactiveSmall = TrayIconHelper.CreateStatusIcon(false, S(32));
            iconActiveWindow = TrayIconHelper.CreateStatusIcon(true, S(48));
            iconInactiveWindow = TrayIconHelper.CreateStatusIcon(false, S(48));

            InitializeLayout();
            SetupTrayIcon();
            BindEvents();

            PopulateDomainGrid();
            SyncCheckboxesFromConfig();
            RefreshUiState();

            statsTimer = new System.Windows.Forms.Timer();
            statsTimer.Interval = 1000;
            statsTimer.Tick += delegate { UpdateLiveMetricsLabel(); };
            statsTimer.Start();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            TryEnableDarkTitleBar(this.Handle);
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);

            SystemProxyManager.SanitizeResidualOnStartup();

            if (config.AutoEnableOnLaunch || startMinimized)
            {
                StartBypass();
            }

            if (startMinimized)
            {
                BeginInvoke(new MethodInvoker(delegate
                {
                    HideToTray(false);
                }));
            }
        }

        private static void TryEnableDarkTitleBar(IntPtr handle)
        {
            try
            {
                int useDark = 1;
                if (DwmSetWindowAttribute(handle, 20, ref useDark, sizeof(int)) != 0)
                {
                    DwmSetWindowAttribute(handle, 19, ref useDark, sizeof(int));
                }
            }
            catch { }
        }

        private void InitializeLayout()
        {
            // Tự quản lý tỷ lệ DPI đồng nhất cho cả TableLayoutPanel, DataGridView và Padding
            this.AutoScaleMode = AutoScaleMode.None;
            this.Text = "AccessFix v2.0 — Vượt chặn Web & Steam (SNI Split + DoH)";
            this.ClientSize = new Size(S(860), S(650));
            this.MinimumSize = new Size(S(800), S(590));
            this.StartPosition = FormStartPosition.CenterScreen;
            this.BackColor = BgWindow;
            this.ForeColor = TextPrimary;
            this.Font = new Font("Segoe UI", 9.25F, FontStyle.Regular, GraphicsUnit.Point);
            this.Icon = iconInactiveWindow;
            this.DoubleBuffered = true;

            TableLayoutPanel root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.Padding = SP(16, 16, 16, 16);
            root.ColumnCount = 1;
            root.RowCount = 3;
            root.BackColor = BgWindow;
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, S(122)));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, S(136)));
            this.Controls.Add(root);

            // =========================================================
            // CARD 1: TRẠNG THÁI & NÚT BẬT / TẮT CHÍNH
            // =========================================================
            BorderPanel cardTop = new BorderPanel();
            cardTop.Dock = DockStyle.Fill;
            cardTop.Margin = SP(0, 0, 0, 12);
            cardTop.Padding = SP(18, 12, 18, 12);
            cardTop.BackColor = BgCard;
            root.Controls.Add(cardTop, 0, 0);

            TableLayoutPanel topGrid = new TableLayoutPanel();
            topGrid.Dock = DockStyle.Fill;
            topGrid.ColumnCount = 2;
            topGrid.RowCount = 1;
            topGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            topGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, S(204)));
            topGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            cardTop.Controls.Add(topGrid);

            Panel statusLeft = new Panel();
            statusLeft.Dock = DockStyle.Fill;
            topGrid.Controls.Add(statusLeft, 0, 0);

            statusDot = new Panel();
            statusDot.Size = new Size(S(12), S(12));
            statusDot.Location = new Point(S(2), S(8));
            statusDot.BackColor = Color.Transparent;
            statusDot.Paint += StatusDot_Paint;
            statusLeft.Controls.Add(statusDot);

            lblStatusTitle = new Label();
            lblStatusTitle.AutoSize = true;
            lblStatusTitle.Location = new Point(S(22), S(1));
            lblStatusTitle.Font = new Font("Segoe UI Semibold", 12.5F, FontStyle.Bold, GraphicsUnit.Point);
            lblStatusTitle.ForeColor = TextPrimary;
            lblStatusTitle.Text = "ĐANG TẮT VƯỢT CHẶN";
            statusLeft.Controls.Add(lblStatusTitle);

            lblStatusMetrics = new Label();
            lblStatusMetrics.AutoSize = true;
            lblStatusMetrics.Location = new Point(S(22), S(31));
            lblStatusMetrics.Font = new Font("Segoe UI", 9.25F, FontStyle.Regular, GraphicsUnit.Point);
            lblStatusMetrics.ForeColor = TextSecondary;
            lblStatusMetrics.Text = "Trình duyệt đang kết nối trực tiếp mặc định.";
            statusLeft.Controls.Add(lblStatusMetrics);

            lblLastActivity = new Label();
            lblLastActivity.AutoSize = true;
            lblLastActivity.Location = new Point(S(22), S(54));
            lblLastActivity.Font = new Font("Segoe UI", 8.75F, FontStyle.Regular, GraphicsUnit.Point);
            lblLastActivity.ForeColor = TextMuted;
            lblLastActivity.Text = "Không đổi DNS card mạng • Chỉ định tuyến riêng các tên miền được bật.";
            statusLeft.Controls.Add(lblLastActivity);

            btnToggleMain = CreateStyledButton("BẬT VƯỢT CHẶN", AccentGreen, AccentGreenHover, Color.White);
            btnToggleMain.Dock = DockStyle.Fill;
            btnToggleMain.Margin = SP(12, 8, 0, 8);
            btnToggleMain.Font = new Font("Segoe UI Semibold", 10.5F, FontStyle.Bold, GraphicsUnit.Point);
            topGrid.Controls.Add(btnToggleMain, 1, 0);

            // =========================================================
            // CARD 2: QUẢN LÝ TÊN MIỀN CẦN VƯỢT
            // =========================================================
            BorderPanel cardDomains = new BorderPanel();
            cardDomains.Dock = DockStyle.Fill;
            cardDomains.Margin = SP(0, 0, 0, 12);
            cardDomains.Padding = SP(16, 12, 16, 12);
            cardDomains.BackColor = BgCard;
            root.Controls.Add(cardDomains, 0, 1);

            TableLayoutPanel domLayout = new TableLayoutPanel();
            domLayout.Dock = DockStyle.Fill;
            domLayout.ColumnCount = 1;
            domLayout.RowCount = 5;
            domLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, S(26)));
            domLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, S(42)));
            domLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, S(36)));
            domLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            domLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, S(40)));
            cardDomains.Controls.Add(domLayout);

            Label lblDomHeader = new Label();
            lblDomHeader.Dock = DockStyle.Fill;
            lblDomHeader.TextAlign = ContentAlignment.MiddleLeft;
            lblDomHeader.Font = new Font("Segoe UI Semibold", 9.75F, FontStyle.Bold, GraphicsUnit.Point);
            lblDomHeader.ForeColor = TextPrimary;
            lblDomHeader.Text = "Danh sách tên miền vượt chặn (áp dụng cho cả tên miền gốc và mọi tên miền phụ *.domain)";
            domLayout.Controls.Add(lblDomHeader, 0, 0);

            TableLayoutPanel inputRow = new TableLayoutPanel();
            inputRow.Dock = DockStyle.Fill;
            inputRow.Margin = SP(0, 2, 0, 4);
            inputRow.ColumnCount = 2;
            inputRow.RowCount = 1;
            inputRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            inputRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, S(150)));
            inputRow.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            domLayout.Controls.Add(inputRow, 0, 1);

            BorderPanel txtWrap = new BorderPanel();
            txtWrap.Dock = DockStyle.Fill;
            txtWrap.Margin = SP(0, 1, 10, 1);
            txtWrap.Padding = SP(10, 6, 10, 4);
            txtWrap.BackColor = BgInput;
            txtWrap.BorderColor = BorderSubtle;
            inputRow.Controls.Add(txtWrap, 0, 0);

            txtDomainInput = new TextBox();
            txtDomainInput.BorderStyle = BorderStyle.None;
            txtDomainInput.Dock = DockStyle.Fill;
            txtDomainInput.BackColor = BgInput;
            txtDomainInput.ForeColor = TextMuted;
            txtDomainInput.Font = new Font("Segoe UI", 9.5F, FontStyle.Regular, GraphicsUnit.Point);
            txtDomainInput.Text = InputPlaceholder;
            txtWrap.Controls.Add(txtDomainInput);

            btnAddDomain = CreateStyledButton("+ Thêm tên miền", AccentBlue, Color.FromArgb(37, 99, 235), Color.White);
            btnAddDomain.Dock = DockStyle.Fill;
            btnAddDomain.Margin = SP(0, 1, 0, 1);
            btnAddDomain.Font = new Font("Segoe UI Semibold", 9.25F, FontStyle.Bold, GraphicsUnit.Point);
            inputRow.Controls.Add(btnAddDomain, 1, 0);

            flowPresets = new FlowLayoutPanel();
            flowPresets.Dock = DockStyle.Fill;
            flowPresets.Margin = SP(0, 0, 0, 4);
            flowPresets.WrapContents = false;
            flowPresets.AutoScroll = false;

            Label lblQuick = new Label();
            lblQuick.AutoSize = true;
            lblQuick.Margin = SP(0, 6, 6, 0);
            lblQuick.ForeColor = TextSecondary;
            lblQuick.Font = new Font("Segoe UI", 8.75F, FontStyle.Regular, GraphicsUnit.Point);
            lblQuick.Text = "Thêm nhanh đủ CDN:";
            flowPresets.Controls.Add(lblQuick);

            foreach (QuickPreset preset in AppConfig.QuickPresets)
            {
                QuickPreset captured = preset;
                Button b = CreateStyledButton(captured.Label, BtnNeutral, BtnNeutralHover, TextPrimary);
                b.AutoSize = true;
                b.AutoSizeMode = AutoSizeMode.GrowAndShrink;
                b.Padding = SP(6, 2, 6, 2);
                b.Margin = SP(0, 2, 6, 2);
                b.Font = new Font("Segoe UI", 8.5F, FontStyle.Regular, GraphicsUnit.Point);
                b.Click += delegate { AddPresetDomains(captured); };
                flowPresets.Controls.Add(b);
            }
            domLayout.Controls.Add(flowPresets, 0, 2);

            gridDomains = new DataGridView();
            gridDomains.Dock = DockStyle.Fill;
            gridDomains.Margin = SP(0, 2, 0, 6);
            gridDomains.BackgroundColor = BgInput;
            gridDomains.BorderStyle = BorderStyle.FixedSingle;
            gridDomains.GridColor = Color.FromArgb(30, 41, 59);
            gridDomains.EnableHeadersVisualStyles = false;
            gridDomains.AllowUserToAddRows = false;
            gridDomains.AllowUserToDeleteRows = false;
            gridDomains.AllowUserToResizeRows = false;
            gridDomains.RowHeadersVisible = false;
            gridDomains.MultiSelect = true;
            gridDomains.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            gridDomains.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            gridDomains.RowTemplate.Height = S(31);

            try
            {
                PropertyInfo pi = typeof(DataGridView).GetProperty("DoubleBuffered", BindingFlags.Instance | BindingFlags.NonPublic);
                if (pi != null) pi.SetValue(gridDomains, true, null);
            }
            catch { }

            gridDomains.ColumnHeadersHeight = S(34);
            gridDomains.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
            gridDomains.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single;
            gridDomains.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(20, 27, 40);
            gridDomains.ColumnHeadersDefaultCellStyle.ForeColor = TextSecondary;
            gridDomains.ColumnHeadersDefaultCellStyle.SelectionBackColor = Color.FromArgb(20, 27, 40);
            gridDomains.ColumnHeadersDefaultCellStyle.SelectionForeColor = TextSecondary;
            gridDomains.ColumnHeadersDefaultCellStyle.Font = new Font("Segoe UI Semibold", 9F, FontStyle.Bold, GraphicsUnit.Point);

            gridDomains.DefaultCellStyle.BackColor = BgInput;
            gridDomains.DefaultCellStyle.ForeColor = TextPrimary;
            gridDomains.DefaultCellStyle.SelectionBackColor = Color.FromArgb(30, 48, 78);
            gridDomains.DefaultCellStyle.SelectionForeColor = Color.White;
            gridDomains.DefaultCellStyle.Padding = SP(4, 0, 4, 0);

            DataGridViewCheckBoxColumn colEnabled = new DataGridViewCheckBoxColumn();
            colEnabled.Name = "Enabled";
            colEnabled.HeaderText = "Bật";
            colEnabled.AutoSizeMode = DataGridViewAutoSizeColumnMode.None;
            colEnabled.Width = S(54);

            DataGridViewTextBoxColumn colDomain = new DataGridViewTextBoxColumn();
            colDomain.Name = "Domain";
            colDomain.HeaderText = "Tên miền";
            colDomain.ReadOnly = true;
            colDomain.FillWeight = 36F;

            DataGridViewTextBoxColumn colGroup = new DataGridViewTextBoxColumn();
            colGroup.Name = "Group";
            colGroup.HeaderText = "Nhóm / Ghi chú";
            colGroup.ReadOnly = true;
            colGroup.FillWeight = 26F;

            DataGridViewTextBoxColumn colStatus = new DataGridViewTextBoxColumn();
            colStatus.Name = "Status";
            colStatus.HeaderText = "Kiểm tra vượt chặn";
            colStatus.ReadOnly = true;
            colStatus.FillWeight = 38F;

            gridDomains.Columns.AddRange(new DataGridViewColumn[] { colEnabled, colDomain, colGroup, colStatus });
            domLayout.Controls.Add(gridDomains, 0, 3);

            TableLayoutPanel domBottomBar = new TableLayoutPanel();
            domBottomBar.Dock = DockStyle.Fill;
            domBottomBar.Margin = new Padding(0);
            domBottomBar.ColumnCount = 4;
            domBottomBar.RowCount = 1;
            domBottomBar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            domBottomBar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            domBottomBar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            domBottomBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            domBottomBar.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            domLayout.Controls.Add(domBottomBar, 0, 4);

            btnTestSelected = CreateStyledButton("Kiểm tra dòng chọn", BtnNeutral, BtnNeutralHover, TextPrimary);
            btnTestSelected.Size = new Size(S(152), S(32));
            btnTestSelected.Margin = SP(0, 3, 8, 2);
            domBottomBar.Controls.Add(btnTestSelected, 0, 0);

            btnTestAll = CreateStyledButton("Kiểm tra tất cả", BtnNeutral, BtnNeutralHover, TextPrimary);
            btnTestAll.Size = new Size(S(128), S(32));
            btnTestAll.Margin = SP(0, 3, 8, 2);
            domBottomBar.Controls.Add(btnTestAll, 1, 0);

            btnRemoveSelected = CreateStyledButton("Xóa dòng chọn", Color.FromArgb(62, 30, 38), Color.FromArgb(92, 38, 48), Color.FromArgb(252, 165, 165));
            btnRemoveSelected.Size = new Size(S(128), S(32));
            btnRemoveSelected.Margin = SP(0, 3, 12, 2);
            domBottomBar.Controls.Add(btnRemoveSelected, 2, 0);

            chkSteamGames = new CheckBox();
            chkSteamGames.AutoSize = true;
            chkSteamGames.Dock = DockStyle.Right;
            chkSteamGames.Margin = SP(4, 8, 0, 0);
            chkSteamGames.ForeColor = TextSecondary;
            chkSteamGames.Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
            chkSteamGames.Text = "Kèm bộ 19 tên miền Steam & Game";
            domBottomBar.Controls.Add(chkSteamGames, 3, 0);

            // =========================================================
            // CARD 3: TÙY CHỌN VẬN HÀNH & CỨU HỘ MẠNG
            // =========================================================
            BorderPanel cardBottom = new BorderPanel();
            cardBottom.Dock = DockStyle.Fill;
            cardBottom.Margin = new Padding(0);
            cardBottom.Padding = SP(16, 10, 16, 10);
            cardBottom.BackColor = BgCard;
            root.Controls.Add(cardBottom, 0, 2);

            TableLayoutPanel botLayout = new TableLayoutPanel();
            botLayout.Dock = DockStyle.Fill;
            botLayout.ColumnCount = 2;
            botLayout.RowCount = 3;
            botLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 54F));
            botLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 46F));
            botLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, S(28)));
            botLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, S(28)));
            botLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            cardBottom.Controls.Add(botLayout);

            chkAutoEnableOnLaunch = CreateOptionCheckBox("Tự bật vượt chặn ngay khi mở ứng dụng");
            botLayout.Controls.Add(chkAutoEnableOnLaunch, 0, 0);

            chkMinimizeToTray = CreateOptionCheckBox("Thu nhỏ xuống khay đồng hồ khi bấm nút [X]");
            botLayout.Controls.Add(chkMinimizeToTray, 1, 0);

            chkAutoStartWin = CreateOptionCheckBox("Tự chạy ngầm cùng Windows (không hỏi Admin)");
            botLayout.Controls.Add(chkAutoStartWin, 0, 1);

            chkPubgDirect = CreateOptionCheckBox("PUBG Sniper cổng 443 + Hosts (cần quyền Admin)");
            botLayout.Controls.Add(chkPubgDirect, 1, 1);

            lblFooterInfo = new Label();
            lblFooterInfo.Dock = DockStyle.Fill;
            lblFooterInfo.TextAlign = ContentAlignment.MiddleLeft;
            lblFooterInfo.Font = new Font("Segoe UI", 8.5F, FontStyle.Regular, GraphicsUnit.Point);
            lblFooterInfo.ForeColor = TextMuted;
            lblFooterInfo.Text = "Cấu hình: " + Path.GetFileName(AppConfig.GetConfigFilePath()) + " (nằm cạnh .exe, chép sang máy khác giữ nguyên danh sách)";
            botLayout.Controls.Add(lblFooterInfo, 0, 2);

            FlowLayoutPanel rightFooterBtns = new FlowLayoutPanel();
            rightFooterBtns.Dock = DockStyle.Fill;
            rightFooterBtns.FlowDirection = FlowDirection.RightToLeft;
            rightFooterBtns.WrapContents = false;
            rightFooterBtns.Margin = SP(0, 4, 0, 0);

            btnEmergencyReset = CreateStyledButton("Khôi phục mạng gốc", BtnNeutral, BtnNeutralHover, Color.FromArgb(253, 224, 71));
            btnEmergencyReset.Size = new Size(S(154), S(30));
            btnEmergencyReset.Margin = SP(8, 0, 0, 0);
            btnEmergencyReset.Font = new Font("Segoe UI", 8.75F, FontStyle.Regular, GraphicsUnit.Point);

            btnHideTray = CreateStyledButton("Ẩn xuống khay", BtnNeutral, BtnNeutralHover, TextPrimary);
            btnHideTray.Size = new Size(S(124), S(30));
            btnHideTray.Margin = new Padding(0);
            btnHideTray.Font = new Font("Segoe UI", 8.75F, FontStyle.Regular, GraphicsUnit.Point);

            rightFooterBtns.Controls.Add(btnEmergencyReset);
            rightFooterBtns.Controls.Add(btnHideTray);
            botLayout.Controls.Add(rightFooterBtns, 1, 2);
        }

        private CheckBox CreateOptionCheckBox(string text)
        {
            CheckBox cb = new CheckBox();
            cb.AutoSize = true;
            cb.Margin = SP(2, 3, 0, 0);
            cb.ForeColor = TextSecondary;
            cb.Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
            cb.Text = text;
            return cb;
        }

        private static Button CreateStyledButton(string text, Color backColor, Color hoverColor, Color foreColor)
        {
            Button b = new Button();
            b.Text = text;
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderSize = 1;
            b.FlatAppearance.BorderColor = Color.FromArgb(52, 65, 90);
            b.FlatAppearance.MouseOverBackColor = hoverColor;
            b.BackColor = backColor;
            b.ForeColor = foreColor;
            b.Cursor = Cursors.Hand;
            b.UseVisualStyleBackColor = false;
            return b;
        }

        private void SetupTrayIcon()
        {
            trayMenu = new ContextMenuStrip();
            ToolStripMenuItem itemOpen = new ToolStripMenuItem("Mở cửa sổ AccessFix");
            itemOpen.Font = new Font(trayMenu.Font, FontStyle.Bold);
            itemOpen.Click += delegate { RestoreFromTray(); };

            trayItemToggle = new ToolStripMenuItem("Bật vượt chặn");
            trayItemToggle.Click += delegate { ToggleBypass(); };

            ToolStripMenuItem itemReset = new ToolStripMenuItem("Khôi phục mạng gốc");
            itemReset.Click += delegate
            {
                StopBypass();
                SystemProxyManager.RestoreSystemProxy();
                RefreshUiState();
            };

            ToolStripMenuItem itemExit = new ToolStripMenuItem("Thoát hoàn toàn & Khôi phục mạng");
            itemExit.Click += delegate { ExitApplicationCleanly(); };

            trayMenu.Items.Add(itemOpen);
            trayMenu.Items.Add(new ToolStripSeparator());
            trayMenu.Items.Add(trayItemToggle);
            trayMenu.Items.Add(itemReset);
            trayMenu.Items.Add(new ToolStripSeparator());
            trayMenu.Items.Add(itemExit);

            trayIcon = new NotifyIcon();
            trayIcon.Icon = iconInactiveSmall;
            trayIcon.Text = "AccessFix — Đang tắt";
            trayIcon.ContextMenuStrip = trayMenu;
            trayIcon.Visible = true;
            trayIcon.DoubleClick += delegate { RestoreFromTray(); };
        }

        private void BindEvents()
        {
            btnToggleMain.Click += delegate { ToggleBypass(); };

            txtDomainInput.Enter += delegate
            {
                if (isPlaceholderActive)
                {
                    txtDomainInput.Text = "";
                    txtDomainInput.ForeColor = TextPrimary;
                    isPlaceholderActive = false;
                }
            };

            txtDomainInput.Leave += delegate
            {
                if (string.IsNullOrWhiteSpace(txtDomainInput.Text))
                {
                    isPlaceholderActive = true;
                    txtDomainInput.ForeColor = TextMuted;
                    txtDomainInput.Text = InputPlaceholder;
                }
            };

            txtDomainInput.KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Enter)
                {
                    e.SuppressKeyPress = true;
                    AddDomainFromInput();
                }
            };

            btnAddDomain.Click += delegate { AddDomainFromInput(); };
            btnRemoveSelected.Click += delegate { RemoveSelectedDomains(); };
            btnTestSelected.Click += delegate { RunTestSelected(); };
            btnTestAll.Click += delegate { RunTestAll(); };

            gridDomains.CurrentCellDirtyStateChanged += delegate
            {
                if (gridDomains.IsCurrentCellDirty && gridDomains.CurrentCell is DataGridViewCheckBoxCell)
                {
                    gridDomains.CommitEdit(DataGridViewDataErrorContexts.Commit);
                }
            };

            gridDomains.CellValueChanged += GridDomains_CellValueChanged;
            gridDomains.CellFormatting += GridDomains_CellFormatting;
            gridDomains.KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Delete)
                {
                    e.Handled = true;
                    RemoveSelectedDomains();
                }
            };

            chkSteamGames.CheckedChanged += delegate
            {
                if (suppressUiEvents) return;
                config.IncludeSteamGames = chkSteamGames.Checked;
                config.Save();
                HotReloadPacIfRunning();
                RefreshUiState();
            };

            chkAutoEnableOnLaunch.CheckedChanged += delegate
            {
                if (suppressUiEvents) return;
                config.AutoEnableOnLaunch = chkAutoEnableOnLaunch.Checked;
                config.Save();
            };

            chkMinimizeToTray.CheckedChanged += delegate
            {
                if (suppressUiEvents) return;
                config.MinimizeToTrayOnClose = chkMinimizeToTray.Checked;
                config.Save();
            };

            chkAutoStartWin.CheckedChanged += delegate
            {
                if (suppressUiEvents) return;
                string err;
                if (!SystemProxyManager.SetAutoStart(chkAutoStartWin.Checked, out err))
                {
                    MessageBox.Show(this, "Không cập nhật được tùy chọn khởi động cùng Windows:\n" + err,
                        "AccessFix", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    suppressUiEvents = true;
                    chkAutoStartWin.Checked = SystemProxyManager.IsAutoStartEnabled();
                    suppressUiEvents = false;
                }
            };

            chkPubgDirect.CheckedChanged += delegate
            {
                if (suppressUiEvents) return;
                HandlePubgDirectToggle();
            };

            btnHideTray.Click += delegate { HideToTray(true); };

            btnEmergencyReset.Click += delegate
            {
                StopBypass();
                SystemProxyManager.RestoreSystemProxy();
                SplitProxyEngine.ClearDnsCache();
                SystemProxyManager.FlushDns();
                RefreshUiState();
                MessageBox.Show(this,
                    "Đã tắt Proxy nội bộ, xóa cấu hình PAC tồn đọng và làm mới bộ nhớ đệm DNS về trạng thái mạng gốc sạch sẽ.",
                    "Đã khôi phục mạng gốc", MessageBoxButtons.OK, MessageBoxIcon.Information);
            };
        }

        private void StatusDot_Paint(object sender, PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Color c = engine.IsRunning ? AccentGreen : TextMuted;
            using (SolidBrush b = new SolidBrush(c))
            {
                e.Graphics.FillEllipse(b, 1, 1, statusDot.Width - 3, statusDot.Height - 3);
            }
        }

        private void GridDomains_CellFormatting(object sender, DataGridViewCellFormattingEventArgs e)
        {
            if (e.RowIndex < 0 || e.ColumnIndex < 0) return;
            if (gridDomains.Columns[e.ColumnIndex].Name == "Status")
            {
                string val = Convert.ToString(e.Value ?? "");
                if (val.StartsWith("✓"))
                    e.CellStyle.ForeColor = AccentGreen;
                else if (val.StartsWith("✗"))
                    e.CellStyle.ForeColor = Color.FromArgb(248, 113, 113);
                else if (val.StartsWith("Đang"))
                    e.CellStyle.ForeColor = Color.FromArgb(250, 204, 21);
                else
                    e.CellStyle.ForeColor = TextMuted;
            }
            else if (gridDomains.Columns[e.ColumnIndex].Name == "Group")
            {
                e.CellStyle.ForeColor = TextSecondary;
            }
        }

        private void GridDomains_CellValueChanged(object sender, DataGridViewCellEventArgs e)
        {
            if (suppressUiEvents || e.RowIndex < 0) return;
            if (gridDomains.Columns[e.ColumnIndex].Name != "Enabled") return;

            if (e.RowIndex < config.Domains.Count)
            {
                bool enabled = Convert.ToBoolean(gridDomains.Rows[e.RowIndex].Cells["Enabled"].Value ?? false);
                config.Domains[e.RowIndex].Enabled = enabled;
                config.Save();
                HotReloadPacIfRunning();
                RefreshUiState();
            }
        }

        private void SyncCheckboxesFromConfig()
        {
            suppressUiEvents = true;
            chkSteamGames.Checked = config.IncludeSteamGames;
            chkAutoEnableOnLaunch.Checked = config.AutoEnableOnLaunch;
            chkMinimizeToTray.Checked = config.MinimizeToTrayOnClose;
            chkAutoStartWin.Checked = SystemProxyManager.IsAutoStartEnabled();
            chkPubgDirect.Checked = config.EnablePubgDirect443 && SystemProxyManager.IsAdministrator();
            suppressUiEvents = false;
        }

        private void PopulateDomainGrid()
        {
            suppressUiEvents = true;
            gridDomains.Rows.Clear();
            foreach (DomainEntry item in config.Domains)
            {
                string statusText;
                if (!testStatusByDomain.TryGetValue(item.Domain, out statusText))
                {
                    statusText = "Chưa kiểm tra";
                }
                gridDomains.Rows.Add(item.Enabled, item.Domain, item.Group, statusText);
            }
            suppressUiEvents = false;
        }

        private void AddDomainFromInput()
        {
            if (isPlaceholderActive || string.IsNullOrWhiteSpace(txtDomainInput.Text)) return;

            string raw = txtDomainInput.Text;
            string[] parts = raw.Split(new char[] { ',', ';', ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            int added = 0;
            int reEnabled = 0;

            foreach (string p in parts)
            {
                string norm = AppConfig.NormalizeDomain(p);
                if (string.IsNullOrEmpty(norm)) continue;

                DomainEntry existing = FindDomainEntry(norm);
                if (existing != null)
                {
                    if (!existing.Enabled)
                    {
                        existing.Enabled = true;
                        reEnabled++;
                    }
                }
                else
                {
                    config.Domains.Insert(0, new DomainEntry(norm, true, "Tùy chỉnh"));
                    added++;
                }
            }

            if (added == 0 && reEnabled == 0)
            {
                MessageBox.Show(this,
                    "Tên miền nhập vào chưa đúng định dạng hoặc đã có sẵn trong danh sách.\nVí dụ hợp lệ: bbc.com, medium.com, https://www.reddit.com/...",
                    "AccessFix", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            txtDomainInput.Text = "";
            txtDomainInput.Focus();

            config.Save();
            PopulateDomainGrid();
            HotReloadPacIfRunning();
            RefreshUiState();

            if (gridDomains.Rows.Count > 0)
            {
                gridDomains.ClearSelection();
                gridDomains.Rows[0].Selected = true;
            }
        }

        private void AddPresetDomains(QuickPreset preset)
        {
            int changed = 0;
            foreach (string d in preset.Domains)
            {
                string norm = AppConfig.NormalizeDomain(d);
                if (string.IsNullOrEmpty(norm)) continue;

                DomainEntry existing = FindDomainEntry(norm);
                if (existing != null)
                {
                    if (!existing.Enabled)
                    {
                        existing.Enabled = true;
                        changed++;
                    }
                }
                else
                {
                    config.Domains.Add(new DomainEntry(norm, true, preset.GroupName));
                    changed++;
                }
            }

            if (changed > 0)
            {
                config.Save();
                PopulateDomainGrid();
                HotReloadPacIfRunning();
                RefreshUiState();
            }
        }

        private DomainEntry FindDomainEntry(string normalizedDomain)
        {
            foreach (DomainEntry d in config.Domains)
            {
                if (string.Equals(d.Domain, normalizedDomain, StringComparison.OrdinalIgnoreCase))
                    return d;
            }
            return null;
        }

        private void RemoveSelectedDomains()
        {
            if (gridDomains.SelectedRows.Count == 0) return;

            List<int> indices = new List<int>();
            foreach (DataGridViewRow row in gridDomains.SelectedRows)
            {
                if (row.Index >= 0 && row.Index < config.Domains.Count)
                {
                    indices.Add(row.Index);
                }
            }
            indices.Sort();
            for (int i = indices.Count - 1; i >= 0; i--)
            {
                config.Domains.RemoveAt(indices[i]);
            }

            config.Save();
            PopulateDomainGrid();
            HotReloadPacIfRunning();
            RefreshUiState();
        }

        private void RunTestSelected()
        {
            if (isTestingBatch) return;
            if (gridDomains.SelectedRows.Count == 0)
            {
                MessageBox.Show(this, "Hãy chọn ít nhất 1 dòng tên miền trong bảng để kiểm tra.",
                    "AccessFix", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            List<string> targets = new List<string>();
            foreach (DataGridViewRow row in gridDomains.SelectedRows)
            {
                string dom = Convert.ToString(row.Cells["Domain"].Value ?? "");
                if (!string.IsNullOrEmpty(dom) && !targets.Contains(dom))
                    targets.Add(dom);
            }
            RunDomainTestsAsync(targets);
        }

        private void RunTestAll()
        {
            if (isTestingBatch) return;
            List<string> targets = new List<string>();
            foreach (DomainEntry d in config.Domains)
            {
                if (d.Enabled && !string.IsNullOrEmpty(d.Domain))
                    targets.Add(d.Domain);
            }
            if (targets.Count == 0)
            {
                MessageBox.Show(this, "Chưa có tên miền nào đang bật để kiểm tra.",
                    "AccessFix", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            RunDomainTestsAsync(targets);
        }

        private void RunDomainTestsAsync(List<string> domainsToTest)
        {
            isTestingBatch = true;
            btnTestSelected.Enabled = false;
            btnTestAll.Enabled = false;

            foreach (string dom in domainsToTest)
            {
                SetDomainRowStatus(dom, "Đang kiểm tra...");
            }

            int mode = config.SplitMode;
            ThreadPool.QueueUserWorkItem(delegate
            {
                foreach (string dom in domainsToTest)
                {
                    DomainTestResult res = SplitProxyEngine.TestDomainConnection(dom, mode);
                    string text = res.Success ? ("✓ " + res.Message) : ("✗ " + res.Message);
                    try
                    {
                        if (!this.IsDisposed && this.IsHandleCreated)
                        {
                            this.BeginInvoke(new MethodInvoker(delegate
                            {
                                SetDomainRowStatus(dom, text);
                            }));
                        }
                    }
                    catch { }
                }

                try
                {
                    if (!this.IsDisposed && this.IsHandleCreated)
                    {
                        this.BeginInvoke(new MethodInvoker(delegate
                        {
                            isTestingBatch = false;
                            btnTestSelected.Enabled = true;
                            btnTestAll.Enabled = true;
                        }));
                    }
                }
                catch { }
            });
        }

        private void SetDomainRowStatus(string domain, string statusText)
        {
            testStatusByDomain[domain] = statusText;
            for (int i = 0; i < gridDomains.Rows.Count; i++)
            {
                string rowDom = Convert.ToString(gridDomains.Rows[i].Cells["Domain"].Value ?? "");
                if (string.Equals(rowDom, domain, StringComparison.OrdinalIgnoreCase))
                {
                    gridDomains.Rows[i].Cells["Status"].Value = statusText;
                }
            }
        }

        private void HandlePubgDirectToggle()
        {
            if (chkPubgDirect.Checked && !SystemProxyManager.IsAdministrator())
            {
                suppressUiEvents = true;
                chkPubgDirect.Checked = false;
                suppressUiEvents = false;

                MessageBox.Show(this,
                    "Chế độ PUBG Sniper cần mở cổng 443 và cập nhật tạm thời file hosts của Windows nên yêu cầu quyền Administrator.\n\n" +
                    "(Lưu ý: Để vượt chặn Web như LinkedIn, Quora, Steam... thì KHÔNG cần bật mục này).\n\n" +
                    "Nếu muốn dùng cho PUBG, hãy thoát ứng dụng rồi bấm chuột phải vào AccessFix.exe chọn \"Run as administrator\".",
                    "Cần quyền Administrator cho PUBG Sniper",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            config.EnablePubgDirect443 = chkPubgDirect.Checked;
            config.Save();

            if (engine.IsRunning)
            {
                StopBypass();
                StartBypass();
            }
        }

        private void ToggleBypass()
        {
            if (engine.IsRunning)
                StopBypass();
            else
                StartBypass();
        }

        private void StartBypass()
        {
            List<string> effectiveDomains = config.GetEffectiveDomains();
            string pac = SystemProxyManager.BuildPacScript(config.ProxyPort, effectiveDomains);

            bool wantPubg443 = config.EnablePubgDirect443 && SystemProxyManager.IsAdministrator();
            string err;
            if (!engine.Start(config.ProxyPort, pac, config.SplitMode, wantPubg443, out err))
            {
                MessageBox.Show(this, "Không khởi động được bộ vượt chặn:\n" + err,
                    "AccessFix", MessageBoxButtons.OK, MessageBoxIcon.Error);
                RefreshUiState();
                return;
            }

            if (engine.Port != config.ProxyPort)
            {
                pac = SystemProxyManager.BuildPacScript(engine.Port, effectiveDomains);
                engine.UpdatePacContent(pac);
            }

            SystemProxyManager.ApplyPacUrl(engine.Port);

            if (wantPubg443 && engine.HasDirect443)
            {
                SystemProxyManager.ApplyPubgHostsEntries();
            }

            List<string> customWarmup = new List<string>();
            foreach (DomainEntry d in config.Domains)
            {
                if (d.Enabled && !string.IsNullOrEmpty(d.Domain))
                    customWarmup.Add(d.Domain);
            }
            SplitProxyEngine.WarmUpDomainsAsync(customWarmup);

            RefreshUiState();
        }

        public void StopBypass()
        {
            if (engine.IsRunning)
            {
                engine.Stop();
            }
            SystemProxyManager.RemovePubgHostsEntries();
            SystemProxyManager.RestoreSystemProxy();
            RefreshUiState();
        }

        private void HotReloadPacIfRunning()
        {
            if (!engine.IsRunning) return;
            List<string> effectiveDomains = config.GetEffectiveDomains();
            string pac = SystemProxyManager.BuildPacScript(engine.Port, effectiveDomains);
            engine.UpdatePacContent(pac);
            SystemProxyManager.ApplyPacUrl(engine.Port);
        }

        private void RefreshUiState()
        {
            bool active = engine.IsRunning;
            statusDot.Invalidate();

            if (active)
            {
                lblStatusTitle.Text = "ĐANG BẬT VƯỢT CHẶN";
                lblStatusTitle.ForeColor = AccentGreen;
                btnToggleMain.Text = "TẮT VƯỢT CHẶN";
                btnToggleMain.BackColor = AccentRed;
                btnToggleMain.FlatAppearance.MouseOverBackColor = AccentRedHover;

                this.Icon = iconActiveWindow;
                trayIcon.Icon = iconActiveSmall;
                trayIcon.Text = string.Format("AccessFix — Đang bật (Cổng {0})", engine.Port);
                trayItemToggle.Text = "Tắt vượt chặn";
            }
            else
            {
                lblStatusTitle.Text = "ĐANG TẮT VƯỢT CHẶN";
                lblStatusTitle.ForeColor = TextPrimary;
                btnToggleMain.Text = "BẬT VƯỢT CHẶN";
                btnToggleMain.BackColor = AccentGreen;
                btnToggleMain.FlatAppearance.MouseOverBackColor = AccentGreenHover;

                this.Icon = iconInactiveWindow;
                trayIcon.Icon = iconInactiveSmall;
                trayIcon.Text = "AccessFix — Đang tắt";
                trayItemToggle.Text = "Bật vượt chặn";
            }

            UpdateLiveMetricsLabel();
        }

        private void UpdateLiveMetricsLabel()
        {
            int count = config.GetEffectiveDomains().Count;
            if (engine.IsRunning)
            {
                string pubgBadge = engine.HasDirect443 ? " • PUBG 443: Bật" : "";
                lblStatusMetrics.Text = string.Format(
                    "Cổng nội bộ: 127.0.0.1:{0}  •  Đang áp dụng: {1} tên miền  •  Kết nối đi qua: {2} (đang mở: {3}){4}",
                    engine.Port, count, engine.TotalConnections, engine.ActiveConnections, pubgBadge);

                string lastHost = engine.LastConnectedHost;
                if (!string.IsNullOrEmpty(lastHost))
                {
                    lblLastActivity.Text = "Tên miền vừa đi qua gần nhất: " + lastHost + " (DoH 1.1.1.1 + Cắt gói SNI)";
                    lblLastActivity.ForeColor = Color.FromArgb(110, 231, 183);
                }
                else
                {
                    lblLastActivity.Text = "Sẵn sàng vượt chặn • Mọi trang ngoài danh sách vẫn đi thẳng tốc độ gốc.";
                    lblLastActivity.ForeColor = TextMuted;
                }
            }
            else
            {
                lblStatusMetrics.Text = string.Format(
                    "Trạng thái mạng: Trực tiếp mặc định  •  Đã cấu hình sẵn: {0} tên miền.", count);
                lblLastActivity.Text = "Bấm \"BẬT VƯỢT CHẶN\" để sử dụng ngay (không cần quyền Admin, không đổi DNS máy).";
                lblLastActivity.ForeColor = TextMuted;
            }
        }

        private void HideToTray(bool showBalloon)
        {
            this.Hide();
            this.ShowInTaskbar = false;
            if (showBalloon && trayIcon != null)
            {
                string status = engine.IsRunning ? "đang bật vượt chặn ngầm" : "đang chạy chờ ở khay hệ thống";
                trayIcon.ShowBalloonTip(2000, "AccessFix v2.0",
                    "Ứng dụng " + status + ". Nhấp đúp vào biểu tượng khiên để mở lại.",
                    ToolTipIcon.Info);
            }
        }

        public void RestoreFromTray()
        {
            this.ShowInTaskbar = true;
            this.Show();
            if (this.WindowState == FormWindowState.Minimized)
            {
                this.WindowState = FormWindowState.Normal;
            }
            this.Activate();
        }

        public void ExitApplicationCleanly()
        {
            allowExit = true;
            try { StopBypass(); } catch { }
            try
            {
                if (trayIcon != null)
                {
                    trayIcon.Visible = false;
                    trayIcon.Dispose();
                }
            }
            catch { }
            Application.Exit();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!allowExit && e.CloseReason == CloseReason.UserClosing && config.MinimizeToTrayOnClose)
            {
                e.Cancel = true;
                HideToTray(true);
                return;
            }

            try { statsTimer.Stop(); } catch { }
            try { StopBypass(); } catch { }
            try
            {
                if (trayIcon != null)
                {
                    trayIcon.Visible = false;
                    trayIcon.Dispose();
                }
            }
            catch { }

            base.OnFormClosing(e);
        }
    }
}
