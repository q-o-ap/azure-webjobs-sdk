// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System;

namespace Microsoft.Azure.WebJobs.Host.Scale
{
    public class ConcurrencyStatus
    {
        private const int FailedAdjustmentQuietWindowSeconds = 30;
        private const int AdjustmentRunWindowSeconds = 10;
        private const int MinAdjustmentFrequencySeconds = 5;

        private readonly ConcurrencyManager _concurrencyManager;

        private object _syncLock = new object();
        private int _adjustmentRunDirection;
        private int _adjustmentRunCount;
        private DateTime? _lastFailedAdjustmentTimestamp;
        private int _maxConcurrentExecutionsSinceLastAdjustment;
        private DateTime _lastAdjustmentTimestamp;

        public ConcurrencyStatus(ConcurrencyManager concurrencyManager)
        {
            _concurrencyManager = concurrencyManager;
            CurrentParallelism = 1;
            OutstandingInvocations = 0;
            _lastAdjustmentTimestamp = DateTime.UtcNow;
            _maxConcurrentExecutionsSinceLastAdjustment = 0;
            _adjustmentRunDirection = 1;
        }

        /// <summary>
        /// Gets the recommended amount of new work this function can safely process.
        /// When throttling is enabled, this may return 0 meaning no new work should be
        /// started.
        /// </summary>
        public int FetchCount
        {
            get
            {
                if (_concurrencyManager.IsThrottleEnabled() || OutstandingInvocations > CurrentParallelism)
                {
                    // we can't take any work right now
                    return 0;
                }
                else
                {
                    // no throttles are enabled, so we can take work up to the current concurrency level
                    return CurrentParallelism - OutstandingInvocations;
                }
            }
        }

        /// <summary>
        /// Gets the current level of parallelism for this function. This adjusts
        /// dynamically over time.
        /// </summary>
        public int CurrentParallelism { get; private set; }

        /// <summary>
        /// The current number of in progress invocations of this function. I.e. this
        /// is the current effective degree of actual parallelism.
        /// </summary>
        public int OutstandingInvocations { get; private set; }

        internal bool CanAdjustConcurrency()
        {
            // don't adjust too often, either up or down
            // if we've made an adjustment recently
            TimeSpan? timeSinceLastAdjustment = DateTime.UtcNow - _lastAdjustmentTimestamp;
            return timeSinceLastAdjustment.Value.TotalSeconds > MinAdjustmentFrequencySeconds;
        }

        internal bool CanDecrease()
        {
            return CurrentParallelism > 1;
        }

        internal bool CanIncrease(int maxDegreeOfParallelism)
        {
            var timeSinceLastFailedAdjustment = DateTime.UtcNow - _lastFailedAdjustmentTimestamp;
            if (timeSinceLastFailedAdjustment != null && timeSinceLastFailedAdjustment.Value.TotalSeconds < FailedAdjustmentQuietWindowSeconds)
            {
                // if we've had a recent failed adjustment, we'll avoid any increases for a while
                return false;
            }
            else
            {
                // after the interval expires, clear it
                _lastFailedAdjustmentTimestamp = null;
            }

            if (_maxConcurrentExecutionsSinceLastAdjustment < CurrentParallelism)
            {
                // We only want to increase if we're fully utilizing our current concurrency level.
                // E.g. if we increased to a high concurrency level, then events slowed to a trickle,
                // we wouldn't want to keep increasing.
                return false;
            }

            return CurrentParallelism < maxDegreeOfParallelism;
        }

        internal void IncreaseParallelism()
        {
            int delta = GetNextAdjustment(1);
            AdjustParallelism(delta);
        }

        internal void DecreaseParallelism()
        {
            int delta = GetNextAdjustment(-1);
            AdjustParallelism(-1 * delta);
        }

        internal void FunctionStarted()
        {
            lock (_syncLock)
            {
                OutstandingInvocations++;

                if (OutstandingInvocations > _maxConcurrentExecutionsSinceLastAdjustment)
                {
                    // record the high water mark for utilized concurrency this interval
                    _maxConcurrentExecutionsSinceLastAdjustment = OutstandingInvocations;
                }
            }
        }

        internal void FunctionCompleted()
        {
            lock (_syncLock)
            {
                OutstandingInvocations--;
            }
        }

        private int GetNextAdjustment(int direction)
        {
            // keep track of consecutive adjustment runs in the same direction
            // so we can increase velocity
            var timeSinceLastAdjustment = DateTime.UtcNow - _lastAdjustmentTimestamp;
            int adjustmentRunCount = _adjustmentRunCount;
            if (_adjustmentRunDirection != direction || timeSinceLastAdjustment.TotalSeconds > AdjustmentRunWindowSeconds)
            {
                // clear our adjustment run if we change direction or too
                // much time has elapsed since last change
                // when we change directions, our last move might have been large,
                // but well move back in the other direction slowly
                adjustmentRunCount = _adjustmentRunCount = 0;
            }
            else
            {
                // increment for next cycle
                _adjustmentRunCount++;
            }
            _adjustmentRunDirection = direction;

            // based on consecutive moves in the same direction, we'll adjust velocity
            int speedFactor = Math.Min(5, adjustmentRunCount);
            return 1 + speedFactor;
        }

        private void AdjustParallelism(int delta)
        {
            if (delta < 0)
            {
                // if we're adjusting down, take a timestamp to delay any further
                // scale-up attempts for a period to allow things to stabilize
                _lastFailedAdjustmentTimestamp = DateTime.UtcNow;
            }

            // ensure we don't adjust below 1
            int newParallelism = CurrentParallelism + delta;
            newParallelism = Math.Max(1, newParallelism);

            CurrentParallelism = newParallelism;
            _lastAdjustmentTimestamp = DateTime.UtcNow;

            lock (_syncLock)
            {
                _maxConcurrentExecutionsSinceLastAdjustment = 0;
            }
        }
    }
}
