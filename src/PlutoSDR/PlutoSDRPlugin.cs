using System.Reflection;
using System.Windows.Forms;
using SDRSharp.Common;

namespace SDRSharp.PlutoSDR;

/// <summary>
/// SDR# plug-in entry point.
///
/// SDR# 1921 has no public extension point for hardware frontends: its built-in sources are
/// registered by name from <c>MainForm.InitializeGUI()</c> through the private instance method
/// <c>LoadSourceType(string name, Type type, int rank)</c>, which fills the frontend table and
/// appends the matching entry to the Source menu. Plug-ins are loaded later, from
/// <c>MainForm_Load</c>, so this plug-in invokes that same method by reflection. The host then
/// treats PlutoSDR exactly like a built-in source: it appears in the Source menu, is instantiated
/// through its parameterless constructor, and gets wired to the streaming/tuning interfaces it
/// implements.
///
/// The plug-in is deliberately passive: it registers the source and does nothing else on its own.
/// It never selects a source, never starts reception and never writes to SDR#'s own configuration.
/// Every other action comes from the user, either through the Source menu or through the buttons in
/// the plug-in panel.
/// </summary>
public class PlutoSDRPlugin : ISharpPlugin
{
    internal const string SourceName = "PlutoSDR (ADALM-PLUTO)";
    private const int SourceRank = 10;

    private PlutoPluginPanel _gui;
    private ISharpControl _control;
    private Form _mainForm;
    private bool _registered;
    private int _builtinSourceCount;
    private string _status = "Not initialised";

    public string DisplayName => "PlutoSDR";

    public UserControl Gui => _gui ??= new PlutoPluginPanel(this);

    internal string Status => _status;
    internal bool Registered => _registered;

    /// <summary>What SDR# currently reports as its active source, for the panel's status line.</summary>
    internal string CurrentSourceName
    {
        get
        {
            try { return _control?.SourceName; }
            catch { return null; }
        }
    }

    public void Initialize(ISharpControl control)
    {
        PlutoLog.Write("=== plug-in Initialize ===");
        _control = control;
        _mainForm = ResolveMainForm(control);
        PlutoLog.Write($"host control object = {control?.GetType().FullName ?? "<null>"}, " +
                       $"main form = {_mainForm?.GetType().FullName ?? "<not found>"}");
        _gui ??= new PlutoPluginPanel(this);

        TryRegister();
    }

    public void Close()
    {
        PlutoLog.Write("=== plug-in Close ===");
    }

    /// <summary>
    /// Finds the host form that owns the frontend table. SDR# does not hand plugins the form: the
    /// ISharpControl instance is a SharpControlProxy that merely forwards to MainForm, so the form
    /// is recovered from the proxy (or from the open-forms list, since plug-ins are initialised
    /// while MainForm is loading).
    /// </summary>
    private static Form ResolveMainForm(ISharpControl control)
    {
        if (control is Form direct) return direct;

        if (control != null)
        {
            foreach (FieldInfo field in control.GetType().GetFields(
                         BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
            {
                object value;
                try { value = field.GetValue(control); }
                catch { continue; }
                if (value is Form nested) return nested;
            }
        }

        try
        {
            foreach (Form open in Application.OpenForms)
            {
                if (open.GetType().FullName == "SDRSharp.MainForm") return open;
            }
        }
        catch
        {
        }

        return null;
    }

    /// <summary>Registers the frontend with the host; safe to call again from the panel.</summary>
    internal bool TryRegister()
    {
        if (_registered) return true;

        if (_mainForm == null)
        {
            _status = "Host form not available.";
            PlutoLog.Write("registration failed: could not locate the host MainForm");
            return false;
        }

        try
        {
            Type type = _mainForm.GetType();

            FieldInfo typesField = type.GetField("_frontendTypes",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            MethodInfo loadMethod = type.GetMethod("LoadSourceType",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
                null, new[] { typeof(string), typeof(Type), typeof(int) }, null);

            if (typesField == null || loadMethod == null)
            {
                _status = "This SDR# build does not expose the frontend registration hook.";
                PlutoLog.Write("registration failed: LoadSourceType/_frontendTypes not found on " +
                               _mainForm.GetType().FullName);
                return false;
            }

            object table = typesField.GetValue(_mainForm);
            if (table == null)
            {
                _status = "The host frontend table is not ready yet.";
                PlutoLog.Write("registration deferred: _frontendTypes is null");
                return false;
            }

            PropertyInfo countProperty = table.GetType().GetProperty("Count");
            _builtinSourceCount = countProperty != null ? (int)countProperty.GetValue(table) : 0;

            if (table is System.Collections.IDictionary dictionary && dictionary.Contains(SourceName))
            {
                _registered = true;
                _status = "Already registered.";
                PlutoLog.Write("frontend already present in the host table");
                _gui?.RefreshStatus();
                return true;
            }

            loadMethod.Invoke(_mainForm, new object[] { SourceName, typeof(PlutoSDRIO), SourceRank });
            _registered = true;
            RepositionSourceMenuItem();
            _status = $"Registered as source #{_builtinSourceCount} - pick it in the Source menu.";
            PlutoLog.Write($"frontend registered as '{SourceName}' (built-in sources: {_builtinSourceCount})");
            LogSourceMenu();
            _gui?.RefreshStatus();
            return true;
        }
        catch (Exception ex)
        {
            _status = "Registration failed: " + Unwrap(ex).Message;
            PlutoLog.Write("registration threw", Unwrap(ex));
            _gui?.RefreshStatus();
            return false;
        }
    }

    /// <summary>
    /// Moves our Source-menu entry to the slot the host will look up.
    ///
    /// LoadSourceType() always appends, but the host resolves a selected source as
    /// <c>DropDownItems[selected + 2]</c>. SDR# appends its own "Baseband from Sound Card" entry
    /// after the built-in frontends, so a plain append leaves our entry one slot too far right and
    /// the host looks up the wrong name, fails the lookup and opens nothing.
    /// </summary>
    private void RepositionSourceMenuItem()
    {
        try
        {
            var menu = GetSourceMenu();
            if (menu == null) return;

            ToolStripItem mine = null;
            int mineIndex = -1;
            for (int i = 0; i < menu.DropDownItems.Count; i++)
            {
                if (menu.DropDownItems[i].Text != SourceName) continue;
                mine = menu.DropDownItems[i];
                mineIndex = i;
                break;
            }
            if (mine == null) return;

            int desired = _builtinSourceCount + 2;
            if (mineIndex == desired) return;

            menu.DropDownItems.Remove(mine);
            if (desired > menu.DropDownItems.Count) desired = menu.DropDownItems.Count;
            menu.DropDownItems.Insert(desired, mine);
        }
        catch (Exception ex)
        {
            PlutoLog.Write("could not reposition the Source menu entry", ex);
        }
    }

    private ToolStripMenuItem GetSourceMenu()
    {
        if (_mainForm == null) return null;
        FieldInfo menuField = _mainForm.GetType().GetField("sourceMenuItem",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        return menuField?.GetValue(_mainForm) as ToolStripMenuItem;
    }

    private void LogSourceMenu()
    {
        try
        {
            var menu = GetSourceMenu();
            if (menu == null) return;
            var parts = new List<string>();
            for (int i = 0; i < menu.DropDownItems.Count; i++)
            {
                ToolStripItem item = menu.DropDownItems[i];
                parts.Add($"[{i}] '{item.Text}' tag={item.Tag ?? "<null>"}");
            }
            PlutoLog.Write("Source menu now: " + string.Join(" | ", parts));
        }
        catch
        {
        }
    }

    /// <summary>
    /// Clicks the Source menu entry the host created for us. Only ever called from a button the user
    /// pressed; nothing here runs on its own.
    /// </summary>
    internal bool SelectPlutoSource()
    {
        var menu = GetSourceMenu();
        if (menu == null) return false;

        foreach (ToolStripItem item in menu.DropDownItems)
        {
            if (item.Text != SourceName) continue;
            item.PerformClick();
            _status = "PlutoSDR selected.";
            PlutoLog.Write("Source menu entry clicked by the user.");
            _gui?.RefreshStatus();
            return true;
        }

        _status = "PlutoSDR entry is missing from the Source menu.";
        PlutoLog.Write("Source menu entry not found");
        _gui?.RefreshStatus();
        return false;
    }

    private static Exception Unwrap(Exception ex)
        => ex is TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : ex;
}

/// <summary>Compact panel SDR# docks for the plug-in itself.</summary>
internal sealed class PlutoPluginPanel : UserControl
{
    private readonly PlutoSDRPlugin _plugin;
    private readonly Label _statusLabel = new Label();
    private readonly Button _selectButton = new Button();
    private readonly Button _retryButton = new Button();

    public PlutoPluginPanel(PlutoSDRPlugin plugin)
    {
        _plugin = plugin;
        Dock = DockStyle.Fill;
        BackColor = System.Drawing.Color.FromArgb(45, 45, 48);
        ForeColor = System.Drawing.Color.Gainsboro;
        Font = new System.Drawing.Font("Segoe UI", 8.25f);
        Size = new System.Drawing.Size(280, 190);

        _statusLabel.SetBounds(8, 8, 264, 80);
        Controls.Add(_statusLabel);

        _selectButton.SetBounds(8, 94, 264, 26);
        _selectButton.Text = "Use PlutoSDR as the source now";
        _selectButton.Click += (s, e) =>
        {
            _plugin.TryRegister();
            _plugin.SelectPlutoSource();
        };
        Controls.Add(_selectButton);

        _retryButton.SetBounds(8, 126, 264, 24);
        _retryButton.Text = "Re-register the frontend";
        _retryButton.Click += (s, e) => { _plugin.TryRegister(); RefreshStatus(); };
        Controls.Add(_retryButton);

        var hint = new Label
        {
            Text = "Nothing happens automatically: pick PlutoSDR in the Source menu and press Play.",
            ForeColor = System.Drawing.Color.Gray
        };
        hint.SetBounds(8, 156, 264, 32);
        Controls.Add(hint);

        RefreshStatus();
    }

    internal void RefreshStatus()
    {
        if (InvokeRequired && IsHandleCreated)
        {
            try { BeginInvoke(new Action(RefreshStatus)); } catch { }
            return;
        }

        string source = _plugin.CurrentSourceName;
        if (!string.IsNullOrEmpty(source))
        {
            int comma = source.IndexOf(',');
            if (comma > 0) source = source.Substring(0, comma);
        }

        _statusLabel.Text =
            (_plugin.Registered ? "Source registered." : "Source NOT registered.") + "\r\n" +
            _plugin.Status + "\r\n" +
            "Host source: " + (string.IsNullOrEmpty(source) ? "<none>" : source) + "\r\n" +
            "Settings: " + System.IO.Path.GetDirectoryName(typeof(PlutoSDRPlugin).Assembly.Location);
        _retryButton.Enabled = !_plugin.Registered;
    }
}
