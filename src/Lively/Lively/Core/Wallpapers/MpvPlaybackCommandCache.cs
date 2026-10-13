#nullable enable
using System;
using System.Globalization;

namespace Lively.Core.Wallpapers
{
    /// <summary>
    /// Coalesces playback policy polling while periodically reasserting the desired state.
    /// Failed writes are never cached, so the next policy tick can retry.
    /// </summary>
    internal sealed class MpvPlaybackCommandCache
    {
        private readonly object sync = new();
        private readonly Func<string, bool> trySend;
        private readonly TimeProvider clock;
        private readonly TimeSpan refreshInterval;
        private bool? paused;
        private int? volume;
        private long pauseTimestamp;
        private long volumeTimestamp;

        public MpvPlaybackCommandCache(Func<string, bool> trySend, TimeProvider? clock = null,
            TimeSpan? refreshInterval = null)
        {
            this.trySend = trySend ?? throw new ArgumentNullException(nameof(trySend));
            this.clock = clock ?? TimeProvider.System;
            this.refreshInterval = refreshInterval ?? TimeSpan.FromSeconds(30);
            if (this.refreshInterval <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(refreshInterval));
        }

        public bool SetPaused(bool value)
        {
            lock (sync)
            {
                if (paused == value && clock.GetElapsedTime(pauseTimestamp) < refreshInterval)
                    return true;

                var command = value
                    ? "{\"command\":[\"set_property\",\"pause\",true]}\n"
                    : "{\"command\":[\"set_property\",\"pause\",false]}\n";
                if (!trySend(command))
                {
                    paused = null;
                    return false;
                }

                paused = value;
                pauseTimestamp = clock.GetTimestamp();
                return true;
            }
        }

        public bool SetVolume(int value)
        {
            lock (sync)
            {
                if (volume == value && clock.GetElapsedTime(volumeTimestamp) < refreshInterval)
                    return true;

                if (!trySend("{\"command\":[\"set_property\",\"volume\"," + value.ToString(CultureInfo.InvariantCulture) + "]}\n"))
                {
                    volume = null;
                    return false;
                }

                volume = value;
                volumeTimestamp = clock.GetTimestamp();
                return true;
            }
        }

        public void Reset()
        {
            lock (sync)
            {
                paused = null;
                volume = null;
            }
        }
    }
}
