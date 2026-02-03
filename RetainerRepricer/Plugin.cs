using System;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using RetainerRepricer.Windows;

namespace RetainerRepricer;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static IGameGui GameGui { get; private set; } = null!;

    private const string CommandName = "/repricer";

    public Configuration Configuration { get; }

    private readonly Ui.UiReader _ui;

    public readonly WindowSystem WindowSystem = new("RetainerRepricer");
    private ConfigWindow ConfigWindow { get; }
    private MainWindow MainWindow { get; }

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Configuration.Initialize(PluginInterface);

        ConfigWindow = new ConfigWindow(this);
        MainWindow = new MainWindow(this);

        WindowSystem.AddWindow(ConfigWindow);
        WindowSystem.AddWindow(MainWindow);

        _ui = new Ui.UiReader(GameGui);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "toggle | config | mbdump (debug) | mbnodelist (debug)"
        });

        PluginInterface.UiBuilder.Draw += WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi += OpenMainUi;

        Log.Information($"[{PluginInterface.Manifest.Name}] loaded.");
    }

    public void Dispose()
    {
        PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi -= OpenMainUi;

        WindowSystem.RemoveAllWindows();

        ConfigWindow.Dispose();
        MainWindow.Dispose();

        CommandManager.RemoveHandler(CommandName);
    }

    private unsafe void OnCommand(string command, string args)
    {
        var a = (args ?? string.Empty).Trim().ToLowerInvariant();

        if (a == "config")
        {
            ToggleConfigUi();
            return;
        }

        // Debug: dump ItemSearchResult addon's NodeList (flat)
        if (a == "mbnodelist")
        {
            _ui.DumpAddonNodeList("ItemSearchResult", s => Log.Information(s));
            return;
        }

        // Debug: dump first rows from Market Board results list
        if (a == "mbdump")
        {
            var list = _ui.GetMarketList();
            if (list == null)
            {
                Log.Warning("[MB] Market list not found. Make sure ItemSearchResult is open and populated.");
                return;
            }

            var count = list->GetItemCount();
            Log.Information($"[MB] renderer count = {count}");

            var max = Math.Min(count, 10);
            for (int i = 0; i < max; i++)
            {
                var r = list->GetItemRenderer(i);
                if (r == null) continue;

                var seller = _ui.ReadRendererText(r, Ui.NodePaths.SellerNodeId);
                var unitRaw = _ui.ReadRendererText(r, Ui.NodePaths.UnitPriceNodeId);
                var qtyRaw = _ui.ReadRendererText(r, Ui.NodePaths.QuantityNodeId);

                // Optional debug (remove once confirmed)
                _ui.DumpHqIconState(r, i, s => Log.Information(s));

                var isHq = _ui.RowIsHq(r);

                var unit = ParseGil(unitRaw);
                var qty = int.TryParse(qtyRaw, out var q) ? q : 0;

                Log.Information($"[MB] row {i}: seller={seller} unit={unit} qty={qty} hq={(isHq ? "HQ" : "NQ")}");
            }

            return;
        }

        // Default: toggle overlay intent
        MainWindow.ToggleIntent();
    }

    private static int? ParseGil(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;

        long value = 0;
        bool any = false;

        foreach (var ch in s)
        {
            if (ch >= '0' && ch <= '9')
            {
                any = true;
                value = (value * 10) + (ch - '0');
                if (value > int.MaxValue) return int.MaxValue;
            }
        }

        return any ? (int)value : null;
    }

    public void ToggleConfigUi() => ConfigWindow.Toggle();

    private void OpenMainUi()
    {
        // Do NOT force the overlay to appear outside RetainerList.
        // Just ensure the user's intent is "on".
        MainWindow.EnsureIntentOpen();
    }
}
