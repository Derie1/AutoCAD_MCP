using System.Reflection;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Autodesk.Windows;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace AcadMcpBridge;

/// <summary>
/// "AI companion" ribbon panel with MCPSTART / MCPSTOP / MCPSTATUS buttons, added at run time
/// through the ribbon API (AdWindows) to the Home tab. Nothing is written to the user's CUIx:
/// the panel exists while the plugin is loaded and disappears when it is uninstalled.
/// If a workspace has no Home tab, the panel gets its own "AI companion" tab.
/// </summary>
internal static class RibbonUi
{
    private const string PanelId = "ACADMCP_PANEL";
    private const string HomeTabId = "ID_TabHome";
    private const string OwnTabId = "ACADMCP_TAB";
    private const string Title = "AI companion";

    /// <summary>Where the panel ended up, reported by health_check.</summary>
    public static string State { get; private set; } = "not created";

    public static void Init()
    {
        if (ComponentManager.Ribbon == null) ComponentManager.ItemInitialized += OnItemInitialized;
        else Add();
        // Switching the workspace rebuilds the ribbon from the CUIx: add the panel again.
        AcApp.SystemVariableChanged += (_, e) =>
        {
            if (e.Name.Equals("WSCURRENT", StringComparison.OrdinalIgnoreCase)) Add();
        };
    }

    private static void OnItemInitialized(object? sender, RibbonItemEventArgs e)
    {
        if (ComponentManager.Ribbon == null) return;
        ComponentManager.ItemInitialized -= OnItemInitialized;
        Add();
    }

    private static int _retries;

    /// <summary>
    /// When the plugin loads, the ribbon may exist but still be empty (tabs come from the CUIx a
    /// moment later). Then try again when AutoCAD is idle, a limited number of times.
    /// </summary>
    private static void RetryWhenIdle()
    {
        if (_retries++ >= 20) return;
        void Once(object? s, EventArgs e) { AcApp.Idle -= Once; Add(); }
        AcApp.Idle += Once;
    }

    private static void Add()
    {
        try
        {
            var ribbon = ComponentManager.Ribbon;
            if (ribbon == null) return;
            // Tab ids carry the CUI group: "ACAD.ID_TabHome" in AutoCAD, other prefixes in verticals.
            var home = ribbon.Tabs.FirstOrDefault(t => t.Id == HomeTabId || (t.Id?.EndsWith("." + HomeTabId) ?? false));
            if (home == null && ribbon.Tabs.Count == 0 && _retries < 20)
            {
                RetryWhenIdle(); // ribbon not filled yet
                return;
            }
            if (home == null)
                Log.Info($"ribbon: no Home tab in this workspace (tabs: {string.Join(", ", ribbon.Tabs.Take(15).Select(t => t.Id))}…)");
            // The Home tab appeared after a fallback tab was made: move the panel to Home.
            if (home != null && ribbon.FindTab(OwnTabId) is { } own) ribbon.Tabs.Remove(own);

            var tab = home ?? ribbon.FindTab(OwnTabId);
            if (tab == null)
            {
                tab = new RibbonTab { Id = OwnTabId, Title = Title };
                ribbon.Tabs.Add(tab);
            }
            if (tab.Panels.Any(p => p.Source?.Id == PanelId)) return;

            var source = new RibbonPanelSource { Id = PanelId, Title = Title };
            source.Items.Add(Button("MCPSTART", "Start", "McpStart",
                "Start the MCP bridge: Claude can then read and change this drawing."));
            source.Items.Add(Button("MCPSTOP", "Stop", "McpStop",
                "Stop the MCP bridge: Claude loses access to AutoCAD."));
            source.Items.Add(Button("MCPSTATUS", "Status", "McpStatus",
                "Show whether the MCP bridge is running, its port and how many requests it served."));
            tab.Panels.Add(new RibbonPanel { Source = source });
            State = tab == home ? "Home tab" : "own AI companion tab";
            Log.Info($"ribbon panel added ({State})");
        }
        catch (Exception ex)
        {
            State = "error: " + ex.Message;
            Log.Error("cannot add the ribbon panel", ex);
        }
    }

    private static RibbonButton Button(string command, string text, string icon, string description) => new()
    {
        Id = "ACADMCP_" + command,
        Text = text,
        ShowText = true,
        ShowImage = true,
        Size = RibbonItemSize.Large,
        Orientation = System.Windows.Controls.Orientation.Vertical,
        LargeImage = Image(icon + "_32.png"),
        Image = Image(icon + "_16.png"),
        CommandParameter = "_" + command + " ",
        CommandHandler = new RunCommand(),
        ToolTip = new RibbonToolTip { Title = text + " MCP bridge", Content = description, Command = command },
    };

    private static BitmapImage? Image(string resource)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource);
        if (stream == null) return null;
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.StreamSource = stream;
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    /// <summary>Runs the button's command line in the active drawing, like typing it.</summary>
    private sealed class RunCommand : ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter)
        {
            string? cmd = (parameter as RibbonButton)?.CommandParameter as string ?? parameter as string;
            var doc = AcApp.DocumentManager.MdiActiveDocument;
            if (cmd != null && doc != null) doc.SendStringToExecute(cmd, true, false, true);
        }
    }
}
