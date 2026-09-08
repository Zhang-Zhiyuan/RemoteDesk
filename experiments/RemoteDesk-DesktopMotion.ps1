param(
    [ValidateRange(2, 3600)]
    [int]$Seconds = 45,

    [string]$DeviceName = '\\.\DISPLAY5',

    [string]$ResultPath = ''
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
Add-Type `
    -ReferencedAssemblies @(
        'System.Drawing',
        'System.Windows.Forms',
        'System.Core'
    ) `
    -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

public sealed class RemoteDeskDesktopMotionForm : Form
{
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    [DllImport("winmm.dll", ExactSpelling = true)]
    private static extern uint timeBeginPeriod(uint periodMilliseconds);

    [DllImport("winmm.dll", ExactSpelling = true)]
    private static extern uint timeEndPeriod(uint periodMilliseconds);

    private readonly int durationSeconds;
    private readonly string deviceName;
    private readonly string resultPath;
    private readonly Screen targetScreen;
    private readonly System.Windows.Forms.Timer timer;
    private readonly Stopwatch stopwatch = new Stopwatch();
    private readonly List<double> tickIntervals = new List<double>();
    private readonly Bitmap checker;
    private readonly Font font9;
    private readonly Font font11;
    private readonly Font font18;
    private readonly Pen gridPen;
    private readonly Pen motionPen;
    private readonly Brush blackBrush;
    private readonly Brush blueBrush;
    private readonly bool timerResolutionAcquired;
    private long lastTickAt;
    private int ticks;

    private RemoteDeskDesktopMotionForm(
        int seconds,
        string requestedDeviceName,
        string requestedResultPath)
    {
        durationSeconds = seconds;
        deviceName = requestedDeviceName;
        resultPath = requestedResultPath ?? String.Empty;
        targetScreen = Screen.AllScreens.FirstOrDefault(
            screen => String.Equals(
                screen.DeviceName,
                deviceName,
                StringComparison.OrdinalIgnoreCase));
        if (targetScreen == null)
        {
            throw new InvalidOperationException(
                "Display target was not found: " + deviceName);
        }

        timerResolutionAcquired = timeBeginPeriod(1) == 0;
        Rectangle bounds = targetScreen.Bounds;
        int cardWidth = Math.Min(820, Math.Max(520, bounds.Width - 80));
        int cardHeight = Math.Min(560, Math.Max(360, bounds.Height - 80));
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = Color.White;
        Bounds = new Rectangle(
            bounds.Left + 30,
            bounds.Top + 30,
            cardWidth,
            cardHeight);
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Text = "RemoteDesk 4K clarity and motion gate";
        TopMost = true;
        DoubleBuffered = true;

        checker = new Bitmap(160, 160);
        for (int y = 0; y < checker.Height; y++)
        {
            for (int x = 0; x < checker.Width; x++)
            {
                checker.SetPixel(
                    x,
                    y,
                    ((x + y) & 1) == 0
                        ? Color.Black
                        : Color.White);
            }
        }

        font9 = new Font(
            "Segoe UI",
            9,
            FontStyle.Regular,
            GraphicsUnit.Point);
        font11 = new Font(
            "Segoe UI",
            11,
            FontStyle.Regular,
            GraphicsUnit.Point);
        font18 = new Font(
            "Segoe UI",
            18,
            FontStyle.Bold,
            GraphicsUnit.Point);
        gridPen = new Pen(Color.FromArgb(80, 80, 80), 1);
        motionPen = new Pen(Color.Red, 2);
        blackBrush = new SolidBrush(Color.Black);
        blueBrush = new SolidBrush(Color.RoyalBlue);

        // Use a compiled managed callback rather than a PowerShell event
        // action. The previous script callback was serviced at only about
        // 38 Hz even with timeBeginPeriod(1), which contaminated a 30 FPS
        // capture gate. An 8 ms request stays comfortably above both the
        // 30 FPS encoder and a 60 Hz desktop while normal WM_TIMER
        // coalescing keeps it inexpensive.
        timer = new System.Windows.Forms.Timer();
        timer.Interval = 8;
        timer.Tick += OnMotionTick;
        Shown += OnMotionShown;
    }

    public static string Run(
        int seconds,
        string deviceName,
        string resultPath)
    {
        SetProcessDpiAwarenessContext(new IntPtr(-4));
        Application.EnableVisualStyles();
        using (var form = new RemoteDeskDesktopMotionForm(
            seconds,
            deviceName,
            resultPath))
        {
            Application.Run(form);
            return form.BuildResult();
        }
    }

    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        base.OnPaint(eventArgs);
        Graphics graphics = eventArgs.Graphics;
        graphics.Clear(Color.White);
        graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
        graphics.PixelOffsetMode = PixelOffsetMode.Half;
        graphics.DrawString(
            "RemoteDesk native-resolution clarity gate",
            font18,
            blueBrush,
            18,
            14);
        graphics.DrawString(
            "The 9 pt and 11 pt lines must remain readable; the 1 px checker must not become a uniform blur.",
            font9,
            blackBrush,
            20,
            62);
        graphics.DrawString(
            "ABCDEFGHIJKLMNOPQRSTUVWXYZ 0123456789  中文细字：远程桌面清晰度",
            font11,
            blackBrush,
            20,
            88);
        graphics.DrawImageUnscaled(checker, 20, 125);

        for (int x = 205; x <= 525; x += 8)
        {
            graphics.DrawLine(gridPen, x, 125, x, 285);
        }
        for (int y = 125; y <= 285; y += 8)
        {
            graphics.DrawLine(gridPen, 205, y, 525, y);
        }

        int phase = ticks % 320;
        int motionX = 205 + phase;
        graphics.DrawLine(motionPen, motionX, 125, motionX, 285);
        graphics.FillRectangle(
            blueBrush,
            20 + (ticks % Math.Max(1, ClientSize.Width - 100)),
            325,
            64,
            36);
        graphics.DrawString(
            String.Format(
                CultureInfo.InvariantCulture,
                "tick={0} elapsed={1:F2}s display={2} source={3}x{4}",
                ticks,
                stopwatch.Elapsed.TotalSeconds,
                targetScreen.DeviceName,
                targetScreen.Bounds.Width,
                targetScreen.Bounds.Height),
            font9,
            blackBrush,
            20,
            385);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            timer.Dispose();
            checker.Dispose();
            font9.Dispose();
            font11.Dispose();
            font18.Dispose();
            gridPen.Dispose();
            motionPen.Dispose();
            blackBrush.Dispose();
            blueBrush.Dispose();
            if (timerResolutionAcquired)
            {
                timeEndPeriod(1);
            }
        }

        base.Dispose(disposing);
    }

    private void OnMotionShown(object sender, EventArgs eventArgs)
    {
        stopwatch.Restart();
        timer.Start();
    }

    private void OnMotionTick(object sender, EventArgs eventArgs)
    {
        long tickAt = Stopwatch.GetTimestamp();
        if (lastTickAt != 0)
        {
            tickIntervals.Add(
                (tickAt - lastTickAt) *
                1000.0 /
                Stopwatch.Frequency);
        }

        lastTickAt = tickAt;
        ticks++;
        Invalidate();
        if (stopwatch.Elapsed.TotalSeconds >= durationSeconds)
        {
            timer.Stop();
            Close();
        }
    }

    private string BuildResult()
    {
        double[] ordered = tickIntervals
            .OrderBy(value => value)
            .ToArray();
        double p50 = Percentile(ordered, 0.50);
        double p95 = Percentile(ordered, 0.95);
        double maximum =
            ordered.Length == 0
                ? 0
                : ordered[ordered.Length - 1];
        string result = String.Format(
            CultureInfo.InvariantCulture,
            "MOTION_RESULT device={0} source={1}x{2} ticks={3} seconds={4:F3} timerHz={5:F2} tickP50Ms={6:F2} tickP95Ms={7:F2} tickMaxMs={8:F2} timerResolution1ms={9}",
            targetScreen.DeviceName,
            targetScreen.Bounds.Width,
            targetScreen.Bounds.Height,
            ticks,
            stopwatch.Elapsed.TotalSeconds,
            ticks / Math.Max(0.001, stopwatch.Elapsed.TotalSeconds),
            p50,
            p95,
            maximum,
            timerResolutionAcquired);
        if (!String.IsNullOrWhiteSpace(resultPath))
        {
            File.WriteAllText(
                resultPath,
                result + Environment.NewLine,
                new UTF8Encoding(false));
        }

        return result;
    }

    private static double Percentile(
        double[] ordered,
        double percentile)
    {
        if (ordered.Length == 0)
        {
            return 0;
        }

        int index = (int)Math.Ceiling(
            Math.Max(0, Math.Min(1, percentile)) *
            ordered.Length) - 1;
        return ordered[
            Math.Max(0, Math.Min(ordered.Length - 1, index))];
    }
}
'@

[RemoteDeskDesktopMotionForm]::Run(
    $Seconds,
    $DeviceName,
    $ResultPath)
