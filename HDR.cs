using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Forms;
using Microsoft.Win32;

namespace ConsoleApp1;


public static class HDR
{
    public const string APPLICATION_NAME = "HDR Manager";
    public const int COLOR_INFO_HDR_VALUE = 3;
    
    private static NotifyIcon _notifyIcon;
    private static List<string> _applicationsToListenTo;
    private static System.Windows.Forms.Timer _monitoringTimer;

    [STAThread]
    private static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // Load applications from JSON file
        _applicationsToListenTo = ApplicationsJson.LoadApplications();

        // 1. Initialize System Tray Icon

        // Extract the icon directly from the running executable
        Icon appIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application;

        _notifyIcon = new NotifyIcon()
        {
            Icon = appIcon,
            Text = APPLICATION_NAME,
            Visible = true
        };
        
        // 2. Build Context Menu for System Tray
        ContextMenuStrip trayMenu = new ContextMenuStrip();
        
        ToolStripMenuItem manageAppsItem = new ToolStripMenuItem("Manage Applications", null, OnManageAppsClicked);
        ToolStripMenuItem startupItem = new ToolStripMenuItem("Run on Windows Startup", null, OnToggleStartupClicked)
        {
            Checked = IsStartupEnabled()
        };
        ToolStripMenuItem exitItem = new ToolStripMenuItem("Exit", null, OnExitClicked);

        trayMenu.Items.Add(manageAppsItem);
        trayMenu.Items.Add(startupItem);
        trayMenu.Items.Add("-"); // Separator line
        trayMenu.Items.Add(exitItem);

        _notifyIcon.ContextMenuStrip = trayMenu;
        _notifyIcon.Text = APPLICATION_NAME;
        _notifyIcon.DoubleClick += OnManageAppsClicked;

        // 3. Initialize Background Monitoring Loop (Replaces while(true) loop)
        _monitoringTimer = new System.Windows.Forms.Timer();
        _monitoringTimer.Interval = 1000; // Check every 1 second
        _monitoringTimer.Tick += OnMonitoringLoopTick;
        _monitoringTimer.Start();

        // Keep app running in background without a main window console
        Application.Run();
    }

    private static void OnMonitoringLoopTick(object sender, EventArgs e)
    {
        // Reload list in case user changed it externally or through the UI
        var newApplications = ApplicationsJson.LoadApplications();
        if (newApplications is not null)
        {
            _applicationsToListenTo = newApplications;
        }

        // Get Display Data
        var monitors = GetDisplayData();
        if (monitors.Count == 0) return;

        DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO currentMonitor = monitors[0];
        if (monitors.Count > 1)
        {
            currentMonitor = monitors[1]; // Target secondary display if present
        }

        bool running = IsAProgramRunning();
        bool hdrEnabled = IsHDROn(currentMonitor);

        if (running && !hdrEnabled)
        {
            ToggleHdr(true);
        }
        else if (!running && hdrEnabled)
        {
            ToggleHdr(false);
        }
    }

    private static void OnManageAppsClicked(object sender, EventArgs e)
    {
        UIManager.ShowManageAppsDialog(_applicationsToListenTo);
    }

    private static void OnToggleStartupClicked(object sender, EventArgs e)
    {
        ToolStripMenuItem menuItem = sender as ToolStripMenuItem;
        bool enable = !menuItem.Checked;

        SetStartup(enable);
        menuItem.Checked = enable;
    }

    private static void OnExitClicked(object sender, EventArgs e)
    {
        _monitoringTimer.Stop();
        _notifyIcon.Visible = false;
        Application.Exit();
    }

    #region Windows Startup Logic

    private const string StartupRegistryKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = APPLICATION_NAME;

    private static void SetStartup(bool enable)
    {
        using (RegistryKey key = Registry.CurrentUser.OpenSubKey(StartupRegistryKey, true))
        {
            if (key != null)
            {
                if (enable)
                {
                    key.SetValue(AppName, $"\"{Application.ExecutablePath}\"");
                }
                else
                {
                    key.DeleteValue(AppName, false);
                }
            }
        }
    }

    private static bool IsStartupEnabled()
    {
        using (RegistryKey key = Registry.CurrentUser.OpenSubKey(StartupRegistryKey, false))
        {
            return key?.GetValue(AppName) != null;
        }
    }

    #endregion

    // Checks whether the programs in 'ApplicationsToListenTo' are running
    private static bool IsAProgramRunning()
    {
        foreach (var application in _applicationsToListenTo)
        {
            // Normalize process name (remove .exe extension if provided)
            string processName = application.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? application.Substring(0, application.Length - 4)
                : application;

            Process[] proc = Process.GetProcessesByName(processName);

            Console.WriteLine($"Checking: {processName} | Found: {proc.Length}");
            Debug.WriteLine($"Checking: {processName} | Found: {proc.Length}");
            
            if (proc.Length != 0) return true;
        }

        return false;
    }

    private static bool IsHDROn(DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO colorInfo) {
        return colorInfo.value == COLOR_INFO_HDR_VALUE;
        
        // return colorInfo.advancedColorEnabled;
    }

    private static void ToggleHdr(bool enable)
    {
        int err = GetDisplayConfigBufferSizes(QDC.QDC_ONLY_ACTIVE_PATHS, out var pathCount, out var modeCount);
        if (err != 0) return;

        var paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
        var modes = new DISPLAYCONFIG_MODE_INFO[modeCount];
        err = QueryDisplayConfig(QDC.QDC_ONLY_ACTIVE_PATHS, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);
        if (err != 0) return;

        foreach (var path in paths)
        {
            var info = new DISPLAYCONFIG_TARGET_DEVICE_NAME();
            info.header.type = DISPLAYCONFIG_DEVICE_INFO_TYPE.DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME;
            info.header.size = Marshal.SizeOf<DISPLAYCONFIG_TARGET_DEVICE_NAME>();
            info.header.adapterId = path.targetInfo.adapterId;
            info.header.id = path.targetInfo.id;
            
            if (DisplayConfigGetDeviceInfo(ref info) != 0) continue;

            DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO colorInfo = new DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO();
            colorInfo.header.type = DISPLAYCONFIG_DEVICE_INFO_TYPE.DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO;
            colorInfo.header.size = Marshal.SizeOf<DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO>();
            colorInfo.header.adapterId = path.targetInfo.adapterId;
            colorInfo.header.id = path.targetInfo.id;

            if (DisplayConfigGetDeviceInfo(ref colorInfo) != 0) continue;

            uint value = enable ? 1u : 0u;

            var setState = new DISPLAYCONFIG_SET_ADVANCED_COLOR_STATE
            {
                header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
                {
                    type = DISPLAYCONFIG_DEVICE_INFO_TYPE.DISPLAYCONFIG_DEVICE_INFO_SET_ADVANCED_COLOR_STATE,
                    size = Marshal.SizeOf<DISPLAYCONFIG_SET_ADVANCED_COLOR_STATE>(),
                    adapterId = info.header.adapterId,
                    id = colorInfo.header.id
                },
                value = value
            };

            DisplayConfigSetDeviceInfo(ref setState);
        }
    }

    private static List<DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO> GetDisplayData()
    {
        List<DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO> colorInfos = new List<DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO>();
        int err = GetDisplayConfigBufferSizes(QDC.QDC_ONLY_ACTIVE_PATHS, out var pathCount, out var modeCount);
        if (err != 0) return colorInfos;

        var paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
        var modes = new DISPLAYCONFIG_MODE_INFO[modeCount];
        err = QueryDisplayConfig(QDC.QDC_ONLY_ACTIVE_PATHS, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);
        if (err != 0) return colorInfos;

        foreach (var path in paths)
        {
            DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO colorInfo = new DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO();
            colorInfo.header.type = DISPLAYCONFIG_DEVICE_INFO_TYPE.DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO;
            colorInfo.header.size = Marshal.SizeOf<DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO>();
            colorInfo.header.adapterId = path.targetInfo.adapterId;
            colorInfo.header.id = path.targetInfo.id;

            if (DisplayConfigGetDeviceInfo(ref colorInfo) == 0)
            {
                colorInfos.Add(colorInfo);
            }
        }

        return colorInfos;
    }

    #region P/Invoke Structures & Enums

    private enum DISPLAYCONFIG_DEVICE_INFO_TYPE
    {
        DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME = 1,
        DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME = 2,
        DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO = 9,
        DISPLAYCONFIG_DEVICE_INFO_SET_ADVANCED_COLOR_STATE = 10,
    }

    private enum DISPLAYCONFIG_COLOR_ENCODING
    {
        DISPLAYCONFIG_COLOR_ENCODING_RGB = 0,
    }

    private enum QDC
    {
        QDC_ONLY_ACTIVE_PATHS = 0x00000002,
    }

    
    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_DEVICE_INFO_HEADER
    {
        public DISPLAYCONFIG_DEVICE_INFO_TYPE type;
        public int size; // Must be uint, not int
        public LUID adapterId;
        public uint id;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        public uint value;
        public DISPLAYCONFIG_COLOR_ENCODING colorEncoding;
        public int bitsPerColorChannel;

        public bool advancedColorSupported => (value & 0x1) == 0x1;
        public bool advancedColorEnabled => (value & 0x2) == 0x2;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_SET_ADVANCED_COLOR_STATE
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        public uint value;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_SOURCE_INFO
    {
        public LUID adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_TARGET_INFO
    {
        public LUID adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint outputTechnology;
        public uint rotation;
        public uint scaling;
        public DISPLAYCONFIG_RATIONAL refreshRate; // Fixed: ulong corrupts stack alignment!
        public uint scanLineOrdering;
        public bool targetAvailable;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_RATIONAL
    {
        public uint Numerator;
        public uint Denominator;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_INFO
    {
        public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo;
        public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo;
        public uint flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_MODE_INFO
    {
        public uint infoType;
        public uint id;
        public LUID adapterId;
        // Padded union area to ensure correct byte boundary
        public ulong dummy1;
        public ulong dummy2;
        public ulong dummy3;
        public ulong dummy4;
        public ulong dummy5;
        public ulong dummy6;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAYCONFIG_TARGET_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        public uint flags;
        public uint outputTechnology;
        public ushort edidManufactureId;
        public ushort edidProductCodeId;
        public uint connectorInstance;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string monitorFriendlyDeviceName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string monitorDevicePat;
    }

    [DllImport("user32")]
    private static extern int GetDisplayConfigBufferSizes(QDC flags, out int numPathArrayElements, out int numModeInfoArrayElements);

    [DllImport("user32")]
    private static extern int QueryDisplayConfig(QDC flags, ref int numPathArrayElements, [In, Out] DISPLAYCONFIG_PATH_INFO[] pathArray, ref int numModeInfoArrayElements, [In, Out] DISPLAYCONFIG_MODE_INFO[] modeInfoArray, IntPtr currentTopologyId);

    [DllImport("user32")]
    private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO requestPacket);

    [DllImport("user32")]
    private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_TARGET_DEVICE_NAME requestPacket);

    [DllImport("user32")]
    private static extern int DisplayConfigSetDeviceInfo(ref DISPLAYCONFIG_SET_ADVANCED_COLOR_STATE requestPacket);

    #endregion
}

public static class ApplicationsJson
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HDRManager",
        "apps.json"
    );

    public static List<string> LoadApplications()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                // var defaults = new List<string> { "ACShadows.exe", "code.exe" };
                // SaveApplications(defaults);
                // return defaults;
                return null;
            }

            string json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();
        }
        catch
        {
            return new List<string>();
        }
    }

    public static void SaveApplications(List<string> apps)
    {
        try
        {
            string directory = Path.GetDirectoryName(FilePath);
            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string json = JsonSerializer.Serialize(apps, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(FilePath, json);
        }
        catch
        {
            // Ignore write lock errors
        }
    }
}

public static class UIManager
{
    public static void ShowManageAppsDialog(List<string> currentApps)
    {
        Icon appIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application;
        
        // 1. Create Form
        Form prompt = new Form()
        {
            Width = 430,
            Height = 460,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            Text = HDR.APPLICATION_NAME + " - Manage Applications",
            StartPosition = FormStartPosition.CenterScreen,
            MaximizeBox = false,
            MinimizeBox = false,
            Icon = appIcon
        };

        // 2. Add UI Controls
        Label labelCurrent = new Label() { Left = 20, Top = 10, Text = "Current Monitored Applications:", Width = 250 };
        ListBox listBox = new ListBox() { Left = 20, Top = 35, Width = 260, Height = 220 };
        
        listBox.Items.AddRange(currentApps.ToArray());

        // Remove Button placed next to ListBox
        Button btnRemove = new Button() { Text = "Remove Selected", Left = 290, Top = 35, Width = 110, Height = 30 };

        Label labelNew = new Label() { Left = 20, Top = 270, Text = "Add New Application (e.g., chrome.exe):", Width = 250 };
        TextBox txtNewApp = new TextBox() { Left = 20, Top = 295, Width = 260 };
        Button btnAdd = new Button() { Text = "Add", Left = 290, Top = 293, Width = 110 };

        Button btnSaveClose = new Button() { Text = "Save & Close", Left = 280, Top = 360, Width = 120, DialogResult = DialogResult.OK };

        // 3. Event Handlers
        btnAdd.Click += (sender, e) =>
        {
            string newApp = txtNewApp.Text.Trim();
            if (!string.IsNullOrEmpty(newApp) && !listBox.Items.Contains(newApp))
            {
                listBox.Items.Add(newApp);
                txtNewApp.Clear();
            }
        };

        btnRemove.Click += (sender, e) =>
        {
            if (listBox.SelectedItem != null)
            {
                listBox.Items.Remove(listBox.SelectedItem);
            }
            else
            {
                MessageBox.Show("Please select an item from the list to remove.", "Selection Required", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        };

        // 4. Assemble Form Controls
        prompt.Controls.Add(labelCurrent);
        prompt.Controls.Add(listBox);
        prompt.Controls.Add(btnRemove);
        prompt.Controls.Add(labelNew);
        prompt.Controls.Add(txtNewApp);
        prompt.Controls.Add(btnAdd);
        prompt.Controls.Add(btnSaveClose);
        prompt.AcceptButton = btnSaveClose;

        // 5. Save Changes on Close
        if (prompt.ShowDialog() == DialogResult.OK)
        {
            currentApps.Clear();
            foreach (var item in listBox.Items)
            {
                currentApps.Add(item.ToString());
            }

            ApplicationsJson.SaveApplications(currentApps);
            // MessageBox.Show("Application list updated successfully!", "Saved", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }
}