using System.Globalization;
using System.Numerics;
using ImGuiNET;
using SkyPass.Core.Storage;
using SkyPass.Core.Time;
using SkyPass.Presenters;

namespace SkyPass.App.Views;

internal static class LiveTrackingPage
{
    public static void Draw(MainPresenter main, UiState ui, JulianDate now)
    {
        var live = main.LiveTracking;
        var offset = main.Settings.Current.DisplayUtcOffset;
        var supported = main.Catalog.Rows.Where(r => r.Status != CatalogRowStatus.Unsupported).ToList();
        ImGui.SetNextItemWidth(320);
        if (ImGui.BeginCombo(Ui.T("衛星", "Satellite"), live.Satellite is { } s ? $"{s.Name} ({s.NoradId})" : Ui.T("（請選擇）", "(select)")))
        {
            foreach (var row in supported)
            {
                if (ImGui.Selectable($"{row.Name} ({row.NoradId})", live.Satellite?.NoradId == row.NoradId))
                {
                    main.SelectLiveSatellite(row.NoradId);
                }
            }

            ImGui.EndCombo();
        }

        ImGui.SameLine();
        ImGui.TextDisabled($"{Ui.T("觀測點", "Site")}：{main.Settings.Current.SelectedSite.Name}　{Ui.T("下行", "Downlink")} {main.Settings.Current.DownlinkHz / 1e6:F3} MHz");
        ImGui.Separator();

        var r = live.Latest;
        if (r is null)
        {
            ImGui.TextDisabled(Ui.T("請選擇一顆衛星；也可以在過境表點選過境。", "Select a satellite."));
            return;
        }

        ImGui.TextDisabled(TimeDisplay.Format(r.Time, offset) + "  " + TimeDisplay.OffsetLabel(offset));
        if (ImGui.BeginTable("##live", 2, ImGuiTableFlags.SizingFixedFit))
        {
            Row(Ui.T("方位角", "Azimuth"), $"{r.AzimuthDeg:F2}°");
            Row(Ui.T("仰角", "Elevation"), $"{r.ElevationDeg:F2}°", r.AboveMask ? Ui.Green : null);
            Row(Ui.T("距離", "Range"), $"{r.RangeKm:F1} km");
            Row(Ui.T("距離變化率", "Range rate"), $"{r.RangeRateKmPerSec:+0.000;-0.000} km/s");
            Row(Ui.T("接收頻率", "Receive freq"), $"{r.ReceivedHz / 1e6:F6} MHz", Ui.Accent);
            Row(Ui.T("都卜勒頻移", "Doppler shift"), $"{r.ShiftHz:+0;-0} Hz");
            if (r.CurrentPass is { } cur)
            {
                Row(Ui.T("過境中，LOS 倒數", "In pass, LOS in"), Countdown(r.UntilLos), Ui.Green);
                Row(Ui.T("本次最大仰角", "Max elevation"), $"{cur.MaxElevationDeg:F1}° @ {TimeDisplay.Format(cur.MaxElevationTime, offset, TimeDisplay.TimeFormat)}");
            }

            if (r.NextPass is { } next)
            {
                Row(Ui.T("下一次 AOS 倒數", "Next AOS in"), Countdown(r.UntilAos), Ui.Amber);
                Row(Ui.T("下一次過境", "Next pass"),
                    $"{TimeDisplay.Format(next.Aos, offset)}  {Ui.T("最大", "max")} {next.MaxElevationDeg:F1}°  {Ui.T("方位", "az")} {next.AosAzimuthDeg:F0}° → {next.LosAzimuthDeg:F0}°");
            }

            ImGui.EndTable();
        }

        if (r.Status is { } status)
        {
            ImGui.TextColored(Ui.Muted, status);
        }
    }

    private static void Row(string label, string value, Vector4? color = null)
    {
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.TextDisabled(label);
        ImGui.TableNextColumn();
        ImGui.SetWindowFontScale(1.35f);
        if (color is { } c)
        {
            ImGui.TextColored(c, value);
        }
        else
        {
            ImGui.TextUnformatted(value);
        }

        ImGui.SetWindowFontScale(1f);
    }

    private static string Countdown(TimeSpan? t) => t is not { } v ? "—"
        : v.TotalDays >= 1 ? v.ToString(@"d\.hh\:mm\:ss", CultureInfo.InvariantCulture)
        : v.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);
}

internal static class CatalogPage
{
    public static void Draw(MainPresenter main, UiState ui)
    {
        var catalog = main.Catalog;
        var offset = main.Settings.Current.DisplayUtcOffset;
        ImGui.TextUnformatted(Ui.T("匯入 TLE 檔（也可以把檔案拖曳到視窗）", "Import TLE file (or drag & drop onto the window)"));
        ImGui.SetNextItemWidth(MathF.Max(200, ImGui.GetContentRegionAvail().X - 100));
        ImGui.InputTextWithHint("##importpath", @"C:\path\to\elements.tle", ref ui.ImportPath, 260);
        ImGui.SameLine();
        if (ImGui.Button(Ui.T("匯入", "Import")) && ui.ImportPath.Length > 0)
        {
            _ = main.ImportTleFileAsync(ui.ImportPath.Trim('"'));
        }

        if (catalog.LastImport is { } report)
        {
            var s = report.Summary;
            ImGui.TextColored(report.Errors.Count > 0 ? Ui.Amber : Ui.Green,
                $"{report.FileName}：{Ui.T("讀入", "read")} {report.SetsRead}、{Ui.T("新增", "added")} {s.Added}、{Ui.T("取代", "replaced")} {s.Replaced}、" +
                $"{Ui.T("捨棄較舊", "discarded")} {s.DiscardedOlder}、{Ui.T("錯誤", "errors")} {report.Errors.Count}");
            if (report.Errors.Count > 0 && ImGui.TreeNode(Ui.T("錯誤明細", "Errors")))
            {
                foreach (var e in report.Errors)
                {
                    ImGui.TextColored(Ui.Amber, e.ToString());
                }

                ImGui.TreePop();
            }
        }

        ImGui.Separator();
        var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingFixedFit;
        if (!ImGui.BeginTable("##catalog", 7, flags))
        {
            return;
        }

        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn(Ui.T("預測", "Use"));
        ImGui.TableSetupColumn("NORAD");
        ImGui.TableSetupColumn(Ui.T("名稱", "Name"), ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Epoch");
        ImGui.TableSetupColumn(Ui.T("週期", "Period"));
        ImGui.TableSetupColumn(Ui.T("狀態", "Status"));
        ImGui.TableSetupColumn(Ui.T("歷史（點選使用）", "History"));
        ImGui.TableHeadersRow();

        foreach (var row in catalog.Rows)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            var selected = row.IsSelected;
            ImGui.BeginDisabled(row.Status == CatalogRowStatus.Unsupported);
            if (ImGui.Checkbox($"##sel{row.NoradId}", ref selected))
            {
                catalog.SetSelected(row.NoradId, selected);
            }

            ImGui.EndDisabled();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(row.NoradId.ToString(CultureInfo.InvariantCulture));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(row.Name);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(TimeDisplay.Format(row.Active.Epoch, offset, "yyyy-MM-dd HH:mm"));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted($"{row.Active.PeriodMinutes:F1} min");
            ImGui.TableNextColumn();
            ImGui.TextColored(row.Status switch
            {
                CatalogRowStatus.Ok => Ui.Green,
                CatalogRowStatus.Stale => Ui.Amber,
                _ => Ui.Red,
            }, row.StatusText);
            ImGui.TableNextColumn();
            ImGui.SetNextItemWidth(220);
            var preview = $"{row.History.Count} {Ui.T("組", "sets")}{(row.IsPinned ? Ui.T("（已釘選）", " (pinned)") : "")}";
            if (ImGui.BeginCombo($"##hist{row.NoradId}", preview))
            {
                if (ImGui.Selectable(Ui.T("使用最新一組", "Use latest"), !row.IsPinned))
                {
                    catalog.Unpin(row.NoradId);
                }

                foreach (var set in row.History)
                {
                    if (ImGui.Selectable($"{TimeDisplay.Format(set.Epoch, offset, "yyyy-MM-dd HH:mm:ss")}##{set.Epoch.Value}", row.IsPinned && set == row.Active))
                    {
                        catalog.Pin(row.NoradId, set.Epoch);
                    }
                }

                ImGui.EndCombo();
            }

            ImGui.SameLine();
            if (ImGui.SmallButton($"{Ui.T("刪除", "Delete")}##{row.NoradId}"))
            {
                catalog.Remove(row.NoradId);
            }
        }

        ImGui.EndTable();
    }
}

internal static class SettingsPage
{
    private static readonly TimeSpan[] Offsets = Enumerable.Range(-48, 105).Select(q => TimeSpan.FromMinutes(q * 15)).ToArray();

    public static void Draw(MainPresenter main, UiState ui)
    {
        var settings = main.Settings;
        var s = settings.Current;

        ImGui.SeparatorText(Ui.T($"觀測點（最多 {AppSettings.MaxSites} 個）", $"Sites (max {AppSettings.MaxSites})"));
        for (var i = 0; i < s.Sites.Count; i++)
        {
            var site = s.Sites[i];
            if (ImGui.RadioButton($"{site.Name}##site{i}", s.SelectedSiteIndex == i))
            {
                settings.SelectSite(i);
            }

            ImGui.SameLine(260);
            ImGui.TextDisabled($"{site.Location.LatitudeDeg:F4}°, {site.Location.LongitudeDeg:F4}°, {site.Location.HeightKm * 1000:F0} m");
            ImGui.SameLine();
            if (ImGui.SmallButton($"{Ui.T("刪除", "Delete")}##del{i}"))
            {
                settings.RemoveSite(i);
            }
        }

        ImGui.SetNextItemWidth(160);
        ImGui.InputTextWithHint("##newname", Ui.T("名稱", "Name"), ref ui.NewSiteName, 40);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(130);
        ImGui.InputDouble(Ui.T("緯度", "Lat"), ref ui.NewSiteLat, 0, 0, "%.5f");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(130);
        ImGui.InputDouble(Ui.T("經度", "Lon"), ref ui.NewSiteLon, 0, 0, "%.5f");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(100);
        ImGui.InputDouble(Ui.T("高度 m", "Height m"), ref ui.NewSiteHeightM, 0, 0, "%.0f");
        ImGui.SameLine();
        if (ImGui.Button(Ui.T("新增觀測點", "Add site")) && settings.AddSite(ui.NewSiteName, ui.NewSiteLat, ui.NewSiteLon, ui.NewSiteHeightM))
        {
            ui.NewSiteName = "";
        }

        ImGui.SeparatorText(Ui.T("預報", "Prediction"));
        // 拖曳期間只改暫存值，放開後才套用，避免每一幀都觸發重新預報
        ui.MaskEdit ??= (float)s.MaskDeg;
        var mask = ui.MaskEdit.Value;
        ImGui.SetNextItemWidth(260);
        ImGui.SliderFloat(Ui.T("遮罩仰角", "Mask elevation"), ref mask, 0, 45, "%.1f°");
        ui.MaskEdit = mask;
        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            settings.SetMask(Math.Round(mask, 1));
        }

        if (!ImGui.IsItemActive())
        {
            ui.MaskEdit = (float)settings.Current.MaskDeg;
        }

        ui.DaysEdit ??= (float)s.PredictionDays;
        var days = ui.DaysEdit.Value;
        ImGui.SetNextItemWidth(260);
        ImGui.SliderFloat(Ui.T("預報天數", "Days"), ref days, 0.5f, 14, "%.1f");
        ui.DaysEdit = days;
        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            settings.SetPredictionDays(Math.Round(days * 2) / 2);
        }

        if (!ImGui.IsItemActive())
        {
            ui.DaysEdit = (float)settings.Current.PredictionDays;
        }

        var mhz = s.DownlinkHz / 1e6;
        ImGui.SetNextItemWidth(260);
        if (ImGui.InputDouble(Ui.T("下行頻率 MHz", "Downlink MHz"), ref mhz, 0.005, 0.1, "%.4f", ImGuiInputTextFlags.EnterReturnsTrue))
        {
            settings.SetDownlinkHz(mhz * 1e6);
        }

        ImGui.SeparatorText(Ui.T("時區", "Time zone"));
        OffsetCombo(Ui.T("畫面顯示", "Display"), s.DisplayUtcOffset, settings.SetDisplayOffset);
        OffsetCombo(Ui.T("CSV 輸出", "CSV export"), s.CsvUtcOffset, settings.SetCsvOffset);

        if (settings.LastError is { } error)
        {
            ImGui.Spacing();
            ImGui.TextColored(Ui.Red, error);
        }

        ImGui.Spacing();
        if (ImGui.Button(Ui.T("立即重新預報", "Predict now")))
        {
            main.RequestPrediction();
        }
    }

    private static void OffsetCombo(string label, TimeSpan current, Func<TimeSpan, bool> set)
    {
        ImGui.SetNextItemWidth(260);
        if (ImGui.BeginCombo(label, TimeDisplay.OffsetLabel(current)))
        {
            foreach (var o in Offsets)
            {
                if (ImGui.Selectable(TimeDisplay.OffsetLabel(o), o == current))
                {
                    set(o);
                }
            }

            ImGui.EndCombo();
        }
    }
}

internal static class DiagnosticsPage
{
    public static void Draw(MainPresenter main)
    {
        var items = main.Diagnostics.Items;
        ImGui.TextUnformatted($"{Ui.T("錯誤", "Errors")} {main.Diagnostics.CountOf(DiagnosticSeverity.Error)}　" +
                              $"{Ui.T("警告", "Warnings")} {main.Diagnostics.CountOf(DiagnosticSeverity.Warning)}　" +
                              $"{Ui.T("訊息", "Info")} {main.Diagnostics.CountOf(DiagnosticSeverity.Info)}");
        if (!ImGui.BeginTable("##diag", 3, ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingFixedFit))
        {
            return;
        }

        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn(Ui.T("等級", "Level"));
        ImGui.TableSetupColumn(Ui.T("來源", "Source"));
        ImGui.TableSetupColumn(Ui.T("訊息", "Message"), ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableHeadersRow();
        foreach (var item in items)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            var (color, label) = item.Severity switch
            {
                DiagnosticSeverity.Error => (Ui.Red, Ui.T("錯誤", "Error")),
                DiagnosticSeverity.Warning => (Ui.Amber, Ui.T("警告", "Warning")),
                _ => (Ui.Muted, Ui.T("訊息", "Info")),
            };
            ImGui.TextColored(color, label);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(item.Source);
            ImGui.TableNextColumn();
            ImGui.TextWrapped(item.Message);
        }

        ImGui.EndTable();
    }
}

internal static class StatusBar
{
    public static void Draw(MainPresenter main, JulianDate now)
    {
        var state = main.Prediction.State;
        var offset = main.Settings.Current.DisplayUtcOffset;
        ImGui.Separator();
        ImGui.TextDisabled($"{TimeDisplay.Format(now, offset)} {TimeDisplay.OffsetLabel(offset)}");
        ImGui.SameLine();
        ImGui.TextDisabled($"| {main.Settings.Current.SelectedSite.Name} | {Ui.T("遮罩", "Mask")} {main.Settings.Current.MaskDeg:F1}°");
        ImGui.SameLine();
        switch (state.Status)
        {
            case PredictionStatus.Running:
                ImGui.SetNextItemWidth(220);
                ImGui.ProgressBar((float)main.Prediction.Progress, new Vector2(220, 0), Ui.T("預報中…", "Predicting…"));
                ImGui.SameLine();
                if (ImGui.SmallButton(Ui.T("取消", "Cancel")))
                {
                    main.Prediction.Cancel();
                }

                break;
            case PredictionStatus.Completed when state.Result is { } result:
                ImGui.TextColored(Ui.Green, $"| {Ui.T("預報完成", "Done")}：{state.Request!.Satellites.Count} {Ui.T("顆", "sats")}、{result.Passes.Count} {Ui.T("個過境", "passes")}（{state.Elapsed?.TotalMilliseconds:F0} ms）");
                break;
            case PredictionStatus.Cancelled or PredictionStatus.Failed:
                ImGui.TextColored(Ui.Amber, $"| {state.Message}");
                break;
            default:
                ImGui.TextDisabled(Ui.T("| 尚未預報：請先在「軌道資料」匯入 TLE", "| No prediction yet: import TLE first"));
                break;
        }
    }
}
