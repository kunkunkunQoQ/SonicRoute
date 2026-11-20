using System.Runtime.InteropServices;
using System.Text;
using SonicRoute.AndroidAudio;

namespace SonicRoute.WindowsLink;

internal sealed class SourceSpeakerConsentRequiredException : InvalidOperationException
{
    internal const int ErrorCode = unchecked((int)0x8004A101);
    internal SourceSpeakerConsentRequiredException(string message) : base(message) { HResult = ErrorCode; }
}

internal sealed partial class WindowsLinkService
{
    internal async Task SetSourcePlaybackAsync(Guid incomingStreamId, bool playOnSource, bool allowSpeakerMute,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        Track track;
        var request = Guid.NewGuid();
        var reply = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken stopped;
        lock (_gate)
        {
            if (!_streams.TryGetValue(incomingStreamId, out track!) || !track.Info.Incoming || track.Closing || !track.Announced
                || track.Info.Kind == LinkMediaKind.Window || track.Info.IsRelayed) throw new IOException("The selected audio source is unavailable.");
            if (!track.Connection.Hello.SupportsSourcePlayback) throw new NotSupportedException("The source computer needs an update.");
            if (track.SourceChangeReply != null) throw new InvalidOperationException("The source playback device is changing.");
            if (!playOnSource && track.Info.Kind == LinkMediaKind.SystemAudio && !allowSpeakerMute)
                throw new SourceSpeakerConsentRequiredException("Muting all source speakers requires confirmation.");
            track.SourceChangeId = request;
            track.SourceChangeReply = reply;
            stopped = track.Stop.Token;
        }
        try
        {
            var data = new byte[17];
            request.TryWriteBytes(data);
            data[16] = (byte)(playOnSource ? 1 : allowSpeakerMute ? 2 : 0);
            token.ThrowIfCancellationRequested();
            // After writing a source mute, wait for its authoritative outcome. Closing the
            // stream also releases the source lease, including on a transport failure.
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stopped);
            deadline.CancelAfter(TimeSpan.FromSeconds(12));
            try
            {
                await track.Connection.SendAsync(LinkMessage.SourcePlayback, incomingStreamId, data, deadline.Token).ConfigureAwait(false);
                await reply.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!stopped.IsCancellationRequested)
            {
                await RetireAsync(track, announce: true).ConfigureAwait(false);
                throw new IOException("The source computer did not confirm its playback state.");
            }
        }
        finally
        {
            lock (_gate)
                if (track.SourceChangeReply == reply) track.SourceChangeReply = null;
        }
    }

    private static bool OverlapsSource(Track other, int? pid) =>
        !other.Closing && !other.Info.Incoming && !other.Info.IsRelayed && other.Info.Kind != LinkMediaKind.Window
        && (pid == null || other.Process == null || other.Process == pid);

    private void DispatchSourcePlayback(Track track, ReadOnlySpan<byte> data)
    {
        Guid request = new(data[..16]);
        byte mode = data[16];
        if (request == Guid.Empty || mode > 2) throw new InvalidDataException("Invalid source playback request.");
        string? error = null;
        lock (_gate)
        {
            if (track.Info.Kind == LinkMediaKind.Window || track.Info.IsRelayed || !track.Announced || track.RemotePlaybackReady?.Task.IsCompletedSuccessfully != true)
                error = "The selected audio stream is not ready.";
            else if (track.ChangingSource || _streams.Values.Any(t => t != track && OverlapsSource(t, track.Process)))
                error = "Another stream is using the same source playback device.";
            else if (track.Info.Kind == LinkMediaKind.SystemAudio && mode == 0)
                error = "Muting all source speakers requires confirmation.";
            else track.ChangingSource = true;
        }
        if (error != null) { _ = ReplySourcePlaybackAsync(track, request, error); return; }
        _ = Task.Run(() => ChangeSourcePlaybackAsync(track, request, mode));
    }

    private async Task ChangeSourcePlaybackAsync(Track track, Guid request, byte mode)
    {
        bool only = mode != 1;
        string? error = null;
        bool needsConsent = false;
        try
        {
            if (track.Info.DestinationOnly != only)
            {
                if (only)
                {
                    RefreshExclusiveCapability(force: true);
                    InstallLocalSilence(track, AcquireAudioSilence(track, mode == 2, track.Stop.Token));
                }
                else ReleaseLocalSilence(track);
                lock (_gate)
                {
                    if (track.Closing || (only && track.LocalSilence?.Invalidated.IsCancellationRequested != false))
                        throw new IOException("The source playback device became unavailable.");
                    track.Info = track.Info with { DestinationOnly = only };
                }
                Notify();
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            error = ex.Message;
            needsConsent = ex is SourceSpeakerConsentRequiredException;
            if (only && !track.Info.DestinationOnly) ReleaseLocalSilence(track);
        }
        finally { lock (_gate) track.ChangingSource = false; }
        await ReplySourcePlaybackAsync(track, request, error, needsConsent).ConfigureAwait(false);
    }

    private async Task ReplySourcePlaybackAsync(Track track, Guid request, string? error, bool needsConsent = false)
    {
        try
        {
            byte[] text = error == null ? [] : Encoding.UTF8.GetBytes(error.Length > 120 ? error[..120] : error);
            byte[] data = new byte[18 + text.Length];
            request.TryWriteBytes(data);
            data[16] = (byte)(track.Info.DestinationOnly ? 1 : 0);
            data[17] = (byte)(needsConsent ? 2 : error == null ? 1 : 0);
            text.CopyTo(data, 18);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await track.Connection.SendAsync(LinkMessage.SourcePlaybackReply, track.Info.Id, data, deadline.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
            // A lost confirmation cannot leave an undisclosed source mute behind.
            await RetireAsync(track, announce: true).ConfigureAwait(false);
        }
    }

    private void ReceiveSourcePlaybackReply(Track track, ReadOnlySpan<byte> data)
    {
        Guid request = new(data[..16]);
        if (request == Guid.Empty || data[16] > 1 || data[17] > 2 || data[17] == 1 && data.Length != 18)
            throw new InvalidDataException("Invalid source playback response.");
        TaskCompletionSource<bool>? reply;
        lock (_gate)
        {
            if (track.SourceChangeId != request || (reply = track.SourceChangeReply) == null) return;
            track.Info = track.Info with { DestinationOnly = data[16] != 0 };
        }
        Notify();
        if (data[17] == 1) reply.TrySetResult(true);
        else if (data[17] == 2) reply.TrySetException(new SourceSpeakerConsentRequiredException(Encoding.UTF8.GetString(data[18..])));
        else reply.TrySetException(new IOException(Encoding.UTF8.GetString(data[18..])));
    }

    private ProcessSilence AcquireAudioSilence(Track track, bool allowSpeakerMute, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (track.Process is not { } pid)
        {
            if (allowSpeakerMute && track.Info.Kind == LinkMediaKind.SystemAudio && AcquireSystemSpeakerSilence?.Invoke() is { } system)
                return new ProcessSilence(system, true);
            throw new IOException("The source computer could not mute all active speakers.");
        }
        lock (_gate)
            if (FindSilenceLocked(pid, track.Info.InstanceId) is { } existing) return existing.Retain();
        if (AcquireAppSilence != null)
        {
            var result = AcquireAppSilence(pid, allowSpeakerMute && CanUseSpeakerFallback, token);
            return result.Lease == null ? throw new IOException("The source computer could not isolate this application's audio.")
                : new ProcessSilence(result.Lease, result.MutedSpeaker);
        }
        IPcProcessSilenceLease? acquired = null;
        bool trySpeaker = allowSpeakerMute && CanUseSpeakerFallback;
        if (CanSendExclusively)
        {
            try { acquired = acquireLocalSilence?.Invoke(pid); }
            catch (Exception ex) when ((ex is InvalidOperationException or COMException) && CanOfferSpeakerMute(pid))
            {
                if (!allowSpeakerMute) throw new SourceSpeakerConsentRequiredException(ex.Message);
                System.Diagnostics.Trace.WriteLine(ex);
            }
        }
        if (acquired == null && !allowSpeakerMute && CanOfferSpeakerMute(pid))
            throw new SourceSpeakerConsentRequiredException("Muting the source speaker requires confirmation.");
        bool needsSpeaker = acquired == null && trySpeaker && !token.IsCancellationRequested;
        if (needsSpeaker) acquired = AcquireSpeakerFallback?.Invoke(pid);
        return acquired == null ? throw new IOException("The source computer could not isolate this application's audio.")
            : new ProcessSilence(acquired, needsSpeaker);
    }

    private bool CanOfferSpeakerMute(int pid) => CanUseSpeakerFallback && (ProbeAppSpeaker?.Invoke(pid) ?? true);

    private void InstallLocalSilence(Track track, ProcessSilence silence)
    {
        try
        {
            lock (_gate)
            {
                if (track.Closing || track.LocalSilence != null || silence.Invalidated.IsCancellationRequested)
                    throw new IOException("The stream or its source playback device became unavailable.");
                track.LocalSilenceInvalidation = silence.Invalidated.Register(() =>
                    _ = Task.Run(() => RetireAsync(track, announce: true)));
                track.LocalSilence = silence;
            }
        }
        catch { silence.Dispose(); throw; }
    }

    private void ReleaseLocalSilence(Track track)
    {
        ProcessSilence? silence;
        CancellationTokenRegistration invalidation;
        lock (_gate)
        {
            silence = track.LocalSilence;
            track.LocalSilence = null;
            invalidation = track.LocalSilenceInvalidation;
            track.LocalSilenceInvalidation = default;
        }
        invalidation.Dispose();
        silence?.Dispose();
    }
}
