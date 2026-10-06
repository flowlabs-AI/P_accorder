using System.Drawing.Drawing2D;
using System.Globalization;
using NAudio.Wave;

namespace Accorder;

public sealed class TunerForm : Form
{
    private const int SampleRate = 44100;
    private const int AnalysisSize = 4096;
    private const float RmsGate = 0.008f;
    private const float InTuneCents = 5f;

    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Accorder", "reference.txt");

    private readonly TableLayoutPanel _topBar = new() { Dock = DockStyle.Top, Height = 32, ColumnCount = 4, RowCount = 1 };
    private readonly ComboBox _deviceBox = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly NumericUpDown _referenceBox = new()
    {
        Minimum = 400, Maximum = 480, DecimalPlaces = 1, Increment = 1, Width = 70, Anchor = AnchorStyles.Left,
    };
    private readonly System.Windows.Forms.Timer _animTimer = new() { Interval = 16 };
    private readonly PitchDetector _detector = new(SampleRate, AnalysisSize);

    private WaveInEvent? _waveIn;
    private readonly float[] _ring = new float[AnalysisSize];
    private readonly float[] _analysis = new float[AnalysisSize];
    private int _ringPos;
    private int _samplesSinceAnalysis;
    private readonly Queue<float> _recent = new();

    // État affiché (thread UI)
    private float _reference = Note.DefaultReferenceA4;
    private Note? _note;
    private float _freq;
    private float _targetCents;
    private float _needleCents;
    private float _level;
    private DateTime _lastDetection = DateTime.MinValue;

    public TunerForm()
    {
        ClientSize = new Size(520, 440);
        MinimumSize = new Size(400, 380);
        BackColor = Color.FromArgb(24, 26, 30);
        ForeColor = Color.Gainsboro;
        DoubleBuffered = true;
        SetStyle(ControlStyles.ResizeRedraw, true);

        // Barre du haut : [ micro .......... ] La [ 440.0 ] Hz
        var fieldBack = Color.FromArgb(40, 43, 50);
        _deviceBox.BackColor = fieldBack;
        _deviceBox.ForeColor = Color.Gainsboro;
        _deviceBox.FlatStyle = FlatStyle.Flat;
        _referenceBox.BackColor = fieldBack;
        _referenceBox.ForeColor = Color.Gainsboro;

        _topBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _topBar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _topBar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _topBar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _topBar.Controls.Add(_deviceBox, 0, 0);
        _topBar.Controls.Add(MakeLabel("La ="), 1, 0);
        _topBar.Controls.Add(_referenceBox, 2, 0);
        _topBar.Controls.Add(MakeLabel("Hz"), 3, 0);
        Controls.Add(_topBar);

        for (int i = 0; i < WaveInEvent.DeviceCount; i++)
            _deviceBox.Items.Add(WaveInEvent.GetCapabilities(i).ProductName);
        _deviceBox.SelectedIndexChanged += (_, _) => StartCapture(_deviceBox.SelectedIndex);

        _referenceBox.Value = (decimal)Math.Clamp(LoadReference(), 400f, 480f);
        _referenceBox.ValueChanged += (_, _) => SetReference((float)_referenceBox.Value);
        SetReference((float)_referenceBox.Value);

        _animTimer.Tick += (_, _) => Animate();
        _animTimer.Start();

        Load += (_, _) =>
        {
            if (_deviceBox.Items.Count > 0) _deviceBox.SelectedIndex = 0;
            else MessageBox.Show(this, "Aucune entrée micro détectée.", "Accordeur",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        };
        FormClosing += (_, _) =>
        {
            StopCapture();
            SaveReference(_reference);
        };
    }

    private static Label MakeLabel(string text) => new()
    {
        Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(6, 0, 2, 0),
    };

    // ---------- Référence du La ----------

    private void SetReference(float hz)
    {
        _reference = hz;
        Text = $"Accordeur — La {hz.ToString("0.#", CultureInfo.CurrentCulture)} Hz";
        _recent.Clear();
        if (_note != null) _note = Note.FromFrequency(_freq, _reference);
        Invalidate();
    }

    private static float LoadReference()
    {
        try
        {
            if (File.Exists(SettingsPath) &&
                float.TryParse(File.ReadAllText(SettingsPath), NumberStyles.Float, CultureInfo.InvariantCulture, out var hz))
                return hz;
        }
        catch { /* réglage illisible : on garde la valeur par défaut */ }
        return Note.DefaultReferenceA4;
    }

    private static void SaveReference(float hz)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, hz.ToString(CultureInfo.InvariantCulture));
        }
        catch { /* pas bloquant */ }
    }

    // ---------- Audio ----------

    private void StartCapture(int device)
    {
        StopCapture();
        if (device < 0) return;
        try
        {
            _waveIn = new WaveInEvent
            {
                DeviceNumber = device,
                WaveFormat = new WaveFormat(SampleRate, 16, 1),
                BufferMilliseconds = 25,
                NumberOfBuffers = 3,
            };
            _waveIn.DataAvailable += OnData;
            _waveIn.StartRecording();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Impossible d'ouvrir le micro :\n" + ex.Message, "Accordeur",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void StopCapture()
    {
        if (_waveIn == null) return;
        _waveIn.DataAvailable -= OnData;
        _waveIn.StopRecording();
        _waveIn.Dispose();
        _waveIn = null;
    }

    // Appelé sur le thread audio
    private void OnData(object? sender, WaveInEventArgs e)
    {
        for (int i = 0; i + 1 < e.BytesRecorded; i += 2)
        {
            _ring[_ringPos] = BitConverter.ToInt16(e.Buffer, i) / 32768f;
            _ringPos = (_ringPos + 1) % AnalysisSize;
            _samplesSinceAnalysis++;
        }

        // Analyse environ tous les 2048 échantillons (~46 ms)
        if (_samplesSinceAnalysis < AnalysisSize / 2) return;
        _samplesSinceAnalysis = 0;

        // Remet le tampon circulaire dans l'ordre chronologique
        int tail = AnalysisSize - _ringPos;
        Array.Copy(_ring, _ringPos, _analysis, 0, tail);
        Array.Copy(_ring, 0, _analysis, tail, _ringPos);

        double sumSq = 0;
        foreach (var s in _analysis) sumSq += s * s;
        float rms = (float)Math.Sqrt(sumSq / AnalysisSize);

        float? freq = rms >= RmsGate ? _detector.Detect(_analysis) : null;

        if (IsHandleCreated && !IsDisposed)
            BeginInvoke(() => OnPitch(freq, rms));
    }

    // ---------- Logique UI ----------

    private void OnPitch(float? freq, float rms)
    {
        _level = Math.Max(_level, Math.Clamp(rms * 8f, 0f, 1f));
        if (freq is not float f) return;

        // Médiane des dernières mesures pour supprimer les sauts d'octave isolés
        _recent.Enqueue(f);
        while (_recent.Count > 5) _recent.Dequeue();
        float median = _recent.OrderBy(x => x).ElementAt(_recent.Count / 2);

        _freq = median;
        _note = Note.FromFrequency(median, _reference);
        _targetCents = _note.Value.Cents;
        _lastDetection = DateTime.Now;
    }

    private bool IsActive => _note != null && (DateTime.Now - _lastDetection).TotalMilliseconds < 1500;

    private void Animate()
    {
        if (!IsActive)
        {
            _targetCents = 0;
            _recent.Clear();
        }
        _needleCents += (_targetCents - _needleCents) * 0.18f;
        _level *= 0.92f;
        Invalidate();
    }

    // ---------- Dessin ----------

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

        int top = _topBar.Bottom;
        var area = new Rectangle(0, top, ClientSize.Width, ClientSize.Height - top);
        bool active = IsActive;
        bool inTune = active && Math.Abs(_needleCents) <= InTuneCents;

        Color dim = Color.FromArgb(90, 95, 105);
        Color accent = !active ? dim
            : inTune ? Color.FromArgb(70, 210, 110)
            : Math.Abs(_needleCents) < 20 ? Color.FromArgb(240, 190, 60)
            : Color.FromArgb(235, 85, 70);

        // Géométrie du cadran
        float radius = Math.Min(area.Width * 0.40f, area.Height * 0.50f);
        var center = new PointF(area.Left + area.Width / 2f, area.Top + radius + 36);
        const float sweep = 100f; // degrés couverts par ±50 cents
        static float Angle(float cents) => -90 + cents / 50f * (sweep / 2);
        PointF OnCircle(double deg, float r) =>
            new(center.X + (float)Math.Cos(deg * Math.PI / 180) * r, center.Y + (float)Math.Sin(deg * Math.PI / 180) * r);

        // Zone "juste"
        using (var zone = new Pen(Color.FromArgb(70, 70, 210, 110), 14))
        {
            var r = new RectangleF(center.X - radius + 7, center.Y - radius + 7, (radius - 7) * 2, (radius - 7) * 2);
            g.DrawArc(zone, r, Angle(-InTuneCents), Angle(InTuneCents) - Angle(-InTuneCents));
        }

        // Graduations
        using var tickFont = new Font("Segoe UI", 8.5f);
        using var tickBrush = new SolidBrush(Color.FromArgb(140, 145, 155));
        for (int c = -50; c <= 50; c += 5)
        {
            bool major = c % 10 == 0;
            using var pen = new Pen(c == 0 ? Color.Gainsboro : Color.FromArgb(110, 115, 125), major ? 2.5f : 1.2f);
            g.DrawLine(pen, OnCircle(Angle(c), radius - (major ? 18 : 10)), OnCircle(Angle(c), radius));

            if (major)
            {
                string label = c > 0 ? "+" + c : c.ToString();
                var sz = g.MeasureString(label, tickFont);
                var p = OnCircle(Angle(c), radius + 14);
                g.DrawString(label, tickFont, tickBrush, p.X - sz.Width / 2, p.Y - sz.Height / 2);
            }
        }

        // Bémol / dièse
        using var sideFont = new Font("Segoe UI", 18, FontStyle.Bold);
        using var sideBrush = new SolidBrush(Color.FromArgb(120, 125, 135));
        g.DrawString("♭", sideFont, sideBrush, center.X - radius - 6, center.Y - 34);
        g.DrawString("♯", sideFont, sideBrush, center.X + radius - 18, center.Y - 34);

        // Aiguille
        float shown = Math.Clamp(_needleCents, -50, 50);
        using (var needle = new Pen(accent, 4) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            g.DrawLine(needle, center, OnCircle(Angle(shown), radius - 6));
        using (var hub = new SolidBrush(accent))
            g.FillEllipse(hub, center.X - 9, center.Y - 9, 18, 18);

        // Nom de la note
        float textTop = center.Y + 14;
        using var noteFont = new Font("Segoe UI", 54, FontStyle.Bold);
        using var octFont = new Font("Segoe UI", 20, FontStyle.Bold);
        using var infoFont = new Font("Segoe UI", 11);
        using var noteBrush = new SolidBrush(accent);
        using var infoBrush = new SolidBrush(Color.FromArgb(170, 175, 185));

        string name = active ? _note!.Value.Name : "–";
        var nsz = g.MeasureString(name, noteFont);
        float nx = center.X - nsz.Width / 2;
        g.DrawString(name, noteFont, noteBrush, nx, textTop);
        if (active)
            g.DrawString(_note!.Value.Octave.ToString(), octFont, noteBrush, nx + nsz.Width - 16, textTop + nsz.Height - 50);

        string info;
        if (active)
        {
            var n = _note!.Value;
            info = $"{n.FrenchName}{n.Octave}   ·   {_freq:0.0} Hz   ·   {n.Cents:+0;-0;0} cents   ·   cible {n.TargetFreq:0.00} Hz";
        }
        else info = "Jouez une corde…";
        var isz = g.MeasureString(info, infoFont);
        g.DrawString(info, infoFont, infoBrush, center.X - isz.Width / 2, textTop + nsz.Height - 4);

        // Vu-mètre d'entrée
        float barW = area.Width - 40, barY = ClientSize.Height - 14;
        using (var bg = new SolidBrush(Color.FromArgb(45, 48, 55)))
            g.FillRectangle(bg, 20, barY, barW, 5);
        using (var lv = new SolidBrush(Color.FromArgb(90, 150, 220)))
            g.FillRectangle(lv, 20, barY, barW * _level, 5);
    }
}
