using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Media;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using WinForms = System.Windows.Forms;

namespace extract_xiso_gui
{
    enum SelectedMode
    {
        none,
        create,
        rewrite,
        list,
        extract,
        x360extract,
        x360list,
        x360god
    }

    // One unit of work, with everything needed to monitor its progress
    class XisoJob
    {
        public string ExePath;           // extract-xiso.exe or iso2god.exe
        public string Arguments;
        public string DeleteOnSuccess;   // source ISO to delete after verified success
        public string MonitorPath;       // file or folder whose growing size = progress
        public bool MonitorIsFolder;
        public long ExpectedBytes;       // estimated final size of MonitorPath
        public string Label;             // short name for status display
    }

    public partial class MainWindow : Window
    {
        public static string guiVersion = "1.0.0";
        public static string githubLink = "https://github.com/ilukezippo/XBOX-ISO-Extractor";
        string verLink = "https://raw.githubusercontent.com/ilukezippo/XBOX-ISO-Extractor/main/version.txt";
        string xisoDL = "https://raw.githubusercontent.com/ilukezippo/XBOX-ISO-Extractor/main/extract-xiso.exe";
        string iso2godDL = "https://github.com/iliazeus/iso2god-rs/releases/latest/download/iso2god-x86_64-windows.exe";
        string iso2godReleases = "https://github.com/iliazeus/iso2god-rs/releases";

        string rootPath;
        string eXISO;
        string iso2god;

        List<string> selectedInputFiles = new List<string>();
        string selectedInputFolder;
        string selectedOutput;

        // Last-used folders (persisted to gui-settings.txt, one per Browse button)
        string lastInputDir;
        string lastOutputDir;
        string settingsFile;

        bool suppressUpdates = false;

        // Process execution state
        bool isRunning = false;
        CancellationTokenSource cts;
        Process currentProcess;

        // Real-time progress state
        DispatcherTimer progressTimer;
        XisoJob currentJob;
        int jobsDone = 0;
        int jobsTotal = 0;
        bool sampling = false;

        private SelectedMode _status;
        internal SelectedMode Status
        {
            get => _status;
            set
            {
                _status = value;
                switch (_status)
                {
                    case SelectedMode.none:
                        SetOptionState(cbDelISO, false);
                        SetOptionState(cbAutoXBE, false);
                        SetOptionState(cbSkipSys, false);
                        SetOptionState(cbTrim, false);
                        InputStackEnable(false); OutputStackEnable(false);
                        GoBTN.IsEnabled = false; GoBTN.Opacity = 0.2;
                        txtModeDescription.Text = "Please select a mode to get started.";
                        break;
                    case SelectedMode.create:
                        SetOptionState(cbDelISO, false);
                        SetOptionState(cbAutoXBE, true);
                        SetOptionState(cbSkipSys, false);
                        SetOptionState(cbTrim, false);
                        InputStackEnable(true); OutputStackEnable(true);
                        GoBTN.IsEnabled = true; GoBTN.Opacity = 1;
                        txtModeDescription.Text = "CREATE MODE:\nPacks a folder containing Xbox game files into a bootable XISO image.";
                        break;
                    case SelectedMode.list:
                        SetOptionState(cbDelISO, false);
                        SetOptionState(cbAutoXBE, false);
                        SetOptionState(cbSkipSys, false);
                        SetOptionState(cbTrim, false);
                        InputStackEnable(true); OutputStackEnable(false);
                        GoBTN.IsEnabled = true; GoBTN.Opacity = 1;
                        txtModeDescription.Text = "LIST MODE:\nDisplays the file structure of an ISO in the log without extracting anything.";
                        break;
                    case SelectedMode.rewrite:
                        SetOptionState(cbDelISO, true);
                        SetOptionState(cbAutoXBE, true);
                        SetOptionState(cbSkipSys, true);
                        SetOptionState(cbTrim, false);
                        InputStackEnable(true); OutputStackEnable(true);
                        GoBTN.IsEnabled = true; GoBTN.Opacity = 1;
                        txtModeDescription.Text = "REWRITE MODE:\nRe-packs existing XISOs into an optimized format (strips padding).";
                        break;
                    case SelectedMode.extract:
                        SetOptionState(cbDelISO, true);
                        SetOptionState(cbAutoXBE, false);
                        SetOptionState(cbSkipSys, true);
                        SetOptionState(cbTrim, false);
                        InputStackEnable(true); OutputStackEnable(true);
                        GoBTN.IsEnabled = true; GoBTN.Opacity = 1;
                        txtModeDescription.Text = "EXTRACT MODE:\nUnpacks the contents of one or more Xbox ISOs into the output folder (one subfolder per ISO).";
                        break;
                    case SelectedMode.x360extract:
                        SetOptionState(cbDelISO, true);
                        SetOptionState(cbAutoXBE, false);
                        SetOptionState(cbSkipSys, true);
                        SetOptionState(cbTrim, false);
                        InputStackEnable(true); OutputStackEnable(true);
                        GoBTN.IsEnabled = true; GoBTN.Opacity = 1;
                        txtModeDescription.Text = "360 EXTRACT MODE:\nUnpacks Xbox 360 ISOs into XEX game folders (for JTAG/RGH consoles), one subfolder per ISO.\n\nRequires an up-to-date extract-xiso.exe.";
                        break;
                    case SelectedMode.x360god:
                        SetOptionState(cbDelISO, true);
                        SetOptionState(cbAutoXBE, false);
                        SetOptionState(cbSkipSys, false);
                        SetOptionState(cbTrim, true);
                        InputStackEnable(true); OutputStackEnable(true);
                        GoBTN.IsEnabled = true; GoBTN.Opacity = 1;
                        txtModeDescription.Text = "GOD MODE:\nConverts Xbox 360 ISOs to Games on Demand format using iso2god.\n\nOutput goes to the selected folder, ready to copy to the console's Content folder.";
                        break;
                    case SelectedMode.x360list:
                        SetOptionState(cbDelISO, false);
                        SetOptionState(cbAutoXBE, false);
                        SetOptionState(cbSkipSys, false);
                        SetOptionState(cbTrim, false);
                        InputStackEnable(true); OutputStackEnable(false);
                        GoBTN.IsEnabled = true; GoBTN.Opacity = 1;
                        txtModeDescription.Text = "360 LIST MODE:\nDisplays the file structure of an Xbox 360 ISO in the log without extracting anything.";
                        break;
                }
            }
        }

        public MainWindow()
        {
            InitializeComponent();

            rootPath = Directory.GetCurrentDirectory();
            eXISO = Path.Combine(rootPath, "extract-xiso.exe");
            iso2god = Path.Combine(rootPath, "iso2god.exe");
            settingsFile = Path.Combine(rootPath, "gui-settings.txt");
            LoadLastDirs();

            if (File.Exists(Path.Combine(rootPath, "suppress-updates.txt"))) { suppressUpdates = true; }

            // Timer that samples output size for real-time progress
            progressTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            progressTimer.Tick += ProgressTimer_Tick;

            Status = SelectedMode.none;
        }

        private void SetOptionState(System.Windows.Controls.CheckBox cb, bool enabled)
        {
            cb.IsEnabled = enabled;
            cb.Opacity = enabled ? 1.0 : 0.3;
        }

        private void InputStackEnable(bool enable)
        {
            InputPath.IsEnabled = enable;
            InputBrowse.IsEnabled = enable;
            InputBrowse.Opacity = enable ? 1.0 : 0.3;
        }

        private void OutputStackEnable(bool enable)
        {
            OutputPath.IsEnabled = enable;
            OutputBrowse.IsEnabled = enable;
            OutputBrowse.Opacity = enable ? 1.0 : 0.3;
        }

        private void Window_ContentRendered(object sender, EventArgs e)
        {
            if (!suppressUpdates) { CheckForUpdates(); }
            CheckForXISO();
        }

        private void CheckForUpdates()
        {
            Version localVersion = new Version(guiVersion);
            try
            {
                WebClient webClient = new WebClient();
                Version onlineVersion = new Version(webClient.DownloadString(verLink));
                if (onlineVersion.IsDifferentThan(localVersion))
                {
                    MessageBoxResult updateGUI = MessageBox.Show("An update for extract-xiso-gui is available. Would you like to download it?", "Update available", MessageBoxButton.YesNo, MessageBoxImage.Information);
                    if (updateGUI == MessageBoxResult.Yes)
                    {
                        Process.Start(githubLink + "/releases/latest");
                        Application.Current.Shutdown();
                    }
                }
            }
            catch { }
        }

        private void CheckForXISO()
        {
            if (!File.Exists(eXISO))
            {
                MessageBoxResult dlXISO = MessageBox.Show("extract-xiso has not been found. Would you like to download it?", "extract-xiso not found", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (dlXISO == MessageBoxResult.Yes)
                {
                    pb.Visibility = Visibility.Visible;
                    try
                    {
                        WebClient webClient = new WebClient();
                        webClient.DownloadFileCompleted += dlComplete;
                        webClient.DownloadProgressChanged += ProgressChanged;
                        webClient.DownloadFileAsync(new Uri(xisoDL), eXISO);
                    }
                    catch (Exception ex)
                    {
                        DisplayErrorMessage(ex);
                        Application.Current.Shutdown();
                    }
                }
                else
                {
                    Application.Current.Shutdown();
                }
            }
        }

        private void dlComplete(object sender, AsyncCompletedEventArgs e)
        {
            MessageBox.Show("extract-xiso has been downloaded.", "Download Complete", MessageBoxButton.OK, MessageBoxImage.Information);
            pb.Visibility = Visibility.Hidden;
        }

        private void SelectedMode_Changed(object sender, RoutedEventArgs e)
        {
            if (isRunning) { return; }
            try
            {
                SelectedMode newMode = SelectedMode.none;
                if (rbCreate.IsChecked == true) { newMode = SelectedMode.create; }
                if (rbList.IsChecked == true) { newMode = SelectedMode.list; }
                if (rbRewrite.IsChecked == true) { newMode = SelectedMode.rewrite; }
                if (rbExtract.IsChecked == true) { newMode = SelectedMode.extract; }
                if (rb360Extract.IsChecked == true) { newMode = SelectedMode.x360extract; }
                if (rb360GOD.IsChecked == true) { newMode = SelectedMode.x360god; }
                if (rb360List.IsChecked == true) { newMode = SelectedMode.x360list; }

                // Clicking the already-selected mode again: do nothing, keep paths
                if (newMode == Status) { return; }

                // Keep paths when switching between compatible modes.
                // Only Create uses folder-in / file-out, so crossing that
                // boundary invalidates the current selection.
                bool oldIsCreate = (Status == SelectedMode.create);
                bool newIsCreate = (newMode == SelectedMode.create);
                if (oldIsCreate != newIsCreate) { ClearPaths(); }

                Status = newMode;

                // List modes ignore output, so drop a leftover output if the
                // new mode doesn't use one
                if (newMode == SelectedMode.list || newMode == SelectedMode.x360list)
                {
                    OutputPath.Text = string.Empty;
                    selectedOutput = null;
                }
            }
            catch (Exception ex) { DisplayErrorMessage(ex); }
        }

        private void Console_TabChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            // Fires during InitializeComponent before controls exist
            if (rbExtract == null || rb360Extract == null) { return; }

            // Don't allow switching consoles mid-run
            if (isRunning)
            {
                tcConsole.SelectionChanged -= Console_TabChanged;
                tcConsole.SelectedIndex = (Status >= SelectedMode.x360extract) ? 1 : 0;
                tcConsole.SelectionChanged += Console_TabChanged;
                return;
            }

            // Reset mode + paths when moving between consoles
            rbExtract.IsChecked = false;
            rbCreate.IsChecked = false;
            rbRewrite.IsChecked = false;
            rbList.IsChecked = false;
            rb360Extract.IsChecked = false;
            rb360GOD.IsChecked = false;
            rb360List.IsChecked = false;
            ClearPaths();
            Status = SelectedMode.none;
        }

        private void ClearPaths()
        {
            InputPath.Text = string.Empty;
            OutputPath.Text = string.Empty;
            selectedInputFiles.Clear();
            selectedInputFolder = null;
            selectedOutput = null;
        }

        // ==================== DRAG & DROP ====================

        private void InputPath_PreviewDragOver(object sender, DragEventArgs e)
        {
            e.Effects = (!isRunning && Status != SelectedMode.none && e.Data.GetDataPresent(DataFormats.FileDrop))
                ? DragDropEffects.Copy
                : DragDropEffects.None;
            e.Handled = true; // required: read-only TextBox swallows drag events otherwise
        }

        private void InputPath_PreviewDrop(object sender, DragEventArgs e)
        {
            e.Handled = true;
            if (isRunning || !e.Data.GetDataPresent(DataFormats.FileDrop)) { return; }
            if (Status == SelectedMode.none)
            {
                MessageBox.Show("Please select a mode first.", "extract-xiso-gui", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            try
            {
                string[] dropped = (string[])e.Data.GetData(DataFormats.FileDrop);
                if (dropped == null || dropped.Length == 0) { return; }

                if (Status == SelectedMode.create)
                {
                    // Create mode expects a single game folder as input
                    string folder = dropped.FirstOrDefault(Directory.Exists);
                    if (folder == null)
                    {
                        MessageBox.Show("Create mode needs a folder as input. Drop a game folder here.", "extract-xiso-gui", MessageBoxButton.OK, MessageBoxImage.Information);
                        return;
                    }
                    selectedInputFolder = folder;
                    InputPath.Text = folder;
                    lastInputDir = folder;
                    SaveLastDirs();
                    return;
                }

                // All other modes: collect ISOs from dropped files + scan dropped folders
                var isos = new List<string>();
                foreach (string path in dropped)
                {
                    if (File.Exists(path) && path.EndsWith(".iso", StringComparison.OrdinalIgnoreCase))
                    {
                        isos.Add(path);
                    }
                    else if (Directory.Exists(path))
                    {
                        isos.AddRange(Directory.GetFiles(path, "*.iso", SearchOption.TopDirectoryOnly));
                    }
                }
                isos = isos.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

                if (isos.Count == 0)
                {
                    MessageBox.Show("No .iso files found in what was dropped.", "extract-xiso-gui", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                selectedInputFiles = isos;
                selectedInputFolder = dropped.FirstOrDefault(Directory.Exists);
                InputPath.Text = isos.Count == 1
                    ? isos[0]
                    : $"[{isos.Count} ISOs] " + string.Join("; ", isos.Select(Path.GetFileName));

                lastInputDir = Path.GetDirectoryName(isos[0]);
                SaveLastDirs();
            }
            catch (Exception ex) { DisplayErrorMessage(ex); }
        }

        private void OutputPath_PreviewDragOver(object sender, DragEventArgs e)
        {
            bool outputTakesFolder = (Status == SelectedMode.rewrite || Status == SelectedMode.extract || Status == SelectedMode.x360extract || Status == SelectedMode.x360god);
            e.Effects = (!isRunning && outputTakesFolder && e.Data.GetDataPresent(DataFormats.FileDrop))
                ? DragDropEffects.Copy
                : DragDropEffects.None;
            e.Handled = true;
        }

        private void OutputPath_PreviewDrop(object sender, DragEventArgs e)
        {
            e.Handled = true;
            if (isRunning || !e.Data.GetDataPresent(DataFormats.FileDrop)) { return; }
            bool outputTakesFolder = (Status == SelectedMode.rewrite || Status == SelectedMode.extract || Status == SelectedMode.x360extract || Status == SelectedMode.x360god);
            if (!outputTakesFolder) { return; }

            try
            {
                string[] dropped = (string[])e.Data.GetData(DataFormats.FileDrop);
                string folder = dropped?.FirstOrDefault(Directory.Exists);
                if (folder == null)
                {
                    MessageBox.Show("Drop a folder here to use it as the output directory.", "extract-xiso-gui", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                selectedOutput = folder;
                OutputPath.Text = folder;
                lastOutputDir = folder;
                SaveLastDirs();
            }
            catch (Exception ex) { DisplayErrorMessage(ex); }
        }

        private void InputBrowse_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (Status == SelectedMode.none) { MessageBox.Show("Please select a mode first.", "extract-xiso-gui", MessageBoxButton.OK, MessageBoxImage.Information); return; }

                if (Status == SelectedMode.create)
                {
                    BrowseForFolder(true);
                }
                else
                {
                    MessageBoxResult batchChoice = MessageBox.Show("Click 'Yes' to select a whole folder of ISOs (batch), or 'No' to pick individual files.", "Input type", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
                    if (batchChoice == MessageBoxResult.Yes)
                    {
                        BrowseForBatchFolder();
                    }
                    else if (batchChoice == MessageBoxResult.No)
                    {
                        BrowseForISO(true);
                    }
                }
            }
            catch (Exception ex) { DisplayErrorMessage(ex); }
        }

        private void OutputBrowse_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (Status == SelectedMode.none) { MessageBox.Show("Please select a mode first.", "extract-xiso-gui", MessageBoxButton.OK, MessageBoxImage.Information); return; }
                if (Status == SelectedMode.create) { SaveISO(); }
                if (Status == SelectedMode.rewrite || Status == SelectedMode.extract || Status == SelectedMode.x360extract || Status == SelectedMode.x360god) { BrowseForFolder(false); }
            }
            catch (Exception ex) { DisplayErrorMessage(ex); }
        }

        private void BrowseForISO(bool isInput)
        {
            var isoDLG = new Microsoft.Win32.OpenFileDialog();
            isoDLG.CheckFileExists = true;
            isoDLG.Multiselect = (Status != SelectedMode.create);
            isoDLG.Filter = "Xbox Disc Images (*.iso)|*.iso";
            if (Directory.Exists(lastInputDir)) { isoDLG.InitialDirectory = lastInputDir; }
            if (isoDLG.ShowDialog() == true)
            {
                selectedInputFiles = isoDLG.FileNames.ToList();
                InputPath.Text = string.Join("; ", selectedInputFiles);
                lastInputDir = Path.GetDirectoryName(selectedInputFiles[0]);
                SaveLastDirs();
            }
        }

        private void BrowseForBatchFolder()
        {
            WinForms.FolderBrowserDialog folderDLG = new WinForms.FolderBrowserDialog
            {
                SelectedPath = Directory.Exists(lastInputDir) ? lastInputDir : AppDomain.CurrentDomain.BaseDirectory,
                Description = "Select a folder containing Xbox ISO files for batch processing."
            };

            if (folderDLG.ShowDialog() == WinForms.DialogResult.OK)
            {
                lastInputDir = folderDLG.SelectedPath;
                SaveLastDirs();
                selectedInputFolder = folderDLG.SelectedPath;
                string[] foundFiles = Directory.GetFiles(selectedInputFolder, "*.iso", SearchOption.TopDirectoryOnly);
                if (foundFiles.Length == 0)
                {
                    MessageBox.Show("No .iso files were found in the selected folder.", "extract-xiso-gui", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                selectedInputFiles = foundFiles.ToList();
                InputPath.Text = $"[Folder Batch: {foundFiles.Length} ISOs] " + selectedInputFolder;
            }
        }

        private void SaveISO()
        {
            Microsoft.Win32.SaveFileDialog isoDLGs = new Microsoft.Win32.SaveFileDialog
            {
                FileName = "MyXISO.iso",
                DefaultExt = ".iso",
                Filter = "Disc Images (*.iso)|*.iso"
            };
            if (Directory.Exists(lastOutputDir)) { isoDLGs.InitialDirectory = lastOutputDir; }
            if (isoDLGs.ShowDialog() == true)
            {
                selectedOutput = isoDLGs.FileName;
                OutputPath.Text = selectedOutput;
                lastOutputDir = Path.GetDirectoryName(selectedOutput);
                SaveLastDirs();
            }
        }

        private void BrowseForFolder(bool isInput)
        {
            string lastDir = isInput ? lastInputDir : lastOutputDir;
            WinForms.FolderBrowserDialog folderDLG = new WinForms.FolderBrowserDialog
            {
                SelectedPath = Directory.Exists(lastDir) ? lastDir : AppDomain.CurrentDomain.BaseDirectory,
                Description = isInput ? "Select input folder." : "Select destination output folder."
            };

            if (folderDLG.ShowDialog() == WinForms.DialogResult.OK)
            {
                if (isInput)
                {
                    selectedInputFolder = folderDLG.SelectedPath;
                    InputPath.Text = selectedInputFolder;
                    lastInputDir = folderDLG.SelectedPath;
                }
                else
                {
                    selectedOutput = folderDLG.SelectedPath;
                    OutputPath.Text = selectedOutput;
                    lastOutputDir = folderDLG.SelectedPath;
                }
                SaveLastDirs();
            }
        }

        // ==================== SETTINGS (last-used folders) ====================

        private void LoadLastDirs()
        {
            try
            {
                if (!File.Exists(settingsFile)) { return; }
                foreach (var line in File.ReadAllLines(settingsFile))
                {
                    if (line.StartsWith("lastInputDir=")) { lastInputDir = line.Substring("lastInputDir=".Length); }
                    else if (line.StartsWith("lastOutputDir=")) { lastOutputDir = line.Substring("lastOutputDir=".Length); }
                }
            }
            catch { }
        }

        private void SaveLastDirs()
        {
            try
            {
                File.WriteAllLines(settingsFile, new[]
                {
                    "lastInputDir=" + (lastInputDir ?? ""),
                    "lastOutputDir=" + (lastOutputDir ?? "")
                });
            }
            catch { }
        }

        // ==================== EXECUTION ====================

        private async void GoBTN_Click(object sender, RoutedEventArgs e)
        {
            // If a batch is running, the button acts as CANCEL
            if (isRunning)
            {
                cts?.Cancel();
                try { if (currentProcess != null && !currentProcess.HasExited) { currentProcess.Kill(); } } catch { }
                AppendLog(">> Cancelling...");
                return;
            }

            try
            {
                if (selectedInputFiles.Count == 0 && string.IsNullOrEmpty(selectedInputFolder))
                {
                    MessageBox.Show("Please select an input path.", "extract-xiso-gui", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                if (string.IsNullOrEmpty(selectedOutput) && Status != SelectedMode.list && Status != SelectedMode.x360list)
                {
                    MessageBox.Show("Please select an output directory.", "extract-xiso-gui", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                if (Status == SelectedMode.x360god && !File.Exists(iso2god))
                {
                    bool ok = await EnsureIso2God();
                    if (!ok) { return; }
                }

                await RunAllAsync();
            }
            catch (Exception ex) { DisplayErrorMessage(ex); }
        }

        private List<XisoJob> BuildJobs()
        {
            string delISO = (cbDelISO.IsChecked == true) ? "-D " : "";
            string disXBE = (cbAutoXBE.IsChecked == true) ? "-m " : "";
            string skipSys = (cbSkipSys.IsChecked == true) ? "-s " : "";

            var jobs = new List<XisoJob>();

            if (Status == SelectedMode.create)
            {
                jobs.Add(new XisoJob
                {
                    ExePath = eXISO,
                    Arguments = $"{disXBE}-c \"{selectedInputFolder}\" \"{selectedOutput}\"",
                    Label = Path.GetFileName(selectedOutput),
                    MonitorPath = selectedOutput,
                    MonitorIsFolder = false,
                    ExpectedBytes = SafeGetFolderSize(selectedInputFolder)
                });
            }
            else if (Status == SelectedMode.list || Status == SelectedMode.x360list)
            {
                foreach (var file in selectedInputFiles)
                {
                    jobs.Add(new XisoJob
                    {
                        ExePath = eXISO,
                        Arguments = $"-l \"{file}\"",
                        Label = Path.GetFileName(file),
                        MonitorPath = null,          // listing is near-instant; no size monitoring
                        ExpectedBytes = 0
                    });
                }
            }
            else if (Status == SelectedMode.rewrite)
            {
                foreach (var file in selectedInputFiles)
                {
                    jobs.Add(new XisoJob
                    {
                        ExePath = eXISO,
                        // -D makes extract-xiso itself delete the original after rewrite
                        Arguments = $"{delISO}{disXBE}{skipSys}-d \"{selectedOutput}\" -r \"{file}\"",
                        Label = Path.GetFileName(file),
                        MonitorPath = Path.Combine(selectedOutput, Path.GetFileName(file)),
                        MonitorIsFolder = false,
                        ExpectedBytes = SafeGetFileSize(file)
                    });
                }
            }
            else if (Status == SelectedMode.extract || Status == SelectedMode.x360extract)
            {
                foreach (var file in selectedInputFiles)
                {
                    string isoName = Path.GetFileNameWithoutExtension(file);
                    string targetDir = Path.Combine(selectedOutput, isoName);
                    jobs.Add(new XisoJob
                    {
                        ExePath = eXISO,
                        Arguments = $"{skipSys}-d \"{targetDir}\" -x \"{file}\"",
                        Label = Path.GetFileName(file),
                        DeleteOnSuccess = (cbDelISO.IsChecked == true) ? file : null,
                        MonitorPath = targetDir,
                        MonitorIsFolder = true,
                        ExpectedBytes = SafeGetFileSize(file)
                    });
                }
            }
            else if (Status == SelectedMode.x360god)
            {
                string trim = (cbTrim.IsChecked == true) ? "--trim " : "";
                foreach (var file in selectedInputFiles)
                {
                    jobs.Add(new XisoJob
                    {
                        ExePath = iso2god,
                        Arguments = $"{trim}\"{file}\" \"{selectedOutput}\"",
                        Label = Path.GetFileName(file),
                        DeleteOnSuccess = (cbDelISO.IsChecked == true) ? file : null,
                        MonitorPath = selectedOutput,
                        MonitorIsFolder = true,
                        ExpectedBytes = SafeGetFileSize(file)
                    });
                }
            }

            return jobs;
        }

        private async Task RunAllAsync()
        {
            var jobs = BuildJobs();

            SetRunningState(true);
            txtLog.Clear();
            procProgress.Value = 0;
            cts = new CancellationTokenSource();

            jobsDone = 0;
            jobsTotal = jobs.Count;
            int failed = 0;
            bool cancelled = false;

            AppendLog($">> Starting {Status} — {jobs.Count} task(s).");
            AppendLog("");

            foreach (var job in jobs)
            {
                if (cts.IsCancellationRequested) { cancelled = true; break; }

                currentJob = job;
                txtStatus.Text = $"Processing {jobsDone + 1} of {jobsTotal}: {job.Label}";
                AppendLog($">> [{jobsDone + 1}/{jobsTotal}] extract-xiso {job.Arguments}");

                if (job.MonitorPath != null && job.ExpectedBytes > 0) { progressTimer.Start(); }

                int exitCode = await RunToolAsync(job.ExePath, job.Arguments, cts.Token);

                progressTimer.Stop();
                currentJob = null;

                if (cts.IsCancellationRequested) { cancelled = true; break; }

                if (exitCode == 0)
                {
                    AppendLog($">> Done (exit code 0).");

                    // Delete source ISO only after verified success (extract mode)
                    if (job.DeleteOnSuccess != null)
                    {
                        try
                        {
                            File.Delete(job.DeleteOnSuccess);
                            AppendLog($">> Deleted source ISO: {job.DeleteOnSuccess}");
                        }
                        catch (Exception ex)
                        {
                            AppendLog($">> WARNING: Could not delete {job.DeleteOnSuccess}: {ex.Message}");
                        }
                    }
                }
                else
                {
                    failed++;
                    AppendLog($">> FAILED (exit code {exitCode}). Source ISO was NOT deleted.");
                }

                jobsDone++;
                procProgress.Value = (double)jobsDone / jobsTotal * 100.0;
                AppendLog("");
            }

            // Summary
            if (cancelled)
            {
                txtStatus.Text = $"Cancelled after {jobsDone} of {jobsTotal} task(s).";
                AppendLog(">> Process cancelled by user.");
            }
            else
            {
                procProgress.Value = 100;
                txtStatus.Text = failed == 0
                    ? $"Completed {jobsDone} task(s) successfully."
                    : $"Completed with {failed} failure(s) out of {jobsDone} task(s). Check the log.";
                AppendLog(failed == 0 ? ">> All tasks completed successfully!" : $">> Finished with {failed} failure(s).");

                PlayFinishSound();
            }

            SetRunningState(false);

            if (!cancelled)
            {
                if (cbOpenFolder.IsChecked == true && !string.IsNullOrEmpty(selectedOutput))
                {
                    string folderToOpen = Directory.Exists(selectedOutput) ? selectedOutput : Path.GetDirectoryName(selectedOutput);
                    if (Directory.Exists(folderToOpen)) { Process.Start("explorer.exe", $"\"{folderToOpen}\""); }
                }
                if (cbCloseApp.IsChecked == true && failed == 0)
                {
                    Application.Current.Shutdown();
                }
            }
        }

        // Samples the output size every 300ms → smooth real-time progress within the current job
        private async void ProgressTimer_Tick(object sender, EventArgs e)
        {
            var job = currentJob;
            if (job == null || sampling) { return; }
            sampling = true;

            try
            {
                long bytes = await Task.Run(() =>
                {
                    if (job.MonitorIsFolder) { return SafeGetFolderSize(job.MonitorPath); }
                    return SafeGetFileSize(job.MonitorPath);
                });

                double fraction = Math.Min((double)bytes / job.ExpectedBytes, 0.99);
                double overall = ((jobsDone + fraction) / jobsTotal) * 100.0;
                if (overall > procProgress.Value) { procProgress.Value = overall; }

                txtStatus.Text = $"Processing {jobsDone + 1} of {jobsTotal}: {job.Label} — {(int)(fraction * 100)}%";
            }
            catch { }
            finally { sampling = false; }
        }

        private static long SafeGetFileSize(string path)
        {
            try { return File.Exists(path) ? new FileInfo(path).Length : 0; }
            catch { return 0; }
        }

        private static long SafeGetFolderSize(string path)
        {
            try
            {
                if (!Directory.Exists(path)) { return 0; }
                long total = 0;
                foreach (var f in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                {
                    try { total += new FileInfo(f).Length; } catch { }
                }
                return total;
            }
            catch { return 0; }
        }

        private void PlayFinishSound()
        {
            try
            {
                string wav = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Media", "Windows Notify Email.wav");
                if (File.Exists(wav))
                {
                    new SoundPlayer(wav).Play();
                }
                else
                {
                    SystemSounds.Asterisk.Play();
                }
            }
            catch { }
        }

        // Downloads iso2god.exe (asks first). Returns true when the tool is ready.
        private async Task<bool> EnsureIso2God()
        {
            MessageBoxResult dl = MessageBox.Show(
                "GOD conversion requires iso2god, which was not found next to the app.\n\nDownload it now?",
                "iso2god not found", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (dl != MessageBoxResult.Yes) { return false; }

            try
            {
                pb.Visibility = Visibility.Visible;
                txtStatus.Text = "Downloading iso2god...";
                using (WebClient webClient = new WebClient())
                {
                    webClient.DownloadProgressChanged += ProgressChanged;
                    await webClient.DownloadFileTaskAsync(new Uri(iso2godDL), iso2god);
                }
                pb.Visibility = Visibility.Hidden;
                txtStatus.Text = "iso2god downloaded.";
                return File.Exists(iso2god);
            }
            catch (Exception ex)
            {
                pb.Visibility = Visibility.Hidden;
                txtStatus.Text = "iso2god download failed.";
                MessageBoxResult open = MessageBox.Show(
                    $"Automatic download failed ({ex.Message}).\n\nOpen the iso2god releases page so you can download it manually? Save it as iso2god.exe next to this app.",
                    "Download failed", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (open == MessageBoxResult.Yes) { Process.Start(iso2godReleases); }
                return false;
            }
        }

        private Task<int> RunToolAsync(string exePath, string arguments, CancellationToken ct)
        {
            var tcs = new TaskCompletionSource<int>();

            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = rootPath
            };

            var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
            currentProcess = p;

            p.OutputDataReceived += (s, e) => { if (e.Data != null) { AppendLog(e.Data); } };
            p.ErrorDataReceived += (s, e) => { if (e.Data != null) { AppendLog(e.Data); } };
            p.Exited += (s, e) =>
            {
                int code = -1;
                try { code = p.ExitCode; } catch { }
                p.Dispose();
                currentProcess = null;
                tcs.TrySetResult(code);
            };

            try
            {
                p.Start();
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
            }
            catch (Exception ex)
            {
                AppendLog($">> ERROR starting {Path.GetFileName(exePath)}: {ex.Message}");
                tcs.TrySetResult(-1);
            }

            return tcs.Task;
        }

        private void AppendLog(string line)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => AppendLog(line)));
                return;
            }
            txtLog.AppendText(line + Environment.NewLine);
            txtLog.ScrollToEnd();
        }

        private void SetRunningState(bool running)
        {
            isRunning = running;

            GoBTN.Content = running ? "CANCEL" : "START PROCESS";
            GoBTN.Background = running
                ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xB0, 0x2E, 0x2E))
                : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x10, 0x7C, 0x41));

            spinner.Visibility = running ? Visibility.Visible : Visibility.Collapsed;

            // Lock everything else while running
            rbExtract.IsEnabled = !running;
            rbCreate.IsEnabled = !running;
            rbRewrite.IsEnabled = !running;
            rbList.IsEnabled = !running;
            rb360Extract.IsEnabled = !running;
            rb360GOD.IsEnabled = !running;
            rb360List.IsEnabled = !running;
            InputBrowse.IsEnabled = !running;
            OutputBrowse.IsEnabled = !running;
            cbDelISO.IsEnabled = !running && (Status == SelectedMode.extract || Status == SelectedMode.rewrite || Status == SelectedMode.x360extract || Status == SelectedMode.x360god);
            cbAutoXBE.IsEnabled = !running && (Status == SelectedMode.create || Status == SelectedMode.rewrite);
            cbSkipSys.IsEnabled = !running && (Status == SelectedMode.extract || Status == SelectedMode.rewrite || Status == SelectedMode.x360extract);
            cbTrim.IsEnabled = !running && (Status == SelectedMode.x360god);
            cbOpenFolder.IsEnabled = !running;
            cbCloseApp.IsEnabled = !running;

            if (!running)
            {
                progressTimer.Stop();
                cts?.Dispose();
                cts = null;
            }
        }

        // ==================== MISC ====================

        private void OpenAbout(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            About aboutWindow = new About();
            aboutWindow.Show();
        }

        private void MinimizeButton_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e) { WindowState = WindowState.Minimized; }

        private void CloseButton_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (isRunning)
            {
                MessageBoxResult confirm = MessageBox.Show("A process is still running. Cancel it and exit?", "extract-xiso-gui", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (confirm != MessageBoxResult.Yes) { return; }
                cts?.Cancel();
                try { if (currentProcess != null && !currentProcess.HasExited) { currentProcess.Kill(); } } catch { }
            }
            Application.Current.Shutdown();
        }

        private void DisplayErrorMessage(Exception ex)
        {
            MessageBox.Show($"{ex}", "An error occurred!", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        private void DragWindow(object sender, System.Windows.Input.MouseButtonEventArgs e) { DragMove(); }

        private void ProgressChanged(object sender, DownloadProgressChangedEventArgs e) { pb.Value = e.ProgressPercentage; }

        struct Version
        {
            private short major, minor, subMinor;

            internal Version(string _version)
            {
                string[] parts = _version.Split('.');
                major = parts.Length > 0 ? short.Parse(parts[0]) : (short)0;
                minor = parts.Length > 1 ? short.Parse(parts[1]) : (short)0;
                subMinor = parts.Length > 2 ? short.Parse(parts[2]) : (short)0;
            }

            internal bool IsDifferentThan(Version other) => major != other.major || minor != other.minor || subMinor != other.subMinor;
        }
    }
}
