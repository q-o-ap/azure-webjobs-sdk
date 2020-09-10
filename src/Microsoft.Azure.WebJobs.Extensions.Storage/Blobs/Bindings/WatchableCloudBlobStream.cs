﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.WebJobs.Host.Bindings;
using Microsoft.Azure.WebJobs.Host.Protocols;
using Microsoft.Azure.Storage.Blob;
using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Microsoft.Azure.WebJobs.Host.Blobs.Bindings
{
    internal class WatchableCloudBlobStream : DelegatingCloudBlobStream, IWatcher
    {
        private readonly ILogger _logger;
        private readonly ICloudBlob _blob;
        private readonly Stopwatch _timeWrite = new Stopwatch();
        private readonly IBlobCommitedAction _committedAction;

        private bool _committed;
        private bool _completed;
        private long _countWritten;
        private bool _flushed;
        private bool _logged;

        public WatchableCloudBlobStream(CloudBlobStream inner, IBlobCommitedAction committedAction)
            : base(inner)
        {
            _committedAction = committedAction;
        }

        public WatchableCloudBlobStream(CloudBlobStream inner, IBlobCommitedAction committedAction, ICloudBlob blob, ILogger logger)
            : base(inner)
        {
            _committedAction = committedAction;
            _logger = logger ?? throw new ArgumentNullException("logger");
            _blob = blob ?? throw new ArgumentNullException("blob");
        }

        public override bool CanWrite
        {
            get
            {
                // Be nicer than the SDK. Return false when calling Write would throw.
                return base.CanWrite && !_committed;
            }
        }

        public bool HasCommitted
        {
            get { return _committed; }
        }

        public override Task CommitAsync() => CommitAsync(CancellationToken.None);

        public async Task CommitAsync(CancellationToken cancellationToken)
        {
            _committed = true;

            await base.CommitAsync();

            if (_committedAction != null)
            {
                await _committedAction.ExecuteAsync(cancellationToken);
            }
        }

        public override void Close()
        {
            if (!_committed)
            {
                Task.Run(async () => await CommitAsync()).GetAwaiter().GetResult();
            }

            base.Close();
            Log();
        }

        public override void Flush()
        {
            base.Flush();
            _flushed = true;
        }

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            Task baskTask = base.FlushAsync(cancellationToken);
            return FlushAsyncCore(baskTask);
        }

        private async Task FlushAsyncCore(Task task)
        {
            await task;
            _flushed = true;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            try
            {
                _timeWrite.Start();
                base.Write(buffer, offset, count);
                _countWritten += count;
            }
            finally
            {
                _timeWrite.Stop();
            }
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            Task baseTask = base.WriteAsync(buffer, offset, count, cancellationToken);
            return WriteAsyncCore(baseTask, count);
        }

        private async Task WriteAsyncCore(Task task, int count)
        {
            try
            {
                _timeWrite.Start();
                await task;
                _countWritten += count;
            }
            finally
            {
                _timeWrite.Stop();
            }
        }

        public override void WriteByte(byte value)
        {
            base.WriteByte(value);
            _countWritten++;
        }

        [SuppressMessage("Microsoft.Reliability", "CA2000:Dispose objects before losing scope")]
        public override IAsyncResult BeginWrite(byte[] buffer, int offset, int count, AsyncCallback callback, object state)
        {
            Task baseTask = new TaskFactory().FromAsync(base.BeginWrite, base.EndWrite, buffer, offset, count, state: null);
            return new TaskAsyncResult(WriteAsyncCore(baseTask, count), callback, state);
        }

        public override void EndWrite(IAsyncResult asyncResult)
        {
            TaskAsyncResult result = (TaskAsyncResult)asyncResult;
            result.End();
        }

        /// <summary>Commits the stream as appropriate (when written to or flushed) and finalizes status.</summary>
        /// <param name="cancellationToken">The token to monitor for cancellation requests.</param>
        /// <returns>
        /// A <see cref="Task"/> that commits the stream as appropriate and finalizes status. The result of the task is
        /// <see langword="true"/> when the stream was committed (either by this method or previously); otherwise,
        /// <see langword="false"/>
        /// </returns>
        public async Task<bool> CompleteAsync(CancellationToken cancellationToken)
        {
            if (!_committed && (_countWritten > 0 || _flushed))
            {
                await CommitAsync(cancellationToken);
            }

            _completed = true;

            return _committed;
        }

        public ParameterLog GetStatus()
        {
            if (_committed || _countWritten > 0 || _flushed)
            {
                return new WriteBlobParameterLog { WasWritten = true, BytesWritten = _countWritten };
            }
            else if (_completed)
            {
                return new WriteBlobParameterLog { WasWritten = false, BytesWritten = 0 };
            }
            else
            {
                return null;
            }
        }

        private void Log()
        {
            if (_logged || _logger == null || _blob == null)
            {
                return;
            }

            string name = $"{_blob.Container.Name}/{_blob.Name}";
            string type = $"{_blob.Properties.BlobType}/{_blob.Properties.ContentType}";
            _logger.BlobWriteAccess(name, type, _blob.Properties.ETag, _timeWrite.Elapsed, _countWritten);
            _logged = true;
        }
    }
}
