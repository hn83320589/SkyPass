using System.Numerics;
using ImGuiNET;

namespace SkyPass.App;

internal enum Page
{
    Passes,
    LiveTracking,
    Catalog,
    Settings,
    Diagnostics,
}

/// <summary>介面文字：有中文字型時顯示中文，否則退回英文。Presenter 產生的訊息一律為中文。</summary>
internal static class Ui
{
    public static bool Chinese { get; set; } = true;

    public static string T(string zh, string en) => Chinese ? zh : en;

    public static readonly Vector4 Red = new(0.95f, 0.40f, 0.40f, 1f);
    public static readonly Vector4 Amber = new(0.98f, 0.75f, 0.30f, 1f);
    public static readonly Vector4 Green = new(0.45f, 0.85f, 0.50f, 1f);
    public static readonly Vector4 Muted = new(0.60f, 0.62f, 0.66f, 1f);
    public static readonly Vector4 Accent = new(0.40f, 0.70f, 1.00f, 1f);

    public static uint U32(Vector4 c) => ImGui.GetColorU32(c);

    public static uint U32(float r, float g, float b, float a = 1f) => ImGui.GetColorU32(new Vector4(r, g, b, a));

    public static void ApplyStyle()
    {
        ImGui.StyleColorsDark();
        var style = ImGui.GetStyle();
        style.WindowRounding = 0;
        style.FrameRounding = 3;
        style.GrabRounding = 3;
        style.TabRounding = 3;
        style.CellPadding = new Vector2(6, 3);
        style.ItemSpacing = new Vector2(8, 6);
        var colors = style.Colors;
        colors[(int)ImGuiCol.WindowBg] = new Vector4(0.09f, 0.10f, 0.12f, 1f);
        colors[(int)ImGuiCol.ChildBg] = new Vector4(0.11f, 0.12f, 0.14f, 1f);
        colors[(int)ImGuiCol.Header] = new Vector4(0.20f, 0.35f, 0.55f, 0.8f);
        colors[(int)ImGuiCol.HeaderHovered] = new Vector4(0.25f, 0.42f, 0.65f, 0.9f);
        colors[(int)ImGuiCol.HeaderActive] = new Vector4(0.28f, 0.48f, 0.72f, 1f);
    }
}
