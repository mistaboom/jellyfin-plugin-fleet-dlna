using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Streaming;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Dlna.Playback;

/// <summary>
/// Owns the progressive video outputs opened by DLNA renderers, which can disconnect
/// without sending a Jellyfin playback-stopped event.
/// </summary>
public sealed class ProgressiveVideoTranscodes
{
    private static readonly TimeSpan ReconnectGrace = TimeSpan.FromSeconds(10);
    private readonly ConcurrentDictionary<string, Writer> _writers = new(StringComparer.Ordinal);
    private readonly ITranscodeManager _manager;
    private readonly ILogger<ProgressiveVideoTranscodes> _logger;
    private readonly TimeProvider _time;

    /// <summary>
    /// Initializes a new instance of the <see cref="ProgressiveVideoTranscodes"/> class.
    /// </summary>
    /// <param name="transcodeManager">Jellyfin's transcode manager.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="timeProvider">The clock used for the reconnect grace period.</param>
    public ProgressiveVideoTranscodes(ITranscodeManager transcodeManager, ILogger<ProgressiveVideoTranscodes> logger, TimeProvider? timeProvider = null)
    {
        _manager = transcodeManager;
        _logger = logger;
        _time = timeProvider ?? TimeProvider.System;
    }

    internal bool HasOutput(string key) => _writers.TryGetValue(key, out var writer) && IsAvailable(writer);

    /// <summary>
    /// Opens a reader for the requested representation. Disposing the stream releases that reader.
    /// </summary>
    /// <param name="key">A key identifying the renderer and the exact requested encoding.</param>
    /// <param name="outputPath">The server-generated output path supplying the directory and extension.</param>
    /// <param name="start">Starts a writer using its private play session ID and output path.</param>
    /// <param name="cancellationToken">Cancellation of this HTTP request.</param>
    /// <returns>A progressive stream for this HTTP request.</returns>
    public async Task<Stream> OpenAsync(string key, string outputPath, Func<string, string, Task<TranscodingJob>> start, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(start);
        using var gate = await _manager.LockAsync(key, cancellationToken).ConfigureAwait(false);
        if (_writers.TryGetValue(key, out var existing) && IsAvailable(existing))
        {
            try
            {
                return OpenReader(key, existing);
            }
            catch (FileNotFoundException)
            {
                // The server can remove a job between inspecting it and opening its file.
                // Use a new generation without disturbing readers of the previous one.
            }
        }

        // A replacement always gets a new filename, even while a previous generation's
        // readers or core's delayed DeletePartialStreamFiles still hold its old path.
        var sessionId = Guid.NewGuid().ToString("N");
        var path = Path.Combine(Path.GetDirectoryName(outputPath)!, $"fleet-dlna-{sessionId}{Path.GetExtension(outputPath)}");
        Writer writer;
        try
        {
            var job = await start(sessionId, path).ConfigureAwait(false);
            writer = new Writer(sessionId, path, job);
        }
        catch
        {
            // FFmpeg may already have registered the job or created its output.
            await CleanupAsync(sessionId, path).ConfigureAwait(false);
            throw;
        }

        _writers[key] = writer;
        return OpenReader(key, writer);
    }

    private bool IsAvailable(Writer writer)
        => (!writer.Job.HasExited || writer.Job.ExitCode == 0)
            && File.Exists(writer.Path)
            && ReferenceEquals(_manager.GetTranscodingJob(writer.Path, TranscodingJobType.Progressive), writer.Job);

    private Stream OpenReader(string key, Writer writer)
    {
        // StartFfMpeg's initial active request belongs to the owner. Each HTTP
        // stream adds and releases a separate request. The owner pin keeps core's
        // progressive idle timer from racing our last-reader/reconnect handling.
        writer.AdvanceGeneration();
        TranscodingJob? readerJob = null;
        try
        {
            readerJob = _manager.OnTranscodeBeginRequest(writer.Path, TranscodingJobType.Progressive);
            if (!ReferenceEquals(readerJob, writer.Job))
            {
                throw new FileNotFoundException("The DLNA progressive transcode is no longer available.", writer.Path);
            }

            return new Reader(writer.Path, writer.Job, _manager, () => ReaderClosed(key, writer));
        }
        catch
        {
            if (readerJob is not null)
            {
                _manager.OnTranscodeEndRequest(readerJob);
            }

            ReaderClosed(key, writer);
            throw;
        }
    }

    private void ReaderClosed(string key, Writer writer)
    {
        if (writer.Job.ActiveRequestCount <= 1)
        {
            var close = writer.AdvanceGeneration();
            _ = ExpireAsync(key, writer, close);
        }
    }

    private async Task ExpireAsync(string key, Writer writer, long close)
    {
        try
        {
            await Task.Delay(ReconnectGrace, _time).ConfigureAwait(false);
            using (await _manager.LockAsync(key, CancellationToken.None).ConfigureAwait(false))
            {
                if (writer.Job.ActiveRequestCount > 1 || writer.Generation != close)
                {
                    return;
                }

                if (_writers.TryGetValue(key, out var current) && ReferenceEquals(writer, current))
                {
                    _writers.TryRemove(key, out _);
                }
            }

            await CleanupAsync(writer.SessionId, writer.Path).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retiring DLNA progressive transcode {Path}", writer.Path);
        }
    }

    private async Task CleanupAsync(string sessionId, string path)
    {
        try
        {
            // Core selects which jobs to STOP using the session ID alone. The
            // predicate only controls file deletion, so never use a shared/client ID.
            await _manager.KillTranscodingJobs(string.Empty, sessionId, file => string.Equals(file, path, StringComparison.Ordinal)).ConfigureAwait(false);
            // An exited or partially started writer may already be absent from core.
            File.Delete(path);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not clean up DLNA progressive transcode {Path}", path);
        }
    }

    private sealed class Writer(string sessionId, string path, TranscodingJob job)
    {
        private long _generation;

        public string SessionId { get; } = sessionId;

        public string Path { get; } = path;

        public TranscodingJob Job { get; } = job;

        public long Generation => Volatile.Read(ref _generation);

        public long AdvanceGeneration() => Interlocked.Increment(ref _generation);
    }

    private sealed class Reader(string path, TranscodingJob job, ITranscodeManager manager, Action closed)
        : ProgressiveFileStream(path, job, manager)
    {
        private Action? _closed = closed;

        protected override void Dispose(bool disposing)
        {
            var release = Interlocked.Exchange(ref _closed, null);
            if (release is null)
            {
                return;
            }

            try
            {
                base.Dispose(disposing);
            }
            finally
            {
                release();
            }
        }
    }
}
