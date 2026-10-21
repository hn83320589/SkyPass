using Silk.NET.Maths;
using Silk.NET.OpenGL;
using SkyPass.Presenters;

namespace SkyPass.App;

/// <summary>--screenshot：等預報完成後選一個過境，依序切換各頁並擷取畫面為 PNG。</summary>
internal sealed class ScreenshotScript(string directory)
{
    private static readonly (Page Page, string File)[] Shots =
    [
        (Page.Passes, "passes.png"),
        (Page.LiveTracking, "live-tracking.png"),
        (Page.Catalog, "catalog.png"),
        (Page.Settings, "settings.png"),
        (Page.Diagnostics, "diagnostics.png"),
    ];

    private const int SettleFrames = 4;

    private int _frame;
    private int _selectedAtFrame = -1;
    private int _shot = -1;
    private int _framesOnShot;

    public void BeforeFrame(MainPresenter main, UiState ui)
    {
        _frame++;
        if (_selectedAtFrame < 0)
        {
            if (_frame < 30 || main.Prediction.State.Status != PredictionStatus.Completed)
            {
                return;
            }

            // 優先選正在進行中的過境（天空圖有目前位置、即時追蹤有倒數），否則選仰角高且可目視的
            var rows = main.PassTable.Rows;
            var pick = rows.FirstOrDefault(r => r.Pass.StartedBeforeWindow && r.Pass.MaxElevationDeg > 20)
                       ?? rows.Where(r => r.Pass.MaxElevationDeg > 30).OrderByDescending(r => r.Pass.VisibleIntervals.Count).FirstOrDefault()
                       ?? rows.FirstOrDefault();
            main.PassTable.Select(pick);
            _selectedAtFrame = _frame;
            return;
        }

        if (_shot < 0)
        {
            if (_frame < _selectedAtFrame + 10 || !main.LiveTracking.WhenIdleAsync().IsCompleted)
            {
                return;
            }

            _shot = 0;
            _framesOnShot = 0;
        }

        if (_shot < Shots.Length)
        {
            ui.Page = Shots[_shot].Page;
        }
    }

    /// <returns>全部擷取完成時回傳 true。</returns>
    public unsafe bool AfterFrame(GL gl, Vector2D<int> size)
    {
        if (_shot < 0 || _shot >= Shots.Length)
        {
            return _shot >= Shots.Length;
        }

        if (++_framesOnShot < SettleFrames)
        {
            return false;
        }

        var w = size.X;
        var h = size.Y;
        var pixels = new byte[w * h * 4];
        gl.PixelStore(PixelStoreParameter.PackAlignment, 1);
        fixed (byte* p = pixels)
        {
            gl.ReadPixels(0, 0, (uint)w, (uint)h, PixelFormat.Rgba, PixelType.UnsignedByte, p);
        }

        // OpenGL 由下到上，PNG 由上到下
        var flipped = new byte[pixels.Length];
        var stride = w * 4;
        for (var y = 0; y < h; y++)
        {
            System.Buffer.BlockCopy(pixels, (h - 1 - y) * stride, flipped, y * stride, stride);
        }

        for (var i = 3; i < flipped.Length; i += 4)
        {
            flipped[i] = 255;
        }

        Directory.CreateDirectory(directory);
        PngWriter.Write(Path.Combine(directory, Shots[_shot].File), w, h, flipped);
        _shot++;
        _framesOnShot = 0;
        return _shot >= Shots.Length;
    }
}
