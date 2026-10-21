using SkyPass.Core.Coordinates;
using SkyPass.Core.Doppler;
using SkyPass.Core.Passes;
using SkyPass.Core.Propagation;
using SkyPass.Core.Time;
using SkyPass.Core.Tle;

namespace SkyPass.Presenters;

/// <summary>即時追蹤的一次讀數。</summary>
public sealed record LiveReading(
    JulianDate Time,
    double AzimuthDeg,
    double ElevationDeg,
    double RangeKm,
    double RangeRateKmPerSec,
    double ReceivedHz,
    double ShiftHz,
    bool AboveMask,
    SatellitePass? CurrentPass,
    TimeSpan? UntilLos,
    SatellitePass? NextPass,
    TimeSpan? UntilAos,
    string? Status);

/// <summary>
/// 即時追蹤頁：每秒 <see cref="Tick"/> 一次。單次更新只做一次 SGP4、座標轉換與都卜勒計算，
/// 下一次 AOS 由背景預先算好的過境快取（現在 −1 小時到 +24 小時）查表取得。
/// 衛星、軌道資料、觀測點或遮罩改變，或時間超出快取範圍（含時間回溯）時，在背景重建快取。
/// </summary>
public sealed class LiveTrackingPresenter(TimeProvider clock)
{
    private static readonly TimeSpan CacheBefore = TimeSpan.FromHours(1);
    private static readonly TimeSpan CacheAfter = TimeSpan.FromHours(24);
    private static readonly TimeSpan RefreshMargin = TimeSpan.FromHours(2);

    private readonly Lock _gate = new();
    private Sgp4Propagator? _propagator;
    private ObserverSite? _site;
    private double _maskDeg;
    private double _downlinkHz;
    private int _configVersion;
    private PassCache? _cache;
    private Task _pending = Task.CompletedTask;
    private int _pendingConfigVersion = -1;
    private int _cacheVersion;

    public TleSet? Satellite => _propagator?.Tle;

    public LiveReading? Latest { get; private set; }

    /// <summary>過境快取每次更新時遞增；UI 可據此在快取完成後立即重新 Tick，而不必等到下一秒。</summary>
    public int CacheVersion => Volatile.Read(ref _cacheVersion);

    /// <summary>設定追蹤對象；與目前設定相同時不做任何事（可每幀呼叫）。</summary>
    public void Configure(TleSet? tle, Geodetic site, double maskDeg, double downlinkHz)
    {
        _downlinkHz = downlinkHz;
        var sameTle = ReferenceEquals(tle, _propagator?.Tle) || (tle is null && _propagator is null);
        if (sameTle && _site?.Location == site && _maskDeg == maskDeg)
        {
            return;
        }

        lock (_gate)
        {
            _propagator = tle is null ? null : Sgp4Propagator.Create(tle);
            _site = new ObserverSite(site);
            _maskDeg = maskDeg;
            _configVersion++;
            _cache = null;
        }

        // 立即在背景計算過境快取，不必等到第一次 Tick
        if (_propagator is not null)
        {
            EnsureCache(JulianDate.FromUtc(clock.GetUtcNow().UtcDateTime));
        }
    }

    public LiveReading? Tick()
    {
        var propagator = _propagator;
        var site = _site;
        if (propagator is null || site is null)
        {
            Latest = null;
            return null;
        }

        var now = JulianDate.FromUtc(clock.GetUtcNow().UtcDateTime);
        var cache = EnsureCache(now);

        if (Observation.Compute(propagator, site, now, out var o) != Sgp4Error.None)
        {
            Latest = new LiveReading(now, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, false,
                null, null, null, null, $"{propagator.Tle.Name} 在此時刻無法傳播（可能已衰減）。");
            return Latest;
        }

        var look = o.Look;
        var received = DopplerCalculator.ReceivedHz(_downlinkHz, look.RangeRateKmPerSec);
        SatellitePass? current = null;
        SatellitePass? next = null;
        string? status = null;
        if (cache is null)
        {
            status = "計算下一次過境中…";
        }
        else
        {
            foreach (var p in cache.Passes)
            {
                if (current is null && now.SecondsSince(p.Aos) >= 0 && p.Los.SecondsSince(now) >= 0)
                {
                    current = p;
                }
                else if (p.Aos.SecondsSince(now) > 0)
                {
                    next = p;
                    break;
                }
            }

            if (next is null && cache.Diagnostic is null)
            {
                status = "24 小時內沒有過境。";
            }

            status ??= cache.Diagnostic?.Message;
        }

        Latest = new LiveReading(
            now, look.AzimuthDeg, look.ElevationDeg, look.RangeKm, look.RangeRateKmPerSec, received, received - _downlinkHz,
            look.ElevationDeg >= _maskDeg,
            current, current is null ? null : TimeSpan.FromSeconds(current.Los.SecondsSince(now)),
            next, next is null ? null : TimeSpan.FromSeconds(next.Aos.SecondsSince(now)),
            status);
        return Latest;
    }

    /// <summary>等待背景快取計算完成（測試與啟動時使用）。</summary>
    public Task WhenIdleAsync()
    {
        lock (_gate)
        {
            return _pending;
        }
    }

    private PassCache? EnsureCache(JulianDate now)
    {
        var cache = Volatile.Read(ref _cache);
        if (cache is not null && cache.ConfigVersion == _configVersion &&
            now.SecondsSince(cache.Start) >= 0 && cache.End.SecondsSince(now) > RefreshMargin.TotalSeconds)
        {
            return cache;
        }

        lock (_gate)
        {
            var version = _configVersion;
            if (_pendingConfigVersion == version && !_pending.IsCompleted)
            {
                return cache?.ConfigVersion == version ? cache : null;
            }

            var propagator = _propagator!;
            var site = _site!;
            var mask = _maskDeg;
            var start = now.AddSeconds(-CacheBefore.TotalSeconds);
            var end = now.AddSeconds(CacheAfter.TotalSeconds);
            _pendingConfigVersion = version;
            _pending = Task.Run(() =>
            {
                var result = PassFinder.Find(propagator, site, start, end, mask);
                lock (_gate)
                {
                    if (_configVersion == version)
                    {
                        Volatile.Write(ref _cache, new PassCache(version, start, end, result.Passes, result.Diagnostic));
                        Interlocked.Increment(ref _cacheVersion);
                    }
                }
            });

            // 時間回溯或超出範圍時，舊快取不再可信
            return null;
        }
    }

    private sealed record PassCache(int ConfigVersion, JulianDate Start, JulianDate End, IReadOnlyList<SatellitePass> Passes, PredictionDiagnostic? Diagnostic);
}
