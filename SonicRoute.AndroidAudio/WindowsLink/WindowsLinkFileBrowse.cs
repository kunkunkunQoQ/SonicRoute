using System.Text.Json;

namespace SonicRoute.WindowsLink;

internal sealed partial class WindowsLinkService
{
    private sealed record BrowseRequest(Guid Session, string Path, bool Directory = false, int Offset = 0);
    private sealed record BrowseAnswer(bool Ok, LinkBrowsePage? Page = null, string? Error = null, bool Declined = false);

    private sealed class Browser(LinkConnection link, Guid id, bool host)
    {
        internal readonly LinkConnection Link = link;
        internal readonly Guid Id = id;
        internal readonly bool Host = host;
        internal readonly CancellationTokenSource Life = new();
        internal readonly LinkBrowseDirectory.Cursor Listing = new();
        internal bool Granted;
        internal bool TrustRequired;
    }

    private sealed class BrowseCall(Browser browser, Guid id, LinkMessage operation, BrowseRequest request)
    {
        internal readonly Browser Browser = browser;
        internal readonly Guid Id = id;
        internal readonly LinkMessage Operation = operation;
        internal readonly BrowseRequest Request = request;
        internal readonly CancellationTokenSource Stop = CancellationTokenSource.CreateLinkedTokenSource(browser.Life.Token);
        internal readonly TaskCompletionSource<BrowseAnswer> Reply = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource SenderFinished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource<bool> Approval = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Prompt, Matched;
    }

    private readonly Dictionary<Guid, Browser> _browsers = [];
    private readonly Dictionary<Guid, BrowseCall> _browseCalls = [];
    private readonly Dictionary<LinkConnection, Queue<Guid>> _retiredDownloads = [];
    public event Action<LinkBrowseApproval>? FileBrowseRequested;
    public event Action<Guid>? FileBrowseRequestEnded;
    public event Action<string, Guid>? FileBrowserClosed;

    public async Task<Guid> OpenFileBrowserAsync(string peerId, CancellationToken token = default)
    {
        Browser browser;
        lock (_gate)
        {
            var link = Connected(peerId, LinkCapabilities.FileTransfer);
            if (!link.Hello.SupportsFileBrowse) throw new NotSupportedException("The other device does not support file browsing.");
            browser = NewBrowser(link, Guid.NewGuid(), false);
        }
        try
        {
            await CallBrowserAsync(browser, browser.Id, LinkMessage.BrowseOpen, new(browser.Id, ""), token).ConfigureAwait(false);
            lock (_gate) { EnsureBrowser(browser, false); browser.Granted = true; }
            return browser.Id;
        }
        catch { await CloseBrowserAsync(browser, true).ConfigureAwait(false); throw; }
    }

    public async Task<LinkBrowsePage> ListRemoteFilesAsync(string peerId, Guid sessionId, string path, int offset = 0, CancellationToken token = default)
    {
        if (!LinkBrowseDirectory.ValidPath(path, true) || offset < 0) throw new ArgumentException("Invalid folder or page.");
        var browser = ClientBrowser(peerId, sessionId);
        var answer = await CallBrowserAsync(browser, Guid.NewGuid(), LinkMessage.BrowseList, new(sessionId, path, Offset: offset), token).ConfigureAwait(false);
        var page = answer.Page ?? throw new InvalidDataException("Missing folder contents.");
        if (page.Entries == null || page.Entries.Length > 64 || (page.NextOffset.HasValue &&
            (page.Entries.Length == 0 || page.NextOffset != (long)offset + page.Entries.Length))) throw new InvalidDataException("Invalid folder contents.");
        foreach (var entry in page.Entries)
        {
            if (entry == null || !LinkBrowseDirectory.ValidPath(entry.Path, false) ||
                (path == "" ? !entry.IsDirectory || entry.Path.Length != 3 || entry.Name != entry.Path :
                    !IsSafeTransferName(entry.Name) || !string.Equals(Path.Combine(path, entry.Name), entry.Path, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("Invalid remote file entry.");
        }
        return page;
    }

    public async Task DownloadRemoteFileAsync(string peerId, Guid sessionId, LinkBrowseEntry entry, CancellationToken token = default)
    {
        if (!LinkBrowseDirectory.ValidPath(entry.Path, false) || entry.Path.Length == 3 || Path.GetFileName(entry.Path) != entry.Name)
            throw new ArgumentException("Choose a file or folder on the device.");
        var browser = ClientBrowser(peerId, sessionId);
        await CallBrowserAsync(browser, Guid.NewGuid(), LinkMessage.BrowseDownload, new(sessionId, entry.Path, entry.IsDirectory), token).ConfigureAwait(false);
    }

    public Task CloseFileBrowserAsync(string peerId, Guid sessionId)
    {
        Browser? browser;
        lock (_gate) _browsers.TryGetValue(sessionId, out browser);
        return browser is { Host: false } && browser.Link.Id.Equals(peerId, StringComparison.OrdinalIgnoreCase)
            ? CloseBrowserAsync(browser, true) : Task.CompletedTask;
    }

    public void ReplyFileBrowse(Guid id, bool allow)
    {
        lock (_gate)
            if (_browseCalls.TryGetValue(id, out var call) && call.Browser.Host && call.Prompt && !call.Stop.IsCancellationRequested)
                call.Approval.TrySetResult(allow);
    }

    public bool IsFileBrowseRequestPending(Guid id)
    {
        lock (_gate) return _browseCalls.TryGetValue(id, out var call) && call.Browser.Host && call.Prompt
            && _browsers.GetValueOrDefault(call.Browser.Id) == call.Browser && !call.Stop.IsCancellationRequested && !call.Approval.Task.IsCompleted;
    }

    private Browser NewBrowser(LinkConnection link, Guid id, bool host)
    {
        if (_connections.GetValueOrDefault(link.Id) != link || !link.IsOpen || !_saved.ContainsKey(link.Id) ||
            _browsers.ContainsKey(id) || _browseCalls.ContainsKey(id) || _browsers.Values.Any(b => b.Link == link && b.Host == host))
            throw new IOException("A browser is already open or the connection has changed.");
        var browser = new Browser(link, id, host);
        _browsers.Add(id, browser);
        return browser;
    }

    private Browser ClientBrowser(string peerId, Guid sessionId)
    {
        lock (_gate)
        {
            if (!_browsers.TryGetValue(sessionId, out var browser) || browser.Host || !browser.Link.Id.Equals(peerId, StringComparison.OrdinalIgnoreCase))
                throw new IOException("This file browser is closed.");
            EnsureBrowser(browser, true);
            return browser;
        }
    }

    private void EnsureBrowser(Browser browser, bool needsGrant)
    {
        if (_browsers.GetValueOrDefault(browser.Id) != browser || _connections.GetValueOrDefault(browser.Link.Id) != browser.Link
            || !browser.Link.IsOpen || (needsGrant && !browser.Granted) || (browser.TrustRequired && !IsFullyTrusted(browser.Link)))
            throw new IOException("This file browser is closed.");
    }

    private BrowseCall NewBrowseCall(Browser browser, Guid id, LinkMessage operation, BrowseRequest request)
    {
        EnsureBrowser(browser, operation != LinkMessage.BrowseOpen);
        if (_browseCalls.ContainsKey(id) || _browseCalls.Values.Count(c => c.Browser == browser) >= 4 ||
            operation == LinkMessage.BrowseDownload && _browseCalls.Values.Any(c => c.Browser == browser && c.Operation == operation))
            throw new IOException("This browser already has a pending request.");
        var call = new BrowseCall(browser, id, operation, request);
        _browseCalls.Add(id, call);
        return call;
    }

    private async Task<BrowseAnswer> CallBrowserAsync(Browser browser, Guid id, LinkMessage operation, BrowseRequest request, CancellationToken token)
    {
        BrowseCall call;
        lock (_gate) call = NewBrowseCall(browser, id, operation, request);
        bool finished = false;
        try
        {
            using var cancel = token.Register(() => CancelBrowseCall(call));
            call.Stop.CancelAfter(TimeSpan.FromSeconds(operation == LinkMessage.BrowseList ? 30 : 150));
            var body = operation == LinkMessage.BrowseOpen ? [] : JsonSerializer.SerializeToUtf8Bytes(request);
            await browser.Link.SendAsync(operation, id, body, call.Stop.Token).ConfigureAwait(false);
            var answer = await call.Reply.Task.WaitAsync(call.Stop.Token).ConfigureAwait(false);
            finished = true;
            if (!answer.Ok)
            {
                if (answer.Declined) throw new UnauthorizedAccessException("File access was declined or expired.");
                throw new IOException(answer.Error ?? "The remote file request failed.");
            }
            if (operation == LinkMessage.BrowseDownload)
            {
                // The local bytes are committed. An ordinary sender releases its transfer slot before replying,
                // so the next selection can start immediately. A missing final reply cannot strand a saved file.
                try { await call.SenderFinished.Task.WaitAsync(TimeSpan.FromSeconds(2), call.Stop.Token).ConfigureAwait(false); }
                catch (TimeoutException) { }
                catch (OperationCanceledException) when (call.Stop.IsCancellationRequested) { }
            }
            return answer;
        }
        finally
        {
            lock (_gate)
            {
                _browseCalls.Remove(id);
                if (operation == LinkMessage.BrowseDownload && _connections.GetValueOrDefault(browser.Link.Id) == browser.Link)
                {
                    if (!_retiredDownloads.TryGetValue(browser.Link, out var retired)) _retiredDownloads[browser.Link] = retired = new();
                    if (retired.Count == 32) retired.Dequeue();
                    retired.Enqueue(id);
                }
            }
            if (!finished) { CancelBrowseCall(call); await SendBrowserControl(browser.Link, LinkMessage.BrowseCancel, id, []).ConfigureAwait(false); }
            call.Stop.Dispose();
        }
    }

    // Used only by the incoming file handler. A solicited download never authorizes another offer.
    private bool TryAcceptRequestedFile(LinkConnection link, LinkFileOffer offer, bool isDirectory)
    {
        lock (_gate)
        {
            if (_retiredDownloads.TryGetValue(link, out var retired) && retired.Contains(offer.Id))
                throw new IOException("This download request is no longer active.");
            if (!_browseCalls.TryGetValue(offer.Id, out var call) || call.Browser.Host || call.Browser.Link != link
                || call.Operation != LinkMessage.BrowseDownload) return false;
            EnsureBrowser(call.Browser, true);
            call.Stop.Token.ThrowIfCancellationRequested();
            if (call.Matched || Path.GetFileName(call.Request.Path) != offer.Name || call.Request.Directory != isDirectory)
                throw new InvalidDataException("The file offer does not match the requested item.");
            call.Matched = true;
            call.Stop.CancelAfter(Timeout.InfiniteTimeSpan); // The transfer now enforces its own idle timeout.
            return true;
        }
    }

    private void RequestedFileEnded(LinkConnection link, Guid id, LinkFileState final)
    {
        lock (_gate)
        {
            if (!_browseCalls.TryGetValue(id, out var call) || !call.Matched || call.Browser.Host || call.Browser.Link != link) return;
            call.Reply.TrySetResult(final == LinkFileState.Completed ? new(true) : new(false, Error: "The file transfer did not complete."));
        }
    }

    private void DispatchFileBrowser(LinkConnection link, LinkMessage message, Guid id, ReadOnlyMemory<byte> body)
    {
        if (!link.Hello.SupportsFileBrowse || !link.Hello.Capabilities.HasFlag(LinkCapabilities.FileTransfer)) return;
        if (message == LinkMessage.BrowseClose)
        {
            Browser? browser;
            lock (_gate) _browsers.TryGetValue(id, out browser);
            if (browser?.Link == link) _ = CloseBrowserAsync(browser, false);
            return;
        }
        if (message is LinkMessage.BrowseCancel or LinkMessage.BrowseReply or LinkMessage.BrowsePreparing)
        {
            BrowseCall? call;
            lock (_gate) _browseCalls.TryGetValue(id, out call);
            if (call?.Browser.Link != link) return;
            if (message == LinkMessage.BrowsePreparing && !call.Browser.Host && call.Operation == LinkMessage.BrowseDownload)
            {
                lock (_gate)
                    if (_browseCalls.GetValueOrDefault(id) == call && !call.Matched)
                        call.Stop.CancelAfter(TimeSpan.FromHours(1)); // Approved directory packaging may take longer than a consent prompt.
                return;
            }
            if (message == LinkMessage.BrowseCancel && call.Browser.Host) CancelBrowseCall(call);
            if (message == LinkMessage.BrowseReply && !call.Browser.Host)
            {
                var answer = JsonSerializer.Deserialize<BrowseAnswer>(body.Span) ?? throw new InvalidDataException("Invalid file browser reply.");
                if (call.Operation == LinkMessage.BrowseDownload)
                {
                    call.SenderFinished.TrySetResult();
                    if (call.Matched)
                    {
                        if (!answer.Ok) CancelFile(id);
                        return; // Wait for (or preserve) the actual outcome on disk.
                    }
                    if (answer.Ok) answer = new(false, Error: "The selected item was not received.");
                }
                call.Reply.TrySetResult(answer);
            }
            return;
        }
        try
        {
            var request = message == LinkMessage.BrowseOpen ? new BrowseRequest(id, "") :
                JsonSerializer.Deserialize<BrowseRequest>(body.Span) ?? throw new IOException("Invalid browse request.");
            BrowseCall call;
            lock (_gate)
            {
                var browser = message == LinkMessage.BrowseOpen ? NewBrowser(link, id, true) : _browsers.GetValueOrDefault(request.Session);
                if (browser == null || browser.Link != link || !browser.Host) throw new IOException("This file browser is closed.");
                call = NewBrowseCall(browser, id, message, request);
            }
            _ = Task.Run(() => ServeBrowseCallAsync(call));
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            _ = SendBrowserControl(link, LinkMessage.BrowseReply, id, JsonSerializer.SerializeToUtf8Bytes(new BrowseAnswer(false, Error: "File browsing is unavailable.")));
        }
    }

    private async Task ServeBrowseCallAsync(BrowseCall call)
    {
        var browser = call.Browser;
        var request = call.Request;
        bool ok = false, declined = false;
        LinkBrowsePage? page = null;
        string? error = null;
        try
        {
            if (!LinkBrowseDirectory.ValidPath(request.Path, call.Operation != LinkMessage.BrowseDownload) || request.Offset < 0)
                throw new IOException("Invalid local path.");
            if (call.Operation == LinkMessage.BrowseDownload && request.Path.Length == 3) throw new IOException("Choose a file or folder.");
            if (call.Operation is LinkMessage.BrowseOpen or LinkMessage.BrowseDownload)
            {
                bool needsApproval;
                lock (_gate)
                {
                    EnsureBrowser(browser, call.Operation != LinkMessage.BrowseOpen);
                    call.Stop.Token.ThrowIfCancellationRequested();
                    needsApproval = !IsFullyTrusted(browser.Link);
                    if (call.Operation == LinkMessage.BrowseOpen) browser.TrustRequired = !needsApproval;
                    call.Prompt = needsApproval;
                }
                if (needsApproval)
                {
                    if (FileBrowseRequested is not { } handler) throw new UnauthorizedAccessException();
                    handler(new(call.Id, browser.Link.Id, request.Path, request.Directory, call.Operation == LinkMessage.BrowseDownload));
                    if (!await call.Approval.Task.WaitAsync(TimeSpan.FromMinutes(2), call.Stop.Token).ConfigureAwait(false))
                        throw new UnauthorizedAccessException();
                    EndBrowsePrompt(call);
                }
            }
            lock (_gate)
            {
                EnsureBrowser(browser, call.Operation != LinkMessage.BrowseOpen);
                call.Stop.Token.ThrowIfCancellationRequested();
                if (call.Operation == LinkMessage.BrowseOpen) browser.Granted = true;
            }
            if (call.Operation == LinkMessage.BrowseList)
                page = browser.Listing.Read(request.Path, request.Offset, call.Stop.Token);
            else if (call.Operation == LinkMessage.BrowseDownload)
            {
                using var source = LinkBrowseDirectory.Pin(request.Path, request.Directory);
                await browser.Link.SendAsync(LinkMessage.BrowsePreparing, call.Id, [], call.Stop.Token).ConfigureAwait(false);
                await SendPathOnLinkAsync(browser.Link, request.Path, call.Stop.Token, call.Id).ConfigureAwait(false);
            }
            call.Stop.Token.ThrowIfCancellationRequested();
            ok = true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            declined = ex is UnauthorizedAccessException or TimeoutException;
            error = declined ? null : "This file request is no longer available.";
        }
        finally
        {
            EndBrowsePrompt(call);
            if (!ok && call.Operation == LinkMessage.BrowseOpen) await CloseBrowserAsync(browser, false).ConfigureAwait(false);
            Task written;
            lock (_gate)
            {
                _browseCalls.Remove(call.Id);
                if (ok && (_browsers.GetValueOrDefault(browser.Id) != browser || call.Stop.IsCancellationRequested ||
                    _connections.GetValueOrDefault(browser.Link.Id) != browser.Link || browser.TrustRequired && !IsFullyTrusted(browser.Link)))
                    { ok = false; page = null; error = "This file browser is closed."; }
                written = SendBrowserControl(browser.Link, LinkMessage.BrowseReply, call.Id,
                    JsonSerializer.SerializeToUtf8Bytes(new BrowseAnswer(ok, page, error, declined)));
            }
            await written.ConfigureAwait(false);
            call.Stop.Dispose();
        }
    }

    private void EndBrowsePrompt(BrowseCall call)
    {
        lock (_gate) { if (!call.Prompt) return; call.Prompt = false; }
        try { FileBrowseRequestEnded?.Invoke(call.Id); }
        catch (Exception ex) { System.Diagnostics.Trace.WriteLine(ex); }
    }

    private void CancelBrowseCall(BrowseCall call)
    {
        lock (_gate)
        {
            try { call.Stop.Cancel(); } catch (ObjectDisposedException) { return; }
            if (call.Matched) CancelFile(call.Id);
        }
    }

    private void CloseFileBrowsers(LinkConnection link)
    {
        Browser[] browsers;
        lock (_gate)
        {
            _retiredDownloads.Remove(link);
            browsers = _browsers.Values.Where(b => b.Link == link).ToArray();
        }
        foreach (var browser in browsers) _ = CloseBrowserAsync(browser, false);
    }

    private void ClosePeerFileBrowsers(string peerId)
    {
        Browser[] browsers;
        lock (_gate) browsers = _browsers.Values.Where(b => b.Link.Id.Equals(peerId, StringComparison.OrdinalIgnoreCase)).ToArray();
        foreach (var browser in browsers) _ = CloseBrowserAsync(browser, true);
    }

    private async Task CloseBrowserAsync(Browser browser, bool notifyPeer)
    {
        BrowseCall[] calls;
        lock (_gate)
        {
            if (_browsers.GetValueOrDefault(browser.Id) != browser) return;
            _browsers.Remove(browser.Id);
            calls = _browseCalls.Values.Where(c => c.Browser == browser).ToArray();
        }
        browser.Life.Cancel();
        foreach (var call in calls) CancelBrowseCall(call);
        browser.Listing.Dispose();
        browser.Life.Dispose();
        if (!browser.Host)
        {
            try { FileBrowserClosed?.Invoke(browser.Link.Id, browser.Id); }
            catch (Exception ex) { System.Diagnostics.Trace.WriteLine(ex); }
        }
        if (notifyPeer) await SendBrowserControl(browser.Link, LinkMessage.BrowseClose, browser.Id, []).ConfigureAwait(false);
    }

    private static async Task SendBrowserControl(LinkConnection link, LinkMessage message, Guid id, byte[] body)
    {
        try { await link.SendAsync(message, id, body).ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException) { }
    }
}
