using System.Numerics;
using ImGuiNET;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.OpenGL.Extensions.ImGui;
using Silk.NET.Windowing;
using SkyPass.Core.Time;
using SkyPass.Data;
using SkyPass.Presenters;
using SkyPass.App.Views;

namespace SkyPass.App;

/// <summary>視窗生命週期與每幀流程：Update Presenter → 繪製各頁 → 轉送輸入事件。不含任何計算。</summary>
internal sealed class AppHost : IDisposable
{
    private const string ChineseFontPath = @"C:\Windows\Fonts\msjh.ttc";
    private const float FontSize = 18f;

    private readonly AppOptions _options;
    private readonly IWindow _window;
    private readonly MainPresenter _main;
    private readonly TimeProvider _clock;
    private readonly UiState _ui = new();
    private GL? _gl;
    private IInputContext? _input;
    private ImGuiController? _imgui;
    private ScreenshotScript? _screenshots;

    public AppHost(AppOptions options)
    {
        _options = options;
        _clock = options.Now is { } now ? new ShiftedTimeProvider(now) : TimeProvider.System;
        _main = new MainPresenter(new SqliteTleCatalogStore(options.Paths.DatabasePath), new JsonSettingsStore(options.Paths.SettingsPath), _clock);
        _window = Window.Create(WindowOptions.Default with
        {
            Size = new Vector2D<int>(1440, 900),
            Title = "SkyPass 過境預報台",
            API = new GraphicsAPI(ContextAPI.OpenGL, ContextProfile.Core, ContextFlags.ForwardCompatible, new APIVersion(3, 3)),
            VSync = true,
        });
        _window.Load += OnLoad;
        _window.Render += OnRender;
        _window.FramebufferResize += size => _gl?.Viewport(size);
        _window.FileDrop += OnFileDrop;
        _window.Closing += ReleaseGraphics;
    }

    public void Run() => _window.Run();

    public void Dispose() => _window.Dispose();

    /// <summary>GL 資源必須在 context 仍有效時（視窗關閉中）釋放，Run 返回後 context 已不存在。</summary>
    private void ReleaseGraphics()
    {
        _imgui?.Dispose();
        _input?.Dispose();
        _gl?.Dispose();
        _imgui = null;
        _input = null;
        _gl = null;
    }

    private void OnLoad()
    {
        _gl = _window.CreateOpenGL();
        _input = _window.CreateInput();
        _imgui = new ImGuiController(_gl, _window, _input, () =>
        {
            var io = ImGui.GetIO();
            io.ConfigFlags |= ImGuiConfigFlags.NavEnableKeyboard;
            if (File.Exists(ChineseFontPath))
            {
                // 中文完整字集不含數學符號與幾何圖形，另外補上介面用到的字元
                unsafe
                {
                    var builder = new ImFontGlyphRangesBuilderPtr(ImGuiNative.ImFontGlyphRangesBuilder_ImFontGlyphRangesBuilder());
                    builder.AddRanges(io.Fonts.GetGlyphRangesChineseFull());
                    builder.AddText("≥≤−→←■□●○…–—°±");
                    builder.BuildRanges(out var ranges);
                    io.Fonts.AddFontFromFileTTF(ChineseFontPath, FontSize, null, ranges.Data);
                }
            }
            else
            {
                Ui.Chinese = false;
                io.Fonts.AddFontDefault();
            }
        });
        Ui.ApplyStyle();

        _main.Initialize();
        if (!Ui.Chinese)
        {
            _main.Diagnostics.Add(new DiagnosticItem(DiagnosticSeverity.Warning, "介面", $"找不到中文字型 {ChineseFontPath}，介面改用英文；中文訊息可能無法顯示。"));
        }

        if (_options.ImportPath is { } import)
        {
            _main.ImportTleFileAsync(import).GetAwaiter().GetResult();
        }

        if (_options.ScreenshotDirectory is { } dir)
        {
            _screenshots = new ScreenshotScript(dir);
            _ui.ExportPath = "SkyPass-passes.csv"; // 截圖不顯示使用者的實際資料夾路徑
        }
    }

    private void OnFileDrop(string[] paths)
    {
        foreach (var path in paths)
        {
            _ = _main.ImportTleFileAsync(path);
        }

        _ui.Page = Page.Catalog;
    }

    private void OnRender(double delta)
    {
        _main.Update();
        _screenshots?.BeforeFrame(_main, _ui);

        _imgui!.Update((float)delta);
        _gl!.ClearColor(0.07f, 0.08f, 0.09f, 1f);
        _gl.Clear(ClearBufferMask.ColorBufferBit);

        var now = JulianDate.FromUtc(_clock.GetUtcNow().UtcDateTime);
        TickLiveTracking();
        DrawRoot(now);
        _imgui.Render();

        if (_screenshots?.AfterFrame(_gl, _window.FramebufferSize) == true)
        {
            _window.Close();
        }
    }

    /// <summary>即時追蹤每秒更新一次（換秒時呼叫 Tick）；追蹤的衛星改變時立即更新。</summary>
    private void TickLiveTracking()
    {
        var second = _clock.GetUtcNow().ToUnixTimeSeconds();
        var satellite = _main.LiveTracking.Satellite;
        var cacheVersion = _main.LiveTracking.CacheVersion;
        if (second != _ui.LastLiveTickSecond || !ReferenceEquals(satellite, _ui.LastLiveSatellite) || cacheVersion != _ui.LastLiveCacheVersion)
        {
            _ui.LastLiveTickSecond = second;
            _ui.LastLiveSatellite = satellite;
            _ui.LastLiveCacheVersion = cacheVersion;
            _main.LiveTracking.Tick();
        }
    }

    private void DrawRoot(JulianDate now)
    {
        var viewport = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(viewport.WorkPos);
        ImGui.SetNextWindowSize(viewport.WorkSize);
        ImGui.Begin("##root", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoBringToFrontOnFocus | ImGuiWindowFlags.NoSavedSettings);

        DrawNavigation();
        ImGui.SameLine();
        var statusHeight = ImGui.GetFrameHeightWithSpacing() + 6;
        ImGui.BeginChild("##content", new Vector2(0, -statusHeight));
        switch (_ui.Page)
        {
            case Page.Passes:
                PassesPage.Draw(_main, _ui, now);
                break;
            case Page.LiveTracking:
                LiveTrackingPage.Draw(_main, _ui, now);
                break;
            case Page.Catalog:
                CatalogPage.Draw(_main, _ui);
                break;
            case Page.Settings:
                SettingsPage.Draw(_main, _ui);
                break;
            case Page.Diagnostics:
                DiagnosticsPage.Draw(_main);
                break;
        }

        ImGui.EndChild();
        StatusBar.Draw(_main, now);
        ImGui.End();
    }

    private void DrawNavigation()
    {
        ImGui.BeginChild("##nav", new Vector2(150, -ImGui.GetFrameHeightWithSpacing() - 6), ImGuiChildFlags.Border);
        ImGui.TextColored(Ui.Accent, "SkyPass");
        ImGui.TextDisabled(Ui.T("過境預報台", "Pass Predictor"));
        ImGui.Separator();
        NavItem(Page.Passes, Ui.T("過境預報", "Passes"));
        NavItem(Page.LiveTracking, Ui.T("即時追蹤", "Live tracking"));
        NavItem(Page.Catalog, Ui.T("軌道資料", "Orbital data"));
        NavItem(Page.Settings, Ui.T("設定", "Settings"));
        var errors = _main.Diagnostics.CountOf(DiagnosticSeverity.Error);
        NavItem(Page.Diagnostics, errors > 0 ? $"{Ui.T("診斷", "Diagnostics")} ({errors})" : Ui.T("診斷", "Diagnostics"));
        ImGui.EndChild();
    }

    private void NavItem(Page page, string label)
    {
        if (ImGui.Selectable(label, _ui.Page == page, ImGuiSelectableFlags.None, new Vector2(0, 28)))
        {
            _ui.Page = page;
        }
    }
}

/// <summary>跨幀保存的介面狀態（輸入框內容、目前頁面等）。</summary>
internal sealed class UiState
{
    public Page Page { get; set; } = Page.Passes;

    public long LastLiveTickSecond { get; set; }

    public Core.Tle.TleSet? LastLiveSatellite { get; set; }

    public int LastLiveCacheVersion { get; set; }

    public string FilterText = "";

    public float FilterMinElevation;

    public bool FilterVisibleOnly;

    public string ImportPath = "";

    public string ExportPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "SkyPass-passes.csv");

    public string? ExportMessage { get; set; }

    public string NewSiteName = "";

    public double NewSiteLat = 25.0;

    public double NewSiteLon = 121.5;

    public double NewSiteHeightM;

    public float? MaskEdit { get; set; }

    public float? DaysEdit { get; set; }
}
