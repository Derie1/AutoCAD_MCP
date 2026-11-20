using System.Drawing;
using System.Windows.Forms;
using Autodesk.AutoCAD.Runtime;
using AcUiApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace AcadMcpBridge;

/// <summary>
/// Whether run_command and eval_lisp may run. Off by default. The user turns them on for the session
/// with the ribbon toggle or MCPCOMMANDS (after a warning), or permanently with "allowCommands": true in
/// config.json; test sessions use the ACAD_MCP_ALLOW_COMMANDS=1 environment variable.
/// </summary>
internal static class CommandAccess
{
    public const string EnvVar = "ACAD_MCP_ALLOW_COMMANDS";

    public static bool Allowed { get; private set; }

    /// <summary>off, config, environment or session.</summary>
    public static string Source { get; private set; } = "off";

    /// <summary>Raised on the main thread whenever Allowed changes (the ribbon toggle follows it).</summary>
    public static event Action? Changed;

    public static void Init()
    {
        // A batch (AcCoreConsole) may use commands only when config.json allows them.
        if (!Batch.IsConsole && Environment.GetEnvironmentVariable(EnvVar) == "1") Set(true, "environment");
        else
        {
            try { if (BridgeConfig.Load().AllowCommands) Set(true, "config"); }
            catch (System.Exception ex) { Log.Error("config.json: allowCommands not read", ex); }
        }
    }

    /// <summary>Ribbon toggle / MCPCOMMANDS: turns off at once, turns on only after the warning.</summary>
    public static void Toggle()
    {
        if (Allowed) Set(false, "off");
        else if (Warn()) Set(true, "session");
        else Changed?.Invoke(); // declined: the toggle button flips back
    }

    /// <summary>Throws for the model when the user has not allowed commands.</summary>
    public static void Require(string tool)
    {
        if (Allowed) return;
        throw new BridgeException("commands_disabled",
            $"{tool} is turned off in AutoCAD. Only the user can allow it: the 'Commands' button on the AI companion " +
            "panel (Home tab) or the MCPCOMMANDS command, after a warning; or \"allowCommands\": true in " +
            $"{BridgeConfig.ConfigPath}. Prefer the dedicated tools when they can do the job.", 403);
    }

    public static string Describe() => Allowed ? $"allowed ({Source})" : "off";

    private static void Set(bool allowed, string source)
    {
        Allowed = allowed;
        Source = source;
        Log.Info($"run_command / eval_lisp {(allowed ? "allowed" : "turned off")} ({source})");
        Changed?.Invoke();
    }

    /// <summary>Russian when AutoCAD itself runs in Russian, English otherwise.</summary>
    internal static bool Russian
    {
        get
        {
            try { return SystemObjects.DynamicLinker.ProductLcid == 1049; }
            catch { return false; }
        }
    }

    private static bool Warn()
    {
        using var form = new WarningForm(Russian);
        return AcUiApp.ShowModalDialog(form) == DialogResult.Yes;
    }

    /// <summary>The warning shown before commands and AutoLISP are allowed.</summary>
    internal sealed class WarningForm : Form
    {
        public WarningForm(bool russian)
        {
            string title, text, allow, decline;
            if (russian)
            {
                title = "AI companion — команды AutoCAD и AutoLISP";
                text = "Вы разрешаете ИИ-ассистенту выполнять любые команды AutoCAD и выражения AutoLISP в этом сеансе.\n\n" +
                       "Команды и LISP могут изменить или удалить данные чертежа, записать или удалить файлы на компьютере " +
                       "и запустить другие программы. Ассистент выполняет их без отдельного подтверждения каждой команды. " +
                       "Изменения в чертеже можно отменить, а изменения вне чертежа (файлы, настройки) — нельзя.\n\n" +
                       "Разрешайте, только если доверяете задаче, которую поручили ассистенту. Разрешение действует до " +
                       "закрытия AutoCAD или до повторного нажатия кнопки «Commands».";
                allow = "Все равно разрешить";
                decline = "Отказаться";
            }
            else
            {
                title = "AI companion — AutoCAD commands and AutoLISP";
                text = "You are allowing the AI assistant to run any AutoCAD command and AutoLISP expression in this session.\n\n" +
                       "Commands and LISP can change or delete drawing data, write or delete files on this computer and start " +
                       "other programs. The assistant runs them without asking you about each command. Changes in the drawing " +
                       "can be undone; changes outside it (files, settings) cannot.\n\n" +
                       "Allow this only if you trust the task you gave the assistant. The permission lasts until AutoCAD is " +
                       "closed or until you press the \"Commands\" button again.";
                allow = "Allow anyway";
                decline = "Decline";
            }

            Text = title;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96F, 96F);
            Font = new Font("Segoe UI", 9F);
            ClientSize = new Size(520, 250);

            var icon = new PictureBox
            {
                Image = SystemIcons.Warning.ToBitmap(),
                SizeMode = PictureBoxSizeMode.AutoSize,
                Location = new Point(16, 18),
            };
            var label = new Label
            {
                Text = text,
                Location = new Point(64, 16),
                AutoSize = true,
                MaximumSize = new Size(440, 0),
            };
            var allowButton = new Button
            {
                Text = allow,
                DialogResult = DialogResult.Yes,
                AutoSize = true,
                MinimumSize = new Size(150, 30),
                Padding = new Padding(8, 0, 8, 0),
            };
            var declineButton = new Button
            {
                Text = decline,
                DialogResult = DialogResult.No,
                AutoSize = true,
                MinimumSize = new Size(120, 30),
                Padding = new Padding(8, 0, 8, 0),
            };
            var buttons = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.RightToLeft,
                Dock = DockStyle.Bottom,
                Height = 48,
                Padding = new Padding(10, 8, 10, 8),
            };
            buttons.Controls.Add(declineButton);
            buttons.Controls.Add(allowButton);
            Controls.Add(icon);
            Controls.Add(label);
            Controls.Add(buttons);
            int textHeight = label.GetPreferredSize(new Size(440, 0)).Height;
            ClientSize = new Size(520, 16 + textHeight + 16 + buttons.Height);
            // Declining is the safe default: Enter and Esc both decline.
            AcceptButton = declineButton;
            CancelButton = declineButton;
            ActiveControl = declineButton;
        }
    }
}
