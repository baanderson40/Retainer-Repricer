using System;

namespace RetainerRepricer;

/// <summary>
/// Command routing for /repricer operations.
/// </summary>
public unsafe sealed partial class Plugin
{
    private void OnCommand(string command, string args)
    {
        var tokens = string.IsNullOrWhiteSpace(args)
            ? Array.Empty<string>()
            : args.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var primary = tokens.Length > 0 ? tokens[0].ToLowerInvariant() : string.Empty;
        var secondary = tokens.Length > 1 ? tokens[1].ToLowerInvariant() : string.Empty;

        switch (primary)
        {
            case "":
            case "help":
                PrintHelp();
                return;

            case "start":
                HandleStartCommand(secondary);
                return;

            case "stop":
                HandleStopCommand();
                return;

            case "config":
            case "c":
                ToggleConfigUi();
                return;

            default:
                PrintHelp();
                return;
        }
    }

    private void HandleStartCommand(string modeArg)
    {
        var (mode, recognized) = ResolveRunMode(modeArg);
        if (!recognized && !string.IsNullOrEmpty(modeArg))
        {
            PrintError($"Unknown start mode '{modeArg}'. Using default (repricing + selling).");
        }

        _ = StartRun(mode, notifyChatOnFailure: true);
    }

    private void HandleStopCommand()
    {
        if (IsRunning)
        {
            StopRun();
            return;
        }

        PrintError("Not currently running.");
    }

    private static (RunMode mode, bool recognized) ResolveRunMode(string arg)
    {
        if (string.IsNullOrWhiteSpace(arg))
            return (RunMode.PriceAndSell, true);

        return arg.ToLowerInvariant() switch
        {
            "price" => (RunMode.PriceOnly, true),
            "sell" => (RunMode.SellOnly, true),
            "both" => (RunMode.PriceAndSell, true),
            _ => (RunMode.PriceAndSell, false)
        };
    }

    private void PrintHelp()
    {
        PrintInfo("/repricer, /rr - Show this help");
        PrintInfo("/repricer start [mode] - Begin automation (omit mode to run both).");
        PrintInfo("--Modes: price = reprice listings only, sell = Sell List only");
        PrintInfo("/repricer stop - Stop the current run and unwind open UI");
        PrintInfo("/repricer config | c - Open or close the configuration window");
    }
}
