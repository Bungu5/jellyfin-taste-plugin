using Jellyfin.Plugin.Taste.Scoring;

namespace Jellyfin.Plugin.Taste.Tests;

public class PromptRulesTests
{
    private static readonly long Minute = TimeSpan.FromMinutes(1).Ticks;

    [Fact]
    public void OutroSegmentDecidesTheWindow()
    {
        var runtime = 120 * Minute;
        var outro = 110 * Minute;

        Assert.True(PromptRules.InCredits(111 * Minute, runtime, outro, 10));
        Assert.False(PromptRules.InCredits(109 * Minute, runtime, outro, 10));
    }

    [Fact]
    public void LateOutroIsNotWidenedByTheFallback()
    {
        var runtime = 100 * Minute;
        var outro = 98 * Minute;

        Assert.False(PromptRules.InCredits(92 * Minute, runtime, outro, 10));
    }

    [Fact]
    public void FallbackUsesTheLastPercentOfRuntime()
    {
        var runtime = 100 * Minute;

        Assert.True(PromptRules.InCredits(91 * Minute, runtime, null, 10));
        Assert.False(PromptRules.InCredits(89 * Minute, runtime, null, 10));
    }

    [Fact]
    public void FallbackPercentIsClamped()
    {
        var runtime = 100 * Minute;

        Assert.False(PromptRules.InCredits(40 * Minute, runtime, null, 90));
        Assert.True(PromptRules.InCredits(51 * Minute, runtime, null, 90));
    }

    [Fact]
    public void ReachingTheEndAlwaysCounts()
    {
        var runtime = 100 * Minute;

        Assert.True(PromptRules.InCredits(runtime, runtime, 99 * Minute, 10));
        Assert.True(PromptRules.InCredits(runtime - TimeSpan.FromSeconds(1).Ticks, runtime, null, 10));
    }

    [Fact]
    public void UnknownRuntimeWithoutOutroNeverPrompts()
    {
        Assert.False(PromptRules.InCredits(500 * Minute, null, null, 10));
    }

    [Fact]
    public void LastRegularEpisodeIsTheFinale()
    {
        var last = Guid.NewGuid();
        var episodes = new List<EpisodeSlot>
        {
            new(Guid.NewGuid(), 1, 1, false),
            new(Guid.NewGuid(), 1, 2, false),
            new(last, 2, 1, false),
            new(Guid.NewGuid(), 0, 1, false),
        };

        Assert.True(PromptRules.IsFinale(last, episodes));
    }

    [Fact]
    public void EarlierEpisodeIsFinaleWhenEverythingElseIsPlayed()
    {
        var target = Guid.NewGuid();
        var episodes = new List<EpisodeSlot>
        {
            new(Guid.NewGuid(), 1, 1, true),
            new(target, 1, 2, false),
            new(Guid.NewGuid(), 1, 3, true),
        };

        Assert.True(PromptRules.IsFinale(target, episodes));
    }

    [Fact]
    public void MidSeasonEpisodeIsNotTheFinale()
    {
        var target = Guid.NewGuid();
        var episodes = new List<EpisodeSlot>
        {
            new(Guid.NewGuid(), 1, 1, true),
            new(target, 1, 2, false),
            new(Guid.NewGuid(), 1, 3, false),
        };

        Assert.False(PromptRules.IsFinale(target, episodes));
    }

    [Fact]
    public void SpecialsNeverCount()
    {
        var special = Guid.NewGuid();
        var episodes = new List<EpisodeSlot>
        {
            new(Guid.NewGuid(), 1, 1, true),
            new(special, 0, 1, false),
        };

        Assert.False(PromptRules.IsFinale(special, episodes));
    }
}
