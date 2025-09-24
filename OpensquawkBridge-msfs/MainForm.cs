#nullable enable
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Threading.Tasks;
using System.Windows.Forms;

internal sealed class MainForm : Form
{
    private readonly BridgeManager _manager;
    private readonly BadgeLabel _connectionBadge;
    private readonly BadgeLabel _simBadge;
    private readonly BadgeLabel _flightBadge;
    private readonly Label _userStatusLabel;
    private readonly TextBox _tokenBox;
    private readonly RichTextBox _logBox;
    private readonly RoundedButton _connectButton;
    private readonly RoundedButton _resetButton;
    private readonly RoundedButton _copyButton;

    private readonly Color _primaryColor = ColorTranslator.FromHtml("#16BBD7");
    private readonly Color _secondaryColor = ColorTranslator.FromHtml("#292D3B");
    private readonly Color _backgroundColor = ColorTranslator.FromHtml("#0B1020");
    private readonly Color _panelColor = ColorTranslator.FromHtml("#161B2E");

    public MainForm(BridgeManager manager)
    {
        _manager = manager;

        Text = "OpenSquawk Bridge";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        BackColor = _backgroundColor;
        ForeColor = Color.White;
        Font = new Font("Segoe UI", 10F, FontStyle.Regular, GraphicsUnit.Point);
        Padding = new Padding(32, 32, 32, 24);
        MinimumSize = new Size(760, 720);
        DoubleBuffered = true;

        var titleLabel = new Label
        {
            Text = "OpenSquawk Bridge",
            AutoSize = true,
            Font = new Font("Segoe UI", 24F, FontStyle.Bold, GraphicsUnit.Point),
            ForeColor = Color.White,
            Margin = new Padding(0, 0, 0, 8)
        };

        var subtitleLabel = new Label
        {
            Text = "MSFS Telemetry & Login",
            AutoSize = true,
            Font = new Font("Segoe UI", 12F, FontStyle.Regular, GraphicsUnit.Point),
            ForeColor = Color.FromArgb(200, 255, 255, 255),
            Margin = new Padding(0, 0, 0, 24)
        };

        _connectionBadge = CreateBadge("Not connected", _secondaryColor);
        _simBadge = CreateBadge("Simulator offline", _secondaryColor);
        _flightBadge = CreateBadge("Flight inactive", _secondaryColor);

        var badgePanel = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = true,
            Margin = new Padding(0, 0, 0, 24),
            Anchor = AnchorStyles.Left | AnchorStyles.Right
        };
        badgePanel.Controls.Add(_connectionBadge);
        badgePanel.Controls.Add(_simBadge);
        badgePanel.Controls.Add(_flightBadge);

        _userStatusLabel = new Label
        {
            AutoSize = true,
            Font = new Font("Segoe UI", 11F, FontStyle.Bold, GraphicsUnit.Point),
            Text = "No user connected",
            ForeColor = Color.FromArgb(220, 255, 255, 255),
            Margin = new Padding(0, 0, 0, 20)
        };

        var tokenPanel = new Panel
        {
            BackColor = _panelColor,
            Padding = new Padding(16),
            Margin = new Padding(0, 0, 0, 20),
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink
        };

        var tokenLabel = new Label
        {
            Text = "Bridge Token",
            AutoSize = true,
            Font = new Font("Segoe UI", 9.5F, FontStyle.Bold, GraphicsUnit.Point),
            ForeColor = Color.FromArgb(220, 255, 255, 255),
            Margin = new Padding(0, 0, 0, 8),
            Dock = DockStyle.Top
        };

        _tokenBox = new TextBox
        {
            BorderStyle = BorderStyle.None,
            ReadOnly = true,
            BackColor = _panelColor,
            ForeColor = Color.White,
            Font = new Font("Consolas", 10F, FontStyle.Regular, GraphicsUnit.Point),
            Dock = DockStyle.Fill,
            Margin = new Padding(0),
            MinimumSize = new Size(0, 32)
        };

        tokenPanel.Controls.Add(_tokenBox);
        tokenPanel.Controls.Add(tokenLabel);
        tokenPanel.Controls.SetChildIndex(tokenLabel, 0);

        _connectButton = CreatePrimaryButton("Open login in browser");
        _connectButton.Click += (_, __) => _manager.OpenLoginPage();

        _resetButton = CreateSecondaryButton("Log out & new token");
        _resetButton.Click += async (_, __) => await RunAsyncAction(_resetButton, _manager.ResetTokenAsync);

        _copyButton = CreateSecondaryButton("Copy token");
        _copyButton.Click += (_, __) => CopyTokenToClipboard();

        var buttonPanel = new TableLayoutPanel
        {
            ColumnCount = 3,
            RowCount = 1,
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0, 0, 0, 20),
            BackColor = Color.Transparent,
            GrowStyle = TableLayoutPanelGrowStyle.FixedSize,
            Padding = new Padding(0),
            Anchor = AnchorStyles.Left | AnchorStyles.Right
        };
        buttonPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33F));
        buttonPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33F));
        buttonPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.34F));
        buttonPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        _connectButton.Dock = DockStyle.Fill;
        _resetButton.Dock = DockStyle.Fill;
        _copyButton.Dock = DockStyle.Fill;

        _connectButton.Margin = new Padding(0, 0, 16, 0);
        _resetButton.Margin = new Padding(0, 0, 16, 0);
        _copyButton.Margin = new Padding(0);

        buttonPanel.Controls.Add(_connectButton, 0, 0);
        buttonPanel.Controls.Add(_resetButton, 1, 0);
        buttonPanel.Controls.Add(_copyButton, 2, 0);

        _logBox = new RichTextBox
        {
            ReadOnly = true,
            BackColor = Color.FromArgb(20, 27, 48),
            ForeColor = Color.White,
            BorderStyle = BorderStyle.None,
            Font = new Font("Consolas", 9.5F, FontStyle.Regular, GraphicsUnit.Point),
            Dock = DockStyle.Fill,
            DetectUrls = false,
            ScrollBars = RichTextBoxScrollBars.Vertical,
            Margin = new Padding(0)
        };

        var logContainer = new Panel
        {
            BackColor = Color.FromArgb(20, 27, 48),
            Padding = new Padding(16),
            Margin = new Padding(0, 0, 0, 0),
            Dock = DockStyle.Fill
        };
        logContainer.Controls.Add(_logBox);

        var mainLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = false,
            BackColor = Color.Transparent,
            ColumnCount = 1,
            AutoScroll = true,
            Padding = new Padding(0)
        };
        mainLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        mainLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        mainLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        mainLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        mainLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        mainLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        mainLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        mainLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

        mainLayout.Controls.Add(titleLabel, 0, 0);
        mainLayout.Controls.Add(subtitleLabel, 0, 1);
        mainLayout.Controls.Add(badgePanel, 0, 2);
        mainLayout.Controls.Add(_userStatusLabel, 0, 3);
        mainLayout.Controls.Add(tokenPanel, 0, 4);
        mainLayout.Controls.Add(buttonPanel, 0, 5);
        mainLayout.Controls.Add(logContainer, 0, 6);

        Controls.Add(mainLayout);

        _manager.UserStatusChanged += OnUserStatusChanged;
        _manager.SimStatusChanged += OnSimStatusChanged;
        _manager.Log += OnLogReceived;
        _manager.TokenChanged += (_, __) => UpdateTokenText(_manager.Token);

        Shown += MainForm_Shown;
    }

    private async void MainForm_Shown(object? sender, EventArgs e)
    {
        await _manager.InitializeAsync();
        UpdateTokenText(_manager.Token);
    }

    private BadgeLabel CreateBadge(string text, Color color)
    {
        var badge = new BadgeLabel
        {
            Margin = new Padding(0, 0, 12, 12),
            BackColor = color,
            ForeColor = Color.White,
            CornerRadius = 20,
            MinimumSize = new Size(180, 44),
            Padding = new Padding(20, 8, 20, 8),
            Font = new Font("Segoe UI", 10F, FontStyle.SemiBold, GraphicsUnit.Point)
        };

        badge.Text = text;
        return badge;
    }

    private RoundedButton CreatePrimaryButton(string text)
    {
        return new RoundedButton
        {
            Text = text,
            BackColor = _primaryColor,
            ForeColor = Color.Black,
            Margin = new Padding(0, 0, 16, 0),
            MinimumSize = new Size(0, 48),
            CornerRadius = 24,
            Font = new Font("Segoe UI", 10F, FontStyle.Bold, GraphicsUnit.Point)
        };
    }

    private RoundedButton CreateSecondaryButton(string text)
    {
        return new RoundedButton
        {
            Text = text,
            BackColor = _secondaryColor,
            ForeColor = Color.White,
            Margin = new Padding(0, 0, 16, 0),
            MinimumSize = new Size(0, 48),
            CornerRadius = 24,
            Font = new Font("Segoe UI", 10F, FontStyle.Bold, GraphicsUnit.Point)
        };
    }

    private void UpdateTokenText(string token)
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action<string>(UpdateTokenText), token);
            return;
        }

        _tokenBox.Text = token;
    }

    private void OnUserStatusChanged(object? sender, UserStatusChangedEventArgs e)
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action<object?, UserStatusChangedEventArgs>(OnUserStatusChanged), sender, e);
            return;
        }

        if (e.IsConnected)
        {
            _connectionBadge.Text = e.UserName != null ? $"Connected as {e.UserName}" : "Connected";
            _connectionBadge.BackColor = Color.FromArgb(60, _primaryColor);
            _userStatusLabel.Text = e.UserName != null ? $"Signed in as {e.UserName}" : "User connected";
        }
        else
        {
            _connectionBadge.Text = "Not connected";
            _connectionBadge.BackColor = _secondaryColor;
            _userStatusLabel.Text = "No user connected";
        }
    }

    private void OnSimStatusChanged(object? sender, SimStatusChangedEventArgs e)
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action<object?, SimStatusChangedEventArgs>(OnSimStatusChanged), sender, e);
            return;
        }

        if (e.SimConnected)
        {
            _simBadge.Text = "Simulator connected";
            _simBadge.BackColor = Color.FromArgb(60, 76, 217, 100);
        }
        else
        {
            _simBadge.Text = "Simulator offline";
            _simBadge.BackColor = _secondaryColor;
        }

        if (e.FlightLoaded)
        {
            _flightBadge.Text = "Flight active";
            _flightBadge.BackColor = Color.FromArgb(60, 76, 217, 100);
        }
        else if (e.SimConnected)
        {
            _flightBadge.Text = "Simulator running";
            _flightBadge.BackColor = Color.FromArgb(60, 22, 187, 215);
        }
        else
        {
            _flightBadge.Text = "Flight inactive";
            _flightBadge.BackColor = _secondaryColor;
        }
    }

    private void OnLogReceived(object? sender, BridgeLogEventArgs e)
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action<object?, BridgeLogEventArgs>(OnLogReceived), sender, e);
            return;
        }

        var text = $"[{e.Timestamp:HH:mm:ss}] {e.Message}\n";
        _logBox.AppendText(text);
        _logBox.SelectionStart = _logBox.TextLength;
        _logBox.ScrollToCaret();
    }

    private void CopyTokenToClipboard()
    {
        if (string.IsNullOrWhiteSpace(_tokenBox.Text))
        {
            return;
        }

        try
        {
            Clipboard.SetText(_tokenBox.Text);
        }
        catch
        {
        }
    }

    private async Task RunAsyncAction(Control sourceButton, Func<Task> action)
    {
        if (!sourceButton.Enabled)
        {
            return;
        }

        sourceButton.Enabled = false;
        try
        {
            await action();
        }
        finally
        {
            sourceButton.Enabled = true;
        }
    }
}

internal sealed class RoundedButton : Button
{
    public int CornerRadius { get; set; } = 20;

    public RoundedButton()
    {
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        DoubleBuffered = true;
        Cursor = Cursors.Hand;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    protected override void OnPaint(PaintEventArgs pevent)
    {
        pevent.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var baseColor = ResolveBackColor();
        pevent.Graphics.Clear(baseColor);

        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        using var path = CreateRoundPath(rect, CornerRadius);
        using var brush = new SolidBrush(BackColor);
        pevent.Graphics.FillPath(brush, path);

        var textRect = new Rectangle(0, 0, Width, Height);
        TextRenderer.DrawText(pevent.Graphics, Text, Font, textRect, ForeColor, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
    }

    protected override void OnPaintBackground(PaintEventArgs pevent)
    {
        var baseColor = ResolveBackColor();
        using var brush = new SolidBrush(baseColor);
        pevent.Graphics.FillRectangle(brush, pevent.ClipRectangle);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        Invalidate();
    }

    private static GraphicsPath CreateRoundPath(Rectangle rect, int radius)
    {
        int diameter = radius * 2;
        Rectangle arcRect = new Rectangle(rect.Location, new Size(diameter, diameter));
        GraphicsPath path = new GraphicsPath();

        path.AddArc(arcRect, 180, 90);
        arcRect.X = rect.Right - diameter;
        path.AddArc(arcRect, 270, 90);
        arcRect.Y = rect.Bottom - diameter;
        path.AddArc(arcRect, 0, 90);
        arcRect.X = rect.Left;
        path.AddArc(arcRect, 90, 90);
        path.CloseFigure();
        return path;
    }

    private Color ResolveBackColor()
    {
        Control? current = Parent;
        while (current is not null && current.BackColor.A == 0)
        {
            current = current.Parent;
        }

        if (current is not null)
        {
            return current.BackColor;
        }

        return FindForm()?.BackColor ?? SystemColors.Control;
    }
}

internal sealed class BadgeLabel : Label
{
    public int CornerRadius { get; set; } = 16;

    public BadgeLabel()
    {
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = CreateRoundPath(new Rectangle(0, 0, Width - 1, Height - 1), CornerRadius);
        using var brush = new SolidBrush(BackColor);
        e.Graphics.FillPath(brush, path);

        var textRect = new Rectangle(Padding.Left, Padding.Top, Width - Padding.Horizontal, Height - Padding.Vertical);
        TextRenderer.DrawText(e.Graphics, Text, Font, textRect, ForeColor, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
    }

    protected override void OnPaintBackground(PaintEventArgs pevent)
    {
        var baseColor = ResolveBackColor();
        using var brush = new SolidBrush(baseColor);
        pevent.Graphics.FillRectangle(brush, pevent.ClipRectangle);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        Invalidate();
    }

    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        AdjustSize();
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        AdjustSize();
    }

    protected override void OnPaddingChanged(EventArgs e)
    {
        base.OnPaddingChanged(e);
        AdjustSize();
    }

    private void AdjustSize()
    {
        if (!AutoSize)
        {
            Invalidate();
            return;
        }

        var textSize = TextRenderer.MeasureText(Text, Font, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.SingleLine);
        int desiredWidth = Math.Max(MinimumSize.Width, textSize.Width + Padding.Horizontal);
        int desiredHeight = Math.Max(MinimumSize.Height, textSize.Height + Padding.Vertical);
        Size = new Size(desiredWidth, desiredHeight);
        Invalidate();
    }

    private static GraphicsPath CreateRoundPath(Rectangle rect, int radius)
    {
        int diameter = radius * 2;
        Rectangle arcRect = new Rectangle(rect.Location, new Size(diameter, diameter));
        GraphicsPath path = new GraphicsPath();

        path.AddArc(arcRect, 180, 90);
        arcRect.X = rect.Right - diameter;
        path.AddArc(arcRect, 270, 90);
        arcRect.Y = rect.Bottom - diameter;
        path.AddArc(arcRect, 0, 90);
        arcRect.X = rect.Left;
        path.AddArc(arcRect, 90, 90);
        path.CloseFigure();
        return path;
    }

    private Color ResolveBackColor()
    {
        Control? current = Parent;
        while (current is not null && current.BackColor.A == 0)
        {
            current = current.Parent;
        }

        if (current is not null)
        {
            return current.BackColor;
        }

        return FindForm()?.BackColor ?? SystemColors.Control;
    }
}
