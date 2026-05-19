using System;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Windows.Forms;

namespace TrFileTransfer
{
    /// <summary>Main application window — mode/protocol selector, server/client panels, progress, and log.</summary>
    public class MainForm : Form
    {
        // Language
        private ComboBox _cmbLang;

        // Mode
        private RadioButton _rbServer;
        private RadioButton _rbClient;
        private GroupBox _gbMode;

        // Protocol
        private RadioButton _rbTcp;
        private RadioButton _rbUdp;
        private GroupBox _gbProtocol;

        // Server controls
        private GroupBox _gbServer;
        private Label _lblBind;
        private ComboBox _cmbBind;
        private Label _lblPortS;
        private TextBox _txtPortS;
        private Label _lblSaveDir;
        private TextBox _txtSaveDir;
        private Button _btnBrowseDir;
        private Button _btnStartServer;
        private Button _btnStopServer;

        // Client controls
        private GroupBox _gbClient;
        private Label _lblServerIp;
        private TextBox _txtServerIp;
        private Label _lblPortC;
        private TextBox _txtPortC;
        private Label _lblFile;
        private TextBox _txtFile;
        private Button _btnBrowseFile;
        private Button _btnSend;
        private Button _btnCancel;
        private CheckBox _chkFolder;
        private CheckBox _chkMonitor;

        // Progress
        private GroupBox _gbProgress;
        private ProgressBar _progressBar;
        private Label _lblPercent;
        private Label _lblSpeed;
        private Label _lblStatus;

        // Log
        private GroupBox _gbLog;
        private ListBox _lstLog;

        // State
        private TransferServer _server;
        private TransferClient _client;
        private TransferUdpServer _serverUdp;
        private TransferUdpClient _clientUdp;
        private bool _transferActive;

        // Monitor mode
        private System.Threading.CancellationTokenSource _monitorCts;
        private System.Collections.Generic.List<string> _monitorQueue = new System.Collections.Generic.List<string>();
        private readonly object _monitorLock = new object();

        /// <summary>Initializes the form, populates NIC list, and applies default language.</summary>
        public MainForm()
        {
            InitializeComponent();
            Config.Load();
            PopulateBindAddresses();
            ApplyLanguage();
            UpdateModeUI();
            ApplyConfig();
        }

        private void InitializeComponent()
        {
            Text = L.AppTitle;
            Size = new Size(620, 640);
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            Font = new Font("Segoe UI", 9f);

            // Language selector
            _cmbLang = new ComboBox
            {
                Location = new Point(480, 16),
                Width = 110,
                DropDownStyle = ComboBoxStyle.DropDownList
            };
            _cmbLang.Items.Add("English");
            _cmbLang.Items.Add("中文");
            _cmbLang.SelectedIndex = 0;
            _cmbLang.SelectedIndexChanged += CmbLang_SelectedIndexChanged;

            // Mode selection
            _gbMode = new GroupBox { Location = new Point(12, 12), Size = new Size(280, 45) };
            _rbServer = new RadioButton { Location = new Point(10, 18), Width = 130, Checked = true };
            _rbClient = new RadioButton { Location = new Point(145, 18), Width = 130 };
            _rbServer.CheckedChanged += (s, e) => UpdateModeUI();
            _rbClient.CheckedChanged += (s, e) => UpdateModeUI();
            _gbMode.Controls.Add(_rbServer);
            _gbMode.Controls.Add(_rbClient);

            // Protocol selection
            _gbProtocol = new GroupBox { Location = new Point(300, 12), Size = new Size(140, 45) };
            _rbTcp = new RadioButton { Text = "TCP", Location = new Point(10, 18), Width = 55, Checked = true };
            _rbUdp = new RadioButton { Text = "UDP", Location = new Point(70, 18), Width = 60 };
            _gbProtocol.Controls.Add(_rbTcp);
            _gbProtocol.Controls.Add(_rbUdp);

            // Server panel
            _gbServer = new GroupBox { Location = new Point(12, 63), Size = new Size(580, 135) };
            _lblBind = new Label { Location = new Point(15, 28), Width = 70, TextAlign = ContentAlignment.MiddleRight };
            _cmbBind = new ComboBox { Location = new Point(90, 24), Width = 125, DropDownStyle = ComboBoxStyle.DropDownList };
            _lblPortS = new Label { Location = new Point(220, 28), Width = 50, TextAlign = ContentAlignment.MiddleRight };
            _txtPortS = new TextBox { Text = "8080", Location = new Point(275, 25), Width = 55 };
            _lblSaveDir = new Label { Location = new Point(15, 63), Width = 60, TextAlign = ContentAlignment.MiddleRight };
            _txtSaveDir = new TextBox { Text = Environment.GetFolderPath(Environment.SpecialFolder.Desktop), Location = new Point(80, 60), Width = 390 };
            _btnBrowseDir = new Button { Location = new Point(475, 59), Width = 85 };
            _btnBrowseDir.Click += BtnBrowseDir_Click;
            _btnStartServer = new Button { Location = new Point(55, 95), Width = 110, Height = 30 };
            _btnStartServer.Click += BtnStartServer_Click;
            _btnStopServer = new Button { Location = new Point(175, 95), Width = 110, Height = 30, Enabled = false };
            _btnStopServer.Click += BtnStopServer_Click;
            _gbServer.Controls.Add(_lblBind);
            _gbServer.Controls.Add(_cmbBind);
            _gbServer.Controls.Add(_lblPortS);
            _gbServer.Controls.Add(_txtPortS);
            _gbServer.Controls.Add(_lblSaveDir);
            _gbServer.Controls.Add(_txtSaveDir);
            _gbServer.Controls.Add(_btnBrowseDir);
            _gbServer.Controls.Add(_btnStartServer);
            _gbServer.Controls.Add(_btnStopServer);

            // Client panel
            _gbClient = new GroupBox { Location = new Point(12, 63), Size = new Size(580, 135), Visible = false };
            _lblServerIp = new Label { Location = new Point(15, 28), Width = 90, TextAlign = ContentAlignment.MiddleRight };
            _txtServerIp = new TextBox { Text = "127.0.0.1", Location = new Point(110, 25), Width = 105 };
            _lblPortC = new Label { Location = new Point(222, 28), Width = 50, TextAlign = ContentAlignment.MiddleRight };
            _txtPortC = new TextBox { Text = "8080", Location = new Point(277, 25), Width = 60 };
            _lblFile = new Label { Location = new Point(15, 60), Width = 48, TextAlign = ContentAlignment.MiddleRight };
            _txtFile = new TextBox { Location = new Point(68, 57), Width = 290 };
            _btnBrowseFile = new Button { Location = new Point(365, 56), Width = 80 };
            _btnBrowseFile.Click += BtnBrowseFile_Click;
            _chkFolder = new CheckBox { Location = new Point(350, 28), Width = 95, TextAlign = ContentAlignment.MiddleLeft };
            _chkFolder.CheckedChanged += ChkFolder_CheckedChanged;
            _chkMonitor = new CheckBox { Location = new Point(70, 85), Width = 100, TextAlign = ContentAlignment.MiddleLeft };
            _chkMonitor.CheckedChanged += ChkMonitor_CheckedChanged;
            _btnSend = new Button { Location = new Point(455, 24), Width = 110, Height = 30 };
            _btnSend.Click += BtnSend_Click;
            _btnCancel = new Button { Location = new Point(455, 56), Width = 110, Height = 30, Enabled = false };
            _btnCancel.Click += BtnCancel_Click;
            _gbClient.Controls.Add(_lblServerIp);
            _gbClient.Controls.Add(_txtServerIp);
            _gbClient.Controls.Add(_lblPortC);
            _gbClient.Controls.Add(_txtPortC);
            _gbClient.Controls.Add(_lblFile);
            _gbClient.Controls.Add(_txtFile);
            _gbClient.Controls.Add(_btnBrowseFile);
            _gbClient.Controls.Add(_chkFolder);
            _gbClient.Controls.Add(_chkMonitor);
            _gbClient.Controls.Add(_btnSend);
            _gbClient.Controls.Add(_btnCancel);

            // Progress panel
            _gbProgress = new GroupBox { Location = new Point(12, 206), Size = new Size(580, 90) };
            _progressBar = new ProgressBar { Location = new Point(15, 22), Width = 545, Height = 22, Style = ProgressBarStyle.Continuous };
            _lblPercent = new Label { Text = "0%", Location = new Point(15, 48), Width = 60, Font = new Font("Segoe UI", 9f, FontStyle.Bold) };
            _lblSpeed = new Label { Location = new Point(90, 48), Width = 200 };
            _lblStatus = new Label { Location = new Point(300, 48), Width = 260, TextAlign = ContentAlignment.MiddleRight };
            _gbProgress.Controls.Add(_progressBar);
            _gbProgress.Controls.Add(_lblPercent);
            _gbProgress.Controls.Add(_lblSpeed);
            _gbProgress.Controls.Add(_lblStatus);

            // Log panel
            _gbLog = new GroupBox { Location = new Point(12, 306), Size = new Size(580, 285) };
            _lstLog = new ListBox { Location = new Point(10, 20), Width = 555, Height = 255, IntegralHeight = false, Font = new Font("Consolas", 8.5f) };
            _gbLog.Controls.Add(_lstLog);

            Controls.Add(_cmbLang);
            Controls.Add(_gbMode);
            Controls.Add(_gbProtocol);
            Controls.Add(_gbServer);
            Controls.Add(_gbClient);
            Controls.Add(_gbProgress);
            Controls.Add(_gbLog);
        }

        private void CmbLang_SelectedIndexChanged(object sender, EventArgs e)
        {
            L.IsChinese = _cmbLang.SelectedIndex == 1;
            ApplyLanguage();
        }

        private void ApplyLanguage()
        {
            Text = L.AppTitle;
            _gbMode.Text = L.ModeGroup;
            _gbProtocol.Text = L.ProtocolGroup;
            _rbServer.Text = L.ModeServer;
            _rbClient.Text = L.ModeClient;

            _gbServer.Text = L.ServerSettings;
            _lblBind.Text = L.BindAddress;
            _lblPortS.Text = L.Port;
            _lblSaveDir.Text = L.SaveTo;
            _btnBrowseDir.Text = L.Browse;
            _btnStartServer.Text = L.StartServer;
            _btnStopServer.Text = L.StopServer;

            _gbClient.Text = L.ClientSettings;
            _lblServerIp.Text = L.ServerIP;
            _lblPortC.Text = L.Port;
            _lblFile.Text = _chkMonitor.Checked ? L.MonitorLabel : (_chkFolder.Checked ? L.FolderLabel : L.FileLabel);
            _btnBrowseFile.Text = L.Browse;
            _btnSend.Text = _chkMonitor.Checked ? L.StartMonitor : (_chkFolder.Checked ? L.SendFolder : L.SendFile);
            _btnCancel.Text = L.CancelBtn;
            _chkFolder.Text = L.FolderMode;
            _chkMonitor.Text = L.MonitorMode;

            _gbProgress.Text = L.ProgressGroup;
            if (!_transferActive && _btnStartServer.Enabled)
                _lblStatus.Text = L.Ready;
            _lblSpeed.Text = L.SpeedLabel;

            _gbLog.Text = L.LogGroup;

            PopulateBindAddresses();
        }

        private void ApplyConfig()
        {
            // Language
            _cmbLang.SelectedIndex = Config.Get("Language", "English") == "中文" ? 1 : 0;

            // Protocol
            string proto = Config.Get("Protocol", "TCP");
            _rbTcp.Checked = proto != "UDP";
            _rbUdp.Checked = proto == "UDP";

            // Server
            _txtPortS.Text = Config.Get("ServerPort", "8080");
            _txtSaveDir.Text = Config.Get("SaveDir", Environment.GetFolderPath(Environment.SpecialFolder.Desktop));
            string bind = Config.Get("ServerBind", "");
            if (!string.IsNullOrWhiteSpace(bind))
            {
                for (int i = 0; i < _cmbBind.Items.Count; i++)
                {
                    if (_cmbBind.Items[i].ToString() == bind) { _cmbBind.SelectedIndex = i; break; }
                }
            }

            // Client
            _txtServerIp.Text = Config.Get("ClientIP", "127.0.0.1");
            _txtPortC.Text = Config.Get("ClientPort", "8080");
            _txtFile.Text = Config.Get("LastPath", "");
            _chkFolder.Checked = Config.GetBool("FolderMode", false);
            _chkMonitor.Checked = Config.GetBool("MonitorMode", false);
        }

        private void SaveConfig()
        {
            Config.Set("Language", _cmbLang.SelectedIndex == 1 ? "中文" : "English");
            Config.Set("Protocol", _rbUdp.Checked ? "UDP" : "TCP");
            Config.Set("ServerPort", _txtPortS.Text.Trim());
            Config.Set("SaveDir", _txtSaveDir.Text.Trim());
            Config.Set("ServerBind", _cmbBind.SelectedItem != null ? _cmbBind.SelectedItem.ToString() : "");
            Config.Set("ClientIP", _txtServerIp.Text.Trim());
            Config.Set("ClientPort", _txtPortC.Text.Trim());
            Config.Set("LastPath", _txtFile.Text.Trim());
            Config.SetBool("FolderMode", _chkFolder.Checked);
            Config.SetBool("MonitorMode", _chkMonitor.Checked);
            Config.Save();
        }

        private void UpdateModeUI()
        {
            bool isServer = _rbServer.Checked;
            _gbServer.Visible = isServer;
            _gbClient.Visible = !isServer;
        }

        private void PopulateBindAddresses()
        {
            string allText = L.BindAll;
            int previousSelection = _cmbBind.SelectedIndex;

            _cmbBind.Items.Clear();
            _cmbBind.Items.Add(allText);

            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up)
                        continue;

                    foreach (var addr in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (addr.Address.AddressFamily == AddressFamily.InterNetwork)
                        {
                            string ip = addr.Address.ToString();
                            if (!_cmbBind.Items.Contains(ip))
                                _cmbBind.Items.Add(ip);
                        }
                    }
                }
            }
            catch { }

            if (_cmbBind.Items.Count == 1)
            {
                _cmbBind.Items.Add("127.0.0.1");
            }

            if (previousSelection >= 0 && previousSelection < _cmbBind.Items.Count)
                _cmbBind.SelectedIndex = previousSelection;
            else
                _cmbBind.SelectedIndex = 0;
        }

        private void BtnBrowseDir_Click(object sender, EventArgs e)
        {
            using (var dlg = new FolderBrowserDialog())
            {
                dlg.Description = L.BrowseDirDesc;
                if (dlg.ShowDialog() == DialogResult.OK)
                    _txtSaveDir.Text = dlg.SelectedPath;
            }
        }

        private void ChkFolder_CheckedChanged(object sender, EventArgs e)
        {
            if (_chkMonitor.Checked) return; // Monitor mode overrides folder mode
            bool isFolder = _chkFolder.Checked;
            _lblFile.Text = isFolder ? L.FolderLabel : L.FileLabel;
            _btnSend.Text = isFolder ? L.SendFolder : L.SendFile;
            _txtFile.Text = "";
        }

        private void ChkMonitor_CheckedChanged(object sender, EventArgs e)
        {
            bool isMonitor = _chkMonitor.Checked;
            _lblFile.Text = isMonitor ? L.MonitorLabel : (_chkFolder.Checked ? L.FolderLabel : L.FileLabel);
            _btnSend.Text = isMonitor ? L.StartMonitor : (_chkFolder.Checked ? L.SendFolder : L.SendFile);
            _chkFolder.Enabled = !isMonitor;
            _txtFile.Text = "";
        }

        private void BtnBrowseFile_Click(object sender, EventArgs e)
        {
            if (_chkFolder.Checked || _chkMonitor.Checked)
            {
                using (var dlg = new FolderBrowserDialog())
                {
                    dlg.Description = _chkMonitor.Checked ? L.MonitorLabel : L.BrowseFolderDesc;
                    if (dlg.ShowDialog() == DialogResult.OK)
                        _txtFile.Text = dlg.SelectedPath;
                }
            }
            else
            {
                using (var dlg = new OpenFileDialog())
                {
                    dlg.Title = L.BrowseFileTitle;
                    if (dlg.ShowDialog() == DialogResult.OK)
                        _txtFile.Text = dlg.FileName;
                }
            }
        }

        private void BtnStartServer_Click(object sender, EventArgs e)
        {
            int port;
            if (!int.TryParse(_txtPortS.Text.Trim(), out port) || port < 1 || port > 65535)
            {
                MessageBox.Show(L.InvalidPort, L.DlgError, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            string saveDir = _txtSaveDir.Text.Trim();
            if (!Directory.Exists(saveDir))
            {
                MessageBox.Show(L.DirNotExist, L.DlgError, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            string bindAddr = _cmbBind.SelectedItem != null ? _cmbBind.SelectedItem.ToString() : "0.0.0.0";
            if (string.IsNullOrWhiteSpace(bindAddr))
                bindAddr = "0.0.0.0";

            DisableServerInputs();

            if (_rbTcp.Checked)
            {
                _server = new TransferServer(bindAddr, port, saveDir);
                _server.OnLog += msg => this.Invoke((Action)(() => AddLog(msg)));
                _server.OnProgress += p => this.Invoke((Action)(() => UpdateProgress(p)));
                _server.OnError += msg => this.Invoke((Action)(() => _lblStatus.Text = L.ErrorPrefix + msg));
                _server.OnTransferComplete += () => this.Invoke((Action)(() =>
                {
                    _lblStatus.Text = L.Listening;
                }));
                _server.OnStarted += () => this.Invoke((Action)(() => OnServerStarted()));
                _server.OnStopped += () => this.Invoke((Action)(() => OnServerStopped()));
                _server.Start();
            }
            else
            {
                _serverUdp = new TransferUdpServer(port, saveDir);
                _serverUdp.OnLog += msg => this.Invoke((Action)(() => AddLog(msg)));
                _serverUdp.OnProgress += p => this.Invoke((Action)(() => UpdateProgress(p)));
                _serverUdp.OnError += msg => this.Invoke((Action)(() => _lblStatus.Text = L.ErrorPrefix + msg));
                _serverUdp.OnTransferComplete += () => this.Invoke((Action)(() =>
                {
                    _lblStatus.Text = L.Listening;
                }));
                _serverUdp.OnStarted += () => this.Invoke((Action)(() => OnServerStarted()));
                _serverUdp.OnStopped += () => this.Invoke((Action)(() => OnServerStopped()));
                _serverUdp.Start();
            }
        }

        private void DisableServerInputs()
        {
            _rbServer.Enabled = false;
            _rbClient.Enabled = false;
            _rbTcp.Enabled = false;
            _rbUdp.Enabled = false;
            _cmbLang.Enabled = false;
            _cmbBind.Enabled = false;
            _txtPortS.Enabled = false;
            _txtSaveDir.Enabled = false;
            _btnBrowseDir.Enabled = false;
        }

        private void EnableServerInputs()
        {
            _btnStartServer.Enabled = true;
            _btnStopServer.Enabled = false;
            _rbServer.Enabled = true;
            _rbClient.Enabled = true;
            _rbTcp.Enabled = true;
            _rbUdp.Enabled = true;
            _cmbLang.Enabled = true;
            _cmbBind.Enabled = true;
            _txtPortS.Enabled = true;
            _txtSaveDir.Enabled = true;
            _btnBrowseDir.Enabled = true;
            _transferActive = false;
        }

        private void DisableClientInputs()
        {
            _btnSend.Enabled = false;
            _btnCancel.Enabled = true;
            _rbServer.Enabled = false;
            _rbClient.Enabled = false;
            _rbTcp.Enabled = false;
            _rbUdp.Enabled = false;
            _cmbLang.Enabled = false;
            _txtServerIp.Enabled = false;
            _txtPortC.Enabled = false;
            _txtFile.Enabled = false;
            _btnBrowseFile.Enabled = false;
            _chkFolder.Enabled = false;
            _chkMonitor.Enabled = false;
        }

        private void OnServerStarted()
        {
            _btnStartServer.Enabled = false;
            _btnStopServer.Enabled = true;
            _lblStatus.Text = L.Listening;
            _transferActive = false;
        }

        private void OnServerStopped()
        {
            EnableServerInputs();
            _lblStatus.Text = L.ServerStopped;
        }

        private void BtnStopServer_Click(object sender, EventArgs e)
        {
            if (_server != null)
                _server.Stop();
            if (_serverUdp != null)
                _serverUdp.Stop();
        }

        private async void BtnSend_Click(object sender, EventArgs e)
        {
            string path = _txtFile.Text.Trim();
            bool isFolder = _chkFolder.Checked;
            bool isMonitor = _chkMonitor.Checked;

            // Validate IP and port (shared)
            int port;
            if (!int.TryParse(_txtPortC.Text.Trim(), out port) || port < 1 || port > 65535)
            {
                MessageBox.Show(L.InvalidPort, L.DlgError, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            string ip = _txtServerIp.Text.Trim();
            if (string.IsNullOrWhiteSpace(ip))
            {
                MessageBox.Show(L.EnterServerIP, L.DlgError, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            // Monitor mode branch
            if (isMonitor)
            {
                if (!Directory.Exists(path))
                {
                    MessageBox.Show(L.MonitorDirNotExist, L.DlgError, MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
                StartMonitoring(path, ip, port);
                return;
            }

            // Normal send branch
            if (isFolder)
            {
                if (!Directory.Exists(path))
                {
                    MessageBox.Show(L.DirNotExist, L.DlgError, MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
            }
            else
            {
                if (!File.Exists(path))
                {
                    MessageBox.Show(L.FileNotFound, L.DlgError, MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
            }

            DisableClientInputs();
            _transferActive = true;

            if (_rbTcp.Checked)
            {
                _client = new TransferClient(ip, port, path);
                WireClientEvents(_client);
                if (isFolder)
                    await _client.SendFolderAsync(path);
                else
                    await _client.SendAsync();
            }
            else
            {
                _clientUdp = new TransferUdpClient(ip, port, path);
                WireUdpClientEvents(_clientUdp);
                if (isFolder)
                    await _clientUdp.SendFolderAsync(path);
                else
                    await _clientUdp.SendAsync();
            }

            try { ResetClientUI(); } catch { }
        }

        private void WireClientEvents(TransferClient c)
        {
            c.OnLog += msg => this.Invoke((Action)(() => AddLog(msg)));
            c.OnProgress += p => this.Invoke((Action)(() => UpdateProgress(p)));
            c.OnError += msg => this.Invoke((Action)(() =>
            {
                _lblStatus.Text = L.ErrorPrefix + msg;
                ResetClientUI();
            }));
            c.OnTransferComplete += () => this.Invoke((Action)(() => ResetClientUI()));
            c.OnStopped += () => this.Invoke((Action)(() => ResetClientUI()));
        }

        private void WireUdpClientEvents(TransferUdpClient c)
        {
            c.OnLog += msg => this.Invoke((Action)(() => AddLog(msg)));
            c.OnProgress += p => this.Invoke((Action)(() => UpdateProgress(p)));
            c.OnError += msg => this.Invoke((Action)(() =>
            {
                _lblStatus.Text = L.ErrorPrefix + msg;
                ResetClientUI();
            }));
            c.OnTransferComplete += () => this.Invoke((Action)(() => ResetClientUI()));
            c.OnStopped += () => this.Invoke((Action)(() => ResetClientUI()));
        }

        private void BtnCancel_Click(object sender, EventArgs e)
        {
            if (_monitorCts != null)
            {
                StopMonitoring();
                return;
            }
            if (_client != null)
                _client.Cancel();
            if (_clientUdp != null)
                _clientUdp.Cancel();
            _btnCancel.Enabled = false;
            _lblStatus.Text = L.Cancelling;
        }

        private void ResetClientUI()
        {
            _transferActive = false;
            _lblStatus.Text = L.Ready;
            _btnSend.Enabled = true;
            _btnCancel.Enabled = false;
            _rbServer.Enabled = true;
            _rbClient.Enabled = true;
            _rbTcp.Enabled = true;
            _rbUdp.Enabled = true;
            _cmbLang.Enabled = true;
            _txtServerIp.Enabled = true;
            _txtPortC.Enabled = true;
            _txtFile.Enabled = true;
            _btnBrowseFile.Enabled = true;
            _chkFolder.Enabled = true;
            _chkMonitor.Enabled = true;
        }

        private void UpdateProgress(TransferProgress p)
        {
            if (p.TotalBytes > 0)
            {
                int percent = (int)(p.BytesTransferred * 100 / p.TotalBytes);
                _progressBar.Value = percent;
                _lblPercent.Text = string.Format("{0}%", percent);
            }

            double speed = p.SpeedBytesPerSecond;
            string speedStr;
            if (speed >= 1000000000.0)
                speedStr = string.Format("{0:F2} GB/s", speed / 1000000000.0);
            else if (speed >= 1000000.0)
                speedStr = string.Format("{0:F1} MB/s", speed / 1000000.0);
            else if (speed >= 1000.0)
                speedStr = string.Format("{0:F0} KB/s", speed / 1000.0);
            else
                speedStr = string.Format("{0:F0} B/s", speed);

            _lblSpeed.Text = L.SpeedPrefix + speedStr;

            if (p.BytesTransferred >= p.TotalBytes && p.TotalBytes > 0)
            {
                _lblStatus.Text = L.TransferComplete;
            }
            else
            {
                TimeSpan remaining = TimeSpan.Zero;
                if (p.SpeedBytesPerSecond > 0)
                    remaining = TimeSpan.FromSeconds((p.TotalBytes - p.BytesTransferred) / p.SpeedBytesPerSecond);
                string eta = string.Format("{0:mm\\:ss}", remaining);
                _lblStatus.Text = L.Transferring(p.FileName, eta);
            }
        }

        private void AddLog(string msg)
        {
            _lstLog.Items.Add(msg);
            _lstLog.TopIndex = _lstLog.Items.Count - 1;
            while (_lstLog.Items.Count > 500)
                _lstLog.Items.RemoveAt(0);
        }

        // ---- Monitor mode ----

        private void StartMonitoring(string folderPath, string ip, int port)
        {
            _monitorCts = new System.Threading.CancellationTokenSource();

            DisableClientInputs();
            _lblStatus.Text = L.MonitorWaiting;
            AddLog(L.MonitorStarted(folderPath));

            string sentDir = Path.Combine(folderPath, "已发送文件");
            Directory.CreateDirectory(sentDir);

            System.Threading.Tasks.Task.Run(() => MonitorLoop(folderPath, ip, port, sentDir, _monitorCts.Token));
        }

        private void StopMonitoring()
        {
            if (_monitorCts != null)
            {
                try { _monitorCts.Cancel(); } catch { }
            }
            _btnCancel.Enabled = false;
            _lblStatus.Text = L.MonitorStopped;
            AddLog(L.MonitorLogStopped);
            ResetClientUI();
        }

        private async System.Threading.Tasks.Task MonitorLoop(string folderPath, string ip, int port, string sentDir, System.Threading.CancellationToken ct)
        {
            using (var watcher = new FileSystemWatcher(folderPath))
            {
                watcher.NotifyFilter = NotifyFilters.FileName;
                watcher.Created += (s, e) =>
                {
                    lock (_monitorLock) { _monitorQueue.Add(e.FullPath); }
                };
                watcher.EnableRaisingEvents = true;

                while (!ct.IsCancellationRequested)
                {
                    string filePath = null;
                    lock (_monitorLock)
                    {
                        if (_monitorQueue.Count > 0)
                        {
                            filePath = _monitorQueue[0];
                            _monitorQueue.RemoveAt(0);
                        }
                    }

                    if (filePath != null)
                    {
                        await ProcessMonitoredFile(filePath, ip, port, sentDir, ct);
                    }
                    else
                    {
                        await System.Threading.Tasks.Task.Delay(500, ct);
                    }
                }
            }
        }

        private async System.Threading.Tasks.Task ProcessMonitoredFile(string filePath, string ip, int port, string sentDir, System.Threading.CancellationToken ct)
        {
            string fileName = Path.GetFileName(filePath);

            if (!await WaitForFileReady(filePath, ct))
            {
                this.Invoke((Action)(() =>
                    AddLog(L.MonitorFileNotReady(fileName))));
                lock (_monitorLock) { _monitorQueue.Add(filePath); }
                return;
            }

            bool success = false;
            try
            {
                var tcs = new System.Threading.Tasks.TaskCompletionSource<bool>();

                if (_rbTcp.Checked)
                {
                    var client = new TransferClient(ip, port, filePath);
                    client.OnLog += msg => this.Invoke((Action)(() => AddLog(msg)));
                    client.OnProgress += p => this.Invoke((Action)(() => UpdateProgress(p)));
                    client.OnError += msg => this.Invoke((Action)(() => AddLog(L.MonitorFileSendFailed(fileName, msg))));
                    client.OnTransferComplete += () => tcs.TrySetResult(true);
                    client.OnStopped += () => tcs.TrySetResult(false);
                    await client.SendAsync();
                }
                else
                {
                    var clientUdp = new TransferUdpClient(ip, port, filePath);
                    clientUdp.OnLog += msg => this.Invoke((Action)(() => AddLog(msg)));
                    clientUdp.OnProgress += p => this.Invoke((Action)(() => UpdateProgress(p)));
                    clientUdp.OnError += msg => this.Invoke((Action)(() => AddLog(L.MonitorFileSendFailed(fileName, msg))));
                    clientUdp.OnTransferComplete += () => tcs.TrySetResult(true);
                    clientUdp.OnStopped += () => tcs.TrySetResult(false);
                    await clientUdp.SendAsync();
                }

                success = await tcs.Task;
            }
            catch (Exception ex)
            {
                this.Invoke((Action)(() =>
                    AddLog(L.MonitorFileSendFailed(fileName, ex.Message))));
            }

            if (success)
            {
                string destPath = Utils.GetUniqueSavePath(sentDir, fileName);
                try { File.Move(filePath, destPath); } catch { }
                this.Invoke((Action)(() =>
                {
                    AddLog(L.MonitorFileSent(fileName));
                    _lblStatus.Text = L.MonitorWaiting;
                }));
            }
        }

        private async System.Threading.Tasks.Task<bool> WaitForFileReady(string filePath, System.Threading.CancellationToken ct)
        {
            int stableCount = 0;
            long lastSize = -1;
            int totalWaited = 0;
            const int PollIntervalMs = 500;
            const int StableThreshold = 2;
            const int RequeueAfterMs = 120000;
            const int LogIntervalMs = 30000;
            int lastLogAt = 0;
            var fileInfo = new FileInfo(filePath);

            while (!ct.IsCancellationRequested)
            {
                long currentSize;
                try
                {
                    fileInfo.Refresh();
                    if (!fileInfo.Exists)
                        return false;
                    currentSize = fileInfo.Length;
                }
                catch
                {
                    return false;
                }

                if (currentSize == lastSize)
                {
                    stableCount++;
                    if (stableCount >= StableThreshold)
                        return true;
                }
                else
                {
                    stableCount = 0;
                    lastSize = currentSize;
                }

                if (totalWaited >= RequeueAfterMs)
                    return false;

                if (totalWaited - lastLogAt >= LogIntervalMs)
                {
                    lastLogAt = totalWaited;
                    string fileName = Path.GetFileName(filePath);
                    this.Invoke((Action)(() =>
                        AddLog(L.MonitorFileWaiting(fileName, totalWaited / 1000))));
                }

                await System.Threading.Tasks.Task.Delay(PollIntervalMs, ct);
                totalWaited += PollIntervalMs;
            }

            return false;
        }

        /// <summary>Stops any active transfer before closing the window.</summary>
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            SaveConfig();
            if (_monitorCts != null)
            {
                try { _monitorCts.Cancel(); } catch { }
            }
            if (_server != null)
                _server.Stop();
            if (_serverUdp != null)
                _serverUdp.Stop();
            if (_client != null)
                _client.Cancel();
            if (_clientUdp != null)
                _clientUdp.Cancel();
            base.OnFormClosing(e);
        }
    }
}
