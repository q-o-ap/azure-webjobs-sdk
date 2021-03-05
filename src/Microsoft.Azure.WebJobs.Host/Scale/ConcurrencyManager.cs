// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Azure.WebJobs.Logging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Microsoft.Azure.WebJobs.Host.Scale
{
    /// <summary>
    /// Used to implement collaborative dynamic concurrency management between the host and function triggers.
    /// Function listeners can call <see cref="GetStatus"/> within their listener polling loops to determine the
    /// amount of new work that can be fetched. The manager internally adjusts concurrency based on various
    /// health heuristics.
    /// </summary>
    public class ConcurrencyManager
    {
        // TODO: should this be constant for all functions, or taken as a param to Update?
        private const int MaxDegreeOfParallelism = 100;
        private const int MinConsecutiveIncreaseLimit = 5;
        private const int MinConsecutiveDecreaseLimit = 3;
        private const int ThrottleCheckIntervalSeconds = 1;

        private readonly IEnumerable<IConcurrencyThrottleProvider> _throttleProviders;
        private readonly ConcurrentDictionary<string, ConcurrencyStatus> _concurrencyStatuses = new ConcurrentDictionary<string, ConcurrencyStatus>(StringComparer.OrdinalIgnoreCase);
        private readonly ILogger _logger;
        private readonly IOptions<ConcurrencyOptions> _options;

        private int _consecutiveHealthyCount;
        private int _consecutiveUnhealthyCount;
        private bool _throttleEnabled;
        private IEnumerable<ConcurrencyThrottleResult> _lastThrottleResults;
        private DateTime _lastThrottleCheck;

        public ConcurrencyManager(IOptions<ConcurrencyOptions> options, ILoggerFactory loggerFactory, IEnumerable<IConcurrencyThrottleProvider> throttleProviders)
        {
            _options = options;
            _logger = loggerFactory.CreateLogger(LogCategories.Scale);
            _throttleProviders = throttleProviders;
        }

        /// <summary>
        /// Gets a value indicating whether dynamic concurrency is enabled.
        /// </summary>
        public bool Enabled => _options.Value.DynamicConcurrencyEnabled;

        /// <summary>
        /// Returns true if host level throttling is currently enabled.
        /// </summary>
        public bool IsThrottleEnabled()
        {
            // throttle querying of throttle providers so we're not calling them too often
            if ((DateTime.UtcNow - _lastThrottleCheck).TotalSeconds > ThrottleCheckIntervalSeconds)
            {
                UpdateThrottleState();
            }

            return _throttleEnabled;
        }

        /// <summary>
        /// Get the concurrency status for the specified function. 
        /// </summary>
        /// <param name="functionId">This should be the full ID, as returned by <see cref="FunctionDescriptor.ID"/>.</param>
        /// <returns>The updated concurrency status.</returns>
        /// <remarks>
        /// Update shouldn't be called concurrently for the same function ID.
        /// </remarks>
        public ConcurrencyStatus GetStatus(string functionId)
        {
            // because Update won't be called concurrenlty for the same function ID, we can make
            // updates to the function specific status below without locking.
            var concurrencyStatus = GetFunctionConcurrencyStatus(functionId);

            if (!concurrencyStatus.CanAdjustConcurrency())
            {
                // if we've made an adjustment recently, just return
                // current status
                return concurrencyStatus;
            }

            // determine whether any throttles are currently enabled
            bool throttleEnabled = IsThrottleEnabled();

            if (_lastThrottleResults.Any(p => p.ThrottleState == ThrottleState.Unknown))
            {
                // if we're un an unknown state, we'll make no moves
                // however, we will continue to take work at the current concurrency level
                return concurrencyStatus;
            }

            if (!throttleEnabled)
            {
                if (CanIncrease(concurrencyStatus))
                {
                    concurrencyStatus.IncreaseParallelism();
                }
            }
            else if (CanDecrease(concurrencyStatus))
            {
                concurrencyStatus.DecreaseParallelism();
            }

            _logger.LogInformation($"{functionId} Concurrency: {concurrencyStatus.CurrentParallelism}, OutstandingInvocations: {concurrencyStatus.OutstandingInvocations}");

            return concurrencyStatus;
        }

        private void UpdateThrottleState()
        {
            _lastThrottleResults = _throttleProviders.Select(p => p.GetStatus(_logger));
            _throttleEnabled = _lastThrottleResults.Any(p => p.ThrottleState == ThrottleState.Enabled);

            if (!_throttleEnabled)
            {
                // no throttles are enabled so host is healthy
                _consecutiveHealthyCount++;
                _consecutiveUnhealthyCount = 0;
            }
            else
            {
                // one or more throttles enabled, so host is unhealthy
                _consecutiveUnhealthyCount++;
                _consecutiveHealthyCount = 0;
            }

            _lastThrottleCheck = DateTime.UtcNow;
        }

        private bool CanIncrease(ConcurrencyStatus concurrencyStatus)
        {
            if (_consecutiveHealthyCount < MinConsecutiveIncreaseLimit)
            {
                // only increase if we've been healthy for a while
                return false;
            }

            return concurrencyStatus.CanIncrease(MaxDegreeOfParallelism);
        }

        private bool CanDecrease(ConcurrencyStatus concurrencyStatus)
        {
            if (_consecutiveUnhealthyCount < MinConsecutiveDecreaseLimit)
            {
                // only decrease if we've been unhealthy for a while
                return false;
            }

            return concurrencyStatus.CanDecrease();
        }

        internal void FunctionStarted(string functionId)
        {
            var concurrencyStatus = GetFunctionConcurrencyStatus(functionId);
            concurrencyStatus.FunctionStarted();
        }

        internal void FunctionCompleted(string functionId)
        {
            var concurrencyStatus = GetFunctionConcurrencyStatus(functionId);
            concurrencyStatus.FunctionCompleted();
        }

        private ConcurrencyStatus GetFunctionConcurrencyStatus(string functionId)
        {
            return _concurrencyStatuses.GetOrAdd(functionId, new ConcurrencyStatus(this));
        }
    }
}
