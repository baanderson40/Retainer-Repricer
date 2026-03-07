using System;

namespace RetainerRepricer;

/// <summary>
/// Command routing for /repricer operations.
/// </summary>
public unsafe sealed partial class Plugin
{
    private void OnCommand(string command, string args)
    {
        var a = (args ?? string.Empty).Trim().ToLowerInvariant();

        switch (a)
        {
            case "":
            case "help":
                PrintHelp();
                return;

            case "start":
                StartRun();
                return;

            case "stop":
                if (IsRunning)
                    StopRun();
                return;

            case "config":
                ToggleConfigUi();
                return;

            case "count":
                {
                    var raw = _uiReader.ReadRetainerSellListCountText();
                    var count = _uiReader.ReadRetainerSellListListedCount();
                    if (count == null)
                    {
                        ChatGui.Print("[RetainerRepricer] Open RetainerSellList first.");
                        return;
                    }

                    ChatGui.Print($"[RetainerRepricer] Listed = {count} (raw='{raw}')");
                    return;
                }

            case "mbdump":
                DumpMarketRows();
                return;

            case "rldump":
                DumpRetainerRows();
                return;

            case "testgate":
                TestUniversalisGate();
                return;

            default:
                PrintHelp();
                return;
        }
    }

    private void PrintHelp()
    {
        ChatGui.Print("[RetainerRepricer]");
        ChatGui.Print("/repricer start   - Start repricing & selling from Retainer List");
        ChatGui.Print("/repricer stop    - Stop current run");
        ChatGui.Print("/repricer config  - Open configuration window");
    }
}
