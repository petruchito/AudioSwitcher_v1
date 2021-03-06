﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;
using AudioSwitcher.AudioApi;
using FortyOne.AudioSwitcher.AudioSwitcherService;
using FortyOne.AudioSwitcher.Configuration;
using FortyOne.AudioSwitcher.Helpers;
using FortyOne.AudioSwitcher.HotKeyData;
using FortyOne.AudioSwitcher.Properties;
using Microsoft.Win32;
using Timer = System.Windows.Forms.Timer;

namespace FortyOne.AudioSwitcher
{
    public partial class AudioSwitcher : Form
    {
        #region Properties

        private static AudioSwitcher _instance;
        public bool DisableHotKeyFunction = false;

        public static AudioSwitcher Instance
        {
            get
            {
                return _instance ?? (_instance = new AudioSwitcher());
            }
        }

        private IDevice SelectedPlaybackDevice
        {
            get
            {
                if (listBoxPlayback.SelectedItems.Count > 0)
                    return ((IDevice)listBoxPlayback.SelectedItems[0].Tag);
                return null;
            }
        }

        public IDevice SelectedRecordingDevice
        {
            get
            {
                if (listBoxRecording.SelectedItems.Count > 0)
                    return ((IDevice)listBoxRecording.SelectedItems[0].Tag);
                return null;
            }
        }

        public bool TrayIconVisible
        {
            get { return notifyIcon1.Visible; }
            set
            {
                try
                {
                    notifyIcon1.Visible = value;
                }
                catch
                {
                } // rubbish error
            }
        }

        #endregion

        /// <summary>
        /// EASTER EGG! SHHH!
        /// </summary>
        private const string KONAMI_CODE = "UUDDLRLRBA";

        private readonly string[] YOUTUBE_VIDEOS =
        {
            "http://www.youtube.com/watch?v=QJO3ROT-A4E",
            "http://www.youtube.com/watch?v=fWNaR-rxAic",
            "http://www.youtube.com/watch?v=X2WH8mHJnhM",
            "http://www.youtube.com/watch?v=dQw4w9WgXcQ",
            "http://www.youtube.com/watch?v=2Z4m4lnjxkY"
        };

        private readonly Dictionary<DeviceIcon, string> ICON_MAP = new Dictionary<DeviceIcon, string>()
        {
            {DeviceIcon.Speakers,"3010"},
            {DeviceIcon.Headphones,"3011"},
            {DeviceIcon.LineIn,"3012"},
            {DeviceIcon.Digital,"3013"},
            {DeviceIcon.DesktopMicrophone,"3014"},
            {DeviceIcon.Headset,"3015"},
            {DeviceIcon.Phone,"3016"},
            {DeviceIcon.Monitor,"3017"},
            {DeviceIcon.StereoMix,"3018"},
            {DeviceIcon.Kinect,"3020"}
        };

        private bool _doubleClickHappened;
        private bool _firstStart = true;
        private string _input = "";
        private AudioSwitcherVersionInfo _retrievedVersion;

        private DeviceState DeviceStateFilter = DeviceState.Active;
        public Icon OriginalTrayIcon;

        public AudioSwitcher()
        {
            InitializeComponent();

            try
            {
                //try make it look pretty
                SetWindowTheme(listBoxPlayback.Handle, "Explorer", null);
                SetWindowTheme(listBoxRecording.Handle, "Explorer", null);
            }
            catch
            {
            }

            lblVersion.Text = "Version: " + AssemblyVersion;
            lblCopyright.Text = AssemblyCopyright;
            
            OriginalTrayIcon = new Icon(notifyIcon1.Icon, 32, 32);

            LoadSettings();

            RefreshRecordingDevices();
            RefreshPlaybackDevices();

            HotKeyManager.HotKeyPressed += HotKeyManager_HotKeyPressed;
            hotKeyBindingSource.DataSource = HotKeyManager.HotKeys;

            if (Program.Settings.CheckForUpdatesOnStartup || Program.Settings.PollForUpdates >= 1)
            {
                Task.Factory.StartNew(CheckForUpdates);
            }

            IDevice dev = AudioDeviceManager.Controller.GetAudioDevice(Program.Settings.StartupPlaybackDeviceID);

            if (dev != null)
                dev.SetAsDefault();

            dev = AudioDeviceManager.Controller.GetAudioDevice(Program.Settings.StartupRecordingDeviceID);

            if (dev != null)
                dev.SetAsDefault();

            MinimizeFootprint();
        }

        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("uxtheme.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
        private static extern int SetWindowTheme(IntPtr hwnd, string pszSubAppName, string pszSubIdList);

        private void Form1_Load(object sender, EventArgs e)
        {
#if DEBUG
            btnTestError.Visible = true;
#endif

            AudioDeviceManager.Controller.AudioDeviceChanged += AudioDeviceManager_AudioDeviceChanged;

            //Heartbeat
            Task.Factory.StartNew(() =>
            {
                using (AudioSwitcherService.AudioSwitcher client = ConnectionHelper.GetAudioSwitcherProxy())
                {
                    if (client == null)
                        return;

                    _retrievedVersion = client.GetUpdateInfo(AssemblyVersion);
                }
            });

            MinimizeFootprint();
        }

        private void AudioDeviceManager_AudioDeviceChanged(object sender, AudioDeviceChangedEventArgs e)
        {
            Action refreshAction = () => { };

            if (e.Device.IsPlaybackDevice)
                refreshAction = RefreshPlaybackDevices;
            else if (e.Device.IsCaptureDevice)
                refreshAction = RefreshRecordingDevices;

            if (InvokeRequired)
                BeginInvoke(refreshAction);
            else
                refreshAction();
        }

        private void CheckForUpdates()
        {
            CheckForUpdates(null, null);
        }

        private void CheckForUpdates(object o, EventArgs ae)
        {
            try
            {
                using (AudioSwitcherService.AudioSwitcher client = ConnectionHelper.GetAudioSwitcherProxy())
                {
                    if (client == null)
                        return;

                    _retrievedVersion = client.GetUpdateInfo(AssemblyVersion);
                    if (_retrievedVersion != null && !string.IsNullOrEmpty(_retrievedVersion.URL))
                    {
                        notifyIcon1.BalloonTipText = "Click here to download.";
                        notifyIcon1.BalloonTipTitle = "New version available.";
                        notifyIcon1.BalloonTipClicked += notifyIcon1_BalloonTipClicked;
                        notifyIcon1.ShowBalloonTip(3000);
                    }
                }
            }
            catch
            {
            }
        }

        private void notifyIcon1_BalloonTipClicked(object sender, EventArgs e)
        {
            UpdateForm udf;
            if (_retrievedVersion != null)
                udf = new UpdateForm(_retrievedVersion);
            else
                udf = new UpdateForm();

            udf.ShowDialog(this);
        }

        protected override void SetVisibleCore(bool value)
        {
            if (Program.Settings.StartMinimized && _firstStart)
            {
                value = false;
                _firstStart = false;
            }

            base.SetVisibleCore(value);
        }

        private void pictureBox1_Click(object sender, EventArgs e)
        {
            Process.Start("https://www.paypal.com/cgi-bin/webscr?cmd=_s-xclick&hosted_button_id=Q9TDQPY4B369A");
        }

        private void mnuFavouritePlaybackDevice_Click(object sender, EventArgs e)
        {
            if (SelectedPlaybackDevice == null)
                return;

            Guid id = SelectedPlaybackDevice.Id;
            //if checked then we need to remove

            if (mnuFavouritePlaybackDevice.Checked)
                FavouriteDeviceManager.RemoveFavouriteDevice(SelectedPlaybackDevice.Id);
            else
                FavouriteDeviceManager.AddFavouriteDevice(SelectedPlaybackDevice.Id);

            PostPlaybackMenuClick(id);
        }

        private void mnuFavouriteRecordingDevice_Click(object sender, EventArgs e)
        {
            if (SelectedRecordingDevice == null)
                return;

            Guid id = SelectedRecordingDevice.Id;

            if (mnuFavouriteRecordingDevice.Checked)
                FavouriteDeviceManager.RemoveFavouriteDevice(SelectedRecordingDevice.Id);
            else
                FavouriteDeviceManager.AddFavouriteDevice(SelectedRecordingDevice.Id);

            PostRecordingMenuClick(id);
        }

        private void chkDisableHotKeys_CheckedChanged(object sender, EventArgs e)
        {
            Program.Settings.DisableHotKeys = chkDisableHotKeys.Checked;
            if (Program.Settings.DisableHotKeys)
            {
                foreach (HotKey hk in HotKeyManager.HotKeys)
                {
                    hk.UnregsiterHotkey();
                }
            }
            else
            {
                foreach (HotKey hk in HotKeyManager.HotKeys)
                {
                    if (!hk.IsRegistered)
                        hk.RegisterHotkey();
                }
            }
        }

        private void notifyIcon1_MouseClick(object sender, MouseEventArgs e)
        {
            _doubleClickHappened = false;

            if (e.Button == MouseButtons.Left)
            {
                var t = new Timer();
                t.Tick += t_Tick;
                t.Interval = SystemInformation.DoubleClickTime;
                t.Start();
            }
        }

        private void t_Tick(object sender, EventArgs e)
        {
            ((Timer)sender).Stop();
            if (_doubleClickHappened)
                return;

            if (Program.Settings.EnableQuickSwitch)
            {
                if (FavouriteDeviceManager.FavouriteDeviceCount > 0)
                {
                    Guid devid = FavouriteDeviceManager.GetNextFavouritePlaybackDevice();

                    AudioDeviceManager.Controller.GetAudioDevice(devid).SetAsDefault();

                    if (Program.Settings.DualSwitchMode)
                        AudioDeviceManager.Controller.GetAudioDevice(devid).SetAsDefaultCommunications();
                }
            }
            else
            {
                RefreshNotifyIconItems();
                MethodInfo mi = typeof(NotifyIcon).GetMethod("ShowContextMenu",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                mi.Invoke(notifyIcon1, null);
            }
        }

        private void notifyIcon1_MouseDoubleClick(object sender, MouseEventArgs e)
        {
            _doubleClickHappened = true;
        }

        private void button1_Click(object sender, EventArgs e)
        {
            throw new Exception("Fail Message");
        }

        private void AudioSwitcher_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Up)
                _input += "U";
            else if (e.KeyCode == Keys.Down)
                _input += "D";
            else if (e.KeyCode == Keys.Left)
                _input += "L";
            else if (e.KeyCode == Keys.Right)
                _input += "R";
            else if (e.KeyCode == Keys.A)
                _input += "A";
            else if (e.KeyCode == Keys.B)
                _input += "B";

            if (_input.Length > KONAMI_CODE.Length)
            {
                _input = _input.Substring(1);
            }

            if (_input == KONAMI_CODE)
            {
                var rand = new Random();
                int index = rand.Next(YOUTUBE_VIDEOS.Length);
                Process.Start(YOUTUBE_VIDEOS[index]);
            }
        }

        private void listBoxPlayback_ItemSelectionChanged(object sender, ListViewItemSelectionChangedEventArgs e)
        {
            RefreshPlaybackDropDownButton();
        }

        private void listBoxRecording_ItemSelectionChanged(object sender, ListViewItemSelectionChangedEventArgs e)
        {
            RefreshRecordingDropDownButton();
        }

        private void btnCheckUpdate_Click(object sender, EventArgs e)
        {
            using (AudioSwitcherService.AudioSwitcher client = ConnectionHelper.GetAudioSwitcherProxy())
            {
                if (client == null)
                    return;

                AudioSwitcherVersionInfo vi = client.GetUpdateInfo(AssemblyVersion);
                if (vi != null && !string.IsNullOrEmpty(vi.URL))
                {
                    var udf = new UpdateForm(vi);
                    udf.ShowDialog(this);
                }
                else
                {
                    MessageBox.Show(this, "You have the latest version!");
                }
            }
        }

        private void setHotKeyToolStripMenuItem1_Click(object sender, EventArgs e)
        {
            HotKeyForm hkf;
            foreach (HotKey hk in HotKeyManager.HotKeys)
            {
                if (hk.DeviceId == SelectedPlaybackDevice.Id)
                {
                    hkf = new HotKeyForm(hk);
                    hkf.ShowDialog(this);
                    return;
                }
            }

            var newHotKey = new HotKey();
            newHotKey.DeviceId = SelectedPlaybackDevice.Id;
            hkf = new HotKeyForm(newHotKey);
            hkf.ShowDialog(this);
        }

        private void setHotKeyToolStripMenuItem_Click(object sender, EventArgs e)
        {
            HotKeyForm hkf = null;
            foreach (HotKey hk in HotKeyManager.HotKeys)
            {
                if (hk.DeviceId == SelectedRecordingDevice.Id)
                {
                    hkf = new HotKeyForm(hk);
                    hkf.ShowDialog(this);
                    return;
                }
            }
            var newHotKey = new HotKey();
            newHotKey.DeviceId = SelectedRecordingDevice.Id;
            hkf = new HotKeyForm(newHotKey);
            hkf.ShowDialog(this);
        }

        [DllImport("psapi.dll")]
        private static extern int EmptyWorkingSet(IntPtr hwProc);

        private static void MinimizeFootprint()
        {
            EmptyWorkingSet(Process.GetCurrentProcess().Handle);
        }

        private void memoryCleaner_Tick(object sender, EventArgs e)
        {
            MinimizeFootprint();
        }

        private void chkPollForUpdates_CheckedChanged(object sender, EventArgs e)
        {
            if (chkPollForUpdates.Checked)
            {
                spinPollMinutes.Enabled = true;

                if (Program.Settings.PollForUpdates < 0)
                    Program.Settings.PollForUpdates = Program.Settings.PollForUpdates * -1;

                if (Program.Settings.PollForUpdates < spinPollMinutes.Minimum)
                    Program.Settings.PollForUpdates = (int)spinPollMinutes.Minimum;

                if (Program.Settings.PollForUpdates > spinPollMinutes.Maximum)
                    Program.Settings.PollForUpdates = (int)spinPollMinutes.Maximum;

                spinPollMinutes.Value = Program.Settings.PollForUpdates;
            }
            else
            {
                spinPollMinutes.Enabled = false;
                Program.Settings.PollForUpdates = (int)(-1 * spinPollMinutes.Value);
            }
        }

        private void spinPollMinutes_ValueChanged(object sender, EventArgs e)
        {
            try
            {
                UpdateTimer.Stop();
                UpdateTimer.Dispose();
            }
            catch
            {
            }

            UpdateTimer = new Timer();

            Program.Settings.PollForUpdates = (int)spinPollMinutes.Value;

            if (Program.Settings.PollForUpdates > 0)
            {
                UpdateTimer.Interval = (int)TimeSpan.FromHours(Program.Settings.PollForUpdates).TotalMilliseconds;
                UpdateTimer.Tick += CheckForUpdates;
                UpdateTimer.Enabled = true;
                UpdateTimer.Start();
            }
        }

        private void linkLabel1_Click(object sender, EventArgs e)
        {
            Process.Start("http://services.audioswit.ch/versions/");
        }

        private void mnuSetPlaybackStartupDevice_Click(object sender, EventArgs e)
        {
            if (SelectedPlaybackDevice == null)
                return;

            if (Program.Settings.StartupPlaybackDeviceID == SelectedPlaybackDevice.Id)
                Program.Settings.StartupPlaybackDeviceID = Guid.Empty;
            else
                Program.Settings.StartupPlaybackDeviceID = SelectedPlaybackDevice.Id;

            RefreshPlaybackDropDownButton();
        }

        private void mnuSetRecordingStartupDevice_Click(object sender, EventArgs e)
        {
            if (SelectedRecordingDevice == null)
                return;

            if (Program.Settings.StartupRecordingDeviceID == SelectedRecordingDevice.Id)
                Program.Settings.StartupRecordingDeviceID = Guid.Empty;
            else
                Program.Settings.StartupRecordingDeviceID = SelectedRecordingDevice.Id;

            RefreshRecordingDropDownButton();
        }

        #region HotKeyButtons

        private void btnAddHotKey_Click(object sender, EventArgs e)
        {
            var hkf = new HotKeyForm();
            hkf.ShowDialog(this);
            RefreshGrid();
        }

        private void btnEditHotKey_Click(object sender, EventArgs e)
        {
            if (hotKeyBindingSource.Current != null)
            {
                var hkf = new HotKeyForm((HotKey)hotKeyBindingSource.Current);
                hkf.ShowDialog(this);
                RefreshGrid();
            }
        }

        private void btnDeleteHotKey_Click(object sender, EventArgs e)
        {
            if (hotKeyBindingSource.Current != null)
            {
                HotKeyManager.DeleteHotKey((HotKey)hotKeyBindingSource.Current);
                RefreshGrid();
            }
        }

        private void RefreshGrid()
        {
            if (InvokeRequired)
                Invoke(new Action(RefreshGrid));
            else
                dataGridView1.Refresh();
        }

        #endregion

        #region Methods

        private void LoadSettings()
        {
            //Fix to stop the registry thing being removed and not re-added
            Program.Settings.AutoStartWithWindows = Program.Settings.AutoStartWithWindows;

            chkCloseToTray.Checked = Program.Settings.CloseToTray;
            chkStartMinimized.Checked = Program.Settings.StartMinimized;
            chkAutoStartWithWindows.Checked = Program.Settings.AutoStartWithWindows;
            chkDisableHotKeys.Checked = Program.Settings.DisableHotKeys;
            chkQuickSwitch.Checked = Program.Settings.EnableQuickSwitch;
            chkDualSwitchMode.Checked = Program.Settings.DualSwitchMode;
            //chkNotifyUpdates.Checked = Program.Settings.CheckForUpdatesOnStartup;
            chkPollForUpdates.Checked = Program.Settings.PollForUpdates >= 1;
            spinPollMinutes.Enabled = chkPollForUpdates.Checked;

            chkShowDiabledDevices.Checked = Program.Settings.ShowDisabledDevices;
            chkShowDisconnectedDevices.Checked = Program.Settings.ShowDisconnectedDevices;
            chkShowDPDeviceIconInTray.Checked = Program.Settings.ShowDPDeviceIconInTray;

            Width = Program.Settings.WindowWidth;
            Height = Program.Settings.WindowHeight;

            FavouriteDeviceManager.FavouriteDevicesChanged += AudioDeviceManger_FavouriteDevicesChanged;

            var favDeviceStr = Program.Settings.FavouriteDevices.Split(new[] { ",", "[", "]" }, StringSplitOptions.RemoveEmptyEntries);

            FavouriteDeviceManager.LoadFavouriteDevices(Array.ConvertAll(favDeviceStr, x =>
            {
                var r = new Regex(ConfigurationSettings.GUID_REGEX);
                foreach (var match in r.Matches(x))
                    return new Guid(match.ToString());

                return Guid.Empty;
            }));

            RegistryKey runKey = Registry.CurrentUser.OpenSubKey("SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run", true);
            //Ensure the registry key is added/removed
            if (Program.Settings.AutoStartWithWindows)
            {
                if (runKey != null)
                    runKey.SetValue("AudioSwitcher", "\"" + Application.ExecutablePath + "\"");
            }
            else
            {
                if (runKey != null && runKey.GetValue("AudioSwitcher") != null)
                    runKey.DeleteValue("AudioSwitcher");
            }

            if (Program.Settings.ShowDisabledDevices)
                DeviceStateFilter |= DeviceState.Disabled;


            if (Program.Settings.ShowDisconnectedDevices)
                DeviceStateFilter |= DeviceState.Unplugged;
        }

        //Subscribe to favourite devices changing to save it to the configuration file instantly
        private void AudioDeviceManger_FavouriteDevicesChanged(List<Guid> IDs)
        {
            Program.Settings.FavouriteDevices = "[" + string.Join("],[", IDs.ToArray()) + "]";
        }

        #endregion

        #region RefreshHandling

        private void RefreshPlaybackDevices()
        {
            listBoxPlayback.SuspendLayout();
            listBoxPlayback.Items.Clear();
            foreach (IDevice ad in AudioDeviceManager.Controller.GetPlaybackDevices(DeviceStateFilter).ToList())
            {
                var li = new ListViewItem();
                li.Text = ad.Name;
                li.Tag = ad;
                li.SubItems.Add(new ListViewItem.ListViewSubItem(li, ad.InterfaceName));
                try
                {
                    string imageKey = "";
                    if (ICON_MAP.ContainsKey(ad.Icon))
                        imageKey = ICON_MAP[ad.Icon];

                    if (ad.IsDefaultDevice)
                    {
                        li.SubItems.Add(new ListViewItem.ListViewSubItem(li, "Default Device"));
                        li.EnsureVisible();
                    }
                    else if (ad.IsDefaultCommunicationsDevice)
                    {
                        li.SubItems.Add(new ListViewItem.ListViewSubItem(li, "Default Communications Device"));
                        li.EnsureVisible();
                    }
                    else
                    {
                        string caption = "";
                        switch (ad.State)
                        {
                            case DeviceState.Active:
                                caption = "Ready";
                                break;
                            case DeviceState.Disabled:
                                caption = "Disabled";
                                imageKey += "d";
                                break;
                            case DeviceState.Unplugged:
                                caption = "Not Plugged In";
                                imageKey += "d";
                                break;
                        }
                        li.SubItems.Add(new ListViewItem.ListViewSubItem(li, caption));
                    }

                    string imageMod = "";

                    if (ad.State != DeviceState.Unplugged && FavouriteDeviceManager.IsFavouriteDevice(ad))
                    {
                        imageMod += "f";
                    }

                    if (ad.IsDefaultDevice)
                    {
                        imageMod += "e";
                    }
                    else if (ad.IsDefaultCommunicationsDevice)
                    {
                        imageMod += "c";
                    }

                    string imageToGen = imageKey + imageMod + ".png";

                    if (!imageList1.Images.Keys.Contains(imageToGen) &&
                        imageList1.Images.IndexOfKey(imageKey + ".png") >= 0)
                    {
                        Image i = imageList1.Images[imageList1.Images.IndexOfKey(imageKey + ".png")];
                        Graphics g = Graphics.FromImage(i);
                        if (imageMod.Contains("f"))
                        {
                            g.DrawImage(Resources.f, i.Width - 12, 0);
                        }

                        if (imageMod.Contains("c"))
                        {
                            g.DrawImage(Resources.c, i.Width - 12, i.Height - 12);
                        }

                        if (imageMod.Contains("e"))
                        {
                            g.DrawImage(Resources.e, i.Width - 12, i.Height - 12);
                        }

                        imageList1.Images.Add(imageToGen, i);
                    }

                    if (imageList1.Images.IndexOfKey(imageToGen) >= 0)
                        li.ImageKey = imageToGen;
                }
                catch
                {
                    li.ImageKey = "unknown.png";
                }

                listBoxPlayback.Items.Add(li);
            }

            RefreshNotifyIconItems();
            listBoxPlayback.ResumeLayout();
        }

        private void RefreshRecordingDevices()
        {
            listBoxRecording.SuspendLayout();
            listBoxRecording.Items.Clear();

            foreach (IDevice ad in AudioDeviceManager.Controller.GetCaptureDevices(DeviceStateFilter).ToList())
            {
                var li = new ListViewItem();
                li.Text = ad.Name;
                li.Tag = ad;
                li.SubItems.Add(new ListViewItem.ListViewSubItem(li, ad.InterfaceName));
                try
                {
                    string imageKey = "";
                    if (ICON_MAP.ContainsKey(ad.Icon))
                        imageKey = ICON_MAP[ad.Icon];

                    if (ad.IsDefaultDevice)
                    {
                        li.SubItems.Add(new ListViewItem.ListViewSubItem(li, "Default Device"));
                        li.EnsureVisible();
                    }
                    else if (ad.IsDefaultCommunicationsDevice)
                    {
                        li.SubItems.Add(new ListViewItem.ListViewSubItem(li, "Default Communications Device"));
                        li.EnsureVisible();
                    }
                    else
                    {
                        string caption = "";
                        switch (ad.State)
                        {
                            case DeviceState.Active:
                                caption = "Ready";
                                break;
                            case DeviceState.Disabled:
                                caption = "Disabled";
                                imageKey += "d";
                                break;
                            case DeviceState.Unplugged:
                                caption = "Not Plugged In";
                                imageKey += "d";
                                break;
                        }
                        li.SubItems.Add(new ListViewItem.ListViewSubItem(li, caption));
                    }

                    string imageMod = "";

                    if (ad.State != DeviceState.Unplugged && FavouriteDeviceManager.IsFavouriteDevice(ad))
                    {
                        imageMod += "f";
                    }

                    if (ad.IsDefaultDevice)
                    {
                        imageMod += "e";
                    }
                    else if (ad.IsDefaultCommunicationsDevice)
                    {
                        imageMod += "c";
                    }

                    string imageToGen = imageKey + imageMod + ".png";

                    if (!imageList1.Images.Keys.Contains(imageToGen) &&
                        imageList1.Images.IndexOfKey(imageKey + ".png") >= 0)
                    {
                        Image i = imageList1.Images[imageList1.Images.IndexOfKey(imageKey + ".png")];
                        Graphics g = Graphics.FromImage(i);
                        if (imageMod.Contains("f"))
                        {
                            g.DrawImage(Resources.f, i.Width - 12, 0);
                        }

                        if (imageMod.Contains("c"))
                        {
                            g.DrawImage(Resources.c, i.Width - 12, i.Height - 12);
                        }

                        if (imageMod.Contains("e"))
                        {
                            g.DrawImage(Resources.e, i.Width - 12, i.Height - 12);
                        }

                        imageList1.Images.Add(imageToGen, i);
                    }

                    if (imageList1.Images.IndexOfKey(imageToGen) >= 0)
                        li.ImageKey = imageToGen;
                }
                catch
                {
                    li.ImageKey = "unknown.png";
                }

                listBoxRecording.Items.Add(li);
            }

            RefreshNotifyIconItems();
            listBoxRecording.ResumeLayout();
        }

        private void RefreshNotifyIconItems()
        {
            notifyIconStrip.Items.Clear();

            int playbackCount = 0;
            int recordingCount = 0;

            IEnumerable<IDevice> list = AudioDeviceManager.Controller.GetPlaybackDevices(DeviceStateFilter).ToList();

            foreach (IDevice ad in list)
            {
                if (FavouriteDeviceManager.FavouriteDeviceCount > 0 && !FavouriteDeviceManager.IsFavouriteDevice(ad))
                    continue;

                var item = new ToolStripMenuItem(ad.FullName);
                item.Tag = ad;
                item.Checked = ad.IsDefaultDevice;
                notifyIconStrip.Items.Add(item);
                playbackCount++;
            }

            if (playbackCount > 0)
                notifyIconStrip.Items.Add(new ToolStripSeparator());

            list = AudioDeviceManager.Controller.GetCaptureDevices(DeviceStateFilter).ToList();

            foreach (IDevice ad in list)
            {
                if (FavouriteDeviceManager.FavouriteDeviceCount > 0 && !FavouriteDeviceManager.IsFavouriteDevice(ad))
                    continue;

                var item = new ToolStripMenuItem(ad.FullName);
                item.Tag = ad;
                item.Checked = ad.IsDefaultDevice;
                notifyIconStrip.Items.Add(item);
                recordingCount++;
            }

            if (recordingCount > 0)
                notifyIconStrip.Items.Add(new ToolStripSeparator());

            notifyIconStrip.Items.Add(exitToolStripMenuItem);

            //The maximum length of the noitfy text is 64 characters. This keeps it under

            if (AudioDeviceManager.Controller.DefaultPlaybackDevice != null)
            {
                var notifyText = AudioDeviceManager.Controller.DefaultPlaybackDevice.FullName;

                if (notifyText.Length >= 64)
                    notifyText = notifyText.Substring(0, 60) + "...";
                    notifyIcon1.Text = notifyText;
            }
            else
            {
                notifyIcon1.Text = "Audio Switcher";
            }

            RefreshTrayIcon();
           }
        
        private void RefreshTrayIcon()
        {
            
            if (Program.Settings.ShowDPDeviceIconInTray)
            {
                var imageKey = ICON_MAP[AudioDeviceManager.Controller.DefaultPlaybackDevice.Icon];
                notifyIcon1.Icon = Icon.FromHandle(((Bitmap)imageList1.Images[imageList1.Images.IndexOfKey(imageKey + ".png")]).GetHicon());
            }
            else
            {
                notifyIcon1.Icon = OriginalTrayIcon;
            }
        }

        private void RefreshPlaybackDropDownButton()
        {
            if (SelectedPlaybackDevice == null)
            {
                btnSetPlaybackDefault.Enabled = false;
                return;
            }

            if (SelectedPlaybackDevice.IsDefaultDevice)
                mnuSetPlaybackDefault.CheckState = CheckState.Checked;
            else
                mnuSetPlaybackDefault.CheckState = CheckState.Unchecked;

            if (SelectedPlaybackDevice.IsDefaultCommunicationsDevice)
                mnuSetPlaybackCommunicationDefault.CheckState = CheckState.Checked;
            else
                mnuSetPlaybackCommunicationDefault.CheckState = CheckState.Unchecked;

            if (FavouriteDeviceManager.IsFavouriteDevice(SelectedPlaybackDevice.Id))
                mnuFavouritePlaybackDevice.CheckState = CheckState.Checked;
            else
                mnuFavouritePlaybackDevice.CheckState = CheckState.Unchecked;

            if (Program.Settings.StartupPlaybackDeviceID == SelectedPlaybackDevice.Id)
                mnuSetPlaybackStartupDevice.CheckState = CheckState.Checked;
            else
                mnuSetPlaybackStartupDevice.CheckState = CheckState.Unchecked;

            if (SelectedPlaybackDevice.State == DeviceState.Unplugged)
            {
                btnSetPlaybackDefault.Enabled = false;
                mnuFavouritePlaybackDevice.Enabled = false;
            }
            else
            {
                btnSetPlaybackDefault.Enabled = true;
                mnuFavouritePlaybackDevice.Enabled = true;
            }
        }

        private void RefreshRecordingDropDownButton()
        {
            if (SelectedRecordingDevice == null)
            {
                btnSetRecordingDefault.Enabled = false;
                return;
            }

            if (SelectedRecordingDevice.IsDefaultDevice)
                mnuSetRecordingDefault.CheckState = CheckState.Checked;
            else
                mnuSetRecordingDefault.CheckState = CheckState.Unchecked;

            if (SelectedRecordingDevice.IsDefaultCommunicationsDevice)
                mnuSetRecordingCommunicationDefault.CheckState = CheckState.Checked;
            else
                mnuSetRecordingCommunicationDefault.CheckState = CheckState.Unchecked;

            if (FavouriteDeviceManager.IsFavouriteDevice(SelectedRecordingDevice.Id))
                mnuFavouriteRecordingDevice.CheckState = CheckState.Checked;
            else
                mnuFavouriteRecordingDevice.CheckState = CheckState.Unchecked;

            if (Program.Settings.StartupRecordingDeviceID == SelectedRecordingDevice.Id)
                mnuSetRecordingStartupDevice.CheckState = CheckState.Checked;
            else
                mnuSetRecordingStartupDevice.CheckState = CheckState.Unchecked;

            if (SelectedRecordingDevice.State == DeviceState.Unplugged)
            {
                btnSetRecordingDefault.Enabled = false;
                mnuFavouriteRecordingDevice.Enabled = false;
            }
            else
            {
                btnSetRecordingDefault.Enabled = true;
                mnuFavouriteRecordingDevice.Enabled = true;
            }
        }

        #endregion

        #region Events

        private void mnuSetPlaybackCommunicationDefault_Click(object sender, EventArgs e)
        {
            if (SelectedPlaybackDevice == null)
                return;

            Guid id = SelectedPlaybackDevice.Id;
            SelectedPlaybackDevice.SetAsDefaultCommunications();
            PostPlaybackMenuClick(id);
        }

        private void mnuSetPlaybackDefault_Click(object sender, EventArgs e)
        {
            if (SelectedPlaybackDevice == null)
                return;

            Guid id = SelectedPlaybackDevice.Id;
            SelectedPlaybackDevice.SetAsDefault();
            PostPlaybackMenuClick(id);
        }

        private void PostPlaybackMenuClick(Guid id)
        {
            RefreshPlaybackDevices();
            RefreshPlaybackDropDownButton();
            for (int i = 0; i < listBoxPlayback.Items.Count; i++)
            {
                if (((IDevice)listBoxPlayback.Items[i].Tag).Id == id)
                {
                    listBoxPlayback.Items[i].Selected = true;
                    break;
                }
            }
        }

        private void mnuSetRecordingDefault_Click(object sender, EventArgs e)
        {
            if (SelectedRecordingDevice == null)
                return;

            Guid id = SelectedRecordingDevice.Id;
            SelectedRecordingDevice.SetAsDefault();
            PostRecordingMenuClick(id);
        }

        private void PostRecordingMenuClick(Guid id)
        {
            RefreshRecordingDevices();
            RefreshRecordingDropDownButton();
            for (int i = 0; i < listBoxRecording.Items.Count; i++)
            {
                if (((IDevice)listBoxRecording.Items[i].Tag).Id == id)
                {
                    listBoxRecording.Items[i].Selected = true;
                    break;
                }
            }
        }

        private void mnuSetRecordingCommunicationDefault_Click(object sender, EventArgs e)
        {
            if (SelectedRecordingDevice == null)
                return;

            Guid id = SelectedRecordingDevice.Id;
            SelectedRecordingDevice.SetAsDefaultCommunications();
            PostRecordingMenuClick(id);
        }

        private void HotKeyManager_HotKeyPressed(object sender, EventArgs e)
        {
            //Double check here before handling
            if (DisableHotKeyFunction || Program.Settings.DisableHotKeys)
                return;

            if (sender is HotKey)
            {
                var hk = sender as HotKey;

                if (hk.Device == null || hk.Device.IsDefaultDevice)
                    return;

                hk.Device.SetAsDefault();

                if (Program.Settings.DualSwitchMode)
                    hk.Device.SetAsDefaultCommunications();
            }
        }

        private void Form1_FormClosing(object sender, FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                if (Program.Settings.CloseToTray)
                {
                    e.Cancel = true;
                    Hide();
                    MinimizeFootprint();
                }

                HotKeyManager.SaveHotKeys();
            }
        }

        private void notifyIcon1_DoubleClick(object sender, EventArgs e)
        {
            Show();
            BringToFront();
            SetForegroundWindow(Handle);
        }

        private void Form1_Activated(object sender, EventArgs e)
        {
            //RefreshPlaybackDevices();
            //RefreshRecordingDevices();
        }

        private void exitToolStripMenuItem_Click(object sender, EventArgs e)
        {
            Application.Exit();
        }

        private void notifyIconStrip_ItemClicked(object sender, ToolStripItemClickedEventArgs e)
        {
            if (e.ClickedItem != null && e.ClickedItem.Tag is IDevice)
            {
                var dev = (IDevice)e.ClickedItem.Tag;
                dev.SetAsDefault();

                if (Program.Settings.DualSwitchMode)
                    dev.SetAsDefaultCommunications();
            }
        }

        private void chkCloseToTray_CheckedChanged(object sender, EventArgs e)
        {
            Program.Settings.CloseToTray = chkCloseToTray.Checked;
        }

        private void chkAutoStartWithWindows_CheckedChanged(object sender, EventArgs e)
        {
            Program.Settings.AutoStartWithWindows = chkAutoStartWithWindows.Checked;
        }

        private void chkStartMinimized_CheckedChanged(object sender, EventArgs e)
        {
            Program.Settings.StartMinimized = chkStartMinimized.Checked;
        }


        private void chkQuickSwitch_CheckedChanged(object sender, EventArgs e)
        {
            Program.Settings.EnableQuickSwitch = chkQuickSwitch.Checked;
        }

        private void chkDualSwitchMode_CheckedChanged(object sender, EventArgs e)
        {
            Program.Settings.DualSwitchMode = chkDualSwitchMode.Checked;
        }

        private void AudioSwitcher_ResizeEnd(object sender, EventArgs e)
        {
            Program.Settings.WindowWidth = Width;
            Program.Settings.WindowHeight = Height;
        }

        #endregion

        #region Assembly Attribute Accessors

        public string AssemblyTitle
        {
            get
            {
                object[] attributes =
                    Assembly.GetExecutingAssembly().GetCustomAttributes(typeof(AssemblyTitleAttribute), false);
                if (attributes.Length > 0)
                {
                    var titleAttribute = (AssemblyTitleAttribute)attributes[0];
                    if (titleAttribute.Title != "")
                    {
                        return titleAttribute.Title;
                    }
                }
                return Path.GetFileNameWithoutExtension(Assembly.GetExecutingAssembly().CodeBase);
            }
        }

        public string AssemblyVersion
        {
            get { return Assembly.GetExecutingAssembly().GetName().Version.ToString(); }
        }

        public string AssemblyDescription
        {
            get
            {
                object[] attributes =
                    Assembly.GetExecutingAssembly().GetCustomAttributes(typeof(AssemblyDescriptionAttribute), false);
                if (attributes.Length == 0)
                {
                    return "";
                }
                return ((AssemblyDescriptionAttribute)attributes[0]).Description;
            }
        }

        public string AssemblyProduct
        {
            get
            {
                object[] attributes =
                    Assembly.GetExecutingAssembly().GetCustomAttributes(typeof(AssemblyProductAttribute), false);
                if (attributes.Length == 0)
                {
                    return "";
                }
                return ((AssemblyProductAttribute)attributes[0]).Product;
            }
        }

        public string AssemblyCopyright
        {
            get
            {
                object[] attributes =
                    Assembly.GetExecutingAssembly().GetCustomAttributes(typeof(AssemblyCopyrightAttribute), false);
                if (attributes.Length == 0)
                {
                    return "";
                }
                return ((AssemblyCopyrightAttribute)attributes[0]).Copyright;
            }
        }

        public string AssemblyCompany
        {
            get
            {
                object[] attributes =
                    Assembly.GetExecutingAssembly().GetCustomAttributes(typeof(AssemblyCompanyAttribute), false);
                if (attributes.Length == 0)
                {
                    return "";
                }
                return ((AssemblyCompanyAttribute)attributes[0]).Company;
            }
        }

        #endregion

        private void label7_Click(object sender, EventArgs e)
        {
            Process.Start("https://twitter.com/xenolightning");
        }

        private void linkLabel2_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
        {
            Process.Start("http://audioswit.ch/er");
        }

        private void chkShowDiabledDevices_CheckedChanged(object sender, EventArgs e)
        {
            Program.Settings.ShowDisabledDevices = chkShowDiabledDevices.Checked;

            //Set, or remove the disconnected filter
            if (Program.Settings.ShowDisabledDevices)
                DeviceStateFilter |= DeviceState.Disabled;
            else
                DeviceStateFilter ^= DeviceState.Disabled;

            if (this.IsHandleCreated)
            {
                this.BeginInvoke((Action)(() =>
                {
                    RefreshPlaybackDevices();
                    RefreshRecordingDevices();
                }));
            }
        }

        private void chkShowDisconnectedDevices_CheckedChanged(object sender, EventArgs e)
        {
            Program.Settings.ShowDisconnectedDevices = chkShowDisconnectedDevices.Checked;

            //Set, or remove the disconnected filter
            if (Program.Settings.ShowDisconnectedDevices)
                DeviceStateFilter |= DeviceState.Unplugged;
            else
                DeviceStateFilter ^= DeviceState.Unplugged;

            if (this.IsHandleCreated)
            {
                this.BeginInvoke((Action)(() =>
                {
                    RefreshPlaybackDevices();
                    RefreshRecordingDevices();
                }));
            }
        }

        private void playbackStrip_Opening(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (SelectedPlaybackDevice == null)
                e.Cancel = true;
        }

        private void recordingStrip_Opening(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (SelectedRecordingDevice == null)
                e.Cancel = true;
        }

        private void linkIssues_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
        {
            Process.Start("https://github.com/xenolightning/AudioSwitcher_v1/issues");
        }

        private void linkWiki_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
        {
            Process.Start("https://github.com/xenolightning/AudioSwitcher_v1/wiki");
        }

        private void chkShowDPDeviceIconInTray_CheckedChanged(object sender, EventArgs e)
        {
            Program.Settings.ShowDPDeviceIconInTray = chkShowDPDeviceIconInTray.Checked;
            RefreshTrayIcon();
        }
    }
}
