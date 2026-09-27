using HuorongAce.Core.Configuration;
using HuorongAce.Core.Monitoring;
using Xunit;
using Xunit.Abstractions;

namespace HuorongAce.Core.Tests;

/// <summary>
/// Port of the Go test <c>internal/monitor/logdb_test.go</c>.
/// </summary>
/// <remarks>
/// These assertions run against the real Huorong quarantine database when
/// Huorong is installed, and skip otherwise. They cover the two regressions
/// that mattered most in the Go build: history must never pop up on start-up,
/// and one incident must not turn into a stream of windows.
/// </remarks>
public sealed class QuarantineReaderTests
{
    private readonly ITestOutputHelper _output;

    public QuarantineReaderTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void RealQuarantineDatabase_CursorsSuppressHistory()
    {
        var path = AppConfig.QuarantineDatabasePath();
        if (!File.Exists(path))
        {
            _output.WriteLine($"未安装火绒（{path} 不存在），跳过。");
            return;
        }

        var config = new AppConfig { LogPath = path };
        using var reader = new HuorongQuarantineReader(path, config);
        reader.Open();

        var (rowId, timestamp) = reader.Cursors;
        _output.WriteLine($"table={reader.TableName} rowid={rowId} ts={timestamp}");

        // 1. Open parks both cursors at the newest row, so history never alerts.
        var first = reader.Poll();
        Assert.Empty(first);

        // 2. Rewinding the rowid cursor alone must still be quiet: the time
        //    cursor is what keeps duplicated historical rows from firing.
        reader.RewindRowIdCursor();
        reader.ClearDedupHistory();
        var replayed = reader.Poll();
        Assert.Empty(replayed);

        // 3. Rewinding both cursors replays everything, de-duplicated by threat.
        reader.RewindRowIdCursor();
        reader.RewindTimeCursor();
        reader.ClearDedupHistory();
        var everything = reader.Poll();

        foreach (var threat in everything)
        {
            _output.WriteLine($"  name={threat.Name} detail={threat.Detail} time={threat.Timestamp:HH:mm:ss}");
        }

        Assert.NotEmpty(everything);
        Assert.Equal(everything.Count, everything.Select(t => t.Name).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void MissingDatabase_ThrowsFileNotFoundException()
    {
        var missing = Path.Combine(Path.GetTempPath(), "huorong-ace-does-not-exist.db");
        var config = new AppConfig { LogPath = missing };
        using var reader = new HuorongQuarantineReader(missing, config);

        Assert.Throws<FileNotFoundException>(() => reader.Open());
    }

    [Fact]
    public void ThreatInfo_SimulatedCarriesGivenName()
    {
        var info = ThreatInfo.Simulated("EICAR-Test-Signature");

        Assert.Equal("EICAR-Test-Signature", info.Name);
        Assert.NotEqual(default, info.Timestamp);
    }
}
