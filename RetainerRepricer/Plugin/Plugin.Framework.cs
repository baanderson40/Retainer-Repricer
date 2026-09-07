using System;
using System.Threading;

using Dalamud.Plugin.Services;

namespace RetainerRepricer;

/// <summary>
/// Hooks the Dalamud framework tick so the state machine advances at a controlled pace.
/// </summary>
public unsafe sealed partial class Plugin
{
    private void OnFrameworkUpdate(IFramework framework)
    {
        TickAutoRetainerIntegration();
        TickBackgroundSmartSortRefresh();

        if (_dismissContextMenuNextTick)
        {
            _dismissContextMenuNextTick = false;
            FireContextMenuDismiss();
        }

        if (!IsRunning)
        {
            return;
        }

        var now = DateTime.UtcNow;
        if ((now - _lastFrameworkTickUtc).TotalSeconds < FrameworkTickIntervalSeconds)
            return;

        TickRun();
        _lastFrameworkTickUtc = now;
    }

    private void TickBackgroundSmartSortRefresh()
    {
        var now = DateTime.UtcNow;
        if (_backgroundSmartSortTask is { IsCompleted: true } completedTask)
        {
            var cancelledByPlugin = _backgroundSmartSortCancellation?.IsCancellationRequested == true;
            if (completedTask.IsFaulted)
            {
                Log.Warning(
                    completedTask.Exception?.GetBaseException(),
                    "[RR][SmartSort] Background refresh failed; retrying after the configured refresh interval.");
                _nextBackgroundSmartSortAttemptUtc = now + SmartSortRefreshInterval;
            }
            else if (completedTask.IsCanceled)
            {
                if (!cancelledByPlugin)
                {
                    Log.Warning(
                        "[RR][SmartSort] Background refresh was cancelled unexpectedly; " +
                        "retrying after the configured refresh interval.");
                    _nextBackgroundSmartSortAttemptUtc = now + SmartSortRefreshInterval;
                }
            }
            else if (!completedTask.Result)
            {
                _nextBackgroundSmartSortAttemptUtc = now + SmartSortRefreshInterval;
            }

            _backgroundSmartSortTask = null;
            _backgroundSmartSortCancellation?.Dispose();
            _backgroundSmartSortCancellation = null;
        }

        if (_backgroundSmartSortTask != null || now < _nextBackgroundSmartSortAttemptUtc || IsRunning ||
            _autoRetainerHandoffActive || !SmartSortEnabled || SmartSortIsSorting || !SmartSortRefreshDue)
        {
            return;
        }

        if (_universalisClient.TryGetCooldownRemaining(out _))
            return;

        _backgroundSmartSortCancellation = new CancellationTokenSource();
        _backgroundSmartSortTask = _smartSorter.TrySortAsync(
            "periodic_refresh",
            cancellationToken: _backgroundSmartSortCancellation.Token);
    }

    private void CancelBackgroundSmartSortRefresh()
    {
        _backgroundSmartSortCancellation?.Cancel();
    }
}
