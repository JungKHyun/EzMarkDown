using System.IO.Pipes;
using System.Reflection;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace MDDesk;

internal static class Program
{
    private const string MutexName = "MDDesk_SingleInstance_Mutex_Session";
    private const string PipeName = "MDDesk_OpenFile_Pipe_Session";

    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        var initialFile = args.FirstOrDefault();
        using var mutex = new Mutex(true, MutexName, out var isFirstInstance);

        if (!isFirstInstance)
        {
            // 이미 실행 중인 프로세스가 있으면, 파일 경로 또는 새 창 요청 신호를 전달하고 즉시 종료
            var message = (!string.IsNullOrWhiteSpace(initialFile) && File.Exists(initialFile))
                ? Path.GetFullPath(initialFile)
                : "__NEW_WINDOW__";

            SendToRunningInstance(message);
            return;
        }

        try
        {
            var appContext = new MDDeskApplicationContext();
            appContext.OpenWindow(initialFile);
            StartPipeServer(appContext);
            Application.Run(appContext);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"MD Desk 실행 중 오류가 발생했습니다.\n\n{ex.Message}",
                "MD Desk 오류",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private static void SendToRunningInstance(string message)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(1500);
            using var writer = new StreamWriter(client, new System.Text.UTF8Encoding(false)) { AutoFlush = true };
            writer.WriteLine(message);
        }
        catch
        {
            // 통신 실패 시 무시
        }
    }

    private static void StartPipeServer(MDDeskApplicationContext context)
    {
        var thread = new Thread(async () =>
        {
            while (true)
            {
                try
                {
                    using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    await server.WaitForConnectionAsync();
                    using var reader = new StreamReader(server, System.Text.Encoding.UTF8);
                    var line = await reader.ReadLineAsync();
                    if (!string.IsNullOrWhiteSpace(line))
                    {
                        var command = line.Trim();
                        context.PostOnUiThread(() =>
                        {
                            if (command == "__NEW_WINDOW__")
                            {
                                context.OpenWindow(null);
                            }
                            else
                            {
                                context.OpenWindow(command);
                            }
                        });
                    }
                }
                catch
                {
                    Thread.Sleep(300);
                }
            }
        })
        {
            IsBackground = true
        };
        thread.Start();
    }
}

internal sealed class MDDeskApplicationContext : ApplicationContext
{
    private readonly SynchronizationContext? uiContext = SynchronizationContext.Current;
    private readonly List<MainForm> openForms = new();
    private static CoreWebView2Environment? sharedEnvironment;
    private static readonly SemaphoreSlim envLock = new(1, 1);

    public void PostOnUiThread(Action action)
    {
        if (uiContext != null)
        {
            uiContext.Post(_ => action(), null);
        }
        else
        {
            var form = openForms.FirstOrDefault(f => !f.IsDisposed);
            if (form != null)
            {
                form.BeginInvoke(action);
            }
            else
            {
                action();
            }
        }
    }

    public static async Task<CoreWebView2Environment> GetSharedEnvironmentAsync()
    {
        if (sharedEnvironment != null) return sharedEnvironment;
        await envLock.WaitAsync();
        try
        {
            if (sharedEnvironment != null) return sharedEnvironment;
            var dataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MDDesk", "WebView2");
            try
            {
                var options = new CoreWebView2EnvironmentOptions(
                    "--disable-background-networking --disable-component-update --disable-default-apps " +
                    "--disable-extensions --disable-sync --no-first-run");
                sharedEnvironment = await CoreWebView2Environment.CreateAsync(null, dataFolder, options);
            }
            catch
            {
                sharedEnvironment = await CoreWebView2Environment.CreateAsync(null, dataFolder);
            }
            return sharedEnvironment;
        }
        finally
        {
            envLock.Release();
        }
    }

    public MainForm OpenWindow(string? filePath = null)
    {
        string? targetPath = null;
        if (!string.IsNullOrWhiteSpace(filePath) && File.Exists(filePath))
        {
            targetPath = Path.GetFullPath(filePath);

            // 1. 이미 열려 있는 창 중에 동일한 파일이 있다면 그 창을 활성화
            var existing = openForms.FirstOrDefault(f =>
                !f.IsDisposed && string.Equals(f.CurrentFilePath, targetPath, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                existing.BringToFrontAndFocus();
                return existing;
            }

            // 2. 현재 창이 딱 1개이고 아직 아무 내용도 로드/작성되지 않은 빈 창이라면 그 창에 로드
            if (openForms.Count == 1 && openForms[0].IsBlankAndClean)
            {
                openForms[0].BringToFrontAndLoadFile(targetPath);
                return openForms[0];
            }
        }

        // 3. 그 외에는 기존 창을 그대로 유지하고 '새 창'으로 엽니다 (멀티 창)
        Point? cascadeLocation = null;
        var active = Form.ActiveForm as MainForm ?? openForms.LastOrDefault(f => !f.IsDisposed);
        if (active != null)
        {
            var screen = Screen.FromControl(active).WorkingArea;
            var nextX = active.Location.X + 30;
            var nextY = active.Location.Y + 30;
            if (nextX + active.Width > screen.Right || nextY + active.Height > screen.Bottom)
            {
                nextX = screen.Left + 40;
                nextY = screen.Top + 40;
            }
            cascadeLocation = new Point(nextX, nextY);
        }

        var form = new MainForm(this, targetPath, cascadeLocation);
        openForms.Add(form);

        form.FormClosed += (_, _) =>
        {
            openForms.Remove(form);
            if (openForms.Count == 0)
            {
                ExitThread();
            }
        };

        form.Show();
        return form;
    }
}

internal sealed class MainForm : Form
{
    private readonly MDDeskApplicationContext appContext;
    private readonly WebView2 webView = new() { Dock = DockStyle.Fill };
    private readonly string? startupPath;
    private string? currentPath;
    private bool isDirty;
    private bool hasContent;
    private bool pageReady;

    public string? CurrentFilePath => currentPath;
    public bool IsBlankAndClean => string.IsNullOrEmpty(currentPath) && !isDirty && !hasContent;

    public MainForm(MDDeskApplicationContext context, string? path, Point? initialLocation = null)
    {
        appContext = context;
        startupPath = IsMarkdownFile(path) ? Path.GetFullPath(path!) : null;
        currentPath = startupPath;
        hasContent = startupPath != null;

        Text = startupPath != null ? $"{Path.GetFileName(startupPath)} - MD Desk" : "MD Desk";
        Width = 1200;
        Height = 820;
        MinimumSize = new Size(720, 480);
        StartPosition = FormStartPosition.CenterScreen;

        if (initialLocation.HasValue)
        {
            StartPosition = FormStartPosition.Manual;
            Location = initialLocation.Value;
        }
        else
        {
            RestoreWindowBounds();
        }

        Controls.Add(webView);
        Shown += async (_, _) => await InitializeWebViewAsync();
        FormClosing += (_, _) => SaveWindowBounds();
    }

    public void BringToFrontAndFocus()
    {
        if (WindowState == FormWindowState.Minimized)
            WindowState = FormWindowState.Normal;

        BringToFront();
        Activate();
    }

    public void BringToFrontAndLoadFile(string path)
    {
        BringToFrontAndFocus();

        if (IsMarkdownFile(path))
        {
            _ = LoadFileAsync(path);
        }
    }

    private void SaveWindowBounds()
    {
        try
        {
            if (WindowState == FormWindowState.Minimized) return;
            var settingsFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MDDesk");
            Directory.CreateDirectory(settingsFolder);
            var settingsPath = Path.Combine(settingsFolder, "window_state.json");
            var state = new
            {
                X = RestoreBounds.X,
                Y = RestoreBounds.Y,
                Width = RestoreBounds.Width,
                Height = RestoreBounds.Height,
                Maximized = WindowState == FormWindowState.Maximized
            };
            File.WriteAllText(settingsPath, JsonSerializer.Serialize(state));
        }
        catch { }
    }

    private void RestoreWindowBounds()
    {
        try
        {
            var settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MDDesk", "window_state.json");
            if (!File.Exists(settingsPath)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(settingsPath));
            var root = doc.RootElement;
            var x = root.GetProperty("X").GetInt32();
            var y = root.GetProperty("Y").GetInt32();
            var w = root.GetProperty("Width").GetInt32();
            var h = root.GetProperty("Height").GetInt32();
            var max = root.GetProperty("Maximized").GetBoolean();

            var rect = new Rectangle(x, y, w, h);
            if (Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(rect)) && w >= 600 && h >= 400)
            {
                StartPosition = FormStartPosition.Manual;
                Bounds = rect;
                if (max) WindowState = FormWindowState.Maximized;
            }
        }
        catch { }
    }

    private async Task InitializeWebViewAsync()
    {
        try
        {
            var environment = await MDDeskApplicationContext.GetSharedEnvironmentAsync();
            await webView.EnsureCoreWebView2Async(environment);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Microsoft Edge WebView2 초기화에 실패했습니다.\n\n상세 내용: {ex.Message}\n\nWebView2 런타임 복구 또는 재설치가 필요할 수 있습니다.",
                "MD Desk 오류",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            Close();
            return;
        }

        var settings = webView.CoreWebView2.Settings;
        settings.AreDevToolsEnabled = false;
        settings.AreDefaultContextMenusEnabled = false;
        settings.AreBrowserAcceleratorKeysEnabled = true;
        settings.IsStatusBarEnabled = false;
        settings.IsZoomControlEnabled = true;
        settings.IsPasswordAutosaveEnabled = false;
        settings.IsGeneralAutofillEnabled = false;

        webView.CoreWebView2.WebMessageReceived += WebMessageReceived;
        webView.CoreWebView2.NavigationCompleted += async (_, _) =>
        {
            pageReady = true;
            if (startupPath is not null)
            {
                await LoadFileAsync(startupPath);
            }
        };
        webView.CoreWebView2.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) &&
                (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        };

        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("MDDesk.index.html")
            ?? throw new InvalidOperationException("내장된 index.html을 찾을 수 없습니다.");
        using var reader = new StreamReader(stream);
        webView.NavigateToString(await reader.ReadToEndAsync());
    }

    private async void WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var message = JsonDocument.Parse(e.WebMessageAsJson);
            var root = message.RootElement;
            var type = root.GetProperty("type").GetString();

            if (type == "open")
            {
                OpenFile();
            }
            else if (type == "openNewWindow")
            {
                OpenFileInNewWindow();
            }
            else if (type == "newWindow")
            {
                appContext.OpenWindow(null);
            }
            else if (type == "dirty")
            {
                isDirty = root.TryGetProperty("dirty", out var d) && d.GetBoolean();
                hasContent = true;
            }
            else if (type == "clearPath")
            {
                currentPath = null;
                hasContent = false;
                isDirty = false;
            }
            else if (type == "save")
            {
                var content = root.GetProperty("content").GetString() ?? string.Empty;
                var name = root.GetProperty("name").GetString() ?? "문서.md";
                var saveAs = root.TryGetProperty("saveAs", out var value) && value.GetBoolean();
                await SaveFileAsync(content, name, saveAs);
            }
            else if (type == "openRelative")
            {
                var relPath = root.GetProperty("path").GetString();
                var hash = root.TryGetProperty("hash", out var h) ? h.GetString() : null;
                if (!string.IsNullOrWhiteSpace(relPath) && currentPath is not null)
                {
                    var dir = Path.GetDirectoryName(currentPath) ?? "";
                    var full = Path.GetFullPath(Path.Combine(dir, relPath));
                    if (File.Exists(full) && IsMarkdownFile(full))
                    {
                        await LoadFileAsync(full, hash);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "MD Desk", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void OpenFile()
    {
        using var dialog = new OpenFileDialog
        {
            Filter = "Markdown 파일|*.md;*.markdown;*.mdown|텍스트 파일|*.txt|모든 파일|*.*",
            Title = "Markdown 파일 열기",
            CheckFileExists = true
        };

        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            // 현재 창에 이미 열린 파일이 있거나 수정된 상태라면 기존 창 내용을 보존하고 새 창으로 열기
            if (!IsBlankAndClean)
            {
                appContext.OpenWindow(dialog.FileName);
            }
            else
            {
                _ = LoadFileAsync(dialog.FileName);
            }
        }
    }

    private void OpenFileInNewWindow()
    {
        using var dialog = new OpenFileDialog
        {
            Filter = "Markdown 파일|*.md;*.markdown;*.mdown|텍스트 파일|*.txt|모든 파일|*.*",
            Title = "새 창에서 Markdown 파일 열기",
            CheckFileExists = true
        };

        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            appContext.OpenWindow(dialog.FileName);
        }
    }

    public async Task LoadFileAsync(string path, string? targetHeading = null)
    {
        if (!pageReady || !File.Exists(path)) return;
        var text = await File.ReadAllTextAsync(path);
        currentPath = Path.GetFullPath(path);
        hasContent = true;
        isDirty = false;
        var name = Path.GetFileName(path);
        Text = $"{name} - MD Desk";
        await webView.ExecuteScriptAsync(
            $"window.mdDeskOpenText({JsonSerializer.Serialize(name)}, {JsonSerializer.Serialize(text)});");

        if (!string.IsNullOrWhiteSpace(targetHeading))
        {
            await Task.Delay(120);
            await webView.ExecuteScriptAsync(
                $"if (typeof window.scrollToHeading === 'function') window.scrollToHeading({JsonSerializer.Serialize(targetHeading)}, {JsonSerializer.Serialize(targetHeading)});");
        }
    }

    private async Task SaveFileAsync(string content, string suggestedName, bool saveAs)
    {
        var path = saveAs ? null : currentPath;
        if (path is null)
        {
            using var dialog = new SaveFileDialog
            {
                Filter = "Markdown 파일|*.md|텍스트 파일|*.txt",
                Title = "Markdown 파일 저장",
                FileName = Path.GetFileName(suggestedName),
                DefaultExt = "md",
                AddExtension = true
            };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            path = dialog.FileName;
        }

        await File.WriteAllTextAsync(path, content, new System.Text.UTF8Encoding(false));
        currentPath = Path.GetFullPath(path);
        hasContent = true;
        isDirty = false;
        var name = Path.GetFileName(path);
        Text = $"{name} - MD Desk";
        await webView.ExecuteScriptAsync($"window.mdDeskSaved({JsonSerializer.Serialize(name)});");
    }

    private static bool IsMarkdownFile(string? path) =>
        !string.IsNullOrWhiteSpace(path) && File.Exists(path) &&
        new[] { ".md", ".markdown", ".mdown", ".txt" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
}
