// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System;
using System.Diagnostics;

namespace Microsoft.Azure.WebJobs.Host.Scale
{
    /// <summary>
    /// Default implementation, that just delegates to the underlying Process.
    /// </summary>
    internal class DefaultProcessMetricsProvider : IProcessMetricsProvider
    {
        private readonly Process _process;

        public DefaultProcessMetricsProvider(Process process)
        {
            _process = process;
        }

        public TimeSpan TotalProcessorTime
        {
            get
            {
                _process.Refresh();

                return _process.TotalProcessorTime;
            }
        }

        public long MemoryUsageBytes
        {
            get
            {
                _process.Refresh();

                return _process.PrivateMemorySize64;
            }
        }
    }
}
