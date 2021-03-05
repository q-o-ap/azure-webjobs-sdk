// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace Microsoft.Azure.WebJobs.Host.Scale
{
    public enum ThrottleState
    {
        Unknown,
        Enabled,
        Disabled
    }

    public class ConcurrencyThrottleResult
    {
        public ThrottleState ThrottleState { get; set; }
    }

    /// <summary>
    /// Interface for providing throttle signals to dynamic concurrency control.
    /// </summary>
    public interface IConcurrencyThrottleProvider
    {
        ConcurrencyThrottleResult GetStatus(ILogger logger = null);
    }

    /// <summary>
    /// This throttle provider monitors host process health.
    /// </summary>
    public class HostHealthThrottleProvider : IConcurrencyThrottleProvider
    {
        private readonly IHostHealthMonitor _hostHealthMonitor;

        public HostHealthThrottleProvider(IHostHealthMonitor hostHealthMonitor)
        {
            _hostHealthMonitor = hostHealthMonitor;
        }

        public ConcurrencyThrottleResult GetStatus(ILogger logger = null)
        {
            var result = _hostHealthMonitor.GetStatus(logger);
            ThrottleState throttleState = ThrottleState.Unknown;

            switch (result.Status)
            {
                case HealthStatus.Overloaded:
                    throttleState = ThrottleState.Enabled;
                    break;
                case HealthStatus.Ok:
                    throttleState = ThrottleState.Disabled;
                    break;
                default:
                    throttleState = ThrottleState.Unknown;
                    break;
            }

            return new ConcurrencyThrottleResult
            {
                ThrottleState = throttleState
            };
        }
    }

    /// <summary>
    /// This throttle provider monitors for thread starvation signals. For a healthy signal, it relies on its
    /// internal timer being run consistently. Thus it acts as a "canary in the coal mine" (https://en.wikipedia.org/wiki/Sentinel_species)
    /// for thread starvation situations.
    /// </summary>
    public class ThreadPoolStarvationThrottleProvider : IConcurrencyThrottleProvider, IDisposable
    {
        private const int IntervalMS = 100;
        private const double FailureThreshold = 0.5;

        private object _syncLock = new object();
        private bool _disposedValue;
        private Timer _timer;
        private int _invocations;
        private DateTime _lastCheck;

        public ThreadPoolStarvationThrottleProvider()
        {
            _timer = new Timer(OnTimer, null, 0, IntervalMS);
            _lastCheck = DateTime.UtcNow;
        }

        public void OnTimer(object state)
        {
            lock (_syncLock)
            {
                _invocations++;
            }
        }

        public ConcurrencyThrottleResult GetStatus(ILogger logger = null)
        {
            int missedCount;
            int expectedCount;

            lock (_syncLock)
            {
                // determine how many occurrences we expect to have had since
                // the last check
                TimeSpan duration = DateTime.UtcNow - _lastCheck;
                expectedCount = (int)Math.Floor(duration.TotalMilliseconds / IntervalMS);

                // calculate how many we missed
                missedCount = expectedCount - _invocations;

                _invocations = 0;
                _lastCheck = DateTime.UtcNow;
            }

            // if the number of missed occurrences is over threshold
            // we know things are unhealthy
            int failureThreshold = (int)(expectedCount * FailureThreshold);
            var throttleState = ThrottleState.Disabled;
            if (expectedCount > 0 && missedCount > failureThreshold)
            {
                logger?.LogWarning("Possible thread starvation detected.");
                throttleState = ThrottleState.Enabled; 
            }

            return new ConcurrencyThrottleResult
            {
                ThrottleState = throttleState
            };
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposedValue)
            {
                if (disposing)
                {
                    _timer.Dispose();
                }
                _disposedValue = true;
            }
        }

        public void Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }
    }
}
