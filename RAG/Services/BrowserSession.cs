using System.Diagnostics;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Options;

namespace RAG.Services;

/// <summary>
/// Counts how many browser tabs currently have the page open.
///
/// Each tab holds a connection open for as long as it lives, so this is driven by connections
/// arriving and dropping rather than by the page reporting in. A polling heartbeat would be
/// simpler but wrong: browsers throttle timers in background tabs to roughly once a minute, so a
/// tab sitting behind another window would look identical to a closed one. A held-open connection
/// is not throttled, and it drops immediately when a tab closes or the browser quits.
/// </summary>
public sealed class BrowserPresence
{
    private readonly Lock _gate = new();

    private int _open;
    private bool _everConnected;
    private DateTime? _emptySinceUtc;

    public void Opened()
    {
        lock (_gate)
        {
            _open++;
            _everConnected = true;
            _emptySinceUtc = null;
        }
    }

    public void Closed()
    {
        lock (_gate)
        {
            _open = Math.Max(0, _open - 1);
            if (_open == 0) _emptySinceUtc = DateTime.UtcNow;
        }
    }

    public int OpenTabs
    {
        get { lock (_gate) return _open; }
    }

    /// <summary>
    /// True once every tab has been gone for longer than <paramref name="grace"/>.
    ///
    /// The grace period exists because a refresh drops the connection and immediately makes a new
    /// one — without it, pressing F5 would shut the app down. It stays false until a browser has
    /// connected at least once, so running with no browser at all (a headless box, or a machine
    /// with no default browser) keeps the app alive rather than exiting seconds after startup.
    /// </summary>
    public bool HasGoneAway(TimeSpan grace)
    {
        lock (_gate)
        {
            if (!_everConnected || _open > 0 || _emptySinceUtc is not { } since) return false;
            return DateTime.UtcNow - since > grace;
        }
    }
}

/// <summary>
/// Opens the app in a browser on startup, and shuts the app down once that browser has gone.
///
/// This is what makes it feel like a desktop application rather than a server you have to
/// remember to stop: run it, the page appears; close the page, the console returns.
/// </summary>
public sealed class BrowserSessionService(
    IOptions<RagOptions> options,
    IServer server,
    IHostApplicationLifetime lifetime,
    BrowserPresence presence,
    IndexingService indexing,
    ILogger<BrowserSessionService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;

        if (!await WaitForStartupAsync(stoppingToken)) return;

        var url = ResolveUrl();

        if (settings.LaunchBrowser && url is not null) OpenBrowser(url);
        else if (url is not null) logger.LogInformation("Scriptorium is at {Url}", url);

        if (!settings.ShutdownWhenBrowserCloses) return;

        await WatchForClosureAsync(TimeSpan.FromSeconds(Math.Max(2, settings.BrowserGraceSeconds)), stoppingToken);
    }

    /// <summary>Kestrel has not bound its addresses until the host reports started.</summary>
    private async Task<bool> WaitForStartupAsync(CancellationToken stoppingToken)
    {
        var started = new TaskCompletionSource();

        using var onStarted = lifetime.ApplicationStarted.Register(() => started.TrySetResult());
        using var onStopping = stoppingToken.Register(() => started.TrySetCanceled());

        try
        {
            await started.Task;
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private async Task WatchForClosureAsync(TimeSpan grace, CancellationToken stoppingToken)
    {
        var warnedAboutIndexing = false;

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);

                if (!presence.HasGoneAway(grace)) continue;

                // Indexing a large library can run for hours. Closing the tab must not throw that
                // away — the run continues, and the app exits once it finishes and the tab is
                // still gone.
                if (indexing.IsRunning)
                {
                    if (!warnedAboutIndexing)
                    {
                        logger.LogInformation(
                            "Browser closed, but indexing is still running — staying up until it finishes.");
                        warnedAboutIndexing = true;
                    }
                    continue;
                }

                logger.LogInformation("Browser closed — shutting down.");
                lifetime.StopApplication();
                return;
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down for some other reason; nothing to do.
        }
    }

    private string? ResolveUrl()
    {
        var addresses = server.Features.Get<IServerAddressesFeature>()?.Addresses;
        if (addresses is null || addresses.Count == 0) return null;

        // Prefer plain HTTP: the development HTTPS certificate makes the browser show a warning
        // page before the app, which rather spoils the effect of it opening by itself.
        var address = addresses.FirstOrDefault(a => a.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                   ?? addresses.First();

        // A wildcard binding is not something a browser can navigate to.
        return address.Replace("[::]", "localhost").Replace("0.0.0.0", "localhost");
    }

    private void OpenBrowser(string url)
    {
        try
        {
            // UseShellExecute is what hands the URL to the default browser rather than trying to
            // execute it as a program.
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            logger.LogInformation("Opened {Url} in your default browser.", url);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not open a browser automatically. Open {Url} yourself.", url);
        }
    }
}
