using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Dlna.Playback;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Xunit;

namespace Jellyfin.Plugin.FleetDlna.Playback.Tests;

public sealed class ProgressiveVideoTranscodesTests : IDisposable
{
    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(10);
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory();
    private readonly FakeTimeProvider _clock = new();
    private readonly ITranscodeManager _manager = Substitute.For<ITranscodeManager>();
    private readonly ConcurrentDictionary<string, TranscodingJob> _registered = new();
    private readonly ConcurrentQueue<TranscodingJob> _started = new();
    private readonly ConcurrentQueue<string?> _stoppedSessions = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();
    private readonly ProgressiveVideoTranscodes _transcodes;

    public ProgressiveVideoTranscodesTests()
    {
        _manager.LockAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => LockAsync(call.Arg<string>(), call.Arg<CancellationToken>()));
        _manager.GetTranscodingJob(Arg.Any<string>(), TranscodingJobType.Progressive)
            .Returns(call => _registered.GetValueOrDefault(call.Arg<string>()));
        _manager.GetTranscodingJob(Arg.Any<string>())
            .Returns(call => _registered.Values.FirstOrDefault(job => job.PlaySessionId == call.Arg<string>()));
        _manager.OnTranscodeBeginRequest(Arg.Any<string>(), TranscodingJobType.Progressive)
            .Returns(call =>
            {
                var job = _registered.GetValueOrDefault(call.Arg<string>());
                job?.IncrementActiveRequestCount();
                return job;
            });
        _manager.When(manager => manager.OnTranscodeEndRequest(Arg.Any<TranscodingJob>()))
            .Do(call => call.Arg<TranscodingJob>().DecrementActiveRequestCount());
        // Core first selects jobs by session (or device if session is blank). The
        // predicate filters the files removed, not the jobs that are stopped.
        _manager.KillTranscodingJobs(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<Func<string, bool>>())
            .Returns(call =>
            {
                string? session = call.ArgAt<string?>(1);
                string device = call.ArgAt<string>(0);
                var delete = call.Arg<Func<string, bool>>();
                _stoppedSessions.Enqueue(session);
                foreach (var entry in _registered)
                {
                    if (string.IsNullOrWhiteSpace(session) ? entry.Value.DeviceId == device : entry.Value.PlaySessionId == session)
                    {
                        entry.Value.HasExited = true;
                        _registered.TryRemove(entry.Key, out _);
                        if (delete(entry.Key))
                        {
                            File.Delete(entry.Key);
                        }
                    }
                }

                return Task.CompletedTask;
            });
        _transcodes = new ProgressiveVideoTranscodes(
            _manager, NullLogger<ProgressiveVideoTranscodes>.Instance, _clock);
    }

    private string OutputPath => Path.Combine(_directory.FullName, "shared-output.ts");

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FinalReleaseReclaimsPrivateOutputAfterGrace(bool encoderAlreadyFinished)
    {
        var reader = await OpenAsync();
        var job = Assert.Single(_started);
        job.HasExited = encoderAlreadyFinished;
        Assert.NotEqual(OutputPath, job.Path);
        Assert.Equal(_directory.FullName, Path.GetDirectoryName(job.Path));
        Assert.Equal((int)'V', reader.ReadByte());
        _clock.Advance(Grace + Grace);
        Assert.True(File.Exists(job.Path));
        Assert.Empty(_stoppedSessions);

        await reader.DisposeAsync();
        ((IDisposable)reader).Dispose(); // ASP.NET may dispose an already-closed response stream.
        _clock.Advance(Grace - TimeSpan.FromSeconds(1));
        Assert.True(File.Exists(job.Path));
        _clock.Advance(TimeSpan.FromSeconds(1));
        await UntilAsync(() => !File.Exists(job.Path));
        Assert.Equal(job.PlaySessionId, Assert.Single(_stoppedSessions));
    }

    [Fact]
    public async Task OverlappingGetsAndReconnectKeepTheSameProducerAlive()
    {
        var didStart = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var continueStart = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstGet = _transcodes.OpenAsync("movie", OutputPath, async (session, path) =>
        {
            didStart.SetResult();
            await continueStart.Task;
            return await StartAsync(session, path);
        }, CancellationToken.None);
        await didStart.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var secondGet = OpenAsync();
        continueStart.SetResult();
        var first = await firstGet;
        var second = await secondGet;
        var producer = Assert.Single(_started);

        await first.DisposeAsync();
        await first.DisposeAsync();
        Assert.Equal(2, producer.ActiveRequestCount); // One remaining HTTP reader and the producer's pin.
        _clock.Advance(Grace + Grace);
        Assert.Equal((int)'V', second.ReadByte());
        Assert.Empty(_stoppedSessions);
        await second.DisposeAsync();
        _clock.Advance(TimeSpan.FromSeconds(9));
        var resumed = await OpenAsync();
        _clock.Advance(Grace);
        Assert.Single(_started);
        Assert.Empty(_stoppedSessions);
        Assert.True(File.Exists(producer.Path));
        Assert.Equal((int)'V', resumed.ReadByte());
        await resumed.DisposeAsync();
        _clock.Advance(Grace);
        await UntilAsync(() => !File.Exists(producer.Path));
        Assert.Equal(producer.PlaySessionId, Assert.Single(_stoppedSessions));
    }

    [Fact]
    public async Task ExpiringOneVariantPreservesOtherVariantsAndForeignFiles()
    {
        await File.WriteAllTextAsync(OutputPath, "foreign cached output");
        var a = await OpenAsync("profile A");
        var first = Assert.Single(_started);
        var b = await OpenAsync("profile B");
        var second = _started.Last();
        Assert.NotEqual(first.Path, second.Path);
        Assert.False(string.IsNullOrWhiteSpace(first.PlaySessionId));
        Assert.NotEqual(first.PlaySessionId, second.PlaySessionId);

        await a.DisposeAsync();
        _clock.Advance(Grace);
        await UntilAsync(() => !File.Exists(first.Path));
        Assert.Equal(first.PlaySessionId, Assert.Single(_stoppedSessions));
        Assert.Equal("foreign cached output", await File.ReadAllTextAsync(OutputPath));
        Assert.Equal((int)'V', b.ReadByte());
        Assert.True(File.Exists(second.Path));
        await b.DisposeAsync();
        _clock.Advance(Grace);
        await UntilAsync(() => !File.Exists(second.Path));

        var replay = await OpenAsync("profile A");
        var replacement = _started.Last();
        Assert.NotEqual(first.PlaySessionId, replacement.PlaySessionId);
        Assert.NotEqual(first.Path, replacement.Path);
        Assert.Equal((int)'V', replay.ReadByte());
        await replay.DisposeAsync();
        _clock.Advance(Grace);
        await UntilAsync(() => !File.Exists(replacement.Path));
    }

    [Fact]
    public async Task UnregisteredCompletedJobDeletesOnlyItsExactPrivateFile()
    {
        var reader = await OpenAsync();
        var producer = Assert.Single(_started);
        producer.HasExited = true;
        Assert.True(_registered.TryRemove(producer.Path!, out _));
        var sibling = producer.Path + ".keep";
        await File.WriteAllTextAsync(sibling, "another file");
        await reader.DisposeAsync();
        _clock.Advance(Grace);
        await UntilAsync(() => !File.Exists(producer.Path));
        Assert.Equal("another file", await File.ReadAllTextAsync(sibling));
    }

    [Fact]
    public async Task OldReaderClosingAfterEncoderFailureCannotRemoveReplacement()
    {
        var oldReader = await OpenAsync();
        var old = Assert.Single(_started);
        old.HasExited = true;
        old.ExitCode = 1;
        var replacementReader = await OpenAsync();
        var replacement = _started.Last();
        Assert.NotEqual(old.Path, replacement.Path);
        await oldReader.DisposeAsync();
        _clock.Advance(Grace);
        await UntilAsync(() => !File.Exists(old.Path));
        Assert.Equal(old.PlaySessionId, Assert.Single(_stoppedSessions));

        var additionalReader = await OpenAsync();
        Assert.Equal(2, _started.Count);
        Assert.Equal((int)'V', additionalReader.ReadByte());
        await additionalReader.DisposeAsync();
        await replacementReader.DisposeAsync();
        _clock.Advance(Grace);
        await UntilAsync(() => !File.Exists(replacement.Path));
    }

    [Fact]
    public async Task FailedStartRemovesPartialOutputAndCanBeRetried()
    {
        string? partial = null;
        await Assert.ThrowsAsync<IOException>(() => _transcodes.OpenAsync("movie", OutputPath, async (session, path) =>
        {
            partial = path;
            await File.WriteAllTextAsync(path, "partial output");
            throw new IOException("Encoder could not start");
        }, CancellationToken.None));
        Assert.NotNull(partial);
        Assert.False(File.Exists(partial));

        var reader = await OpenAsync();
        var producer = Assert.Single(_started);
        Assert.Equal((int)'V', reader.ReadByte());
        await reader.DisposeAsync();
        _clock.Advance(Grace);
        await UntilAsync(() => !File.Exists(producer.Path));
    }

    public void Dispose()
    {
        _directory.Delete(true);
    }

    private Task<Stream> OpenAsync(string key = "movie")
        => _transcodes.OpenAsync(key, OutputPath, StartAsync, CancellationToken.None);

    private async Task<TranscodingJob> StartAsync(string session, string path)
    {
        // StartFfMpeg creates the output and returns a job with one request.
        await File.WriteAllTextAsync(path, "Video payload");
        var job = new TranscodingJob(NullLogger<TranscodingJob>.Instance)
        {
            ActiveRequestCount = 1,
            DeviceId = "test",
            PlaySessionId = session,
            Path = path,
            Type = TranscodingJobType.Progressive
        };
        Assert.True(_registered.TryAdd(path, job));
        _started.Enqueue(job);
        return job;
    }

    private async ValueTask<IDisposable> LockAsync(string key, CancellationToken cancellationToken)
    {
        var gate = _locks.GetOrAdd(key, _ => new SemaphoreSlim(1));
        await gate.WaitAsync(cancellationToken);
        return new HeldLock(gate);
    }

    private static async Task UntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!predicate())
        {
            await Task.Delay(5, timeout.Token);
        }
    }

    private sealed class HeldLock(SemaphoreSlim gate) : IDisposable
    {
        public void Dispose() => gate.Release();
    }
}
