using Lively.Core.Suspend;
using System;
using System.Threading.Tasks;
using Xunit;

namespace Lively.Tests;

public sealed class WallpaperMemorySuspensionTests
{
    [Fact]
    public async Task DisabledFeatureNeverCapturesOrReleasesAPlayer()
    {
        var test = new Fixture();
        test.Session.ObservePlayback(true);
        test.Clock.Advance(TimeSpan.FromHours(1));
        Assert.False(await test.Tick(TimeSpan.Zero));
        Assert.Equal(0, test.Captures);
        Assert.Equal(0, test.Releases);
    }

    [Fact]
    public async Task RequiresAFullGracePeriodWhileBothPausedAndHidden()
    {
        var test = new Fixture { Hidden = false };
        test.Session.ObservePlayback(true);
        Assert.False(await test.Tick());
        test.Clock.Advance(TimeSpan.FromMinutes(10));
        Assert.False(await test.Tick());
        test.Hidden = true;
        Assert.False(await test.Tick());
        test.Clock.Advance(TimeSpan.FromSeconds(59));
        Assert.False(await test.Tick());
        test.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(await test.Tick());
        Assert.Same(test.Snapshot, test.Session.Snapshot);
        Assert.Equal(1, test.Releases);
        Assert.False(await test.Tick());
    }

    [Fact]
    public async Task ShowingTheDesktopResetsTheHiddenGracePeriod()
    {
        var test = new Fixture();
        test.Session.ObservePlayback(true);
        await test.Tick();
        test.Clock.Advance(TimeSpan.FromSeconds(59));
        test.Hidden = false;
        await test.Tick();
        test.Hidden = true;
        await test.Tick();
        test.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.False(await test.Tick());
        Assert.Equal(0, test.Captures);
    }

    [Theory]
    [InlineData("play")]
    [InlineData("close")]
    [InlineData("visible")]
    [InlineData("play-pause")]
    public async Task ObsoletePositionCapturesCannotCloseThePlayer(string change)
    {
        var test = new Fixture();
        await test.Ready();
        var captured = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = test.Session.TrySuspendAsync(Fixture.Delay, () => test.Hidden,
            () => captured.Task, _ => test.Releases++);

        switch (change)
        {
            case "play": test.Session.ObservePlayback(false); break;
            case "close": test.Session.Invalidate(); break;
            case "visible": test.Hidden = false; break;
            case "play-pause": test.Session.ObservePlayback(false); test.Session.ObservePlayback(true); break;
        }
        captured.SetResult(test.Snapshot);
        Assert.False(await pending);
        Assert.Null(test.Session.Snapshot);
        Assert.Equal(0, test.Releases);
    }

    [Fact]
    public async Task FailedPositionCaptureDoesNotClosePlayerAndCanRetry()
    {
        var test = new Fixture();
        await test.Ready();
        Assert.False(await test.Session.TrySuspendAsync(Fixture.Delay, () => true,
            () => Task.FromResult<object?>(null), _ => test.Releases++));
        Assert.Equal(0, test.Releases);
        Assert.True(await test.Tick());
    }

    [Fact]
    public async Task DesktopVisibilityDuringCaptureRequiresANewFullGracePeriod()
    {
        var test = new Fixture();
        await test.Ready();
        var capture = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = test.Session.TrySuspendAsync(Fixture.Delay, () => test.Hidden, () => capture.Task, _ => test.Releases++);
        test.Hidden = false;
        await test.Tick();
        test.Hidden = true;
        await test.Tick();
        capture.SetResult(test.Snapshot);

        Assert.False(await pending);
        Assert.Equal(0, test.Releases);
    }

    [Fact]
    public async Task RestoringAPausedWallpaperStartsANewGracePeriod()
    {
        var test = new Fixture();
        await test.Ready();
        await test.Tick();
        Assert.True(await test.Session.TryResumeAsync((_, _) => Task.FromResult(true)));
        Assert.True(test.Session.IsPaused);
        Assert.False(await test.Tick());
        test.Clock.Advance(Fixture.Delay);
        Assert.True(await test.Tick());
        Assert.Equal(2, test.Releases);
    }

    [Fact]
    public async Task RepeatedTicksDuringCaptureDoNotStartAnotherRelease()
    {
        var test = new Fixture();
        await test.Ready();
        var capture = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = test.Session.TrySuspendAsync(Fixture.Delay, () => true, () => capture.Task, _ => test.Releases++);
        for (var tick = 0; tick < 100; tick++)
            Assert.False(await test.Tick());

        capture.SetResult(test.Snapshot);
        Assert.True(await pending);
        Assert.Equal(1, test.Releases);
        Assert.Equal(0, test.Captures);
    }

    [Fact]
    public async Task FailedRestorePreservesSelectionAndBacksOffBeforeRetry()
    {
        var test = new Fixture();
        await test.Ready();
        Assert.True(await test.Tick());
        var attempts = 0;
        Task<bool> Restore(object saved, Func<bool> stillSelected)
        {
            Assert.Same(test.Snapshot, saved);
            Assert.Same(saved, test.Session.Snapshot);
            Assert.True(stillSelected());
            return Task.FromResult(++attempts > 1);
        }

        test.Session.ObservePlayback(false);
        Assert.False(await test.Session.TryResumeAsync(Restore));
        Assert.Same(test.Snapshot, test.Session.Snapshot);
        Assert.False(await test.Session.TryResumeAsync(Restore));
        test.Clock.Advance(TimeSpan.FromSeconds(5));
        Assert.True(await test.Session.TryResumeAsync(Restore));
        Assert.Null(test.Session.Snapshot);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task ExplicitCloseDuringRestoreCannotResurrectTheWallpaper()
    {
        var test = new Fixture();
        await test.Ready();
        await test.Tick();
        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Func<bool>? selected = null;
        var pending = test.Session.TryResumeAsync(async (_, stillSelected) =>
        {
            selected = stillSelected;
            await ready.Task;
            return stillSelected();
        });

        test.Session.Invalidate();
        Assert.False(selected!());
        ready.SetResult(true);
        Assert.False(await pending);
        Assert.Null(test.Session.Snapshot);
        Assert.False(await test.Session.TryResumeAsync((_, _) => throw new InvalidOperationException()));
    }

    [Fact]
    public async Task PartialReleaseFailureRetainsRecoveryInformation()
    {
        var test = new Fixture();
        await test.Ready();
        await Assert.ThrowsAsync<InvalidOperationException>(() => test.Session.TrySuspendAsync(Fixture.Delay, () => true,
            () => Task.FromResult<object?>(test.Snapshot), _ => throw new InvalidOperationException()));
        Assert.Same(test.Snapshot, test.Session.Snapshot);
        Assert.True(await test.Session.TryResumeAsync((_, _) => Task.FromResult(true)));
    }

    private sealed class Fixture
    {
        public static readonly TimeSpan Delay = TimeSpan.FromSeconds(60);
        public ManualClock Clock { get; } = new();
        public WallpaperMemorySuspension<object> Session { get; }
        public object Snapshot { get; } = new();
        public bool Hidden { get; set; } = true;
        public int Captures { get; private set; }
        public int Releases { get; set; }
        public Fixture() => Session = new(Clock);
        public Task<bool> Tick(TimeSpan? delay = null) => Session.TrySuspendAsync(delay ?? Delay, () => Hidden,
            () => { Captures++; return Task.FromResult<object?>(Snapshot); }, _ => Releases++);
        public async Task Ready()
        {
            Session.ObservePlayback(true);
            Assert.False(await Tick());
            Clock.Advance(Delay);
        }
    }

    private sealed class ManualClock : TimeProvider
    {
        private long timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => timestamp;
        public void Advance(TimeSpan value) => timestamp += value.Ticks;
    }
}
