using SkyPass.Core.Tle;

namespace SkyPass.Tests.Support;

/// <summary>由 tools/refgen/gen_sgp4.py 產生的 SGP4 參考資料。</summary>
public sealed record Sgp4ReferenceRow(
    string Case, string Source, string Line1, string Line2, double TsinceMin, int Error,
    double Rx, double Ry, double Rz, double Vx, double Vy, double Vz)
{
    public TleSet Tle => TleParser.Parse($"{Line1}\n{Line2}").Sets.Single();

    public static IReadOnlyList<Sgp4ReferenceRow> Load(string fileName) =>
        TestData.ReadCsv(fileName)
            .Select(c => new Sgp4ReferenceRow(
                c[0], c[1], c[3], c[4], TestData.D(c[5]), int.Parse(c[6]),
                TestData.D(c[7]), TestData.D(c[8]), TestData.D(c[9]),
                TestData.D(c[10]), TestData.D(c[11]), TestData.D(c[12])))
            .ToList();
}
