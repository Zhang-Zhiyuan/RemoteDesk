using System.Globalization;
using Xunit;

namespace RemoteDesk.Tests;

public sealed class RelayPathStabilityTests
{
    [Fact]
    public void SharedCrossPlatformVectors()
    {
        var state = new RelayPathStability();
        foreach (string line in File.ReadLines(Path.Combine(AppContext.BaseDirectory, "relay-path-stability.tsv")))
        {
            if (line.StartsWith('#') || string.IsNullOrWhiteSpace(line)) continue;
            string[] fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            long now = long.Parse(fields[1], CultureInfo.InvariantCulture);
            switch (fields[0])
            {
                case "reset": state = new(); state.Connected(fields[2], now); break;
                case "round": state.ObserveRound(now, new Dictionary<string, double>
                    { ["wifi"] = Value(fields[2]), ["wired"] = Value(fields[3]) }); break;
                case "connected": state.Connected(fields[2], now, fields[3] == "-" ? null : fields[3]); break;
                default: throw new InvalidDataException(line);
            }
            string expected = fields[^1];
            Assert.True(state.Preferred(now) == expected, $"{line}; actual={state.Preferred(now)}");
        }
        static double Value(string value) => value switch
        {
            "inf" => double.PositiveInfinity, "nan" => double.NaN,
            _ => double.Parse(value, CultureInfo.InvariantCulture)
        };
    }

    [Fact]
    public void ProbeFrequencyAndIdleHistoryAreBounded()
    {
        var state = new RelayPathStability();
        state.Connected("wifi", 0);
        Assert.True(state.BeginProbe(0));
        Assert.False(state.BeginProbe(29_999));
        Assert.True(state.BeginProbe(30_000));
        Assert.Equal("wifi", state.Preferred(599_999));
        Assert.Null(state.Preferred(1_199_999));
    }

    [Theory]
    [InlineData(20, 12, true)]
    [InlineData(100, 80, false)]
    [InlineData(20, 14, false)]
    [InlineData(40, 30, true)]
    [InlineData(0, 0, false)]
    [InlineData(40, double.NaN, false)]
    [InlineData(40, double.PositiveInfinity, false)]
    public void ImprovementNeedsAbsoluteAndRelativeMargin(double current, double candidate, bool expected) =>
        Assert.Equal(expected, RelayPathStability.WorthSwitching(current, candidate));

    [Fact]
    public void MissingCurrentRoundBreaksConsecutiveChallenge()
    {
        var state = new RelayPathStability();
        state.Connected("wifi", 0);
        for (int i = 0; i < 4; i++) state.ObserveRound(180_000 + i * 30_000,
            new Dictionary<string, double> { ["wifi"] = 40, ["wired"] = 12 });
        state.ObserveRound(300_000, new Dictionary<string, double> { ["wired"] = 12 });
        state.ObserveRound(330_000, new Dictionary<string, double> { ["wifi"] = 40, ["wired"] = 12 });
        Assert.Equal("wifi", state.Preferred(330_000));
    }
}
