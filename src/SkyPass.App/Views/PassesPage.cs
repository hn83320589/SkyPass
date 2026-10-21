using System.Numerics;
using ImGuiNET;
using SkyPass.Core.Layout;
using SkyPass.Core.Time;
using SkyPass.Presenters;

namespace SkyPass.App.Views;

/// <summary>過境預報頁：過境表（左上）、天空圖（右上）、24 小時時間軸（下方），點選任一處即聯動。</summary>
internal static class PassesPage
{
    public static void Draw(MainPresenter main, UiState ui, JulianDate now)
    {
        var avail = ImGui.GetContentRegionAvail();
        var topHeight = MathF.Max(260, avail.Y * 0.58f);
        var skyWidth = MathF.Min(avail.X * 0.38f, topHeight + 40);

        ImGui.BeginChild("##table", new Vector2(avail.X - skyWidth - 8, topHeight), ImGuiChildFlags.Border);
        DrawFilterBar(main, ui);
        DrawTable(main);
        ImGui.EndChild();

        ImGui.SameLine();
        ImGui.BeginChild("##sky", new Vector2(skyWidth, topHeight), ImGuiChildFlags.Border);
        SkyPlotView.Draw(main, now);
        ImGui.EndChild();

        ImGui.BeginChild("##timeline", Vector2.Zero, ImGuiChildFlags.Border);
        TimelineView.Draw(main, now);
        ImGui.EndChild();
    }

    private static void DrawFilterBar(MainPresenter main, UiState ui)
    {
        var changed = false;
        ImGui.SetNextItemWidth(180);
        changed |= ImGui.InputTextWithHint("##filter", Ui.T("衛星名稱或編號", "Name or NORAD"), ref ui.FilterText, 64);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(160);
        changed |= ImGui.SliderFloat(Ui.T("最大仰角下限", "Min max-el"), ref ui.FilterMinElevation, 0, 90, "%.0f°");
        ImGui.SameLine();
        changed |= ImGui.Checkbox(Ui.T("僅可目視", "Visible only"), ref ui.FilterVisibleOnly);
        if (changed)
        {
            main.PassTable.SetFilter(new PassFilter(ui.FilterText, ui.FilterMinElevation, ui.FilterVisibleOnly));
        }

        ImGui.SameLine();
        ImGui.TextDisabled($"{main.PassTable.Rows.Count} / {main.PassTable.TotalCount}");

        ImGui.SetNextItemWidth(MathF.Max(120, ImGui.GetContentRegionAvail().X - 120));
        ImGui.InputText("##export", ref ui.ExportPath, 260);
        ImGui.SameLine();
        if (ImGui.Button(Ui.T("匯出 CSV", "Export CSV")))
        {
            try
            {
                using var file = File.Create(ui.ExportPath);
                main.ExportCsv(file);
                ui.ExportMessage = Ui.T($"已匯出 {main.PassTable.Rows.Count} 筆到 {ui.ExportPath}", $"Exported {main.PassTable.Rows.Count} rows");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                ui.ExportMessage = Ui.T($"匯出失敗：{ex.Message}", $"Export failed: {ex.Message}");
            }
        }

        if (ui.ExportMessage is { } msg)
        {
            ImGui.TextDisabled(msg);
        }
    }

    private static unsafe void DrawTable(MainPresenter main)
    {
        var flags = ImGuiTableFlags.Sortable | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.ScrollX |
                    ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingFixedFit;
        if (!ImGui.BeginTable("##passes", 10, flags))
        {
            return;
        }

        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn(Ui.T("衛星", "Satellite"), ImGuiTableColumnFlags.WidthFixed, 170, (uint)PassSortColumn.Satellite);
        ImGui.TableSetupColumn("AOS", ImGuiTableColumnFlags.DefaultSort, 0, (uint)PassSortColumn.Aos);
        ImGui.TableSetupColumn(Ui.T("方位", "Az"), ImGuiTableColumnFlags.NoSort);
        ImGui.TableSetupColumn(Ui.T("最大仰角時刻", "Max time"), ImGuiTableColumnFlags.NoSort);
        ImGui.TableSetupColumn(Ui.T("最大仰角", "Max el"), ImGuiTableColumnFlags.PreferSortDescending, 0, (uint)PassSortColumn.MaxElevation);
        ImGui.TableSetupColumn("LOS", ImGuiTableColumnFlags.NoSort);
        ImGui.TableSetupColumn(Ui.T("方位", "Az"), ImGuiTableColumnFlags.NoSort);
        ImGui.TableSetupColumn(Ui.T("持續", "Duration"), ImGuiTableColumnFlags.NoSort);
        ImGui.TableSetupColumn(Ui.T("可目視", "Visible"), ImGuiTableColumnFlags.NoSort);
        ImGui.TableSetupColumn(Ui.T("標示", "Flags"), ImGuiTableColumnFlags.NoSort);
        ImGui.TableHeadersRow();

        var specs = ImGui.TableGetSortSpecs();
        if (specs.NativePtr != null && specs.SpecsDirty && specs.SpecsCount > 0)
        {
            var spec = specs.Specs;
            main.PassTable.SortBy((PassSortColumn)spec.ColumnUserID, spec.SortDirection == ImGuiSortDirection.Descending);
            specs.SpecsDirty = false;
        }

        var rows = main.PassTable.Rows;
        var clipper = new ImGuiListClipperPtr(ImGuiNative.ImGuiListClipper_ImGuiListClipper());
        clipper.Begin(rows.Count);
        while (clipper.Step())
        {
            for (var i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
            {
                var r = rows[i];
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                if (ImGui.Selectable($"{r.Satellite}##{i}", r.IsSelected, ImGuiSelectableFlags.SpanAllColumns))
                {
                    main.PassTable.Select(r);
                }

                Cell(r.Aos);
                Cell(r.AosAzimuth);
                Cell(r.MaxTime);
                Cell(r.MaxElevation);
                Cell(r.Los);
                Cell(r.LosAzimuth);
                Cell(r.Duration);
                ImGui.TableNextColumn();
                if (r.Visible.Length > 0)
                {
                    ImGui.TextColored(Ui.Amber, r.Visible);
                }

                ImGui.TableNextColumn();
                if (r.Flags.Length > 0)
                {
                    ImGui.TextColored(Ui.Muted, r.Flags);
                }
            }
        }

        clipper.End();
        clipper.Destroy();
        ImGui.EndTable();
    }

    private static void Cell(string text)
    {
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(text);
    }
}

internal static class SkyPlotView
{
    private static readonly Vector4 VisibleColor = new(1.00f, 0.85f, 0.25f, 1f);
    private static readonly Vector4 SunlitColor = new(0.55f, 0.80f, 1.00f, 1f);
    private static readonly Vector4 ShadowColor = new(0.40f, 0.45f, 0.60f, 1f);

    public static void Draw(MainPresenter main, JulianDate now)
    {
        var pass = main.SkyPlot.Model.Pass;
        ImGui.TextUnformatted(pass is null ? Ui.T("天空圖（請在過境表或時間軸點選過境）", "Sky plot (select a pass)") : $"{pass.SatelliteName}  {pass.MaxElevationDeg:F1}°");
        var origin = ImGui.GetCursorScreenPos();
        var size = ImGui.GetContentRegionAvail() - new Vector2(0, ImGui.GetTextLineHeightWithSpacing());
        main.SkyPlot.SetViewport(origin.X, origin.Y, size.X, size.Y);
        var m = main.SkyPlot.Model;
        var dl = ImGui.GetWindowDrawList();

        var grid = Ui.U32(0.35f, 0.38f, 0.45f);
        Polyline(dl, m.Horizon, Ui.U32(0.6f, 0.62f, 0.68f), 1.5f);
        foreach (var ring in m.ElevationRings)
        {
            Polyline(dl, ring, grid, 1f);
        }

        foreach (var (from, to) in m.Spokes)
        {
            dl.AddLine(V(from), V(to), grid);
        }

        Polyline(dl, m.MaskCircle, Ui.U32(0.95f, 0.45f, 0.35f, 0.9f), 1.5f);
        foreach (var (label, pos) in m.Labels)
        {
            var ts = ImGui.CalcTextSize(label);
            dl.AddText(V(pos) - ts / 2, Ui.U32(0.85f, 0.87f, 0.92f), label);
        }

        for (var i = 1; i < m.Track.Count; i++)
        {
            var color = SkyPlotModel.StyleOf(m.Track[i]) switch
            {
                TrackStyle.Visible => VisibleColor,
                TrackStyle.Sunlit => SunlitColor,
                _ => ShadowColor,
            };
            dl.AddLine(V(m.Track[i - 1].Point), V(m.Track[i].Point), Ui.U32(color), 2.5f);
        }

        if (m.Track.Count > 0)
        {
            dl.AddCircleFilled(V(m.Track[0].Point), 4, Ui.U32(Ui.Green));
            dl.AddText(V(m.Track[0].Point) + new Vector2(6, -8), Ui.U32(Ui.Green), "AOS");
            dl.AddCircle(V(m.Track[^1].Point), 4, Ui.U32(Ui.Red), 12, 2);
        }

        if (main.SkyPlot.CurrentPosition(now) is { } current)
        {
            dl.AddCircleFilled(V(current), 6, Ui.U32(1, 1, 1));
            dl.AddCircle(V(current), 9, Ui.U32(1, 1, 1, 0.6f), 16, 1.5f);
        }

        ImGui.InvisibleButton("##skycanvas", Vector2.Max(size, Vector2.One));
        if (ImGui.IsItemHovered())
        {
            var mouse = ImGui.GetMousePos();
            var (az, el) = m.Layout.Unproject(new PointD(mouse.X, mouse.Y));
            if (el >= 0)
            {
                ImGui.SetTooltip($"{Ui.T("方位", "Az")} {az:F0}°  {Ui.T("仰角", "El")} {el:F0}°");
            }
        }

        ImGui.TextColored(VisibleColor, Ui.T("■ 可目視", "■ Visible"));
        ImGui.SameLine();
        ImGui.TextColored(SunlitColor, Ui.T("■ 日照", "■ Sunlit"));
        ImGui.SameLine();
        ImGui.TextColored(ShadowColor, Ui.T("■ 地影", "■ Shadow"));
        ImGui.SameLine();
        ImGui.TextColored(new Vector4(0.95f, 0.45f, 0.35f, 1f), Ui.T("○ 遮罩", "○ Mask"));
    }

    private static Vector2 V(PointD p) => new((float)p.X, (float)p.Y);

    private static void Polyline(ImDrawListPtr dl, IReadOnlyList<PointD> points, uint color, float thickness)
    {
        for (var i = 1; i < points.Count; i++)
        {
            dl.AddLine(V(points[i - 1]), V(points[i]), color, thickness);
        }
    }
}

internal static class TimelineView
{
    private const float LabelWidth = 170;
    private const float RowHeight = 20;
    private const float HeaderHeight = 20;

    public static void Draw(MainPresenter main, JulianDate now)
    {
        var timeline = main.Timeline;
        ImGui.BeginDisabled(!timeline.CanGoPrevious);
        if (ImGui.ArrowButton("##prev", ImGuiDir.Left))
        {
            timeline.PreviousDay();
        }

        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.BeginDisabled(!timeline.CanGoNext);
        if (ImGui.ArrowButton("##next", ImGuiDir.Right))
        {
            timeline.NextDay();
        }

        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.TextUnformatted($"{Ui.T("24 小時時間軸", "24-hour timeline")}  {timeline.Model.DayLabel}");

        var origin = ImGui.GetCursorScreenPos();
        var avail = ImGui.GetContentRegionAvail();
        timeline.SetViewport(origin.X + LabelWidth, MathF.Max(50, avail.X - LabelWidth - 8));
        var model = timeline.Model;
        var scaler = new DpiScaler(ImGui.GetIO().DisplayFramebufferScale.X);
        var dl = ImGui.GetWindowDrawList();
        var height = HeaderHeight + RowHeight * Math.Max(1, model.Rows.Count);
        var top = origin.Y + HeaderHeight;

        foreach (var band in model.Bands)
        {
            dl.AddRectFilled(new Vector2(Snap(scaler, band.X0), top), new Vector2(Snap(scaler, band.X1), origin.Y + height), BandColor(band.Kind));
        }

        foreach (var tick in model.Ticks)
        {
            var x = Snap(scaler, tick.X);
            dl.AddLine(new Vector2(x, top - 4), new Vector2(x, origin.Y + height), Ui.U32(1, 1, 1, 0.10f));
            dl.AddText(new Vector2(x - 8, origin.Y), Ui.U32(Ui.Muted), tick.Label);
        }

        for (var i = 0; i < model.Rows.Count; i++)
        {
            var row = model.Rows[i];
            var y = top + i * RowHeight;
            dl.AddText(new Vector2(origin.X, y + 2), Ui.U32(0.85f, 0.87f, 0.92f), Truncate(row.Name, 20));
            foreach (var seg in row.Segments)
            {
                var span = scaler.SnapSpan(seg.X0, seg.X1);
                var x0 = span.Start / scaler.Scale;
                var x1 = MathF.Max((float)(span.End / scaler.Scale), (float)(x0 + 2));
                dl.AddRectFilled(new Vector2((float)x0, y + 3), new Vector2(x1, y + RowHeight - 3), Ui.U32(Ui.Accent));
                foreach (var iv in seg.Pass.VisibleIntervals)
                {
                    var v0 = Snap(scaler, Math.Max(seg.X0, model.Layout.XOf(iv.Start)));
                    var v1 = Snap(scaler, Math.Min(seg.X1, model.Layout.XOf(iv.End)));
                    if (v1 > v0)
                    {
                        dl.AddRectFilled(new Vector2(v0, y + 6), new Vector2(v1, y + RowHeight - 6), Ui.U32(Ui.Amber));
                    }
                }

                if (ReferenceEquals(seg.Pass, model.Selected))
                {
                    dl.AddRect(new Vector2((float)x0 - 2, y + 1), new Vector2(x1 + 2, y + RowHeight - 1), Ui.U32(1, 1, 1), 2, ImDrawFlags.None, 2);
                }
            }
        }

        var nowX = model.Layout.XOf(now);
        if (nowX >= model.Layout.X0 && nowX <= model.Layout.X0 + model.Layout.Width)
        {
            dl.AddLine(new Vector2(Snap(scaler, nowX), top - 4), new Vector2(Snap(scaler, nowX), origin.Y + height), Ui.U32(Ui.Red), 1.5f);
        }

        ImGui.InvisibleButton("##timelinecanvas", new Vector2(MathF.Max(1, avail.X), height));
        if (ImGui.IsItemClicked())
        {
            var mouse = ImGui.GetMousePos();
            var rowIndex = (int)Math.Floor((mouse.Y - top) / RowHeight);
            if (timeline.HitTest(rowIndex, mouse.X) is { } hit)
            {
                main.PassTable.Select(hit);
            }
        }

        if (ImGui.IsItemHovered())
        {
            var t = model.Layout.TimeOf(ImGui.GetMousePos().X);
            ImGui.SetTooltip(TimeDisplay.Format(t, main.Settings.Current.DisplayUtcOffset, "MM-dd HH:mm"));
        }
    }

    private static float Snap(DpiScaler scaler, double logical) => (float)(scaler.ToPixels(logical) / scaler.Scale);

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    private static uint BandColor(DaylightKind kind) => kind switch
    {
        DaylightKind.Day => Ui.U32(0.26f, 0.29f, 0.36f),
        DaylightKind.CivilTwilight => Ui.U32(0.20f, 0.22f, 0.32f),
        DaylightKind.NauticalTwilight => Ui.U32(0.15f, 0.16f, 0.26f),
        DaylightKind.AstronomicalTwilight => Ui.U32(0.11f, 0.12f, 0.20f),
        _ => Ui.U32(0.07f, 0.08f, 0.14f),
    };
}
