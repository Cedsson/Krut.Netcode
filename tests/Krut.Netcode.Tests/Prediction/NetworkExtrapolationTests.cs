using Krut.Netcode;
using Xunit;

namespace Krut.Netcode.Tests;

/// <summary>
/// Dead reckoning past the newest snapshot moves a unit by its velocity times the elapsed time, whatever the gap
/// between the two snapshots it extends.
/// </summary>
public class NetworkExtrapolationTests
{
    private struct Point
    {
        public float X;
    }

    private const float UnitsPerTick = 7f;

    private static NetworkSnapshotInterpolator<Point> Build(int gapTicks, bool carStyleBlend)
    {
        var interpolator = new NetworkSnapshotInterpolator<Point>(delayTicks: 0) { MaxExtrapolationTicks = 12 };
        if (carStyleBlend)
        {
            // The game's blend: from + (to - from) * alpha, alpha = 1 at the newest snapshot (CarStateInterpolation).
            interpolator.Extrapolate = static (from, to, alpha) => new Point { X = from.X + ((to.X - from.X) * alpha) };
        }

        for (int i = 0; i < 4; i++)
            interpolator.Push(100 + (i * gapTicks), new Point { X = UnitsPerTick * i * gapTicks });
        return interpolator;
    }

    [Theory]
    [InlineData(1, 1, false)]
    [InlineData(2, 1, false)]
    [InlineData(2, 3, false)]
    [InlineData(4, 1, false)]
    [InlineData(4, 3, false)]
    [InlineData(2, 1, true)]
    [InlineData(2, 3, true)]
    [InlineData(4, 1, true)]
    [InlineData(4, 3, true)]
    public void Extrapolation_MovesByVelocityTimesElapsedTicks(int gapTicks, int elapsedTicks, bool carStyleBlend)
    {
        NetworkSnapshotInterpolator<Point> interpolator = Build(gapTicks, carStyleBlend);
        int newestTick = 100 + (3 * gapTicks);

        bool ok = interpolator.TrySampleTime(newestTick + elapsedTicks, out Point state, out NetworkSampleMode mode);

        Assert.True(ok);
        Assert.Equal(NetworkSampleMode.Extrapolated, mode);
        Assert.Equal(UnitsPerTick * ((3 * gapTicks) + elapsedTicks), state.X, 3);
    }

    [Fact]
    public void Extrapolation_FractionalTickWithGapOfFour()
    {
        NetworkSnapshotInterpolator<Point> interpolator = Build(gapTicks: 4, carStyleBlend: false);

        interpolator.TrySampleTime(112 + 0.5, out Point state, out NetworkSampleMode mode);

        Assert.Equal(NetworkSampleMode.Extrapolated, mode);
        Assert.Equal(UnitsPerTick * 12.5f, state.X, 3);
    }

    [Fact]
    public void Extrapolation_LimitIsStillCountedInTicks()
    {
        NetworkSnapshotInterpolator<Point> interpolator = Build(gapTicks: 4, carStyleBlend: false);
        interpolator.MaxExtrapolationTicks = 3;

        interpolator.TrySampleTime(112 + 3, out Point atLimit, out NetworkSampleMode atLimitMode);
        interpolator.TrySampleTime(112 + 4, out Point past, out NetworkSampleMode pastMode);

        Assert.Equal(NetworkSampleMode.Extrapolated, atLimitMode);
        Assert.Equal(UnitsPerTick * 15f, atLimit.X, 3);
        Assert.Equal(NetworkSampleMode.Held, pastMode);
        Assert.Equal(UnitsPerTick * 12f, past.X, 3);
    }

    [Fact]
    public void Extrapolation_DuplicateAndOlderSnapshotsNeverReachTheGap()
    {
        NetworkSnapshotInterpolator<Point> interpolator = Build(gapTicks: 2, carStyleBlend: false);

        // A duplicated or reordered packet is refused by the ring, so the two newest ticks always differ.
        interpolator.Push(106, new Point { X = 1000f });
        interpolator.Push(104, new Point { X = 1000f });
        interpolator.TrySampleTime(107, out Point state, out NetworkSampleMode mode);

        Assert.Equal(2, interpolator.LateSnapshotCount);
        Assert.Equal(NetworkSampleMode.Extrapolated, mode);
        Assert.Equal(UnitsPerTick * 7f, state.X, 3);
    }
}
