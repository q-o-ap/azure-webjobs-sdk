// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Microsoft.Extensions.Logging;

namespace Microsoft.Azure.WebJobs.Host.Scale
{
    /// <summary>
    /// Defines a service that can be used to monitor process health for the
    /// primary host and any child processes.
    /// </summary>
    public interface IHostHealthMonitor
    {
        /// <summary>
        /// Register the specified child process for monitoring.
        /// </summary>
        /// <param name="process">The process to register.</param>
        void Register(Process process);

        /// <summary>
        /// Unregister the specified child process from monitoring.
        /// </summary>
        /// <param name="process">The process to unregister.</param>
        void Unregister(Process process);

        /// <summary>
        /// Get the current host health status.
        /// </summary>
        /// <param name="logger">If specified, results will be logged to this logger.</param>
        /// <returns>The status.</returns>
        HostHealthResult GetStatus(ILogger logger = null);
    }

    /// <summary>
    /// Represents the health status of the host process.
    /// </summary>
    public class HostHealthResult
    {
        /// <summary>
        /// Gets the current health status of the host.
        /// </summary>
        public HealthStatus Status { get; set; }
    }

    internal class DefaultHostHealthMonitor : IHostHealthMonitor, IDisposable
    {
        private const int BytesPerGB = 1024 * 1024 * 1024;
        private const float ElasticPremiumMemoryGBPerCore = 3.5F;
        private const float DynamicMemoryGBPerCore = 3.5F;
        private const int MinSampleCount = 5;
        private const float _maxCpuThreshold = 0.80F;
        private const float _maxMemoryThreshold = 0.90F;
        private bool _shouldMonitorMemory = false;
        private double _maxMemoryThresholdBytes;
        private bool _disposed;
        private ProcessMonitor _processMonitor;
        private List<ProcessMonitor> _childProcessMonitors = new List<ProcessMonitor>();
        private object _syncLock = new object();

        public DefaultHostHealthMonitor()
        {
            _processMonitor = new ProcessMonitor(Process.GetCurrentProcess());
            _processMonitor.Start();

            _shouldMonitorMemory = Utility.IsDynamicSku();
            if (_shouldMonitorMemory)
            {
                _maxMemoryThresholdBytes = GetMemoryBytesThreshold();
            }
        }

        public void Register(Process process)
        {
            var monitor = new ProcessMonitor(process);
            monitor.Start();

            lock (_syncLock)
            {
                _childProcessMonitors.Add(monitor);
            }
        }

        public void Unregister(Process process)
        {
            ProcessMonitor monitor = null;
            lock (_syncLock)
            {
                monitor = _childProcessMonitors.SingleOrDefault(p => p.Process == process);
                if (monitor != null)
                {
                    _childProcessMonitors.Remove(monitor);
                }
            }
            
            monitor?.Dispose();
        }

        public HostHealthResult GetStatus(ILogger logger = null)
        {
            var healthResult = new HostHealthResult
            {
                Status = HealthStatus.Unknown
            };

            // get the current stats for the host process
            var hostProcessStats = _processMonitor.GetStats();

            // get the current stats for any child processes
            ProcessMonitor[] currChildProcessMonitors;
            lock (_syncLock)
            {
                // snapshot the current set of child monitors
                currChildProcessMonitors = _childProcessMonitors.ToArray();
            }
            var childProcessStats = currChildProcessMonitors.Select(p => p.GetStats());

            var cpuStatus = GetCpuStatus(hostProcessStats, childProcessStats, logger);
            var statuses = new List<HealthStatus>
            {
                cpuStatus
            };

            if (_shouldMonitorMemory)
            {
                var memoryStatus = GetMemoryStatus(hostProcessStats, childProcessStats, logger);
                statuses.Add(memoryStatus);
            }

            if (statuses.All(p => p == HealthStatus.Unknown))
            {
                healthResult.Status = HealthStatus.Unknown;
            }
            else if (statuses.Any(p => p == HealthStatus.Overloaded))
            {
                healthResult.Status = HealthStatus.Overloaded;
            }
            else
            {
                healthResult.Status = HealthStatus.Ok;
            }

            return healthResult;
        }

        private HealthStatus GetMemoryStatus(ProcessStats hostProcessStats, IEnumerable<ProcessStats> childProcessStats, ILogger logger = null)
        {
            HealthStatus status = HealthStatus.Unknown;

            if (!_shouldMonitorMemory)
            {
                return status;
            }

            // first compute Memory usage for any registered child processes
            double averageChildMemoryUsageTotal = 0;
            var averageChildMemoryUsages = new List<double>();
            foreach (var currentChildStats in childProcessStats.Where(p => p.MemoryUsageHistory.Count() >= MinSampleCount))
            {
                // take the last N samples
                int currChildProcessMemoryStatsCount = currentChildStats.MemoryUsageHistory.Count();
                var currChildMemoryStats = currentChildStats.MemoryUsageHistory.Skip(currChildProcessMemoryStatsCount - MinSampleCount).Take(MinSampleCount);
                var currChildMemoryStatsAverage = currChildMemoryStats.Average();
                averageChildMemoryUsages.Add(currChildMemoryStatsAverage);

                string formattedLoadHistory = string.Join(",", currChildMemoryStats);
                logger?.HostProcessMemoryUsage(formattedLoadHistory, currChildMemoryStatsAverage, currChildMemoryStats.Max());
            }
            averageChildMemoryUsageTotal = averageChildMemoryUsages.Sum();

            // calculate the aggregate usage across host + child processes
            int hostProcessMemoryStatsCount = hostProcessStats.MemoryUsageHistory.Count();
            if (hostProcessMemoryStatsCount > MinSampleCount)
            {
                var lastSamples = hostProcessStats.MemoryUsageHistory.Skip(hostProcessMemoryStatsCount - MinSampleCount).Take(MinSampleCount);

                string formattedUsageHistory = string.Join(",", hostProcessStats.MemoryUsageHistory);
                var hostAverageMemoryUsage = lastSamples.Average();
                logger?.HostProcessMemoryUsage(formattedUsageHistory, Math.Round(hostAverageMemoryUsage), Math.Round(lastSamples.Max()));

                // compute the aggregate average memory usage for host + children for the last MinSampleCount samples
                var aggregateAverage = Math.Round(hostAverageMemoryUsage + averageChildMemoryUsageTotal);
                logger?.HostAggregateMemoryUsage(aggregateAverage);

                // if the average is above our threshold, return true (we're overloaded)
                // TODO: need to pull threshold values from options
                if (aggregateAverage >= _maxMemoryThresholdBytes)
                {
                    logger?.HostMemoryThresholdExceeded(aggregateAverage, _maxMemoryThresholdBytes);
                    return HealthStatus.Overloaded;
                }
                else
                {
                    return HealthStatus.Ok;
                }
            }

            return status;
        }

        private HealthStatus GetCpuStatus(ProcessStats hostProcessStats, IEnumerable<ProcessStats> childProcessStats, ILogger logger = null)
        {
            HealthStatus status = HealthStatus.Unknown;

            // first compute CPU usage for any registered child processes
            double childAverageCpuTotal = 0;
            var averageChildCpuStats = new List<double>();
            foreach (var currentStatus in childProcessStats.Where(p => p.CpuLoadHistory.Count() >= MinSampleCount))
            {
                // take the last N samples
                int currChildProcessCpuStatsCount = currentStatus.CpuLoadHistory.Count();
                var currChildCpuStats = currentStatus.CpuLoadHistory.Skip(currChildProcessCpuStatsCount - MinSampleCount).Take(MinSampleCount);
                var currChildCpuStatsAverage = currChildCpuStats.Average();
                averageChildCpuStats.Add(currChildCpuStatsAverage);

                string formattedLoadHistory = string.Join(",", currChildCpuStats);
                logger?.HostProcessCpuStats(formattedLoadHistory, currChildCpuStatsAverage, currChildCpuStats.Max());
            }
            childAverageCpuTotal = averageChildCpuStats.Sum();

            // calculate the aggregate load of host + child processes
            int hostProcessCpuStatsCount = hostProcessStats.CpuLoadHistory.Count();
            if (hostProcessCpuStatsCount > MinSampleCount)
            {
                var lastSamples = hostProcessStats.CpuLoadHistory.Skip(hostProcessCpuStatsCount - MinSampleCount).Take(MinSampleCount);

                string formattedLoadHistory = string.Join(",", lastSamples);
                var hostAverageCpu = lastSamples.Average();
                logger?.HostProcessCpuStats(formattedLoadHistory, Math.Round(hostAverageCpu), Math.Round(lastSamples.Max()));

                // compute the aggregate average CPU usage for host + children for the last MinSampleCount samples
                var aggregateAverage = Math.Round(hostAverageCpu + childAverageCpuTotal);
                logger?.HostAggregateCpuLoad(aggregateAverage);

                // if the average is above our threshold, return true (we're overloaded)
                var adjustedThreshold = _maxCpuThreshold * 100;
                if (aggregateAverage >= adjustedThreshold)
                {
                    logger?.HostCpuThresholdExceeded(aggregateAverage, adjustedThreshold);
                    return HealthStatus.Overloaded;
                }
                else
                {
                    return HealthStatus.Ok;
                }
            }

            return status;
        }

        private double GetMemoryBytesThreshold()
        {
            double maxAllowedMemoryBytes = 0;

            if (Utility.IsConsumptionSku())
            {
                // Dynamic plan has a single 1.5 GB memory allowance for the single core
                maxAllowedMemoryBytes = DynamicMemoryGBPerCore * BytesPerGB;
            }
            else
            {
                // for ElasticPremium we get 3.5GB per core
                int cores = Utility.GetEffectiveCoresCount();
                maxAllowedMemoryBytes = ElasticPremiumMemoryGBPerCore * cores * BytesPerGB;
            }

            // threshold is a percentage of the upper limit
            return maxAllowedMemoryBytes * _maxMemoryThreshold;
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    _processMonitor?.Dispose();

                    foreach (var childMonitor in _childProcessMonitors)
                    {
                        childMonitor?.Dispose();
                    }
                }

                _disposed = true;
            }
        }

        public void Dispose()
        {
            Dispose(true);
        }
    }
}
