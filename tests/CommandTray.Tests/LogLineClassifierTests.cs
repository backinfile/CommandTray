using CommandTray.Services;

namespace CommandTray.Tests;

public sealed class LogLineClassifierTests
{
    [Theory]
    [InlineData("2026-08-20T12:00:00Z [ERR] failed")]
    [InlineData("level=ERROR request failed")]
    [InlineData("发生错误，操作已终止")]
    public void Classify_ReturnsErrorForErrorMarkers(string line)
    {
        Assert.Equal(LogLineSeverity.Error, LogLineClassifier.Classify(line));
    }

    [Theory]
    [InlineData("2026-08-20T12:00:00Z [WARN] retrying")]
    [InlineData("[WRN] disk space is low")]
    [InlineData("WARNING: response was slow")]
    [InlineData("警告：磁盘空间不足")]
    public void Classify_ReturnsWarningForWarningMarkers(string line)
    {
        Assert.Equal(LogLineSeverity.Warning, LogLineClassifier.Classify(line));
    }

    [Theory]
    [InlineData("2026-08-20T12:00:00Z [OUT] server started")]
    [InlineData("errorless operation")]
    [InlineData("warningsEnabled=true")]
    public void Classify_DoesNotMatchMarkerFragments(string line)
    {
        Assert.Equal(LogLineSeverity.Normal, LogLineClassifier.Classify(line));
    }

    [Fact]
    public void Classify_PrioritizesErrorWhenBothMarkersArePresent()
    {
        Assert.Equal(LogLineSeverity.Error, LogLineClassifier.Classify("WARNING escalated to ERROR"));
    }
}
