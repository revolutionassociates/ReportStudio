using Microsoft.Extensions.Options;
using ReportStudio.Core.Server;

namespace ReportStudio.Web;

/// <summary>
/// Looks about every <see cref="ServerOptions.PollSeconds"/> seconds for subscriptions that are due and runs them one after another
/// (requirement 50). A run that fails is recorded and the next run is worked out as usual; one bad subscription never stops the others,
/// and a damaged subscription file is logged and tried again at the next look. When the server is switched off the service does nothing.
/// </summary>
public sealed class SchedulerService : BackgroundService
{
    private readonly SubscriptionStore _store;
    private readonly SubscriptionRunner _runner;
    private readonly IOptions<ServerOptions> _options;
    private readonly TimeProvider _time;
    private readonly ILogger<SchedulerService> _log;

    public SchedulerService(SubscriptionStore store, SubscriptionRunner runner, IOptions<ServerOptions> options, TimeProvider time, ILogger<SchedulerService> log)
    {
        _store = store;
        _runner = runner;
        _options = options;
        _time = time;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Value.Enabled)
        {
            _log.LogInformation("The report server is switched off (ReportStudio:Server:Enabled); no subscriptions will run.");
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(5, _options.Value.PollSeconds)), _time);
        _log.LogInformation("The report server is on; looking for due subscriptions every {Seconds} seconds.", Math.Max(5, _options.Value.PollSeconds));
        try
        {
            do
            {
                await RunDueAsync(stoppingToken).ConfigureAwait(false);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            // the server is stopping
        }
    }

    /// <summary>Runs every subscription that is due now and returns how many it ran. Public so a test can drive it without waiting.</summary>
    public async Task<int> RunDueAsync(CancellationToken ct)
    {
        IReadOnlyList<Subscription> due;
        try
        {
            due = _store.Due(_time.GetUtcNow());
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            _log.LogError(ex, "The subscription file could not be read; will try again.");
            return 0;
        }

        int ran = 0;
        foreach (var s in due)
        {
            ct.ThrowIfCancellationRequested();
            SubscriptionOutcome outcome;
            try
            {
                outcome = await _runner.RunAsync(s, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
            {
                _log.LogError(ex, "Subscription {Id} ({Name}) failed", s.Id, s.Name);
                outcome = new SubscriptionOutcome(false, "The subscription failed: " + ex.GetType().Name);
            }

            ran++;
            try
            {
                _store.RecordRun(s.Id, outcome.Ok, outcome.Ok ? null : outcome.Message);
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                _log.LogError(ex, "The result of subscription {Id} could not be saved", s.Id);
            }
        }
        return ran;
    }
}
