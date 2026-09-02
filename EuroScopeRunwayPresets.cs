
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;
using System.Windows.Automation;
using System.Windows.Forms;

[DataContract]
public class RunwayConfiguration
{
    [DataMember(Name = "columns")]
    public List<RunwayColumn> Columns { get; set; }

    public static RunwayConfiguration Load(string path)
    {
        if (!File.Exists(path))
            throw new ConfigurationException("Configuration file not found: " + path);

        try
        {
            RunwayConfiguration configuration;
            using (FileStream stream = File.OpenRead(path))
            {
                var serializer = new DataContractJsonSerializer(typeof(RunwayConfiguration));
                configuration = (RunwayConfiguration)serializer.ReadObject(stream);
            }

            if (configuration == null)
                throw new ConfigurationException("Configuration is empty: " + path);

            configuration.Validate();
            return configuration;
        }
        catch (ConfigurationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new ConfigurationException("Could not read configuration " + path + ": " + ex.Message);
        }
    }

    public void Validate()
    {
        if (Columns == null || Columns.Count == 0)
            throw new ConfigurationException("Configuration must contain at least one column.");

        var codes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int airportCount = 0;

        foreach (RunwayColumn column in Columns)
        {
            if (column == null || column.Airports == null || column.Airports.Count == 0)
                throw new ConfigurationException("Every column must contain at least one airport.");

            foreach (AirportConfiguration airport in column.Airports)
            {
                airportCount++;
                if (airport == null || String.IsNullOrWhiteSpace(airport.Code))
                    throw new ConfigurationException("Every airport must have a non-empty code.");

                airport.Code = airport.Code.Trim().ToUpperInvariant();
                if (!codes.Add(airport.Code))
                    throw new ConfigurationException("Duplicate airport code: " + airport.Code);

                ValidateState(airport.Code, "west", airport.West);
                ValidateState(airport.Code, "east", airport.East);
            }
        }

        if (airportCount == 0)
            throw new ConfigurationException("Configuration must contain at least one airport.");
    }

    private static void ValidateState(string code, string name, RunwayState state)
    {
        if (state == null)
            throw new ConfigurationException(code + " is missing the " + name + " state.");
        if (String.IsNullOrWhiteSpace(state.Dep) || String.IsNullOrWhiteSpace(state.Arr))
            throw new ConfigurationException(code + " " + name + " must define non-empty DEP and ARR runways.");

        state.Dep = state.Dep.Trim();
        state.Arr = state.Arr.Trim();
    }

    public IEnumerable<AirportConfiguration> GetAirports()
    {
        foreach (RunwayColumn column in Columns)
            foreach (AirportConfiguration airport in column.Airports)
                yield return airport;
    }
}

[DataContract]
public class RunwayColumn
{
    [DataMember(Name = "airports")]
    public List<AirportConfiguration> Airports { get; set; }
}

[DataContract]
public class AirportConfiguration
{
    [DataMember(Name = "code")]
    public string Code { get; set; }

    [DataMember(Name = "west")]
    public RunwayState West { get; set; }

    [DataMember(Name = "east")]
    public RunwayState East { get; set; }
}

[DataContract]
public class RunwayState
{
    [DataMember(Name = "dep")]
    public string Dep { get; set; }

    [DataMember(Name = "arr")]
    public string Arr { get; set; }
}

public class ConfigurationException : Exception
{
    public ConfigurationException(string message) : base(message) { }
}

public class PresetButtonData
{
    public AirportConfiguration Airport;
    public RunwayState State;
    public string Key;
}

public class MainForm : Form
{
    private FlowLayoutPanel panel;
    private Label status;
    private readonly RunwayConfiguration configuration;
    private bool inputLockWarningAccepted;
    private readonly Dictionary<string, Button> presetButtons =
        new Dictionary<string, Button>(StringComparer.OrdinalIgnoreCase);

    public MainForm(RunwayConfiguration configuration)
    {
        this.configuration = configuration;
        Text = "EuroScope Runway Preset Manager";
        Width = 1040;
        Height = 470;
        StartPosition = FormStartPosition.CenterScreen;

        panel = new FlowLayoutPanel();
        panel.FlowDirection = FlowDirection.LeftToRight;
        panel.WrapContents = false;
        panel.AutoScroll = false;
        panel.Location = new Point(12, 12);
        panel.Size = new Size(1000, 320);
        Controls.Add(panel);

        foreach (RunwayColumn column in configuration.Columns)
            panel.Controls.Add(BuildGroupColumn(column));

        status = new Label();
        status.Text = "Open EuroScope runway selector, then choose a preset.";
        status.AutoSize = false;
        status.Width = 1000;
        status.Height = 28;
        status.Location = new Point(12, 336);
        Controls.Add(status);

        var cfgCurrent = new Button();
        cfgCurrent.Text = "All RWY West";
        cfgCurrent.Width = 150;
        cfgCurrent.Height = 30;
        cfgCurrent.Location = new Point(530, 375);
        cfgCurrent.Click += (s,e) => ApplyAllConfiguration(false);
        Controls.Add(cfgCurrent);

        var cfgOpposite = new Button();
        cfgOpposite.Text = "All RWY East";
        cfgOpposite.Width = 150;
        cfgOpposite.Height = 30;
        cfgOpposite.Location = new Point(695, 375);
        cfgOpposite.Click += (s,e) => ApplyAllConfiguration(true);
        Controls.Add(cfgOpposite);

        var refresh = new Button();
        refresh.Text = "Refresh";
        refresh.Width = 150;
        refresh.Height = 30;
        refresh.Location = new Point(860, 375);
        refresh.Click += (s,e) => RefreshCurrentStates();
        Controls.Add(refresh);

        this.Shown += (s,e) => RefreshCurrentStates();
    }

    private Control BuildGroupColumn(RunwayColumn column)
    {
        var col = new FlowLayoutPanel();
        col.FlowDirection = FlowDirection.TopDown;
        col.WrapContents = false;
        col.AutoScroll = false;
        col.Width = 192;
        col.Height = 310;
        col.Margin = new Padding(0, 0, 8, 0);

        foreach (AirportConfiguration airport in column.Airports)
            col.Controls.Add(BuildAirportRow(airport));

        return col;
    }

    private Control BuildAirportRow(AirportConfiguration airport)
    {
        var box = new GroupBox();
        box.Text = airport.Code;
        box.Width = 188;
        box.Height = 70;
        box.Margin = new Padding(0, 0, 0, 4);

        box.Controls.Add(BuildPresetButton(airport, airport.West, "west", new Point(12, 27)));
        box.Controls.Add(BuildPresetButton(airport, airport.East, "east", new Point(96, 27)));

        return box;
    }

    private Button BuildPresetButton(AirportConfiguration airport, RunwayState state, string side, Point location)
    {
        string key = airport.Code + ":" + side;
        var button = new Button();
        button.Text = FormatPresetLabel(state);
        button.Width = 76;
        button.Height = 30;
        button.Location = location;
        button.Tag = new PresetButtonData { Airport = airport, State = state, Key = key };
        button.Click += PresetClick;
        presetButtons[key] = button;
        return button;
    }

    private static string FormatPresetLabel(RunwayState state)
    {
        return state.Dep.Equals(state.Arr, StringComparison.OrdinalIgnoreCase)
            ? state.Dep
            : state.Dep + "/" + state.Arr;
    }

    private void PresetClick(object sender, EventArgs e)
    {
        var btn = (Button)sender;
        var data = (PresetButtonData)btn.Tag;

        if (!ConfirmInputLock())
            return;

        EnableButtons(false);
        try
        {
            status.Text = "Applying " + data.Airport.Code + " " + FormatPresetLabel(data.State) + "...";
            Application.DoEvents();

            var mgr = new EuroScopeManager(configuration);
            mgr.ApplyPreset(data.Airport, data.State);

            string verification;
            if (!mgr.VerifyPreset(data.Airport, data.State, out verification))
                throw new Exception("Verification failed: " + verification);

            status.Text = "Verified: " + data.Airport.Code + " " + FormatPresetLabel(data.State) + ".";
            RefreshCurrentStates();
        }
        catch (Exception ex)
        {
            status.Text = "ERROR: " + ex.Message;
        }
        finally
        {
            EnableButtons(true);
        }
    }

    private void ApplyAllConfiguration(bool opposite)
    {
        if (!ConfirmInputLock())
            return;

        EnableButtons(false);
        try
        {
            status.Text = opposite
                ? "Applying East configuration to all airports..."
                : "Applying West configuration to all airports...";
            Application.DoEvents();

            var mgr = new EuroScopeManager(configuration);
            mgr.ApplyAllConfiguration(opposite);

            string verification;
            if (!mgr.VerifyAllConfiguration(opposite, out verification))
                throw new Exception("Verification failed: " + verification);

            status.Text = opposite
                ? "Verified: East configuration applied to all airports."
                : "Verified: West configuration applied to all airports.";

            RefreshCurrentStates();
        }
        catch (Exception ex)
        {
            status.Text = "ERROR: " + ex.Message;
        }
        finally
        {
            EnableButtons(true);
        }
    }

    private void RefreshCurrentStates()
    {
        ResetButtonMarks();

        try
        {
            var mgr = new EuroScopeManager(configuration);

            foreach (AirportConfiguration airport in configuration.GetAirports())
            {
                string details;
                if (mgr.VerifyPreset(airport, airport.West, out details))
                    MarkActive(airport.Code + ":west");
                if (mgr.VerifyPreset(airport, airport.East, out details))
                    MarkActive(airport.Code + ":east");
            }

            status.Text = "Current EuroScope runway states refreshed.";
        }
        catch (Exception ex)
        {
            status.Text = ex.Message.Equals(
                "EuroScope runway selector dialog not found.",
                StringComparison.OrdinalIgnoreCase)
                ? "EuroScope runway selector is not open. Open the Active airport/runway selector window in EuroScope, then click Refresh."
                : "State read failed: " + ex.Message;
        }
    }

    private void ResetButtonMarks()
    {
        foreach (var kv in presetButtons)
        {
            Button b = kv.Value;
            b.Font = new Font(b.Font, FontStyle.Regular);
            b.BackColor = SystemColors.Control;
            b.UseVisualStyleBackColor = true;
        }
    }

    private bool ConfirmInputLock()
    {
        if (inputLockWarningAccepted)
            return true;

        DialogResult result = MessageBox.Show(
            this,
            "Runway configuration will now be applied. Mouse and keyboard input will be temporarily locked. " +
            "Control will return automatically when the operation is complete.",
            "EuroScope Runway Preset Manager",
            MessageBoxButtons.OKCancel,
            MessageBoxIcon.Information);

        if (result != DialogResult.OK)
            return false;

        inputLockWarningAccepted = true;
        return true;
    }

    private void MarkActive(string key)
    {
        Button b;
        if (!presetButtons.TryGetValue(key, out b))
            return;

        b.UseVisualStyleBackColor = false;
        b.BackColor = Color.LightGreen;
        b.Font = new Font(b.Font, FontStyle.Regular);
    }

    private void EnableButtons(bool enabled)
    {
        foreach (Control column in panel.Controls)
            foreach (Control group in column.Controls)
                foreach (Control c in group.Controls)
                    if (c is Button) c.Enabled = enabled;
    }
}

public class EuroScopeManager
{
    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

    [DllImport("user32.dll")]
    static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    static extern bool SetCursorPos(int X, int Y);

    [DllImport("user32.dll")]
    static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    static extern int ShowCursor(bool bShow);

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool BlockInput(bool fBlockIt);

    [DllImport("user32.dll", SetLastError = true)]
    static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    const uint INPUT_MOUSE = 0;
    const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    const uint MOUSEEVENTF_LEFTUP   = 0x0004;

    [StructLayout(LayoutKind.Sequential)]
    struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct INPUT
    {
        public uint type;
        public MOUSEINPUT mi;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }

    private readonly RunwayConfiguration configuration;

    public EuroScopeManager(RunwayConfiguration configuration)
    {
        this.configuration = configuration;
    }

    public void ApplyAllConfiguration(bool opposite)
    {
        IntPtr dlgHwnd = FindWindow("#32770", "Active airport/runway selector dialog");
        if (dlgHwnd == IntPtr.Zero)
            throw new Exception("EuroScope runway selector dialog not found.");

        AutomationElement dlg = AutomationElement.FromHandle(dlgHwnd);
        if (dlg == null)
            throw new Exception("Could not attach to EuroScope dialog.");

        AutomationElement grid = dlg.FindFirst(
            TreeScope.Descendants,
            new PropertyCondition(AutomationElement.AutomationIdProperty, "1012")
        );
        if (grid == null)
            throw new Exception("Runway list not found.");

        POINT original;
        if (!GetCursorPos(out original))
            throw new Exception("Could not read mouse cursor position.");

        if (!BlockInput(true))
            throw new Exception("Could not temporarily lock mouse and keyboard input. No changes were made.");

        try
        {
            HideCursorFully();
            foreach (AirportConfiguration airport in configuration.GetAirports())
                ApplyAirportState(dlgHwnd, grid, airport, opposite ? airport.East : airport.West);
        }
        finally
        {
            SetCursorPos(original.X, original.Y);
            ShowCursorFully();
            BlockInput(false);
        }
    }

    public bool VerifyAllConfiguration(bool opposite, out string details)
    {
        AutomationElement grid = GetGrid();
        foreach (AirportConfiguration airport in configuration.GetAirports())
        {
            RunwayState selected = opposite ? airport.East : airport.West;
            if (!VerifyPreset(grid, airport, selected, out details))
            {
                details = airport.Code + ": " + details;
                return false;
            }
        }

        details = "OK";
        return true;
    }

    private AutomationElement GetGrid()
    {
        IntPtr dlgHwnd = FindWindow("#32770", "Active airport/runway selector dialog");
        if (dlgHwnd == IntPtr.Zero)
            throw new Exception("EuroScope runway selector dialog not found.");

        AutomationElement dlg = AutomationElement.FromHandle(dlgHwnd);
        if (dlg == null)
            throw new Exception("Could not attach to EuroScope dialog.");

        AutomationElement grid = dlg.FindFirst(
            TreeScope.Descendants,
            new PropertyCondition(AutomationElement.AutomationIdProperty, "1012")
        );
        if (grid == null)
            throw new Exception("Runway list not found.");

        return grid;
    }

    public bool TryGetRunwayState(string airport, string runway, out bool depActive, out bool arrActive)
    {
        return TryGetRunwayState(GetGrid(), airport, runway, out depActive, out arrActive);
    }

    private bool TryGetRunwayState(
        AutomationElement grid,
        string airport,
        string runway,
        out bool depActive,
        out bool arrActive)
    {
        depActive = false;
        arrActive = false;

        AutomationElement row = FindRunwayRow(grid, airport, runway);
        if (row == null)
            return false;

        AutomationElementCollection cells = row.FindAll(TreeScope.Children, Condition.TrueCondition);
        if (cells.Count < 6)
            return false;

        depActive = SafeName(cells[4]).Equals("ICON:1", StringComparison.OrdinalIgnoreCase);
        arrActive = SafeName(cells[5]).Equals("ICON:2", StringComparison.OrdinalIgnoreCase);
        return true;
    }

    public bool VerifyPreset(AirportConfiguration airport, RunwayState selected, out string details)
    {
        return VerifyPreset(GetGrid(), airport, selected, out details);
    }

    private bool VerifyPreset(
        AutomationElement grid,
        AirportConfiguration airport,
        RunwayState selected,
        out string details)
    {
        foreach (string runway in GetConfiguredRunways(airport))
        {
            bool dep, arr;
            if (!TryGetRunwayState(grid, airport.Code, runway, out dep, out arr))
            {
                details = "cannot read " + airport.Code + " " + runway;
                return false;
            }

            bool depWanted = runway.Equals(selected.Dep, StringComparison.OrdinalIgnoreCase);
            bool arrWanted = runway.Equals(selected.Arr, StringComparison.OrdinalIgnoreCase);

            if (dep != depWanted || arr != arrWanted)
            {
                details = runway + " DEP=" + dep + " ARR=" + arr +
                          " expected DEP=" + depWanted + " ARR=" + arrWanted;
                return false;
            }
        }

        details = "OK";
        return true;
    }

    public void ApplyPreset(AirportConfiguration airport, RunwayState selected)
    {
        IntPtr dlgHwnd = FindWindow("#32770", "Active airport/runway selector dialog");
        if (dlgHwnd == IntPtr.Zero)
            throw new Exception("EuroScope runway selector dialog not found.");

        AutomationElement dlg = AutomationElement.FromHandle(dlgHwnd);
        if (dlg == null)
            throw new Exception("Could not attach to EuroScope dialog.");

        AutomationElement grid = dlg.FindFirst(
            TreeScope.Descendants,
            new PropertyCondition(AutomationElement.AutomationIdProperty, "1012")
        );
        if (grid == null)
            throw new Exception("Runway list not found.");

        POINT original;
        if (!GetCursorPos(out original))
            throw new Exception("Could not read mouse cursor position.");

        if (!BlockInput(true))
            throw new Exception("Could not temporarily lock mouse and keyboard input. No changes were made.");

        try
        {
            HideCursorFully();
            ApplyAirportState(dlgHwnd, grid, airport, selected);
        }
        finally
        {
            SetCursorPos(original.X, original.Y);
            ShowCursorFully();
            BlockInput(false);
        }
    }

    private void ApplyAirportState(
        IntPtr dlgHwnd,
        AutomationElement grid,
        AirportConfiguration airport,
        RunwayState selected)
    {
        foreach (string runway in GetConfiguredRunways(airport))
            SetRunwayState(
                dlgHwnd,
                grid,
                airport.Code,
                runway,
                runway.Equals(selected.Dep, StringComparison.OrdinalIgnoreCase),
                runway.Equals(selected.Arr, StringComparison.OrdinalIgnoreCase));
    }

    private IEnumerable<string> GetConfiguredRunways(AirportConfiguration airport)
    {
        var runways = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        runways.Add(airport.West.Dep);
        runways.Add(airport.West.Arr);
        runways.Add(airport.East.Dep);
        runways.Add(airport.East.Arr);
        return runways;
    }

    private AutomationElement FindRunwayRow(AutomationElement grid, string airport, string runway)
    {
        AutomationElementCollection rows = grid.FindAll(
            TreeScope.Children,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.DataItem)
        );

        foreach (AutomationElement row in rows)
        {
            AutomationElementCollection cells = row.FindAll(TreeScope.Children, Condition.TrueCondition);
            if (cells.Count < 6) continue;

            string apt = SafeName(cells[0]).Trim();
            string rwy = SafeName(cells[3]).Trim();

            if (apt.Equals(airport, StringComparison.OrdinalIgnoreCase) &&
                rwy.Equals(runway, StringComparison.OrdinalIgnoreCase))
                return row;
        }

        return null;
    }

    private void SetRunwayState(
        IntPtr dlgHwnd,
        AutomationElement grid,
        string airport,
        string runway,
        bool depWanted,
        bool arrWanted)
    {
        AutomationElement row = FindRunwayRow(grid, airport, runway);
        if (row == null)
            throw new Exception("Could not find " + airport + " RWY " + runway + ".");

        ScrollIntoView(row);
        Thread.Sleep(120);

        row = FindRunwayRow(grid, airport, runway);
        if (row == null)
            throw new Exception("Could not refresh " + airport + " RWY " + runway + ".");

        AutomationElementCollection cells = row.FindAll(TreeScope.Children, Condition.TrueCondition);
        if (cells.Count < 6)
            throw new Exception("Unexpected EuroScope row structure.");

        bool depActive = SafeName(cells[4]).Equals("ICON:1", StringComparison.OrdinalIgnoreCase);
        bool arrActive = SafeName(cells[5]).Equals("ICON:2", StringComparison.OrdinalIgnoreCase);

        if (depActive != depWanted)
        {
            PhysicalHiddenClick(dlgHwnd, cells[4]);
            Thread.Sleep(180);
        }

        row = FindRunwayRow(grid, airport, runway);
        if (row == null)
            throw new Exception("Could not refresh row after DEP change.");

        ScrollIntoView(row);
        Thread.Sleep(80);
        cells = row.FindAll(TreeScope.Children, Condition.TrueCondition);
        if (cells.Count < 6)
            throw new Exception("Unexpected EuroScope row structure after DEP change.");

        arrActive = SafeName(cells[5]).Equals("ICON:2", StringComparison.OrdinalIgnoreCase);

        if (arrActive != arrWanted)
        {
            PhysicalHiddenClick(dlgHwnd, cells[5]);
            Thread.Sleep(180);
        }
    }

    private void ScrollIntoView(AutomationElement row)
    {
        object pat;
        if (row.TryGetCurrentPattern(ScrollItemPattern.Pattern, out pat))
            ((ScrollItemPattern)pat).ScrollIntoView();
    }

    private string SafeName(AutomationElement el)
    {
        try { return el.Current.Name ?? ""; }
        catch { return ""; }
    }

    private void PhysicalHiddenClick(IntPtr hwnd, AutomationElement cell)
    {
        System.Windows.Rect r = cell.Current.BoundingRectangle;
        if (r.IsEmpty)
            throw new Exception("Target cell has no screen rectangle.");

        int x = (int)Math.Round(r.Left + Math.Min(9.0, Math.Max(3.0, r.Width / 3.0)));
        int y = (int)Math.Round(r.Top + r.Height / 2.0);

        SetForegroundWindow(hwnd);
        Thread.Sleep(90);

        SetCursorPos(x, y);
        Thread.Sleep(35);

        SendMouseInput(MOUSEEVENTF_LEFTDOWN);
        Thread.Sleep(55);
        SendMouseInput(MOUSEEVENTF_LEFTUP);
    }

    private void SendMouseInput(uint flags)
    {
        var input = new INPUT[1];
        input[0].type = INPUT_MOUSE;
        input[0].mi.dwFlags = flags;

        if (SendInput(1, input, Marshal.SizeOf(typeof(INPUT))) != 1)
            throw new Exception("Could not send a mouse click to EuroScope.");
    }

    private void HideCursorFully()
    {
        while (ShowCursor(false) >= 0) { }
    }

    private void ShowCursorFully()
    {
        while (ShowCursor(true) < 0) { }
    }
}

static class Program
{
    [STAThread]
    static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        try
        {
            string configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "runways.json");
            RunwayConfiguration configuration = RunwayConfiguration.Load(configPath);
            Application.Run(new MainForm(configuration));
        }
        catch (ConfigurationException ex)
        {
            MessageBox.Show(
                ex.Message,
                "EuroScope Runway Preset Manager - configuration error",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }
}
