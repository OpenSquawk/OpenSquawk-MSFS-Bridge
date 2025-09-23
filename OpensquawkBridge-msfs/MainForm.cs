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
        MinimumSize = new Size(580, 640);
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
            Text = "MSFS Telemetrie & Login",
            AutoSize = true,
            Font = new Font("Segoe UI", 12F, FontStyle.Regular, GraphicsUnit.Point),
            ForeColor = Color.FromArgb(200, 255, 255, 255),
            Margin = new Padding(0, 0, 0, 24)
        };

        _connectionBadge = CreateBadge("Nicht verbunden", _secondaryColor);
        _simBadge = CreateBadge("Sim offline", _secondaryColor);
        _flightBadge = CreateBadge("Flug inaktiv", _secondaryColor);

        var badgePanel = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true,
            WrapContents = false,
            Margin = new Padding(0, 0, 0, 24)
        };
        badgePanel.Controls.Add(_connectionBadge);
        badgePanel.Controls.Add(_simBadge);
        badgePanel.Controls.Add(_flightBadge);

        _userStatusLabel = new Label
        {
            AutoSize = true,
            Font = new Font("Segoe UI", 11F, FontStyle.Bold, GraphicsUnit.Point),
            Text = "Kein Benutzer verbunden",
            ForeColor = Color.FromArgb(220, 255, 255, 255),
            Margin = new Padding(0, 0, 0, 20)
        };

        var tokenPanel = new Panel
        {
            BackColor = _panelColor,
            Padding = new Padding(16),
            Margin = new Padding(0, 0, 0, 20),
            Height = 90,
            Dock = DockStyle.Top
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
            Dock = DockStyle.Fill
        };

        tokenPanel.Controls.Add(_tokenBox);
        tokenPanel.Controls.Add(tokenLabel);
        tokenPanel.Controls.SetChildIndex(tokenLabel, 0);

        _connectButton = CreatePrimaryButton("Im Browser verbinden");
        _connectButton.Click += (_, __) => _manager.OpenLoginPage();

        _resetButton = CreateSecondaryButton("Logout & neuen Token");
        _resetButton.Click += async (_, __) => await RunAsyncAction(_resetButton, _manager.ResetTokenAsync);

        _copyButton = CreateSecondaryButton("Token kopieren");
        _copyButton.Click += (_, __) => CopyTokenToClipboard();

        var buttonPanel = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true,
            WrapContents = false,
            Margin = new Padding(0, 0, 0, 20)
        };
        buttonPanel.Controls.Add(_connectButton);
        buttonPanel.Controls.Add(_resetButton);
        buttonPanel.Controls.Add(_copyButton);

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
            ColumnCount = 1
        };
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
        return new BadgeLabel
        {
            Text = text,
            AutoSize = false,
            Size = new Size(170, 40),
            Margin = new Padding(0, 0, 12, 0),
            BackColor = color,
            ForeColor = Color.White,
            CornerRadius = 20,
            Padding = new Padding(0),
            TextAlign = ContentAlignment.MiddleCenter
        };
    }

    private RoundedButton CreatePrimaryButton(string text)
    {
        return new RoundedButton
        {
            Text = text,
            BackColor = _primaryColor,
            ForeColor = Color.Black,
            Margin = new Padding(0, 0, 12, 0),
            Size = new Size(200, 44),
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
            Margin = new Padding(0, 0, 12, 0),
            Size = new Size(200, 44),
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
            _connectionBadge.Text = e.UserName != null ? $"Verbunden als {e.UserName}" : "Verbunden";
            _connectionBadge.BackColor = Color.FromArgb(60, _primaryColor);
            _userStatusLabel.Text = e.UserName != null ? $"Angemeldet als {e.UserName}" : "Benutzer verbunden";
        }
        else
        {
            _connectionBadge.Text = "Nicht verbunden";
            _connectionBadge.BackColor = _secondaryColor;
            _userStatusLabel.Text = "Kein Benutzer verbunden";
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
            _simBadge.Text = "Sim verbunden";
            _simBadge.BackColor = Color.FromArgb(60, 76, 217, 100);
        }
        else
        {
            _simBadge.Text = "Sim offline";
            _simBadge.BackColor = _secondaryColor;
        }

        if (e.FlightLoaded)
        {
            _flightBadge.Text = "Flug aktiv";
            _flightBadge.BackColor = Color.FromArgb(60, 76, 217, 100);
        }
        else if (e.SimConnected)
        {
            _flightBadge.Text = "Sim läuft";
            _flightBadge.BackColor = Color.FromArgb(60, 22, 187, 215);
        }
        else
        {
            _flightBadge.Text = "Flug inaktiv";
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
    }

    protected override void OnPaint(PaintEventArgs pevent)
    {
        pevent.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        pevent.Graphics.Clear(Color.Transparent);

        using var path = CreateRoundPath(ClientRectangle, CornerRadius);
        using var brush = new SolidBrush(BackColor);
        pevent.Graphics.FillPath(brush, path);

        var textRect = new Rectangle(0, 0, Width, Height);
        TextRenderer.DrawText(pevent.Graphics, Text, Font, textRect, ForeColor, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    protected override void OnPaintBackground(PaintEventArgs pevent)
    {
        // verhindert Flackern
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
}

internal sealed class BadgeLabel : Label
{
    public int CornerRadius { get; set; } = 16;

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = CreateRoundPath(new Rectangle(0, 0, Width - 1, Height - 1), CornerRadius);
        using var brush = new SolidBrush(BackColor);
        e.Graphics.FillPath(brush, path);

        TextRenderer.DrawText(e.Graphics, Text, Font, new Rectangle(0, 0, Width, Height), ForeColor, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    protected override void OnPaintBackground(PaintEventArgs pevent)
    {
        // verhindern, dass der Parent-Hintergrund gezeichnet wird
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
}
