using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

// Tray app: copies new photos/videos from any inserted card with a DCIM folder into the
// photo library (Library\YYYY\YYYY-MM-DD). Optionally deletes files from the card afterwards,
// but only files proven to exist in the library with an identical SHA-256 hash.
// Click the tray icon to open the flyout window above it.
class CardImporter : ApplicationContext
{
    internal const string AppVersion = "1.1.2";
    const string Repo = "ImSammyTTV/lightroom-card-importer"; // where releases are published
    // Only a suggestion: the first time the app runs it asks where photos should go.
    static readonly string DefaultLibrary = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Photography", "Photos");
    const string AppName = "CardImporter";
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string SettingsKey = @"Software\CardImporter";
    // File types, so you can choose what to import: RAW photos, JPEG/HEIC photos and videos
    // (with their .srt / .lrf sidecar files). Anything you don't pick is left alone on the card.
    static readonly string[] RawExt = { ".arw", ".dng", ".cr2", ".cr3", ".nef", ".raf", ".orf", ".rw2", ".srf", ".sr2", ".pef" };
    static readonly string[] JpegExt = { ".jpg", ".jpeg", ".heic", ".heif", ".hif" };
    static readonly string[] VideoExt = { ".mp4", ".mov", ".mxf", ".srt", ".lrf" };
    static readonly string[] Ext = RawExt.Concat(JpegExt).Concat(VideoExt).ToArray();
    static bool importRaw = true, importJpeg = true, importVideo = true;

    static bool Wanted(string ext)
    {
        ext = ext.ToLower();
        if (RawExt.Contains(ext)) return importRaw;
        if (JpegExt.Contains(ext)) return importJpeg;
        if (VideoExt.Contains(ext)) return importVideo;
        return false;
    }

    // The photo folder is a setting (change it in the window); this is only the default.
    static string Library = LoadLibrary();

    static string LoadLibrary()
    {
        using (var k = Registry.CurrentUser.OpenSubKey(SettingsKey))
        {
            var v = k == null ? null : k.GetValue("LibraryPath") as string;
            return string.IsNullOrWhiteSpace(v) ? DefaultLibrary : v;
        }
    }

    // Never treat the Windows drive or the drive holding the photo library as a card.
    internal static string[] SkipDrives
    {
        get
        {
            var l = new List<string> { Path.GetPathRoot(Environment.SystemDirectory).ToUpper() };
            try { l.Add(Path.GetPathRoot(Library).ToUpper()); } catch { }
            return l.ToArray();
        }
    }

    readonly NotifyIcon tray = new NotifyIcon();
    readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
    readonly FlyoutForm ui = new FlyoutForm();
    readonly HashSet<string> done = new HashSet<string>();
    readonly HashSet<string> seen = new HashSet<string>();
    readonly List<string> history = new List<string>();
    readonly string logFile, historyFile;
    readonly object gate = new object();
    bool busy;
    bool deleteAfter;
    bool autoShown;
    bool autoUpdate, checking, updateNotified;
    string updTag, updExeUrl, updSumsUrl;
    readonly System.Windows.Forms.Timer updateTimer = new System.Windows.Forms.Timer();

    [STAThread]
    static void Main(string[] args)
    {
        // `CardImporter.exe --screenshot out.png` renders the flyout with example data (used for the README).
        if (args.Length >= 2 && args[0] == "--screenshot") { FlyoutForm.Screenshot(args[1]); return; }
        // `CardImporter.exe --screenshot-frames dir` renders the frames of a whole example import (for the animated README image).
        if (args.Length >= 2 && args[0] == "--screenshot-frames") { FlyoutForm.ScreenshotFrames(args[1]); return; }
        // `CardImporter.exe --list-phones out.txt` writes the USB phones/portable devices Windows can see (for troubleshooting).
        if (args.Length >= 2 && args[0] == "--list-phones")
        {
            try
            {
                var found = ListPhones();
                File.WriteAllLines(args[1], new[] { found.Count + " device(s)" }.Concat(found.Select(d => d.Name + "  [" + d.Kind + "]  " + d.Id)));
            }
            catch (Exception ex) { File.WriteAllText(args[1], "error: " + ex); }
            return;
        }

        bool first;
        using (var m = new Mutex(true, "Local\\" + AppName, out first))
        {
            // Started by an update: the old copy is still shutting down, so wait for it to let go.
            if (!first && args.Contains("--updated"))
            {
                try { first = m.WaitOne(15000); } catch (AbandonedMutexException) { first = true; }
            }
            if (!first) return;
            try { File.Delete(Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "CardImporter.old.exe")); } catch { }
            Application.EnableVisualStyles();
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) => LogError("ThreadException", e.Exception);
            Application.Run(new CardImporter());
        }
    }

    CardImporter()
    {
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppName);
        Directory.CreateDirectory(dir);
        logFile = Path.Combine(dir, "copied.log");
        historyFile = Path.Combine(dir, "history.log");
        if (File.Exists(logFile)) foreach (var l in File.ReadAllLines(logFile)) if (l.Length > 0) done.Add(l);
        if (File.Exists(historyFile)) history.AddRange(File.ReadAllLines(historyFile).Where(l => l.Length > 0).Reverse().Take(3));
        ui.SetHistory(string.Join("\n", history));
        deleteAfter = ReadDeleteSetting();
        var forceHandle = ui.Handle; // create the window's handle now so worker threads can BeginInvoke to it

        ui.DeleteAfter = deleteAfter;
        ui.StartupOn = IsStartup();
        ui.SetLibrary(Library);
        WriteLibraryHint(); // lets the Lightroom plugin follow the same folder
        ui.ChooseLibrary += ChooseLibrary;
        ui.ImportNow += () => Scan(true);
        ui.OpenFolder += () => { Directory.CreateDirectory(Library); System.Diagnostics.Process.Start("explorer.exe", Library); };
        ui.DeleteToggled += on => { deleteAfter = on; WriteDeleteSetting(on); };
        ui.StartupToggled += on => SetStartup(on);
        ui.ExitApp += () => { tray.Visible = false; Application.Exit(); };

        importRaw = ReadSetting("ImportRaw", "1") == "1";
        importJpeg = ReadSetting("ImportJpeg", "1") == "1";
        importVideo = ReadSetting("ImportVideo", "1") == "1";
        ui.SetTypes(importRaw, importJpeg, importVideo);
        ui.TypeToggled += (kind, on) =>
        {
            if (kind == "raw") { importRaw = on; WriteSetting("ImportRaw", on ? "1" : "0"); }
            else if (kind == "jpeg") { importJpeg = on; WriteSetting("ImportJpeg", on ? "1" : "0"); }
            else if (kind == "video") { importVideo = on; WriteSetting("ImportVideo", on ? "1" : "0"); }
        };

        phonesEnabled = ReadSetting("PhonesEnabled", "0") == "1";
        LoadApprovedPhones();
        ui.PhonesOn = phonesEnabled;
        ui.PhonesToggled += OnPhonesToggled;
        ui.ForgetPhones += ForgetApprovedPhones;
        if (phonesEnabled) StartPhoneWatcher();

        autoUpdate = ReadSetting("AutoUpdate", "1") == "1";
        ui.AutoUpdate = autoUpdate;
        ui.SetUpdate("Version " + AppVersion + "  ·  not checked yet", false);
        ui.AutoUpdateToggled += on => { autoUpdate = on; WriteSetting("AutoUpdate", on ? "1" : "0"); };
        ui.CheckUpdates += () => CheckForUpdate(true);
        ui.InstallUpdate += ApplyUpdate;
        // check about 30 seconds after start, then re-check hourly but only act once a day
        updateTimer.Interval = 30000;
        updateTimer.Tick += (s, e) =>
        {
            updateTimer.Interval = 3600000;
            long last; long.TryParse(ReadSetting("LastUpdateCheck", "0"), out last);
            if (autoUpdate && DateTime.UtcNow.Ticks - last > TimeSpan.TicksPerDay) CheckForUpdate(false);
        };
        updateTimer.Start();

        var menu = new ContextMenuStrip();
        menu.Items.Add("Open", null, (s, e) => ToggleFlyout());
        menu.Items.Add("Import now", null, (s, e) => Scan(true));
        menu.Items.Add("Open photos folder", null, (s, e) => ui.RaiseOpenFolder());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (s, e) => ui.RaiseExit());

        tray.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        tray.Text = "Card Importer – waiting for a card";
        tray.ContextMenuStrip = menu;
        tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) ToggleFlyout(); };
        tray.Visible = true;

        // A card that is already in at startup is only imported via "Import now",
        // so launching the app never starts a copy (or a delete) by surprise.
        foreach (var d in DriveInfo.GetDrives()) { try { if (d.IsReady) seen.Add(d.Name); } catch { } }
        timer.Interval = 3000;
        timer.Tick += (s, e) => Scan(false);
        timer.Start();

        // First run: no folder has been chosen yet, so ask (Lightroom will read from the same place).
        if (ReadSetting("LibraryPath", "").Length == 0)
        {
            var t = new System.Windows.Forms.Timer { Interval = 1500 };
            t.Tick += (s, e) => { t.Stop(); t.Dispose(); FirstRunFolder(); };
            t.Start();
        }
    }

    void FirstRunFolder()
    {
        var owner = new Form { TopMost = true, ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-2000, -2000), Size = new Size(1, 1) };
        owner.Show(); // an invisible owner keeps the dialog in front
        using (var d = new FolderBrowserDialog())
        {
            d.Description = "Welcome to Card Importer.\n\nChoose the folder your photos should be saved to. Lightroom reads from here too. You can change it later in the window.";
            d.ShowNewFolderButton = true;
            d.SelectedPath = Directory.Exists(Library) ? Library : Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            if (d.ShowDialog(owner) == DialogResult.OK && d.SelectedPath.Length > 0) Library = d.SelectedPath;
        }
        owner.Dispose();
        WriteSetting("LibraryPath", Library); // remember the choice (or the suggested folder) so this isn't asked again
        WriteLibraryHint();
        ui.SetLibrary(Library);
    }

    // ---- flyout ----

    void ToggleFlyout()
    {
        try
        {
            if (ui.Visible) { ui.Hide(); return; }
            if (Environment.TickCount - ui.LastHide < 400) return; // the same click that just dismissed it
            autoShown = false;
            ui.Open(true, Cursor.Position); // the click was on the tray icon
        }
        catch (Exception ex) { LogError("ToggleFlyout", ex); }
    }

    // ---- phones (iPhone and Android over USB) ----
    // Off by default. When switched on, each phone has to be approved by you before anything is read from it.
    // Phones are read through the Windows shell (the same MTP / PTP access File Explorer uses), only the DCIM
    // camera folders are looked at, and nothing is ever deleted from or changed on a phone.

    class PhoneDevice { public string Id, Name, Kind; }
    class PhoneFile { public dynamic Item; public string Name; public long Size; public DateTime Mtime; }

    bool phonesEnabled, prompting;
    volatile bool phoneWatcherRunning;
    readonly Dictionary<string, string> approvedPhones = new Dictionary<string, string>(); // id -> name, saved in the registry
    readonly HashSet<string> allowedThisTime = new HashSet<string>();
    readonly HashSet<string> seenPhones = new HashSet<string>();
    volatile List<PhoneDevice> connectedPhones = new List<PhoneDevice>();
    internal static volatile string PhoneLines = "";

    void LoadApprovedPhones()
    {
        approvedPhones.Clear();
        foreach (var line in ReadSetting("ApprovedPhones", "").Split('\n'))
        {
            var p = line.Split('\t');
            if (p.Length == 2 && p[0].Length > 0) approvedPhones[p[0]] = p[1];
        }
    }

    void SaveApprovedPhones()
    {
        WriteSetting("ApprovedPhones", string.Join("\n", approvedPhones.Select(kv => kv.Key + "\t" + kv.Value)));
    }

    bool IsApproved(PhoneDevice d)
    {
        lock (allowedThisTime) return approvedPhones.ContainsKey(d.Id) || allowedThisTime.Contains(d.Id);
    }

    void OnPhonesToggled(bool on)
    {
        if (on)
        {
            ui.KeepOpen = true; // the window loses focus while the question is showing
            var r = MessageBox.Show(
                "Turn on phone import?\n\n" +
                "Card Importer will look for iPhones and Android phones connected by USB. The first time each phone connects, " +
                "you'll be asked to allow it.\n\n" +
                "It only reads the camera folders (DCIM) and never deletes or changes anything on a phone.",
                "Card Importer", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (r != DialogResult.Yes) { ui.PhonesOn = false; return; }
            phonesEnabled = true;
            WriteSetting("PhonesEnabled", "1");
            StartPhoneWatcher();
        }
        else
        {
            phonesEnabled = false;
            WriteSetting("PhonesEnabled", "0");
            PhoneLines = "";
        }
    }

    void ForgetApprovedPhones()
    {
        approvedPhones.Clear();
        lock (allowedThisTime) allowedThisTime.Clear();
        SaveApprovedPhones();
        Tip("Phones forgotten", "Each phone will ask for permission again the next time it connects.");
    }

    // Everything the shell shows under "This PC" that is a USB portable device rather than a drive.
    static List<PhoneDevice> ListPhones()
    {
        var list = new List<PhoneDevice>();
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application"));
        dynamic items = shell.NameSpace(17).Items(); // 17 = This PC
        int count = items.Count;
        for (int i = 0; i < count; i++)
        {
            dynamic it = items.Item(i);
            string path = it.Path, name = it.Name;
            if (path == null || (path.Length > 1 && path[1] == ':')) continue;            // a normal drive
            if (path.IndexOf("usb#", StringComparison.OrdinalIgnoreCase) < 0) continue;  // not a USB device
            bool apple = name.IndexOf("iphone", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         name.IndexOf("ipad", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         name.IndexOf("apple", StringComparison.OrdinalIgnoreCase) >= 0;
            list.Add(new PhoneDevice { Id = path, Name = name, Kind = apple ? "iPhone or iPad" : "Android phone or other device" });
        }
        return list;
    }

    void StartPhoneWatcher()
    {
        if (phoneWatcherRunning) return;
        phoneWatcherRunning = true;
        var t = new Thread(() =>
        {
            while (phonesEnabled)
            {
                try
                {
                    var now = ListPhones();
                    connectedPhones = now;
                    PhoneLines = string.Join("\n", now.Select(d => "●  " + d.Name + "  ·  phone  ·  " + (IsApproved(d) ? "allowed" : "needs your permission")));
                    var ids = new HashSet<string>(now.Select(d => d.Id));
                    foreach (var d in now)
                        if (seenPhones.Add(d.Id)) { var dev = d; ui.BeginInvoke(new Action(() => OnPhoneArrived(dev))); }
                    seenPhones.RemoveWhere(id => !ids.Contains(id));
                    lock (allowedThisTime) allowedThisTime.RemoveWhere(id => !ids.Contains(id)); // "this time" ends when it's unplugged
                }
                catch (Exception ex) { LogError("PhoneWatcher", ex); }
                Thread.Sleep(4000);
            }
            phoneWatcherRunning = false;
            connectedPhones = new List<PhoneDevice>();
        });
        t.SetApartmentState(ApartmentState.STA); // the shell objects need a single-threaded apartment
        t.IsBackground = true;
        t.Start();
    }

    void OnPhoneArrived(PhoneDevice dev)
    {
        if (!phonesEnabled) return;
        if (!IsApproved(dev) && !AskPhonePermission(dev)) return;
        StartPhoneImport(dev);
    }

    bool AskPhonePermission(PhoneDevice dev)
    {
        if (prompting) return false;
        prompting = true;
        try
        {
            var r = PhonePermissionForm.Ask(dev.Name, dev.Kind);
            if (r == DialogResult.Yes) { approvedPhones[dev.Id] = dev.Name; SaveApprovedPhones(); return true; }
            if (r == DialogResult.OK) { lock (allowedThisTime) allowedThisTime.Add(dev.Id); return true; }
            return false;
        }
        finally { prompting = false; }
    }

    void ImportNowPhones()
    {
        foreach (var d in connectedPhones)
        {
            if (!IsApproved(d) && !AskPhonePermission(d)) continue;
            StartPhoneImport(d);
            return;
        }
    }

    void StartPhoneImport(PhoneDevice dev)
    {
        lock (gate) { if (busy) return; busy = true; }
        if (!ui.Visible) { autoShown = true; ui.Open(false, TrayCenter()); }
        var t = new Thread(() => ImportPhone(dev));
        t.SetApartmentState(ApartmentState.STA);
        t.IsBackground = true;
        t.Start();
    }

    static dynamic FindDevice(dynamic shell, string id)
    {
        dynamic items = shell.NameSpace(17).Items();
        int count = items.Count;
        for (int i = 0; i < count; i++)
        {
            dynamic it = items.Item(i);
            string p = it.Path;
            if (p == id) return it.GetFolder;
        }
        return null;
    }

    // Finds every wanted file under any folder called DCIM (iPhone: Internal Storage\DCIM\100APPLE,
    // Android: Internal shared storage\DCIM\Camera).
    static void CollectPhoneFiles(dynamic folder, bool inDcim, int depth, List<PhoneFile> found)
    {
        if (depth > 6) return;
        dynamic items = folder.Items();
        int count = items.Count;
        for (int i = 0; i < count; i++)
        {
            dynamic it = items.Item(i);
            bool isFolder = it.IsFolder;
            string name = it.Name;
            if (isFolder)
            {
                bool dcim = inDcim || string.Equals(name, "DCIM", StringComparison.OrdinalIgnoreCase);
                if (dcim || depth < 2) CollectPhoneFiles(it.GetFolder, dcim, depth + 1, found); // look for DCIM near the top only
            }
            else if (inDcim)
            {
                object fnObj = it.ExtendedProperty("System.FileName");
                string fn = fnObj as string ?? name;
                if (!Wanted(Path.GetExtension(fn))) continue;
                object sz = it.ExtendedProperty("System.Size");
                object dm = it.ExtendedProperty("System.DateModified");
                if (sz == null) continue;
                found.Add(new PhoneFile { Item = it, Name = fn, Size = Convert.ToInt64(sz), Mtime = dm is DateTime ? (DateTime)dm : DateTime.Now });
            }
        }
    }

    void ImportPhone(PhoneDevice dev)
    {
        string stage = Path.Combine(Path.GetTempPath(), "CardImporter-phone");
        try
        {
            Report("Connecting to " + dev.Name, "Unlock your phone and allow access if it asks.", 0);
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application"));
            dynamic root = FindDevice(shell, dev.Id);
            if (root == null) throw new IOException("The phone was disconnected.");

            var found = new List<PhoneFile>();
            CollectPhoneFiles(root, false, 0, found);
            if (found.Count == 0)
            {
                string msg = "No photos found. Unlock the phone, choose File transfer (Android) or Trust (iPhone), then try Import now.";
                Report("Done", msg, 100);
                AddHistory(dev.Name + "  nothing found");
                Tip("Nothing found on " + dev.Name, msg);
                return;
            }

            Report("Checking library", found.Count + " files on " + dev.Name, 0);
            var lib = LibraryIndex();
            var todo = new List<PhoneFile>();
            foreach (var f in found)
            {
                DateTime utc = f.Mtime.ToUniversalTime();
                if (lib.ContainsKey(SizeTime(f.Size, utc)) || lib.ContainsKey(NameSize(f.Name, f.Size))) continue; // already in the library
                if (done.Contains(Key(f.Name, f.Size, utc))) continue;
                todo.Add(f);
            }

            int copied = 0;
            bool clean = true;
            long totalBytes = Math.Max(1, todo.Sum(f => f.Size)), soFar = 0;
            Directory.CreateDirectory(stage);
            for (int i = 0; i < todo.Count; i++)
            {
                var f = todo[i];
                long baseBytes = soFar;
                int idx = i + 1;
                Action<long> prog = b => Report("Copying " + idx + " of " + todo.Count, f.Name, 100.0 * (baseBytes + b) / totalBytes);
                try
                {
                    CopyPhoneFile(shell, f, stage, dev.Kind.StartsWith("iPhone"), prog);
                    copied++;
                }
                catch (Exception ex) { clean = false; LogError("CopyPhoneFile " + f.Name, ex); Tip("Copy failed: " + f.Name, ex.Message); }
                soFar += f.Size;
            }

            string summary = copied + " new files copied" + (clean ? "" : "; some failed") + "; nothing deleted from the phone";
            Report("Done", summary, 100);
            AddHistory(dev.Name + "  " + summary);
            Tip(copied == 0 && todo.Count == 0 ? "Nothing new on " + dev.Name : "Import complete", summary + ".");
        }
        catch (Exception ex)
        {
            LogError("ImportPhone", ex);
            Report("Failed", ex.Message, 0);
            AddHistory(dev.Name + "  failed: " + ex.Message);
            Tip("Phone import failed", ex.Message);
        }
        finally
        {
            try { if (Directory.Exists(stage)) Directory.Delete(stage, true); } catch { }
            tray.Text = "Card Importer – waiting for a card";
            lock (gate) busy = false;
            if (autoShown) { Thread.Sleep(6000); if (!busy) ui.BeginInvoke(new Action(() => { if (autoShown && ui.KeepOpen) ui.Hide(); })); }
        }
    }

    // The shell copies the file into its own temp folder, which only becomes visible to us once the copy is
    // finished. Only then is it moved into the library, so Lightroom never sees a half-written file.
    // A phone can't give us the original bytes to hash, so a copy is checked by size. (An iPhone can convert
    // HEIC to JPEG while copying, so for iPhones a steady size is accepted too.)
    void CopyPhoneFile(dynamic shell, PhoneFile f, string stage, bool iPhone, Action<long> progress)
    {
        string dir = Path.Combine(stage, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        dynamic dest = shell.NameSpace(dir);
        dest.CopyHere(f.Item, 4 | 16 | 512 | 1024); // no progress window, yes to all, no confirmations, no error dialogs

        string got = null;
        long lastLen = -1;
        int stable = 0, stalled = 0;
        while (true)
        {
            var files = Directory.GetFiles(dir);
            long len = 0;
            if (files.Length > 0) { got = files[0]; try { len = new FileInfo(got).Length; } catch { } }
            progress(Math.Min(len, f.Size));
            bool sizeMatches = len == f.Size && len > 0;
            if (len == lastLen && len > 0) stable++; else stable = 0;
            if (len == lastLen) stalled++; else stalled = 0;
            lastLen = len;
            if (sizeMatches || (iPhone && stable >= 20))   // 20 * 200 ms = 4 seconds unchanged
            {
                try { using (File.Open(got, FileMode.Open, FileAccess.Read, FileShare.None)) { } break; } // shell has let go of it
                catch (IOException) { }
            }
            if (stalled > 300) throw new IOException("The phone stopped sending " + f.Name + " (the size didn't match).");
            Thread.Sleep(200);
        }

        // move into Library\YYYY\YYYY-MM-DD, keeping the phone's modified time
        string name = Path.GetFileName(got);
        string folder = Path.Combine(Library, f.Mtime.ToString("yyyy"), f.Mtime.ToString("yyyy-MM-dd"));
        Directory.CreateDirectory(folder);
        string target = Path.Combine(folder, name);
        int n = 1;
        while (File.Exists(target)) target = Path.Combine(folder, Path.GetFileNameWithoutExtension(name) + "_" + (n++) + Path.GetExtension(name));
        File.Move(got, target);
        File.SetLastWriteTime(target, f.Mtime);
        try { Directory.Delete(dir, true); } catch { }

        string k = Key(f.Name, f.Size, f.Mtime.ToUniversalTime());
        done.Add(k);
        File.AppendAllText(logFile, k + Environment.NewLine);
    }

    // ---- updates (from GitHub releases) ----

    static string ReadSetting(string name, string fallback)
    {
        using (var k = Registry.CurrentUser.OpenSubKey(SettingsKey))
        {
            var v = k == null ? null : k.GetValue(name) as string;
            return v ?? fallback;
        }
    }

    static void WriteSetting(string name, string value)
    {
        using (var k = Registry.CurrentUser.CreateSubKey(SettingsKey)) k.SetValue(name, value);
    }

    static HttpWebRequest Request(string url, int timeoutMs)
    {
        ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; // TLS 1.2
        var req = (HttpWebRequest)WebRequest.Create(url);
        req.UserAgent = "CardImporter/" + AppVersion;
        req.Timeout = timeoutMs;
        return req;
    }

    static string HttpText(string url)
    {
        var req = Request(url, 20000);
        req.Accept = "application/vnd.github+json";
        using (var resp = req.GetResponse())
        using (var rd = new StreamReader(resp.GetResponseStream())) return rd.ReadToEnd();
    }

    static void HttpFile(string url, string path)
    {
        using (var resp = Request(url, 60000).GetResponse())
        using (var s = resp.GetResponseStream())
        using (var f = File.Create(path)) s.CopyTo(f);
    }

    static bool IsNewer(string tag)
    {
        Version latest, current;
        return Version.TryParse(tag.TrimStart('v', 'V'), out latest) && Version.TryParse(AppVersion, out current) && latest > current;
    }

    void CheckForUpdate(bool manual)
    {
        if (checking) return;
        checking = true;
        ui.SetUpdate("Checking for updates…", false);
        new Thread(() =>
        {
            try
            {
                var rel = new JavaScriptSerializer().DeserializeObject(HttpText("https://api.github.com/repos/" + Repo + "/releases/latest")) as Dictionary<string, object>;
                string tag = rel["tag_name"] as string;
                WriteSetting("LastUpdateCheck", DateTime.UtcNow.Ticks.ToString());
                if (IsNewer(tag))
                {
                    string exe = null, sums = null;
                    foreach (Dictionary<string, object> a in (System.Collections.IEnumerable)rel["assets"])
                    {
                        string n = a["name"] as string, u = a["browser_download_url"] as string;
                        if (n == "CardImporter.exe") exe = u;
                        else if (n == "SHA256SUMS.txt") sums = u;
                    }
                    if (exe != null && sums != null)
                    {
                        updTag = tag; updExeUrl = exe; updSumsUrl = sums;
                        ui.SetUpdate("Update available: " + tag, true);
                        if (!updateNotified) { updateNotified = true; Tip("Card Importer update available", tag + " is ready. Open the window and choose Install update."); }
                        return;
                    }
                }
                ui.SetUpdate("Version " + AppVersion + "  ·  up to date", false);
            }
            catch (Exception ex)
            {
                LogError("CheckForUpdate", ex);
                ui.SetUpdate(manual ? "Couldn't check for updates" : "Version " + AppVersion, false);
            }
            finally { checking = false; }
        }) { IsBackground = true }.Start();
    }

    // Downloads the new exe next to the running one, checks it against the release's SHA256SUMS.txt,
    // swaps the files (a running exe can be renamed, just not overwritten) and restarts.
    void ApplyUpdate()
    {
        if (busy) { Tip("Import in progress", "Install the update once the import has finished."); return; }
        if (updExeUrl == null || checking) return;
        checking = true;
        string tag = updTag, exeUrl = updExeUrl, sumsUrl = updSumsUrl;
        ui.SetUpdate("Downloading " + tag + "…", false);
        new Thread(() =>
        {
            string exe = Application.ExecutablePath;
            string dir = Path.GetDirectoryName(exe);
            string tmp = Path.Combine(dir, "CardImporter.update.exe");
            try
            {
                HttpFile(exeUrl, tmp);
                string want = null;
                foreach (var line in HttpText(sumsUrl).Split('\n'))
                {
                    var parts = line.Trim().Split(new[] { ' ', '*' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length == 2 && parts[1] == "CardImporter.exe") want = parts[0].ToLower();
                }
                string got;
                using (var s = File.OpenRead(tmp)) got = BitConverter.ToString(SHA256.Create().ComputeHash(s)).Replace("-", "").ToLower();
                if (want == null || got != want) throw new IOException("The download didn't match its checksum, so it was not installed.");
                if (new FileInfo(tmp).Length < 10000) throw new IOException("The download looks incomplete.");

                ui.BeginInvoke(new Action(() =>
                {
                    string old = Path.Combine(dir, "CardImporter.old.exe");
                    try
                    {
                        if (File.Exists(old)) File.Delete(old);
                        File.Move(exe, old);
                        try { File.Move(tmp, exe); }
                        catch { File.Move(old, exe); throw; } // put the old one back if the swap fails
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, "--updated") { UseShellExecute = false, WorkingDirectory = dir });
                        tray.Visible = false;
                        Application.Exit();
                    }
                    catch (Exception ex)
                    {
                        LogError("ApplyUpdate swap", ex);
                        try { File.Delete(tmp); } catch { }
                        ui.SetUpdate("Couldn't install: " + ex.Message, true);
                        checking = false;
                    }
                }));
            }
            catch (Exception ex)
            {
                LogError("ApplyUpdate", ex);
                try { File.Delete(tmp); } catch { }
                ui.SetUpdate("Update failed: " + ex.Message, true);
                checking = false;
            }
        }) { IsBackground = true }.Start();
    }

    static void LogError(string where, Exception ex)
    {
        try
        {
            string p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppName, "error.log");
            File.AppendAllText(p, DateTime.Now.ToString("s") + " " + where + ": " + ex + Environment.NewLine);
        }
        catch { }
    }

    [StructLayout(LayoutKind.Sequential)] struct NOTIFYICONIDENTIFIER { public int cbSize; public IntPtr hWnd; public uint uID; public Guid guidItem; }
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int left, top, right, bottom; }
    [DllImport("shell32.dll")] static extern int Shell_NotifyIconGetRect(ref NOTIFYICONIDENTIFIER id, out RECT rect);

    // Centre of the tray icon on screen, so the window can open above it even when nothing was clicked.
    Point? TrayCenter()
    {
        try
        {
            var t = typeof(NotifyIcon);
            var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            var wnd = (NativeWindow)t.GetField("window", flags).GetValue(tray);
            var id = new NOTIFYICONIDENTIFIER { hWnd = wnd.Handle, uID = (uint)(int)t.GetField("id", flags).GetValue(tray) };
            id.cbSize = Marshal.SizeOf(id);
            RECT r;
            if (Shell_NotifyIconGetRect(ref id, out r) == 0 && r.right > r.left)
                return new Point((r.left + r.right) / 2, (r.top + r.bottom) / 2);
        }
        catch { }
        return null;
    }

    void ChooseLibrary()
    {
        if (busy) { Tip("Import in progress", "Change the photo folder once the import has finished."); return; }
        ui.KeepOpen = true; // the window loses focus while the dialog is open
        using (var d = new FolderBrowserDialog())
        {
            d.Description = "Choose the folder photos are imported into (and that Lightroom reads from).";
            d.ShowNewFolderButton = true;
            if (Directory.Exists(Library)) d.SelectedPath = Library;
            if (d.ShowDialog() == DialogResult.OK && d.SelectedPath.Length > 0)
            {
                Library = d.SelectedPath;
                using (var k = Registry.CurrentUser.CreateSubKey(SettingsKey)) k.SetValue("LibraryPath", Library);
                WriteLibraryHint();
                ui.SetLibrary(Library);
            }
        }
        if (!ui.Visible) ui.Open(true);
    }

    // %AppData%\CardImporter\library.txt holds the current photo folder for the Lightroom plugin.
    void WriteLibraryHint()
    {
        try
        {
            string p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppName, "library.txt");
            File.WriteAllText(p, Library, new System.Text.UTF8Encoding(false));
        }
        catch { }
    }

    void Report(string phase, string detail, double pct)
    {
        ui.State(phase, detail, pct);
        string t = "Card Importer – " + phase + " " + (int)pct + "%";
        tray.Text = t.Substring(0, Math.Min(63, t.Length));
    }

    void AddHistory(string text)
    {
        string line = DateTime.Now.ToString("d MMM h:mm tt") + "  ·  " + text;
        history.Insert(0, line);
        try { File.AppendAllText(historyFile, line + Environment.NewLine); } catch { }
        ui.SetHistory(string.Join("\n", history.Take(3)));
    }

    // ---- settings ----

    static bool ReadDeleteSetting()
    {
        using (var k = Registry.CurrentUser.OpenSubKey(SettingsKey))
            return k == null || !"0".Equals(k.GetValue("DeleteAfterImport") as string);
    }

    static void WriteDeleteSetting(bool on)
    {
        using (var k = Registry.CurrentUser.CreateSubKey(SettingsKey)) k.SetValue("DeleteAfterImport", on ? "1" : "0");
    }

    static bool IsStartup()
    {
        using (var k = Registry.CurrentUser.OpenSubKey(RunKey)) return k != null && k.GetValue(AppName) != null;
    }

    static void SetStartup(bool on)
    {
        using (var k = Registry.CurrentUser.OpenSubKey(RunKey, true))
        {
            if (on) k.SetValue(AppName, "\"" + Application.ExecutablePath + "\"");
            else k.DeleteValue(AppName, false);
        }
    }

    // ---- scanning ----

    // force=true re-checks every drive, otherwise only newly appeared ones
    void Scan(bool force)
    {
        var drives = new List<string>();
        foreach (var d in DriveInfo.GetDrives())
        {
            try { if (d.IsReady && !SkipDrives.Contains(d.Name.ToUpper())) drives.Add(d.Name); } catch { }
        }
        var fresh = force ? drives : drives.Where(d => !seen.Contains(d)).ToList();
        seen.Clear();
        foreach (var d in drives) seen.Add(d);

        foreach (var d in fresh)
        {
            if (!Directory.Exists(Path.Combine(d, "DCIM"))) continue;
            lock (gate) { if (busy) return; busy = true; }
            string drive = d;
            if (!ui.Visible) { autoShown = true; ui.Open(false, TrayCenter()); } // appear without stealing focus
            new Thread(() => Import(drive)) { IsBackground = true }.Start();
            return;
        }
        if (force && phonesEnabled) ImportNowPhones();
    }

    void Import(string drive)
    {
        try
        {
            bool clean = true;
            Report("Scanning card", drive, 0);
            Thread.Sleep(2000); // let the card settle
            var files = new DirectoryInfo(Path.Combine(drive, "DCIM"))
                .EnumerateFiles("*", SearchOption.AllDirectories)
                .Where(f => Wanted(f.Extension)).ToList();

            // Skip anything already in the library (by size+time or name+size), e.g. imported by hand.
            Report("Checking library", files.Count + " files on card", 0);
            var lib = LibraryIndex();
            var todo = new List<FileInfo>();
            foreach (var f in files)
            {
                if (lib.ContainsKey(SizeTime(f)) || lib.ContainsKey(NameSize(f))) continue;
                if (done.Contains(Key(f))) continue; // copied before but since removed from the library: leave it alone
                todo.Add(f);
            }

            int n = 0;
            if (todo.Count > 0)
            {
                long total = todo.Sum(f => f.Length) * 2; // each file is read once to copy and once to verify
                long soFar = 0;
                foreach (var f in todo)
                {
                    int idx = n + 1;
                    long baseBytes = soFar;
                    Action<long> prog = b => Report("Copying " + idx + " of " + todo.Count, f.Name, 100.0 * (baseBytes + b) / total);
                    try { CopyVerified(f, prog); n++; }
                    catch (Exception ex) { clean = false; Tip("Copy failed: " + f.Name, ex.Message); }
                    soFar += f.Length * 2;
                }
            }

            string summary = n + " new files copied";
            if (deleteAfter && clean && IsRemovable(drive))
            {
                int removed, kept;
                DeleteVerified(files, out removed, out kept);
                summary += "; " + removed + " deleted from card" + (kept > 0 ? ", " + kept + " kept (not verified)" : "");
            }
            else if (deleteAfter && !clean) summary += "; card left untouched because of errors";
            Report("Done", summary, 100);
            AddHistory(drive.TrimEnd('\\') + "  " + summary);
            Tip(n == 0 && todo.Count == 0 ? "Nothing new on " + drive : "Import complete", summary + ".");
        }
        catch (Exception ex) { Report("Failed", ex.Message, 0); AddHistory(drive.TrimEnd('\\') + "  failed: " + ex.Message); Tip("Import failed", ex.Message); }
        finally
        {
            tray.Text = "Card Importer – waiting for a card";
            lock (gate) busy = false;
            if (autoShown) { Thread.Sleep(6000); if (!busy) ui.BeginInvoke(new Action(() => { if (autoShown && ui.KeepOpen) ui.Hide(); })); }
        }
    }

    static bool IsRemovable(string drive)
    {
        try { return new DriveInfo(drive).DriveType == DriveType.Removable; } catch { return false; }
    }

    // Copies into Library\YYYY\YYYY-MM-DD by capture date, hashing while reading,
    // then re-reads the copy and refuses to keep it unless the hashes match.
    void CopyVerified(FileInfo f, Action<long> progress)
    {
        string folder = Path.Combine(Library, f.LastWriteTime.ToString("yyyy"), f.LastWriteTime.ToString("yyyy-MM-dd"));
        Directory.CreateDirectory(folder);
        string dest = Path.Combine(folder, f.Name);
        int i = 1;
        while (File.Exists(dest) || File.Exists(dest + ".part"))
            dest = Path.Combine(folder, Path.GetFileNameWithoutExtension(f.Name) + "_" + (i++) + f.Extension);
        string part = dest + ".part"; // .part so a half-copied file is never mistaken for a photo

        byte[] srcHash;
        long copied = 0;
        using (var inp = File.OpenRead(f.FullName))
        using (var outp = File.Create(part))
        using (var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
        {
            var buf = new byte[4 * 1024 * 1024];
            int r;
            while ((r = inp.Read(buf, 0, buf.Length)) > 0)
            {
                outp.Write(buf, 0, r); h.AppendData(buf, 0, r);
                copied += r; progress(copied);
            }
            srcHash = h.GetHashAndReset();
        }
        if (!srcHash.SequenceEqual(Sha(part, b => progress(f.Length + b)))) { File.Delete(part); throw new IOException("Verification failed after copy"); }
        File.Move(part, dest);
        File.SetLastWriteTime(dest, f.LastWriteTime);
        done.Add(Key(f));
        File.AppendAllText(logFile, Key(f) + Environment.NewLine);
    }

    // Deletes a card file only if a file in the library has the same SHA-256.
    void DeleteVerified(List<FileInfo> files, out int removed, out int kept)
    {
        removed = 0; kept = 0;
        Report("Verifying card", "Indexing library", 0);
        var lib = LibraryIndex();
        long total = Math.Max(1, files.Sum(f => f.Length));
        long soFar = 0;
        int n = 0;
        foreach (var f in files)
        {
            n++;
            long baseBytes = soFar;
            Action<long> prog = b => Report("Verifying & deleting " + n + " of " + files.Count, f.Name, 100.0 * (baseBytes + b) / total);
            bool safe = false;
            try
            {
                var cands = new List<string>();
                List<string> p;
                if (lib.TryGetValue(NameSize(f), out p)) cands.AddRange(p);
                if (lib.TryGetValue(SizeTime(f), out p)) cands.AddRange(p);
                cands = cands.Distinct().Where(c => new FileInfo(c).Length == f.Length).ToList();
                if (cands.Count > 0)
                {
                    var h = Sha(f.FullName, prog);
                    safe = cands.Any(c => h.SequenceEqual(Sha(c, null)));
                }
                if (safe)
                {
                    File.SetAttributes(f.FullName, FileAttributes.Normal);
                    File.Delete(f.FullName);
                    removed++;
                }
            }
            catch { safe = false; }
            if (!safe) kept++;
            soFar += f.Length;
            Report("Verifying & deleting " + n + " of " + files.Count, f.Name, 100.0 * soFar / total);
        }
    }

    static byte[] Sha(string path, Action<long> progress)
    {
        using (var s = File.OpenRead(path))
        using (var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
        {
            var buf = new byte[4 * 1024 * 1024];
            long total = 0;
            int r;
            while ((r = s.Read(buf, 0, buf.Length)) > 0)
            {
                h.AppendData(buf, 0, r);
                total += r;
                if (progress != null) progress(total);
            }
            return h.GetHashAndReset();
        }
    }

    // Size + modified time (2s tolerance, as card filesystems round) survives Lightroom renaming;
    // name + size catches files whose timestamps got changed.
    static string SizeTime(long size, DateTime utc) { return "T|" + size + "|" + (utc.Ticks / 20000000L); }
    static string NameSize(string name, long size) { return "N|" + name.ToLower() + "|" + size; }
    static string SizeTime(FileInfo f) { return SizeTime(f.Length, f.LastWriteTimeUtc); }
    static string NameSize(FileInfo f) { return NameSize(f.Name, f.Length); }

    static Dictionary<string, List<string>> LibraryIndex()
    {
        var idx = new Dictionary<string, List<string>>();
        Action<string, string> add = (k, path) =>
        {
            List<string> l;
            if (!idx.TryGetValue(k, out l)) idx[k] = l = new List<string>();
            l.Add(path);
        };
        if (!Directory.Exists(Library)) return idx;
        foreach (var f in new DirectoryInfo(Library).EnumerateFiles("*", SearchOption.AllDirectories))
        {
            if (!Ext.Contains(f.Extension.ToLower()) || f.Length == 0) continue;
            add(NameSize(f), f.FullName);
            long t = f.LastWriteTimeUtc.Ticks / 20000000L;
            foreach (long d in new long[] { -1, 0, 1 }) add("T|" + f.Length + "|" + (t + d), f.FullName);
        }
        return idx;
    }

    static string Key(string name, long size, DateTime utc) { return name + "|" + size + "|" + utc.Ticks; }
    static string Key(FileInfo f) { return Key(f.Name, f.Length, f.LastWriteTimeUtc); }

    void Tip(string title, string msg) { tray.ShowBalloonTip(5000, title, msg, ToolTipIcon.Info); }
}

// Asks whether Card Importer may read photos from a phone. Returns Yes (always for this phone),
// OK (just this time) or No (don't allow).
class PhonePermissionForm : Form
{
    public static DialogResult Ask(string name, string kind)
    {
        using (var f = new PhonePermissionForm(name, kind)) return f.ShowDialog();
    }

    PhonePermissionForm(string name, string kind)
    {
        Text = "Card Importer";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false; MinimizeBox = false;
        TopMost = true;
        ClientSize = new Size(460, 250);
        BackColor = Color.FromArgb(18, 20, 26);
        ForeColor = Color.White;
        Font = new Font("Segoe UI", 9.5f);

        Controls.Add(new Label { Text = "Allow access to " + name + "?", Left = 24, Top = 20, Width = 412, Height = 28, Font = new Font("Segoe UI Semibold", 13f), ForeColor = Color.White });
        Controls.Add(new Label
        {
            Left = 24, Top = 58, Width = 412, Height = 120, ForeColor = Color.FromArgb(200, 206, 220),
            Text = "Card Importer would like to copy photos and videos from this " + kind + " into your photo library.\n\n" +
                   "It only reads the camera folders (DCIM). It never deletes or changes anything on the phone.\n\n" +
                   "You may also need to unlock the phone and choose Allow, Trust or File transfer on it."
        });

        Func<string, DialogResult, int, int, bool, Button> make = (text, r, x, w, primary) =>
        {
            var b = new Button { Text = text, DialogResult = r, Left = x, Top = 196, Width = w, Height = 34, FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand,
                                 BackColor = primary ? Color.FromArgb(38, 132, 255) : Color.FromArgb(48, 54, 68), ForeColor = Color.White };
            b.FlatAppearance.BorderSize = 0;
            Controls.Add(b);
            return b;
        };
        var always = make("Always allow", DialogResult.Yes, 24, 130, true);
        make("Just this time", DialogResult.OK, 164, 130, false);
        var no = make("Don't allow", DialogResult.No, 304, 132, false);
        AcceptButton = always;
        CancelButton = no;
    }
}

// Flyout window that opens above the tray in the bottom-right corner.
class FlyoutForm : Form
{
    static readonly Color Bg = Color.FromArgb(18, 20, 26), Card = Color.FromArgb(30, 34, 44),
        Track = Color.FromArgb(60, 66, 82), Fill = Color.FromArgb(38, 132, 255), Ok = Color.FromArgb(40, 200, 110),
        Accent = Color.FromArgb(76, 150, 255), Grey = Color.FromArgb(150, 158, 175), Line = Color.FromArgb(48, 54, 68);

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int val, int size);

    public event Action ImportNow, OpenFolder, ExitApp, ChooseLibrary, CheckUpdates, InstallUpdate, ForgetPhones;
    public event Action<bool> DeleteToggled, StartupToggled, AutoUpdateToggled, PhonesToggled;
    public event Action<string, bool> TypeToggled;
    public int LastHide;
    public bool KeepOpen;

    volatile string phase = "Waiting for a card";
    volatile string detail = "Plug in your camera or drone, or choose Import now.";
    volatile float pct;
    volatile string history = "";
    volatile string library = "";
    volatile string updateText = "";
    volatile bool canInstall;
    volatile bool pinned;
    bool noActivate;
    int tick;

    readonly Label lblPhase, lblPct, lblDetail, lblCards, lblRecent, lblLibrary, lblUpdate, lnkUpdate, lnkPin;
    readonly BarPanel bar;
    readonly CheckBox chkDelete, chkStartup, chkUpdates, chkPhones, chkRaw, chkJpeg, chkVideo;
    bool quiet; // set while the app itself changes a switch, so no event fires

    public bool DeleteAfter { set { chkDelete.Checked = value; } }
    public bool StartupOn { set { chkStartup.Checked = value; } }
    public bool AutoUpdate { set { chkUpdates.Checked = value; } }
    public bool PhonesOn { set { quiet = true; chkPhones.Checked = value; quiet = false; } }
    public void SetTypes(bool raw, bool jpeg, bool video) { chkRaw.Checked = raw; chkJpeg.Checked = jpeg; chkVideo.Checked = video; }
    public void RaiseOpenFolder() { if (OpenFolder != null) OpenFolder(); }
    public void RaiseExit() { if (ExitApp != null) ExitApp(); }

    public FlyoutForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        Size = new Size(380, 640);
        BackColor = Bg;
        DoubleBuffered = true;
        Font = new Font("Segoe UI", 9f);

        Add(Text("Card Importer", 20, 16, 200, 24, new Font("Segoe UI Semibold", 11.5f), Color.White, ContentAlignment.MiddleLeft));
        var lnkNow = Link("Import now", 20, 16, 80, 24, ContentAlignment.MiddleRight); lnkNow.Left = 190; lnkNow.Width = 100;
        lnkNow.Click += (s, e) => { if (ImportNow != null) ImportNow(); };
        lnkPin = Link("Pin", 300, 16, 60, 24, ContentAlignment.MiddleRight);
        lnkPin.Click += (s, e) => { pinned = !pinned; lnkPin.Text = pinned ? "Pinned" : "Pin"; };

        lblPhase = Text("", 20, 62, 250, 26, new Font("Segoe UI Semibold", 12.5f), Accent, ContentAlignment.MiddleLeft);
        lblPct = Text("", 270, 62, 90, 26, new Font("Segoe UI Semibold", 12.5f), Color.White, ContentAlignment.MiddleRight);
        bar = new BarPanel { Left = 20, Top = 96, Width = 340, Height = 12 };
        Controls.Add(bar);
        lblDetail = Text("", 20, 116, 340, 40, Font, Grey, ContentAlignment.TopLeft);
        lblDetail.AutoEllipsis = true;

        Add(Header("CARDS AND PHONES", 20, 164));
        lblCards = Text("Looking for cards…", 20, 184, 340, 52, Font, Color.White, ContentAlignment.TopLeft);
        lblCards.AutoEllipsis = true;

        Add(Header("RECENT", 20, 240));
        lblRecent = Text("Nothing imported yet.", 20, 260, 340, 62, Font, Grey, ContentAlignment.TopLeft);
        lblRecent.AutoEllipsis = true;

        Add(Header("IMPORT THESE FILE TYPES", 20, 336));
        chkRaw = Check("RAW", 20, 356, 90);
        chkRaw.CheckedChanged += (s, e) => { if (TypeToggled != null) TypeToggled("raw", chkRaw.Checked); };
        chkJpeg = Check("JPEG / HEIC", 120, 356, 120);
        chkJpeg.CheckedChanged += (s, e) => { if (TypeToggled != null) TypeToggled("jpeg", chkJpeg.Checked); };
        chkVideo = Check("Videos", 250, 356, 110);
        chkVideo.CheckedChanged += (s, e) => { if (TypeToggled != null) TypeToggled("video", chkVideo.Checked); };

        chkDelete = Check("Delete files from card after import", 20, 392);
        chkDelete.CheckedChanged += (s, e) => { if (DeleteToggled != null) DeleteToggled(chkDelete.Checked); };
        chkStartup = Check("Start with Windows", 20, 418);
        chkStartup.CheckedChanged += (s, e) => { if (StartupToggled != null) StartupToggled(chkStartup.Checked); };

        chkUpdates = Check("Check for updates automatically", 20, 444);
        chkUpdates.CheckedChanged += (s, e) => { if (AutoUpdateToggled != null) AutoUpdateToggled(chkUpdates.Checked); };

        chkPhones = Check("Import from phones", 20, 470, 190);
        chkPhones.CheckedChanged += (s, e) => { if (!quiet && PhonesToggled != null) PhonesToggled(chkPhones.Checked); };
        var lnkForget = Link("Forget phones", 220, 470, 140, 24, ContentAlignment.MiddleRight);
        lnkForget.Click += (s, e) => { if (ForgetPhones != null) ForgetPhones(); };

        Add(Header("PHOTO FOLDER", 20, 510));
        lblLibrary = Text("", 20, 530, 270, 22, Font, Color.White, ContentAlignment.MiddleLeft);
        lblLibrary.AutoEllipsis = true;
        var lnkChange = Link("Change…", 290, 530, 70, 22, ContentAlignment.MiddleRight);
        lnkChange.Click += (s, e) => { if (ChooseLibrary != null) ChooseLibrary(); };

        var lnkFolder = Link("Open photos folder", 20, 570, 160, 24, ContentAlignment.MiddleLeft);
        lnkFolder.Click += (s, e) => { if (OpenFolder != null) OpenFolder(); };
        var lnkExit = Link("Exit", 300, 570, 60, 24, ContentAlignment.MiddleRight);
        lnkExit.Click += (s, e) => { if (ExitApp != null) ExitApp(); };

        lblUpdate = Text("", 20, 602, 250, 24, Font, Grey, ContentAlignment.MiddleLeft);
        lblUpdate.AutoEllipsis = true;
        lnkUpdate = Link("Check now", 270, 602, 90, 24, ContentAlignment.MiddleRight);
        lnkUpdate.Click += (s, e) =>
        {
            if (canInstall) { if (InstallUpdate != null) InstallUpdate(); }
            else if (CheckUpdates != null) CheckUpdates();
        };

        var refresh = new System.Windows.Forms.Timer { Interval = 200 };
        refresh.Tick += (s, e) => Refresh1();
        refresh.Start();
    }

    // --- control helpers ---
    void Add(Control c) { Controls.Add(c); }

    Label Text(string text, int x, int y, int w, int h, Font f, Color c, ContentAlignment a)
    {
        var l = new Label { Text = text, Left = x, Top = y, Width = w, Height = h, Font = f, ForeColor = c, BackColor = Color.Transparent, TextAlign = a };
        Controls.Add(l); return l;
    }

    Label Header(string text, int x, int y) { return Text(text, x, y, 200, 18, new Font("Segoe UI Semibold", 8f), Grey, ContentAlignment.MiddleLeft); }

    Label Link(string text, int x, int y, int w, int h, ContentAlignment a)
    {
        var l = Text(text, x, y, w, h, new Font("Segoe UI", 9.5f), Accent, a);
        l.Cursor = Cursors.Hand;
        l.MouseEnter += (s, e) => l.ForeColor = Color.White;
        l.MouseLeave += (s, e) => l.ForeColor = Accent;
        return l;
    }

    CheckBox Check(string text, int x, int y, int w = 340)
    {
        var c = new ToggleBox { Text = text, Left = x, Top = y, Width = w, Height = 24, Cursor = Cursors.Hand };
        Controls.Add(c); return c;
    }

    // A checkbox drawn to suit the dark theme, so on and off are clearly different.
    class ToggleBox : CheckBox
    {
        public ToggleBox()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Bg);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var box = new Rectangle(1, (Height - 18) / 2, 18, 18);
            using (var path = new GraphicsPath())
            {
                int d = 6;
                path.AddArc(box.X, box.Y, d, d, 180, 90); path.AddArc(box.Right - d, box.Y, d, d, 270, 90);
                path.AddArc(box.Right - d, box.Bottom - d, d, d, 0, 90); path.AddArc(box.X, box.Bottom - d, d, d, 90, 90);
                path.CloseFigure();
                using (var b = new SolidBrush(Checked ? Fill : Card)) g.FillPath(b, path);
                using (var p = new Pen(Checked ? Fill : Grey)) g.DrawPath(p, path);
            }
            if (Checked)
                using (var p = new Pen(Color.White, 2f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                    g.DrawLines(p, new[] { new PointF(box.X + 4.5f, box.Y + 9.5f), new PointF(box.X + 7.5f, box.Y + 12.5f), new PointF(box.X + 13.5f, box.Y + 5.5f) });
            TextRenderer.DrawText(g, Text, Font, new Rectangle(28, 0, Width - 28, Height), Color.White, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
        }
    }

    // --- state from the worker thread ---
    public void State(string p, string d, double percent)
    {
        phase = p; detail = d; pct = (float)Math.Max(0, Math.Min(100, percent));
    }

    public void SetHistory(string text) { history = text; }
    public void SetLibrary(string path) { library = path; }
    public void SetUpdate(string text, bool install) { updateText = text; canInstall = install; }

    public string CardsOverride;

    public static void Screenshot(string path)
    {
        var f = new FlyoutForm();
        f.DeleteAfter = true;
        f.StartupOn = true;
        f.State("Copying 42 of 218", "DSC01547.ARW", 38);
        f.SetHistory("6 Oct 10:21 AM  ·  F:  218 new files copied; 218 deleted\n4 Oct 6:12 PM  ·  F:  96 new files copied; 96 deleted\n2 Oct 3:40 PM  ·  G:  41 new files copied; 41 deleted");
        f.CardsOverride = "●  F:  SONY_A7IV  ·  41.9 GB used";
        f.SetLibrary(@"C:\Users\You\Pictures\Photography\Photos");
        f.AutoUpdate = true;
        f.SetTypes(true, true, true);
        f.PhonesOn = false;
        f.SetUpdate("Version " + CardImporter.AppVersion + "  ·  up to date", false);
        f.noActivate = true;
        f.Location = new Point(-20000, -20000); // controls only paint once the form is shown, so show it off-screen
        f.Show();
        f.Refresh1(true);
        Application.DoEvents();
        using (var bmp = new Bitmap(f.Width, f.Height))
        {
            f.DrawToBitmap(bmp, new Rectangle(0, 0, f.Width, f.Height));
            bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        }
    }

    // Renders every frame of an example import (card appears, copying, verifying and deleting, done) as
    // frame_000.png ... plus frames.txt ("file delay-in-ms" per line), ready to be joined into an animation.
    public static void ScreenshotFrames(string dir)
    {
        Directory.CreateDirectory(dir);
        var f = new FlyoutForm();
        f.DeleteAfter = true; f.StartupOn = true; f.AutoUpdate = true; f.PhonesOn = false;
        f.SetTypes(true, true, true);
        f.SetLibrary(@"C:\Users\You\Pictures\Photography\Photos");
        f.SetUpdate("Version " + CardImporter.AppVersion + "  ·  up to date", false);
        string older = "6 Oct 10:21 AM  ·  F:  96 new files copied; 96 deleted\n4 Oct 6:12 PM  ·  F:  41 new files copied; 41 deleted\n2 Oct 3:40 PM  ·  G:  12 new files copied; 12 deleted";
        f.SetHistory(older);
        f.noActivate = true;
        f.Location = new Point(-20000, -20000);
        f.Show();

        var list = new List<string>();
        Action<int> snap = ms =>
        {
            f.Refresh1(true);
            Application.DoEvents();
            string name = "frame_" + list.Count.ToString("D3") + ".png";
            using (var bmp = new Bitmap(f.Width, f.Height))
            {
                f.DrawToBitmap(bmp, new Rectangle(0, 0, f.Width, f.Height));
                bmp.Save(Path.Combine(dir, name), System.Drawing.Imaging.ImageFormat.Png);
            }
            list.Add(name + " " + ms);
        };

        f.CardsOverride = "Looking for cards…";
        f.State("Waiting for a card", "Plug in your camera or drone, or choose Import now.", 0);
        snap(1400);

        f.CardsOverride = "●  F:  SONY_A7IV  ·  41.9 GB used";
        f.State("Scanning card", "F:\\", 0);
        snap(900);

        for (int p = 0; p <= 100; p += 5)
        {
            int k = Math.Max(1, (int)Math.Round(218 * p / 100.0));
            f.State("Copying " + k + " of 218", "DSC" + (1500 + k).ToString("D5") + ".ARW", p);
            snap(p == 100 ? 350 : 110);
        }
        for (int p = 0; p <= 100; p += 10)
        {
            int k = Math.Max(1, (int)Math.Round(218 * p / 100.0));
            f.State("Verifying & deleting " + k + " of 218", "DSC" + (1500 + k).ToString("D5") + ".ARW", p);
            snap(p == 100 ? 350 : 110);
        }

        f.SetHistory("10 Oct 12:44 PM  ·  F:  218 new files copied; 218 deleted\n" + string.Join("\n", older.Split('\n').Take(2)));
        f.State("Done", "218 new files copied; 218 deleted from card", 100);
        snap(3000);

        File.WriteAllLines(Path.Combine(dir, "frames.txt"), list);
    }

    public void Refresh1(bool force = false)
    {
        if (!Visible && !force) return;
        lblPhase.Text = phase;
        bool idle = phase == "Waiting for a card";
        lblPct.Text = idle ? "" : ((int)pct) + "%";
        lblDetail.Text = detail;
        bar.Value = idle ? 0 : pct;
        bar.Done = phase == "Done";
        lblPhase.ForeColor = phase == "Done" ? Ok : Accent;
        string h = history;
        lblRecent.Text = h.Length == 0 ? "Nothing imported yet." : h;
        lblLibrary.Text = library;
        lblUpdate.Text = updateText;
        lblUpdate.ForeColor = canInstall ? Accent : Grey;
        lnkUpdate.Text = canInstall ? "Install update" : "Check now";
        if (CardsOverride != null) lblCards.Text = CardsOverride;
        else if (tick++ % 10 == 0) lblCards.Text = Cards();
        bar.Invalidate();
    }

    static string Cards()
    {
        var lines = new List<string>();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (!d.IsReady || CardImporter.SkipDrives.Contains(d.Name.ToUpper())) continue;
                if (!Directory.Exists(Path.Combine(d.Name, "DCIM"))) continue;
                lines.Add("●  " + d.Name.TrimEnd('\\') + "  " + d.VolumeLabel + "  ·  " + Math.Round((d.TotalSize - d.TotalFreeSpace) / 1073741824.0, 1) + " GB used");
            }
            catch { }
        }
        string phones = CardImporter.PhoneLines;
        if (phones.Length > 0) lines.AddRange(phones.Split('\n'));
        return lines.Count == 0 ? "Looking for cards…" : string.Join("\n", lines);
    }

    // --- showing / hiding ---
    Point? lastAnchor;

    // `anchor` is where the tray icon is: the window opens centred above it, like the other tray flyouts.
    // With no anchor the last one is reused, and failing that the window sits in the bottom-right corner.
    public void Open(bool activate, Point? anchor = null)
    {
        noActivate = !activate;
        KeepOpen = !activate;
        if (anchor.HasValue) lastAnchor = anchor;
        var wa = (lastAnchor.HasValue ? Screen.FromPoint(lastAnchor.Value) : Screen.PrimaryScreen).WorkingArea;
        int x = lastAnchor.HasValue ? lastAnchor.Value.X - Width / 2 : wa.Right - Width - 12;
        x = Math.Max(wa.Left + 8, Math.Min(x, wa.Right - Width - 8));   // keep it fully on screen
        Location = new Point(x, wa.Bottom - Height - 12);
        tick = 0;
        shownAt = Environment.TickCount;
        Show();
        if (activate) { Activate(); SetForegroundWindow(Handle); }
    }

    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hwnd);
    int shownAt;

    protected override bool ShowWithoutActivation { get { return noActivate; } }

    protected override CreateParams CreateParams
    {
        get { var cp = base.CreateParams; cp.ExStyle |= 0x80; cp.ClassStyle |= 0x20000; return cp; } // TOOLWINDOW + drop shadow
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int round = 2; DwmSetWindowAttribute(Handle, 33, ref round, 4); // rounded corners on Windows 11
    }

    protected override void OnActivated(EventArgs e) { base.OnActivated(e); KeepOpen = false; }

    // Clicking the tray icon makes the taskbar grab focus a split second after we open, so a
    // deactivation right after opening is ignored; we re-check once things have settled.
    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        if (pinned || KeepOpen) return;
        if (Environment.TickCount - shownAt < 700)
        {
            var t = new System.Windows.Forms.Timer { Interval = 800 };
            t.Tick += (s, a) => { t.Stop(); t.Dispose(); if (Visible && !pinned && !KeepOpen && ActiveForm != this) HideFlyout(); };
            t.Start();
            return;
        }
        HideFlyout();
    }

    void HideFlyout() { Hide(); LastHide = Environment.TickCount; }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using (var pen = new Pen(Line)) e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        using (var pen = new Pen(Line)) e.Graphics.DrawLine(pen, 20, 328, Width - 20, 328);
        using (var pen = new Pen(Line)) e.Graphics.DrawLine(pen, 20, 384, Width - 20, 384);
        using (var pen = new Pen(Line)) e.Graphics.DrawLine(pen, 20, 502, Width - 20, 502);
        using (var pen = new Pen(Line)) e.Graphics.DrawLine(pen, 20, 562, Width - 20, 562);
    }

    class BarPanel : Panel
    {
        public float Value;
        public bool Done;
        public BarPanel() { DoubleBuffered = true; BackColor = Color.Transparent; }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            using (var path = Round(r, 6))
            {
                using (var b = new SolidBrush(Track)) g.FillPath(b, path);
                int w = (int)(r.Width * Value / 100f);
                if (w > 0)
                {
                    g.SetClip(path);
                    using (var b = new SolidBrush(Done ? Ok : Fill)) g.FillRectangle(b, 0, 0, w, Height);
                    g.ResetClip();
                }
            }
        }
        static GraphicsPath Round(Rectangle r, int radius)
        {
            var p = new GraphicsPath(); int d = radius * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure(); return p;
        }
    }
}
