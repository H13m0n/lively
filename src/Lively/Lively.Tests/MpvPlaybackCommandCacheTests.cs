using Lively.Core.Wallpapers;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Xunit;

namespace Lively.Tests;

public sealed class MpvPlaybackCommandCacheTests
{
    [Fact]
    public void SteadyPlaybackPolicyPollingDoesNotOpenPipesOnEveryTick()
    {
        var commands = new List<string>();
        var clock = new ManualClock();
        var cache = new MpvPlaybackCommandCache(command => { commands.Add(command); return true; }, clock);

        for (var tick = 0; tick < 120; tick++)
        {
            Assert.True(cache.SetPaused(false));
            Assert.True(cache.SetVolume(0));
            clock.Advance(TimeSpan.FromMilliseconds(500));
        }

        // Two initial commands and two periodic reconciliation commands, rather than 240 writes.
        Assert.Equal(4, commands.Count);
    }

    [Fact]
    public void PlaybackAndVolumeChangesAreSentImmediately()
    {
        var commands = new List<string>();
        var cache = new MpvPlaybackCommandCache(command => { commands.Add(command); return true; });

        cache.SetPaused(false);
        cache.SetVolume(75);
        cache.SetPaused(true);
        cache.SetVolume(0);
        cache.SetPaused(false);

        Assert.Equal(new[]
        {
            "{\"command\":[\"set_property\",\"pause\",false]}\n",
            "{\"command\":[\"set_property\",\"volume\",75]}\n",
            "{\"command\":[\"set_property\",\"pause\",true]}\n",
            "{\"command\":[\"set_property\",\"volume\",0]}\n",
            "{\"command\":[\"set_property\",\"pause\",false]}\n"
        }, commands);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PipeUnavailableDuringStartupIsRetriedOnTheNextTick(bool pauseCommand)
    {
        var attempts = 0;
        var cache = new MpvPlaybackCommandCache(_ => ++attempts > 1);
        bool Send() => pauseCommand ? cache.SetPaused(true) : cache.SetVolume(0);

        Assert.False(Send());
        Assert.True(Send());
        Assert.True(Send());
        Assert.Equal(2, attempts);
    }

    [Fact]
    public void ReconciliationFailuresRemainEligibleForRetry()
    {
        var attempts = 0;
        var clock = new ManualClock();
        var cache = new MpvPlaybackCommandCache(_ => ++attempts != 2, clock);

        Assert.True(cache.SetPaused(true));
        clock.Advance(TimeSpan.FromSeconds(30));
        Assert.False(cache.SetPaused(true));
        Assert.True(cache.SetPaused(true));
        Assert.Equal(3, attempts);
    }

    [Fact]
    public void ResetReassertsPropertiesForARecreatedPlayer()
    {
        var attempts = 0;
        var cache = new MpvPlaybackCommandCache(_ => { attempts++; return true; });

        cache.SetPaused(false);
        cache.SetVolume(0);
        cache.Reset();
        cache.SetPaused(false);
        cache.SetVolume(0);

        Assert.Equal(4, attempts);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FailedTransitionInvalidatesPreviouslyCachedState(bool pauseCommand)
    {
        var attempts = 0;
        var cache = new MpvPlaybackCommandCache(_ => ++attempts != 2);
        bool Send(bool changed) => pauseCommand ? cache.SetPaused(changed) : cache.SetVolume(changed ? 0 : 75);

        Assert.True(Send(false));
        Assert.False(Send(true));
        // The failed write may have reached the player before the pipe broke.
        Assert.True(Send(false));
        Assert.Equal(3, attempts);
    }

    [Fact]
    public void ConcurrentPolicyNotificationsDoNotProduceDuplicateWrites()
    {
        var attempts = 0;
        var cache = new MpvPlaybackCommandCache(_ => { attempts++; return true; });

        Parallel.For(0, 100, _ => cache.SetPaused(true));

        Assert.Equal(1, attempts);
    }

    private sealed class ManualClock : TimeProvider
    {
        private long timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => timestamp;
        public void Advance(TimeSpan value) => timestamp += value.Ticks;
    }
}
