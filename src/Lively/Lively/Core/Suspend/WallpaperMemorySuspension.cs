#nullable enable
using System;
using System.Threading.Tasks;

namespace Lively.Core.Suspend
{
    /// <summary>
    /// Tracks a delayed, reversible suspension without changing the user's wallpaper selection.
    /// Callers supply player lifecycle operations; failed captures and restores remain retryable.
    /// </summary>
    internal sealed class WallpaperMemorySuspension<T> where T : class
    {
        private readonly object sync = new();
        private readonly TimeProvider clock;
        private bool paused;
        private long? hiddenTimestamp;
        private long pauseEpoch;
        private long generation;
        private bool busy;
        private long? restoreAttempt;
        private T? snapshot;

        public WallpaperMemorySuspension(TimeProvider? clock = null)
        {
            this.clock = clock ?? TimeProvider.System;
        }

        public T? Snapshot { get { lock (sync) return snapshot; } }
        public bool IsPaused { get { lock (sync) return paused; } }

        public void ObservePlayback(bool isPaused)
        {
            lock (sync)
            {
                if (paused == isPaused)
                    return;

                paused = isPaused;
                hiddenTimestamp = null;
                pauseEpoch++;
            }
        }

        /// <summary>Explicit selection, close and shutdown cancel any in-flight automatic restore.</summary>
        public void Invalidate()
        {
            lock (sync)
            {
                generation++;
                pauseEpoch++;
                paused = false;
                hiddenTimestamp = null;
                snapshot = null;
                restoreAttempt = null;
            }
        }

        public async Task<bool> TrySuspendAsync(TimeSpan delay, Func<bool> isHidden,
            Func<Task<T?>> capture, Action<T> release)
        {
            long capturedGeneration, capturedEpoch;
            lock (sync)
            {
                if (snapshot != null)
                    return false;

                if (!paused || delay <= TimeSpan.Zero || !isHidden())
                {
                    hiddenTimestamp = null;
                    return false;
                }
                hiddenTimestamp ??= clock.GetTimestamp();
                if (busy || clock.GetElapsedTime(hiddenTimestamp.Value) < delay)
                    return false;

                busy = true;
                capturedGeneration = generation;
                capturedEpoch = pauseEpoch;
            }

            try
            {
                var captured = await capture();
                lock (sync)
                {
                    if (captured == null || generation != capturedGeneration || pauseEpoch != capturedEpoch || !paused)
                        return false;
                    if (!hiddenTimestamp.HasValue || clock.GetElapsedTime(hiddenTimestamp.Value) < delay)
                        return false;
                    if (!isHidden())
                    {
                        hiddenTimestamp = null;
                        return false;
                    }

                    // Publish first so layout-saving callbacks cannot persist an empty selection.
                    snapshot = captured;
                    // Preserve the snapshot even if a partially completed release throws.
                    release(captured);
                    return true;
                }
            }
            finally
            {
                lock (sync) busy = false;
            }
        }

        public async Task<bool> TryResumeAsync(Func<T, Func<bool>, Task<bool>> restore)
        {
            T captured;
            long capturedGeneration;
            lock (sync)
            {
                if (busy || snapshot == null ||
                    (restoreAttempt.HasValue && clock.GetElapsedTime(restoreAttempt.Value) < TimeSpan.FromSeconds(5)))
                    return false;

                busy = true;
                captured = snapshot;
                capturedGeneration = generation;
                restoreAttempt = clock.GetTimestamp();
            }

            bool StillSelected()
            {
                lock (sync) return generation == capturedGeneration && ReferenceEquals(snapshot, captured);
            }

            try
            {
                if (!await restore(captured, StillSelected))
                    return false;

                lock (sync)
                {
                    if (!StillSelected())
                        return false;

                    snapshot = null;
                    restoreAttempt = null;
                    // A newly restored player receives a full grace period even if it is still paused.
                    hiddenTimestamp = null;
                    return true;
                }
            }
            finally
            {
                lock (sync) busy = false;
            }
        }
    }
}
