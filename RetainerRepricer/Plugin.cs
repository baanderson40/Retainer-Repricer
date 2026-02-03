using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Interface.Windowing;
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

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "toggle | config"
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

    private void OnCommand(string command, string args)
    {
        var a = (args ?? string.Empty).Trim().ToLowerInvariant();

        if (a == "config")
        {
            ToggleConfigUi();
            return;
        }

        // default: toggle overlay intent
        MainWindow.ToggleIntent();
    }

    public void ToggleConfigUi() => ConfigWindow.Toggle();

    private void OpenMainUi()
    {
        // Do NOT force the overlay to appear outside RetainerList.
        // Just ensure the user's intent is "on".
        MainWindow.EnsureIntentOpen();
    }

}
