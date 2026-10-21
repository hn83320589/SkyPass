using SkyPass.Core.Mathematics;
using SkyPass.Core.Time;
using SkyPass.Core.Tle;

namespace SkyPass.Core.Propagation;

/// <summary>
/// SGP4 近地軌道傳播器。逐行移植自 Vallado 等人（2006）〈Revisiting Spacetrack Report #3〉的
/// C++ 參考實作（python-sgp4 2.27 所附 SGP4.cpp）：WGS-72、opsmode 'i'、近地點 &lt; 220 km 時 isimp = 1。
/// <para>
/// 與原碼的差異：原碼在 sgp4() 中會寫回 satrec 的平均元素（am、em、nm…），此處全部改為區域變數，
/// 初始化後物件不可變，因此 <see cref="Propagate(double, out TemeState)"/> 執行緒安全且不配置記憶體。
/// 運算式的順序與原碼一致，以確保與參考實作的浮點結果相同。
/// </para>
/// </summary>
public sealed class Sgp4Propagator
{
    // ---------------- WGS-72 常數（getgravconst, wgs72） ----------------
    private const double Mus = 398600.8;
    private const double RadiusEarthKm = 6378.135;
    private const double J2 = 0.001082616;
    private const double J3 = -0.00000253881;
    private const double J4 = -0.00000165597;
    private const double J3oj2 = J3 / J2;
    private static readonly double Xke = 60.0 / Math.Sqrt(RadiusEarthKm * RadiusEarthKm * RadiusEarthKm / Mus);
    private static readonly double VkmPerSec = RadiusEarthKm * Xke / 60.0;

    private const double TwoPi = 2.0 * Math.PI;
    private const double X2o3 = 2.0 / 3.0;
    private const double Temp4 = 1.5e-12;

    // ---------------- 初始化後固定的欄位 ----------------
    private readonly double _bstar, _ecco, _argpo, _inclo, _mo, _nodeo, _noUnkozai;
    private readonly bool _isimp;
    private readonly double _aycof, _con41, _cc1, _cc4, _cc5, _d2, _d3, _d4, _delmo, _eta, _argpdot, _omgcof,
        _sinmao, _t2cof, _t3cof, _t4cof, _t5cof, _x1mth2, _x7thm1, _mdot, _nodedot, _xlcof, _xmcof, _nodecf;

    private Sgp4Propagator(TleSet tle)
    {
        Tle = tle;
        Epoch = tle.Epoch;

        // ---- twoline2rv：單位換算 ----
        const double deg2rad = Math.PI / 180.0;
        const double xpdotp = 1440.0 / (2.0 * Math.PI);
        var noKozai = tle.MeanMotionRevPerDay / xpdotp;
        _bstar = tle.BStar;
        _ecco = tle.Eccentricity;
        _inclo = tle.InclinationDeg * deg2rad;
        _nodeo = tle.RaanDeg * deg2rad;
        _argpo = tle.ArgOfPerigeeDeg * deg2rad;
        _mo = tle.MeanAnomalyDeg * deg2rad;

        // ---- sgp4init ----
        var ss = 78.0 / RadiusEarthKm + 1.0;
        var qzms2ttemp = (120.0 - 78.0) / RadiusEarthKm;
        var qzms2t = qzms2ttemp * qzms2ttemp * qzms2ttemp * qzms2ttemp;

        // ---- initl ----
        var eccsq = _ecco * _ecco;
        var omeosq = 1.0 - eccsq;
        var rteosq = Math.Sqrt(omeosq);
        var cosio = Math.Cos(_inclo);
        var cosio2 = cosio * cosio;

        var ak = Math.Pow(Xke / noKozai, X2o3);
        var d1 = 0.75 * J2 * (3.0 * cosio2 - 1.0) / (rteosq * omeosq);
        var del = d1 / (ak * ak);
        var adel = ak * (1.0 - del * del - del * (1.0 / 3.0 + 134.0 * del * del / 81.0));
        del = d1 / (adel * adel);
        _noUnkozai = noKozai / (1.0 + del);

        var ao = Math.Pow(Xke / _noUnkozai, X2o3);
        var sinio = Math.Sin(_inclo);
        var po = ao * omeosq;
        var con42 = 1.0 - 5.0 * cosio2;
        _con41 = -con42 - cosio2 - cosio2;
        var posq = po * po;
        var rp = ao * (1.0 - _ecco);
        // ---- end initl ----

        if (tle.IsDeepSpace || 2 * Math.PI / _noUnkozai >= 225.0)
        {
            InitError = Sgp4Error.DeepSpaceNotSupported;
            return;
        }

        _isimp = rp < 220.0 / RadiusEarthKm + 1.0;
        var sfour = ss;
        var qzms24 = qzms2t;
        var perige = (rp - 1.0) * RadiusEarthKm;

        // 近地點低於 156 km 時調整 s 與 qoms2t
        if (perige < 156.0)
        {
            sfour = perige - 78.0;
            if (perige < 98.0)
            {
                sfour = 20.0;
            }

            var qzms24temp = (120.0 - sfour) / RadiusEarthKm;
            qzms24 = qzms24temp * qzms24temp * qzms24temp * qzms24temp;
            sfour = sfour / RadiusEarthKm + 1.0;
        }

        var pinvsq = 1.0 / posq;

        var tsi = 1.0 / (ao - sfour);
        _eta = ao * _ecco * tsi;
        var etasq = _eta * _eta;
        var eeta = _ecco * _eta;
        var psisq = Math.Abs(1.0 - etasq);
        var coef = qzms24 * Math.Pow(tsi, 4.0);
        var coef1 = coef / Math.Pow(psisq, 3.5);
        var cc2 = coef1 * _noUnkozai * (ao * (1.0 + 1.5 * etasq + eeta *
            (4.0 + etasq)) + 0.375 * J2 * tsi / psisq * _con41 *
            (8.0 + 3.0 * etasq * (8.0 + etasq)));
        _cc1 = _bstar * cc2;
        var cc3 = 0.0;
        if (_ecco > 1.0e-4)
        {
            cc3 = -2.0 * coef * tsi * J3oj2 * _noUnkozai * sinio / _ecco;
        }

        _x1mth2 = 1.0 - cosio2;
        _cc4 = 2.0 * _noUnkozai * coef1 * ao * omeosq *
            (_eta * (2.0 + 0.5 * etasq) + _ecco *
            (0.5 + 2.0 * etasq) - J2 * tsi / (ao * psisq) *
            (-3.0 * _con41 * (1.0 - 2.0 * eeta + etasq *
            (1.5 - 0.5 * eeta)) + 0.75 * _x1mth2 *
            (2.0 * etasq - eeta * (1.0 + etasq)) * Math.Cos(2.0 * _argpo)));
        _cc5 = 2.0 * coef1 * ao * omeosq * (1.0 + 2.75 *
            (etasq + eeta) + eeta * etasq);
        var cosio4 = cosio2 * cosio2;
        var temp1 = 1.5 * J2 * pinvsq * _noUnkozai;
        var temp2 = 0.5 * temp1 * J2 * pinvsq;
        var temp3 = -0.46875 * J4 * pinvsq * pinvsq * _noUnkozai;
        _mdot = _noUnkozai + 0.5 * temp1 * rteosq * _con41 + 0.0625 *
            temp2 * rteosq * (13.0 - 78.0 * cosio2 + 137.0 * cosio4);
        _argpdot = -0.5 * temp1 * con42 + 0.0625 * temp2 *
            (7.0 - 114.0 * cosio2 + 395.0 * cosio4) +
            temp3 * (3.0 - 36.0 * cosio2 + 49.0 * cosio4);
        var xhdot1 = -temp1 * cosio;
        _nodedot = xhdot1 + (0.5 * temp2 * (4.0 - 19.0 * cosio2) +
            2.0 * temp3 * (3.0 - 7.0 * cosio2)) * cosio;
        _omgcof = _bstar * cc3 * Math.Cos(_argpo);
        _xmcof = 0.0;
        if (_ecco > 1.0e-4)
        {
            _xmcof = -X2o3 * coef * _bstar / eeta;
        }

        _nodecf = 3.5 * omeosq * xhdot1 * _cc1;
        _t2cof = 1.5 * _cc1;
        // 傾角 180° 時避免除以零
        if (Math.Abs(cosio + 1.0) > 1.5e-12)
        {
            _xlcof = -0.25 * J3oj2 * sinio * (3.0 + 5.0 * cosio) / (1.0 + cosio);
        }
        else
        {
            _xlcof = -0.25 * J3oj2 * sinio * (3.0 + 5.0 * cosio) / Temp4;
        }

        _aycof = -0.5 * J3oj2 * sinio;
        var delmotemp = 1.0 + _eta * Math.Cos(_mo);
        _delmo = delmotemp * delmotemp * delmotemp;
        _sinmao = Math.Sin(_mo);
        _x7thm1 = 7.0 * cosio2 - 1.0;

        if (!_isimp)
        {
            var cc1sq = _cc1 * _cc1;
            _d2 = 4.0 * ao * tsi * cc1sq;
            var temp = _d2 * tsi * _cc1 / 3.0;
            _d3 = (17.0 * ao + sfour) * temp;
            _d4 = 0.5 * temp * ao * tsi * (221.0 * ao + 31.0 * sfour) * _cc1;
            _t3cof = _d2 + 2.0 * cc1sq;
            _t4cof = 0.25 * (3.0 * _d3 + _cc1 * (12.0 * _d2 + 10.0 * cc1sq));
            _t5cof = 0.2 * (3.0 * _d4 +
                12.0 * _cc1 * _d3 +
                6.0 * _d2 * _d2 +
                15.0 * cc1sq * (2.0 * _d2 + cc1sq));
        }

        // 原碼在初始化結尾傳播到 t = 0 並記錄錯誤碼
        InitError = Propagate(0.0, out _);
    }

    public TleSet Tle { get; }

    public JulianDate Epoch { get; }

    /// <summary>初始化結果：深空軌道為 <see cref="Sgp4Error.DeepSpaceNotSupported"/>，否則為 epoch 時刻的傳播結果。</summary>
    public Sgp4Error InitError { get; }

    public bool IsSupported => InitError != Sgp4Error.DeepSpaceNotSupported;

    public static Sgp4Propagator Create(TleSet tle) => new(tle);

    /// <summary>傳播到指定 UTC 時刻（tsince 依兩段式儒略日計算）。</summary>
    public Sgp4Error Propagate(in JulianDate time, out TemeState state) => Propagate(time.MinutesSince(Epoch), out state);

    /// <summary>
    /// 傳播到 epoch 後 <paramref name="tsinceMinutes"/> 分鐘。熱路徑：不配置記憶體、不擲例外、執行緒安全。
    /// 失敗時回傳錯誤碼，<paramref name="state"/> 為 default。
    /// </summary>
    public Sgp4Error Propagate(double tsinceMinutes, out TemeState state)
    {
        state = default;
        if (InitError == Sgp4Error.DeepSpaceNotSupported)
        {
            return Sgp4Error.DeepSpaceNotSupported;
        }

        var t = tsinceMinutes;

        // ------- 長期重力與大氣阻力 -------
        var xmdf = _mo + _mdot * t;
        var argpdf = _argpo + _argpdot * t;
        var nodedf = _nodeo + _nodedot * t;
        var argpm = argpdf;
        var mm = xmdf;
        var t2 = t * t;
        var nodem = nodedf + _nodecf * t2;
        var tempa = 1.0 - _cc1 * t;
        var tempe = _bstar * _cc4 * t;
        var templ = _t2cof * t2;

        if (!_isimp)
        {
            var delomg = _omgcof * t;
            var delmtemp = 1.0 + _eta * Math.Cos(xmdf);
            var delm = _xmcof * (delmtemp * delmtemp * delmtemp - _delmo);
            var temp0 = delomg + delm;
            mm = xmdf + temp0;
            argpm = argpdf - temp0;
            var t3 = t2 * t;
            var t4 = t3 * t;
            tempa = tempa - _d2 * t2 - _d3 * t3 - _d4 * t4;
            tempe = tempe + _bstar * _cc5 * (Math.Sin(mm) - _sinmao);
            templ = templ + _t3cof * t3 + t4 * (_t4cof + t * _t5cof);
        }

        var nm = _noUnkozai;
        var em = _ecco;
        var inclm = _inclo;

        if (nm <= 0.0)
        {
            return Sgp4Error.MeanMotion;
        }

        var am = Math.Pow(Xke / nm, X2o3) * tempa * tempa;
        nm = Xke / Math.Pow(am, 1.5);
        em = em - tempe;

        if (em >= 1.0 || em < -0.001)
        {
            return Sgp4Error.MeanEccentricity;
        }

        if (em < 1.0e-6)
        {
            em = 1.0e-6;
        }

        mm = mm + _noUnkozai * templ;
        var xlm = mm + argpm + nodem;

        nodem %= TwoPi;
        argpm %= TwoPi;
        xlm %= TwoPi;
        mm = (xlm - argpm - nodem) % TwoPi;

        var sinim = Math.Sin(inclm);
        var cosim = Math.Cos(inclm);

        // ------- 長週期項 -------
        var ep = em;
        var xincp = inclm;
        var argpp = argpm;
        var nodep = nodem;
        var mp = mm;
        var sinip = sinim;
        var cosip = cosim;

        var axnl = ep * Math.Cos(argpp);
        var temp = 1.0 / (am * (1.0 - ep * ep));
        var aynl = ep * Math.Sin(argpp) + temp * _aycof;
        var xl = mp + argpp + nodep + temp * _xlcof * axnl;

        // ------- 解 Kepler 方程式 -------
        var u = (xl - nodep) % TwoPi;
        var eo1 = u;
        var tem5 = 9999.9;
        var ktr = 1;
        double sineo1 = 0.0, coseo1 = 0.0;
        while (Math.Abs(tem5) >= 1.0e-12 && ktr <= 10)
        {
            sineo1 = Math.Sin(eo1);
            coseo1 = Math.Cos(eo1);
            tem5 = 1.0 - coseo1 * axnl - sineo1 * aynl;
            tem5 = (u - aynl * coseo1 + axnl * sineo1 - eo1) / tem5;
            if (Math.Abs(tem5) >= 0.95)
            {
                tem5 = tem5 > 0.0 ? 0.95 : -0.95;
            }

            eo1 = eo1 + tem5;
            ktr = ktr + 1;
        }

        // ------- 短週期前置量 -------
        var ecose = axnl * coseo1 + aynl * sineo1;
        var esine = axnl * sineo1 - aynl * coseo1;
        var el2 = axnl * axnl + aynl * aynl;
        var pl = am * (1.0 - el2);
        if (pl < 0.0)
        {
            return Sgp4Error.SemiLatusRectum;
        }

        var rl = am * (1.0 - ecose);
        var rdotl = Math.Sqrt(am) * esine / rl;
        var rvdotl = Math.Sqrt(pl) / rl;
        var betal = Math.Sqrt(1.0 - el2);
        temp = esine / (1.0 + betal);
        var sinu = am / rl * (sineo1 - aynl - axnl * temp);
        var cosu = am / rl * (coseo1 - axnl + aynl * temp);
        var su = Math.Atan2(sinu, cosu);
        var sin2u = (cosu + cosu) * sinu;
        var cos2u = 1.0 - 2.0 * sinu * sinu;
        temp = 1.0 / pl;
        var temp1 = 0.5 * J2 * temp;
        var temp2 = temp1 * temp;

        // ------- 短週期修正 -------
        var mrt = rl * (1.0 - 1.5 * temp2 * betal * _con41) +
            0.5 * temp1 * _x1mth2 * cos2u;
        su = su - 0.25 * temp2 * _x7thm1 * sin2u;
        var xnode = nodep + 1.5 * temp2 * cosip * sin2u;
        var xinc = xincp + 1.5 * temp2 * cosip * sinip * cos2u;
        var mvt = rdotl - nm * temp1 * _x1mth2 * sin2u / Xke;
        var rvdot = rvdotl + nm * temp1 * (_x1mth2 * cos2u +
            1.5 * _con41) / Xke;

        // ------- 方向向量 -------
        var sinsu = Math.Sin(su);
        var cossu = Math.Cos(su);
        var snod = Math.Sin(xnode);
        var cnod = Math.Cos(xnode);
        var sini = Math.Sin(xinc);
        var cosi = Math.Cos(xinc);
        var xmx = -snod * cosi;
        var xmy = cnod * cosi;
        var ux = xmx * sinsu + cnod * cossu;
        var uy = xmy * sinsu + snod * cossu;
        var uz = sini * sinsu;
        var vx = xmx * cossu - cnod * sinsu;
        var vy = xmy * cossu - snod * sinsu;
        var vz = sini * cossu;

        // 衰減：軌道半徑小於地球半徑
        if (mrt < 1.0)
        {
            return Sgp4Error.Decayed;
        }

        state = new TemeState(
            new Vector3d(mrt * ux * RadiusEarthKm, mrt * uy * RadiusEarthKm, mrt * uz * RadiusEarthKm),
            new Vector3d((mvt * ux + rvdot * vx) * VkmPerSec, (mvt * uy + rvdot * vy) * VkmPerSec, (mvt * uz + rvdot * vz) * VkmPerSec));
        return Sgp4Error.None;
    }
}
