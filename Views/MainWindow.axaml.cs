using System.Diagnostics;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Templates;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using PaxDiagnostics.Diagnostics;
using PaxDiagnostics.Platform;
using Ellipse = Avalonia.Controls.Shapes.Ellipse;
using ShapePath = Avalonia.Controls.Shapes.Path;

namespace PaxDiagnostics.Views;

/// <summary>The whole app: system information, SFC and DISM, the CBS log filter and shortcuts to Windows' own tools.</summary>
public partial class MainWindow : Window
{
    /// <summary>The CBS log can hold hundreds of thousands of matches; the list shows the newest ones, Copy and Save get all.</summary>
    public const int MaxListed = 5000;

    private static readonly IBrush[] KeywordBrushes =
    [
        new SolidColorBrush(Color.Parse("#F2666A")), // error
        new SolidColorBrush(Color.Parse("#F5B14C")), // corrupt
        new SolidColorBrush(Color.Parse("#19F58C")), // restored
        new SolidColorBrush(Color.Parse("#F5E05C")), // repaired
    ];

    private readonly bool _elevated;
    private readonly string? _startTool;
    private List<InfoSection> _sections = [];
    private CbsResult? _cbs;
    private string? _cbsPath;
    private bool _busy, _closeAnyway;
    private readonly StringBuilder _output = new();

    public MainWindow() : this(null, autoLoad: false)
    {
    }

    /// <param name="startTool">A tool to start right away (the app restarted itself as administrator for it).</param>
    /// <param name="autoLoad">Read the system information on open (the self-test fills the window itself).</param>
    /// <param name="elevated">Override the real elevation state, for screenshots.</param>
    public MainWindow(string? startTool, bool autoLoad = true, bool? elevated = null)
    {
        InitializeComponent();
        _elevated = elevated ?? Elevation.IsElevated;
        _startTool = startTool;
        Icon = AppIcon.Create();
        Ctl<ShapePath>("MonitorArt").Data = AppIcon.Monitor;
        Ctl<ShapePath>("PulseArt").Data = AppIcon.Pulse;
        Title = _elevated ? "PAX Diagnostics (Administrator)" : "PAX Diagnostics";
        // The PAX title bar replaces the system one (detach the XAML body first so it can be re-parented).
        var body = (Control)Content!;
        Content = null;
        Content = PaxStandard.PaxChrome.Wrap(this, body, Title, BrandIcon.Render(36));

        var pill = Ctl<Border>("ElevationPill");
        pill.Background = new SolidColorBrush(Color.Parse(_elevated ? "#0C2A1D" : "#0C2230"));
        pill.BorderBrush = new SolidColorBrush(Color.Parse(_elevated ? "#146B44" : "#1B5C78"));
        Ctl<Ellipse>("ElevationDot").Fill = new SolidColorBrush(Color.Parse(_elevated ? "#19F58C" : "#00C8FF"));
        Ctl<TextBlock>("ElevationText").Text = _elevated ? "Administrator" : "Standard user";
        ToolTip.SetTip(pill, _elevated
            ? "Running as administrator: SFC and DISM can run."
            : "Running as a standard user. SFC and DISM ask for administrator rights when you start them.");

        ToolButton(Ctl<Button>("SfcButton"), "Run SFC /scannow", RepairTool.Sfc);
        ToolButton(Ctl<Button>("DismCheckButton"), "CheckHealth", RepairTool.DismCheck);
        ToolButton(Ctl<Button>("DismRestoreButton"), "RestoreHealth", RepairTool.DismRestore);

        Ctl<Button>("NoticeClose").Click += (_, _) => Ctl<Border>("Notice").IsVisible = false;
        Ctl<Button>("RefreshButton").Click += async (_, _) => await LoadSystemAsync();
        Ctl<Button>("CopyInfoButton").Click += async (_, _) => await CopyAsync(SystemInfo.ToText(_sections), "System information copied to the clipboard.");
        Ctl<Button>("CopyOutputButton").Click += async (_, _) => await CopyAsync(_output.ToString(), "Output copied to the clipboard.");
        Ctl<Button>("ViewCbsButton").Click += (_, _) => Ctl<TabControl>("Tabs").SelectedIndex = 2;
        Ctl<Button>("FindCbsButton").Click += async (_, _) => await LoadCbsAsync(CbsLog.Find());
        Ctl<Button>("OpenCbsButton").Click += async (_, _) => await PickCbsAsync();
        Ctl<Button>("CopyCbsButton").Click += async (_, _) =>
        {
            if (_cbs is { } r) await CopyAsync(CbsLog.ToText(r), $"{r.Matches.Count:N0} lines copied to the clipboard.");
        };
        Ctl<Button>("SaveCbsButton").Click += async (_, _) => await SaveCbsAsync();
        Ctl<CheckBox>("SfcOnlyBox").IsCheckedChanged += async (_, _) =>
        {
            if (_cbsPath is not null && !_busy) await LoadCbsAsync(_cbsPath);
        };

        var list = Ctl<ListBox>("CbsList");
        list.ItemTemplate = new FuncDataTemplate<string>((line, _) => LogLine(line));

        foreach (var (text, file, args, tip) in new[]
                 {
                     ("Reliability Monitor", "perfmon.exe", "/rel", "Crashes, failed updates and warnings day by day"),
                     ("Event Viewer", "eventvwr.msc", "", "Windows' detailed event logs"),
                     ("Device Manager", "devmgmt.msc", "", "Hardware and drivers"),
                     ("System Information", "msinfo32.exe", "", "Windows' full hardware and software report"),
                 })
        {
            var b = new Button { Content = text };
            ToolTip.SetTip(b, tip);
            b.Click += (_, _) => OpenWindowsTool(file, args);
            Ctl<WrapPanel>("ToolButtons").Children.Add(b);
        }

        Closing += OnClosing;
        if (autoLoad)
        {
            Opened += async (_, _) =>
            {
                if (RepairTool.FromId(_startTool) is { } tool)
                {
                    Ctl<TabControl>("Tabs").SelectedIndex = 1;
                    if (_elevated) _ = RunToolAsync(tool);
                }

                await LoadSystemAsync();
            };
        }
    }

    private T Ctl<T>(string name) where T : Control => this.FindControl<T>(name)!;

    // ---------- Notices ----------

    public void ShowNotice(string text, bool good = false)
    {
        var n = Ctl<Border>("Notice");
        n.Background = new SolidColorBrush(Color.Parse(good ? "#0C2A1D" : "#2A2210"));
        n.BorderBrush = new SolidColorBrush(Color.Parse(good ? "#146B44" : "#7A5A1A"));
        var t = Ctl<TextBlock>("NoticeText");
        t.Foreground = new SolidColorBrush(Color.Parse(good ? "#7DF5BC" : "#F5C97A"));
        t.Text = text;
        n.IsVisible = true;
    }

    private async Task CopyAsync(string text, string done)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            ShowNotice("Nothing to copy yet.");
            return;
        }

        try
        {
            if (Clipboard is not { } cb) throw new InvalidOperationException("no clipboard");
            await Avalonia.Input.Platform.ClipboardExtensions.SetTextAsync(cb, text);
            ShowNotice(done, good: true);
        }
        catch (Exception ex)
        {
            ShowNotice($"Couldn't copy to the clipboard ({ex.Message}). Another program may be holding it; try again.");
        }
    }

    // ---------- System tab ----------

    private async Task LoadSystemAsync()
    {
        var refresh = Ctl<Button>("RefreshButton");
        refresh.IsEnabled = false;
        Ctl<TextBlock>("SysStatus").Text = "Reading system information…";
        var sw = Stopwatch.StartNew();
        try
        {
            var sections = await Task.Run(SystemInfo.Gather);
            ShowSystem(sections);
            var problems = sections.Count(s => s.Error is not null);
            Ctl<TextBlock>("SysStatus").Text = $"Read at {DateTime.Now:HH:mm} in {sw.Elapsed.TotalSeconds:0.0} s" +
                                               (problems > 0 ? $" · {problems} section(s) incomplete" : "");
        }
        catch (Exception ex)
        {
            Ctl<TextBlock>("SysStatus").Text = $"Couldn't read system information: {ex.Message}";
        }
        finally
        {
            refresh.IsEnabled = true;
        }
    }

    public void ShowSystem(List<InfoSection> sections, string? status = null)
    {
        _sections = sections;
        var left = Ctl<StackPanel>("SysLeft");
        var right = Ctl<StackPanel>("SysRight");
        left.Children.Clear();
        right.Children.Clear();
        int leftRows = 0, rightRows = 0;
        foreach (var s in sections)
        {
            var rows = s.Groups.Sum(g => g.Rows.Count + (g.Heading is null ? 0 : 1)) + 2;
            if (leftRows <= rightRows)
            {
                left.Children.Add(InfoCard(s));
                leftRows += rows;
            }
            else
            {
                right.Children.Add(InfoCard(s));
                rightRows += rows;
            }
        }

        if (status is not null) Ctl<TextBlock>("SysStatus").Text = status;
    }

    private static Control InfoCard(InfoSection s)
    {
        var stack = new StackPanel { Spacing = 8 };
        stack.Children.Add(new TextBlock { Text = s.Title.ToUpperInvariant(), Classes = { "label" } });
        var first = true;
        foreach (var g in s.Groups.Where(g => g.Rows.Count > 0))
        {
            if (!first && g.Heading is not null)
                stack.Children.Add(new Border { Height = 1, Background = (IBrush)Application.Current!.FindResource("BorderBrush2")!, Margin = new Thickness(0, 2) });
            first = false;
            if (g.Heading is not null)
                stack.Children.Add(new SelectableTextBlock { Text = g.Heading, FontWeight = FontWeight.SemiBold, FontSize = 14, TextWrapping = TextWrapping.Wrap });

            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("118,*"), RowSpacing = 4 };
            for (var i = 0; i < g.Rows.Count; i++)
            {
                grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                var label = new TextBlock { Text = g.Rows[i].Label, Classes = { "muted" }, FontSize = 12, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 1, 8, 0) };
                var value = new SelectableTextBlock { Text = g.Rows[i].Value, TextWrapping = TextWrapping.Wrap };
                if (g.Rows[i].Label == "Health" && g.Rows[i].Value != "Healthy")
                    value.Foreground = (IBrush)Application.Current!.FindResource("WarnBrush")!;
                Grid.SetRow(label, i);
                Grid.SetRow(value, i);
                Grid.SetColumn(value, 1);
                grid.Children.Add(label);
                grid.Children.Add(value);
            }

            stack.Children.Add(grid);
        }

        if (s.Error is not null)
            stack.Children.Add(new TextBlock
            {
                Text = (s.IsEmpty ? "Couldn't read this: " : "Partly read: ") + s.Error, TextWrapping = TextWrapping.Wrap, FontSize = 12,
                Foreground = (IBrush)Application.Current!.FindResource("WarnBrush")!,
            });
        return new Border { Classes = { "card" }, Padding = new Thickness(16), Child = stack };
    }

    // ---------- Repair tab ----------

    private void ToolButton(Button b, string text, RepairTool tool)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        content.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
        if (!_elevated)
        {
            var pill = new Border { Classes = { "pill", "warn" }, Padding = new Thickness(7, 1), Child = new TextBlock { Text = "Needs admin" } };
            content.Children.Add(pill);
            ToolTip.SetTip(b, $"{tool.Title} needs administrator rights. Click to restart PAX Diagnostics as administrator and start it.");
        }

        b.Content = content;
        b.Click += async (_, _) => await StartToolAsync(tool);
    }

    private async Task StartToolAsync(RepairTool tool)
    {
        if (_busy) return;
        if (!_elevated)
        {
            if (Elevation.RestartElevated(tool.Id) is { } problem)
            {
                ShowNotice(problem);
                return;
            }

            // The administrator copy takes over and starts the tool.
            _closeAnyway = true;
            Close();
            return;
        }

        await RunToolAsync(tool);
    }

    private (ProgressBar Bar, TextBlock Status, Border Verdict, TextBlock VerdictText) Parts(RepairTool tool) => tool == RepairTool.Sfc
        ? (Ctl<ProgressBar>("SfcProgress"), Ctl<TextBlock>("SfcStatus"), Ctl<Border>("SfcVerdict"), Ctl<TextBlock>("SfcVerdictText"))
        : (Ctl<ProgressBar>("DismProgress"), Ctl<TextBlock>("DismStatus"), Ctl<Border>("DismVerdict"), Ctl<TextBlock>("DismVerdictText"));

    private void SetBusy(bool busy)
    {
        _busy = busy;
        foreach (var n in new[] { "SfcButton", "DismCheckButton", "DismRestoreButton", "FindCbsButton", "OpenCbsButton" })
            Ctl<Button>(n).IsEnabled = !busy;
    }

    private async Task RunToolAsync(RepairTool tool)
    {
        if (_busy) return;
        SetBusy(true);
        var (bar, status, verdict, _) = Parts(tool);
        verdict.IsVisible = false;
        _output.Clear();
        Ctl<TextBox>("OutputBox").Text = "";
        Ctl<TextBlock>("OutputCaption").Text = $"{tool.Title}, started {DateTime.Now:HH:mm}";
        Ctl<Button>("ViewCbsButton").IsVisible = false;
        bar.IsVisible = true;
        bar.IsIndeterminate = true;
        var clock = Stopwatch.StartNew();
        var last = "Starting…";
        // DISM draws a text progress bar ("[=====   40.0%   ]"); show just the number.
        void Tick() => status.Text = (last.StartsWith('[') && RepairTools.ParsePercent(last) is { } pct ? $"Working: {pct:0.#}% done" : last)
                                     + $"  ·  {Elapsed(clock.Elapsed)}";
        Tick();
        var timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => Tick());
        timer.Start();
        try
        {
            var result = await RepairTools.RunAsync(tool,
                line => Dispatcher.UIThread.Post(() => AppendOutput(line)),
                s => Dispatcher.UIThread.Post(() =>
                {
                    last = s;
                    Tick();
                }),
                p => Dispatcher.UIThread.Post(() =>
                {
                    bar.IsIndeterminate = false;
                    bar.Value = p;
                }));
            // Let the output lines still queued for the UI land before the verdict.
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            timer.Stop();
            ShowToolResult(tool, result, clock.Elapsed);

            if (tool != RepairTool.DismCheck && CbsLog.Find() is { } log)
            {
                await LoadCbsAsync(log, keepBusy: true);
                Ctl<Button>("ViewCbsButton").IsVisible = _cbs is not null;
            }
        }
        catch (Exception ex)
        {
            timer.Stop();
            status.Text = $"Stopped after {Elapsed(clock.Elapsed)}.";
            SetVerdict(verdict, Parts(tool).VerdictText, $"Couldn't run {tool.Title}: {ex.Message}", Outcome.Bad);
        }
        finally
        {
            timer.Stop();
            bar.IsVisible = false;
            SetBusy(false);
        }
    }

    public void AppendOutput(string line)
    {
        _output.AppendLine(line);
        // DISM can print a lot; the box keeps the latest part, Copy output gets all of it.
        var text = _output.Length > 60_000 ? "…\n" + _output.ToString(_output.Length - 60_000, 60_000) : _output.ToString();
        var box = Ctl<TextBox>("OutputBox");
        box.Text = text;
        box.CaretIndex = text.Length;
    }

    /// <summary>Shows how a run ended; also used by the self-test to draw a finished run without running anything.</summary>
    public void ShowToolResult(RepairTool tool, RepairResult result, TimeSpan elapsed)
    {
        var (bar, status, verdict, verdictText) = Parts(tool);
        bar.IsVisible = false;
        status.Text = $"{tool.Title} finished at {DateTime.Now:HH:mm} after {Elapsed(elapsed)} (exit code {result.ExitCode}).";
        SetVerdict(verdict, verdictText, result.Verdict, result.Outcome);
    }

    public void ShowRunning(RepairTool tool, string statusText, double percent, string caption)
    {
        var (bar, status, _, _) = Parts(tool);
        bar.IsVisible = true;
        bar.IsIndeterminate = false;
        bar.Value = percent;
        status.Text = statusText;
        Ctl<TextBlock>("OutputCaption").Text = caption;
    }

    private static void SetVerdict(Border pill, TextBlock text, string message, Outcome outcome)
    {
        pill.Classes.Set("good", outcome == Outcome.Good);
        pill.Classes.Set("info", outcome == Outcome.Repaired);
        pill.Classes.Set("warn", outcome == Outcome.Warning);
        pill.Classes.Set("bad", outcome == Outcome.Bad);
        text.Text = message;
        pill.IsVisible = true;
    }

    private static string Elapsed(TimeSpan t) => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");

    private void OpenWindowsTool(string file, string args)
    {
        try
        {
            // These open fine as a standard user (Reliability Monitor never needed administrator rights).
            using var p = Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ShowNotice($"Couldn't open {file}: {ex.Message}");
        }
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (!_busy || _closeAnyway) return;
        e.Cancel = true;
        _closeAnyway = true;
        ShowNotice("A repair is still running. It carries on if you close the window, but you won't see how it ends. Close again to leave anyway.");
    }

    // ---------- CBS log tab ----------

    private async Task PickCbsAsync()
    {
        try
        {
            var start = await StorageProvider.TryGetFolderFromPathAsync(CbsLog.Folder);
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Open a CBS log",
                AllowMultiple = false,
                SuggestedStartLocation = start,
                FileTypeFilter = [new FilePickerFileType("Log files") { Patterns = ["*.log", "*.txt"] }, FilePickerFileTypes.All],
            });
            if (files.FirstOrDefault()?.TryGetLocalPath() is { } path) await LoadCbsAsync(path);
        }
        catch (Exception ex)
        {
            ShowNotice($"Couldn't open the file picker: {ex.Message}");
        }
    }

    public async Task LoadCbsAsync(string? path, bool keepBusy = false)
    {
        if (path is null)
        {
            ShowNotice($"No CBS.log found in {CbsLog.Folder}. Windows creates it the first time SFC, DISM or Windows Update runs.");
            return;
        }

        if (!keepBusy) SetBusy(true);
        var caption = Ctl<TextBlock>("CbsPath");
        caption.Text = $"Reading {path}…";
        try
        {
            var result = await CbsLog.FilterAsync(path, Ctl<CheckBox>("SfcOnlyBox").IsChecked == true);
            ShowCbs(result);
        }
        catch (UnauthorizedAccessException)
        {
            caption.Text = path;
            ShowNotice("Windows didn't allow this log to be read. Open a copy, or run SFC from the Repair tab (as administrator) and the log is read afterwards.");
        }
        catch (FileNotFoundException)
        {
            caption.Text = path;
            ShowNotice($"{Path.GetFileName(path)} isn't there any more.");
        }
        catch (IOException ex)
        {
            caption.Text = path;
            ShowNotice($"{Path.GetFileName(path)} is in use and couldn't be read ({ex.Message.Trim()}). Try again in a moment.");
        }
        finally
        {
            if (!keepBusy) SetBusy(false);
        }
    }

    public void ShowCbs(CbsResult r)
    {
        _cbs = r;
        _cbsPath = r.Path;
        Ctl<TextBlock>("CbsPath").Text = r.Path;
        var counts = Ctl<WrapPanel>("CbsCounts");
        counts.Children.Clear();
        counts.Children.Add(new Border
        {
            Classes = { "pill" },
            Child = new TextBlock { Text = $"{r.LinesRead:N0} lines read · {r.Matches.Count:N0} matching", Foreground = (IBrush)Application.Current!.FindResource("TextBrush")! },
        });
        for (var i = 0; i < CbsLog.Keywords.Length; i++)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            row.Children.Add(new Ellipse { Width = 8, Height = 8, Fill = KeywordBrushes[i], VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(new TextBlock { Text = $"{CbsLog.Keywords[i]}  {r.Counts[i]:N0}", FontSize = 11, FontWeight = FontWeight.SemiBold });
            counts.Children.Add(new Border { Classes = { "pill" }, Child = row });
        }

        if (r.Matches.Count > MaxListed)
            counts.Children.Add(new Border
            {
                Classes = { "pill", "info" },
                Child = new TextBlock { Text = $"Showing the newest {MaxListed:N0}; Copy and Save include all" },
            });

        var shown = r.Matches.Count > MaxListed ? r.Matches.Skip(r.Matches.Count - MaxListed).ToList() : r.Matches.ToList();
        Ctl<ListBox>("CbsList").ItemsSource = shown;
        var empty = Ctl<TextBlock>("CbsEmpty");
        empty.IsVisible = shown.Count == 0;
        empty.Text = "No lines mention an error, corruption or a repair. That's a good sign.";
        Ctl<Button>("CopyCbsButton").IsEnabled = r.Matches.Count > 0;
        Ctl<Button>("SaveCbsButton").IsEnabled = r.Matches.Count > 0;
    }

    private static Control LogLine(string? line)
    {
        var block = new TextBlock { Classes = { "mono" }, TextWrapping = TextWrapping.Wrap };
        if (line is null) return block;
        foreach (var (text, keyword) in CbsLog.Segments(line))
        {
            if (keyword < 0)
                block.Inlines!.Add(new Run(text));
            else
                block.Inlines!.Add(new Run(text) { Foreground = KeywordBrushes[keyword], FontWeight = FontWeight.Bold });
        }

        return block;
    }

    private async Task SaveCbsAsync()
    {
        if (_cbs is not { } r) return;
        try
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save CBS log entries",
                SuggestedFileName = $"CBS_Results_{DateTime.Now:yyyyMMdd_HHmmss}.txt",
                DefaultExtension = "txt",
                FileTypeChoices = [new FilePickerFileType("Text file") { Patterns = ["*.txt"] }],
            });
            if (file is null) return;
            await using (var stream = await file.OpenWriteAsync())
            {
                // Replacing a longer file: don't leave its tail behind.
                if (stream.CanSeek) stream.SetLength(0);
                await CbsLog.SaveAsync(r, stream);
            }

            ShowNotice($"Saved {r.Matches.Count:N0} lines to {file.TryGetLocalPath() ?? file.Name}.", good: true);
        }
        catch (Exception ex)
        {
            ShowNotice($"Couldn't save the results: {ex.Message}");
        }
    }
}
