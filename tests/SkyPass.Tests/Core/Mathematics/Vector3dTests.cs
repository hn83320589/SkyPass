using SkyPass.Core.Mathematics;

namespace SkyPass.Tests.Core.Mathematics;

public class Vector3dTests
{
    [Fact]
    public void Arithmetic_and_products()
    {
        var a = new Vector3d(1, 2, 3);
        var b = new Vector3d(-4, 5, 0.5);

        Assert.Equal(new Vector3d(-3, 7, 3.5), a + b);
        Assert.Equal(new Vector3d(5, -3, 2.5), a - b);
        Assert.Equal(new Vector3d(2, 4, 6), a * 2);
        Assert.Equal(new Vector3d(-1, -2, -3), -a);
        Assert.Equal(-4 + 10 + 1.5, a.Dot(b));
        Assert.Equal(new Vector3d(2 * 0.5 - 3 * 5, 3 * -4 - 1 * 0.5, 1 * 5 - 2 * -4), a.Cross(b));
        Assert.Equal(Math.Sqrt(14), a.Norm, 15);
    }
}
