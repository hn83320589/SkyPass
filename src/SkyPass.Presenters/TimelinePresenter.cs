using SkyPass.Core.Coordinates;
using SkyPass.Core.Layout;
using SkyPass.Core.Passes;
using SkyPass.Core.Time;

namespace SkyPass.Presenters;

public sealed record TimelineModel(
    TimelineLayout Layout,
    IReadOnlyList<TimelineRow> Rows,
    IReadOnlyList<DaylightBand> Bands,
    IReadOnlyList<HourTick> Ticks,
    string DayLabel,
    SatellitePass? Selected);

/// <summary>24 小時時間軸：以顯示時區的午夜為起點，可在預報範圍內切換日期。</summary>
public sealed class TimelinePresenter
{
    private IReadOnlyList<SatellitePass> _passes = [];
    private ObserverSite _site = new(new Geodetic(0, 0, 0));
    private TimeSpan _offset;
    private JulianDate _firstDayStart;
    private int _dayCount = 1;
    private int _day;
    private double _x0;
    private double _width = 1000;
    private SatellitePass? _selected;
    private TimelineModel? _model;

    public TimelineModel Model => _model ??= Build();

    public bool CanGoPrevious => _day > 0;

    public bool CanGoNext => _day < _dayCount - 1;

    public void SetViewport(double x0, double width)
    {
        if (_x0 != x0 || _width != width)
        {
            (_x0, _width) = (x0, width);
            _model = null;
        }
    }

    public void SetData(IReadOnlyList<SatellitePass> passes, Geodetic site, TimeSpan displayOffset, JulianDate predictionStart, JulianDate predictionEnd)
    {
        _passes = passes;
        _site = new ObserverSite(site);
        _offset = displayOffset;
        var localStart = TimeDisplay.ToLocal(predictionStart, displayOffset).Date;
        var localEnd = TimeDisplay.ToLocal(predictionEnd, displayOffset).Date;
        _firstDayStart = JulianDate.FromUtc(DateTime.SpecifyKind(localStart - displayOffset, DateTimeKind.Utc));
        _dayCount = Math.Max(1, (int)(localEnd - localStart).TotalDays + 1);
        _day = Math.Clamp(_day, 0, _dayCount - 1);
        _model = null;
    }

    public void SetSelected(SatellitePass? pass)
    {
        if (!ReferenceEquals(pass, _selected))
        {
            _selected = pass;
            _model = null;
            if (pass is not null)
            {
                // 讓選取的過境所在的日期顯示出來
                var day = (int)Math.Floor(pass.Aos.SecondsSince(_firstDayStart) / 86400.0);
                if (day >= 0 && day < _dayCount)
                {
                    _day = day;
                }
            }
        }
    }

    public void NextDay() => GoTo(_day + 1);

    public void PreviousDay() => GoTo(_day - 1);

    /// <summary>點擊判定：回傳第 <paramref name="rowIndex"/> 列在 x 位置的過境。</summary>
    public SatellitePass? HitTest(int rowIndex, double x)
    {
        var rows = Model.Rows;
        if (rowIndex < 0 || rowIndex >= rows.Count)
        {
            return null;
        }

        return rows[rowIndex].Segments.FirstOrDefault(s => x >= s.X0 && x <= s.X1)?.Pass;
    }

    private void GoTo(int day)
    {
        var clamped = Math.Clamp(day, 0, _dayCount - 1);
        if (clamped != _day)
        {
            _day = clamped;
            _model = null;
        }
    }

    private TimelineModel Build()
    {
        var layout = new TimelineLayout(_firstDayStart.AddSeconds(_day * 86400.0), 86400, _x0, _width);
        var label = $"{TimeDisplay.Format(layout.Start, _offset, "yyyy-MM-dd")}（{TimeDisplay.OffsetLabel(_offset)}）";
        return new TimelineModel(
            layout,
            TimelineGeometry.BuildRows(_passes, layout),
            TimelineGeometry.BuildDaylightBands(_site, layout),
            TimelineGeometry.HourTicks(layout, _offset),
            label,
            _selected);
    }
}
