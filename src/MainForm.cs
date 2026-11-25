using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Namp
{
    class MainForm : Form
    {
        // ---- shell ----
        readonly Dictionary<string, Control> pages = new Dictionary<string, Control>();
        readonly Dictionary<string, NavItem> navs = new Dictionary<string, NavItem>();
        Panel content;
        string currentPage = "overview";
        FlatButton toggleAll;
        Label busyLabel;
        Pill sideStatus;

        // ---- overview ----
        class ServiceCard { public Pill Status; public Label Sub, Detail; public FlatButton Toggle, Open, Logs; }
        readonly ServiceCard web = new ServiceCard(), db = new ServiceCard();
        RichTextBox activity;

        // ---- sites ----
        TextBox siteName, siteFolder;
        ListView siteList;

        // ---- databases ----
        TextBox dbName;
        ListView dbList;
        Label dbCount, dbFoot;

        // ---- logs ----
        class LogSource { public string Name; public string Path; public override string ToString() { return Name; } }
        ComboBox logSource;
        TextBox logSearch;
        Label logPath, logCount;
        RichTextBox logBox;
        long lastLogSize = -1;
        string lastLogPath;
        List<string> logLines = new List<string>();

        // ---- settings ----
        NumericUpDown httpPort, httpsPort, dbPort, backupEvery, backupKeep;
        CheckBox chkHttps, chkAutoStart, chkAutoRestart, chkLan, chkSnapshot;
        ComboBox phpVersion;
        Label aboutLabel;

        Timer timer;
        volatile bool refreshing;
        bool closing, reallyClose, prepared, apacheUp, dbUp;
        volatile bool httpsTrusted;

        public MainForm()
        {
            using (var g = Graphics.FromHwnd(IntPtr.Zero)) Ui.Scale = g.DpiX / 96f;
            SuspendLayout();
            AutoScaleMode = AutoScaleMode.None;
            Text = "NAMP";
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            BackColor = Theme.Page;
            ForeColor = Theme.Text;
            Font = Theme.Base;
            ClientSize = new Size(Ui.S(1220), Ui.S(800));
            MinimumSize = new Size(Ui.S(1060), Ui.S(700));
            StartPosition = FormStartPosition.CenterScreen;

            BuildShell();
            pages["overview"] = BuildOverview();
            pages["sites"] = BuildSites();
            pages["databases"] = BuildDatabases();
            pages["logs"] = BuildLogs();
            pages["settings"] = BuildSettings();
            foreach (var p in pages.Values) { p.Visible = false; content.Controls.Add(p); }
            ShowPage("overview");
            ResumeLayout(false);
            PerformLayout();

            Stack.Confirm = ConfirmFromWorker;
            Stack.BusyChanged += () => SafeInvoke(UpdateBusy);
        }

        // ==================================================================
        // Shell
        // ==================================================================
        void BuildShell()
        {
            content = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Page, Padding = Ui.P(12, 22, 26, 20) };

            var sidebar = new Panel { Dock = DockStyle.Left, Width = Ui.S(240), BackColor = Theme.Page, Padding = Ui.P(18, 20, 12, 18) };
            var side = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, BackColor = Theme.Page };
            side.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            var brand = new Panel { Width = Ui.S(200), Height = Ui.S(48), BackColor = Theme.Page, Margin = Ui.P(0, 0, 0, 22) };
            var logo = new PictureBox { Size = new Size(Ui.S(42), Ui.S(42)), Location = new Point(0, Ui.S(2)), SizeMode = PictureBoxSizeMode.Zoom, BackColor = Theme.Page };
            try
            {
                var stream = typeof(MainForm).Assembly.GetManifestResourceStream("Namp.logo.png");
                if (stream != null) logo.Image = Image.FromStream(stream);
            }
            catch { }
            var name = new Label { Text = "NAMP", Font = Theme.Brand, AutoSize = true, Location = new Point(Ui.S(52), Ui.S(2)), ForeColor = Theme.Text, BackColor = Theme.Page };
            var tag = new Label { Text = "by Lvato", Font = Theme.Small, AutoSize = true, Location = new Point(Ui.S(53), Ui.S(26)), ForeColor = Theme.Muted, BackColor = Theme.Page };
            brand.Controls.AddRange(new Control[] { logo, name, tag });

            toggleAll = new FlatButton("Start all", BtnKind.Primary, Glyph.Play);
            toggleAll.FixedWidth = Ui.S(200);
            toggleAll.Width = Ui.S(200);
            toggleAll.Margin = Ui.P(0, 0, 0, 6);
            toggleAll.Click += (s, e) =>
            {
                if (apacheUp || dbUp) Stack.RunOp("stop all", () => Stack.StopAll(true));
                else Stack.RunOp("start all", () => Stack.StartAll());
            };
            busyLabel = new Label { Text = "", Font = Theme.Small, ForeColor = Theme.Accent, AutoSize = true, Margin = Ui.P(2, 0, 0, 14), BackColor = Theme.Page };

            var nav = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, BackColor = Theme.Page, Margin = new Padding(0) };
            foreach (var item in new[] {
                new[] { "overview", Glyph.Home, "Overview" }, new[] { "sites", Glyph.Globe, "Sites" },
                new[] { "databases", Glyph.Database, "Databases" }, new[] { "logs", Glyph.Logs, "Logs" },
                new[] { "settings", Glyph.Settings, "Settings" } })
            {
                var n = new NavItem(item[1], item[2]);
                string key = item[0];
                n.Click += (s, e) => ShowPage(key);
                navs[key] = n;
                nav.Controls.Add(n);
            }

            sideStatus = new Pill { Margin = Ui.P(0, 0, 0, 8) };
            var ver = new Label
            {
                Text = "NAMP " + Versions.Namp, Font = Theme.Small, ForeColor = Theme.Faint, AutoSize = true, BackColor = Theme.Page, Margin = new Padding(0)
            };

            var rows = new Control[] { brand, toggleAll, busyLabel, nav, new Panel { BackColor = Theme.Page }, sideStatus, ver };
            side.RowCount = rows.Length;
            for (int i = 0; i < rows.Length; i++)
            {
                side.RowStyles.Add(i == 4 ? new RowStyle(SizeType.Percent, 100) : new RowStyle(SizeType.AutoSize));
                side.Controls.Add(rows[i], 0, i);
            }
            sidebar.Controls.Add(side);

            Controls.Add(content);
            Controls.Add(sidebar);
        }

        internal int ConsoleChars { get { return activity.TextLength; } }
        internal string ConsoleText { get { return activity.Text; } }

        /// <summary>Marketing screenshots: show what a real install looks like (default ports, C:\NAMP, trusted HTTPS).</summary>
        internal static bool Demo;

        internal void DemoActivity(string[][] lines)
        {
            activity.Clear();
            foreach (var l in lines)
                AppendActivity(new LogEntry { Time = DateTime.Today.Add(TimeSpan.Parse(l[0])), Message = l[2],
                    Level = l[1] == "ok" ? LogLevel.Ok : l[1] == "warn" ? LogLevel.Warn : LogLevel.Info });
        }

        internal void ShowPage(string key)
        {
            currentPage = key;
            foreach (var kv in pages) kv.Value.Visible = kv.Key == key;
            foreach (var kv in navs) kv.Value.Active = kv.Key == key;
            if (key == "sites") RefreshSites();
            if (key == "databases") RefreshDatabases();
            if (key == "logs") { PopulateLogSources(null); LoadLog(true); }
            if (key == "settings") LoadSettingsIntoForm();
        }

        // ==================================================================
        // Overview
        // ==================================================================
        Control BuildOverview()
        {
            var webCard = ServiceCardView(web, Glyph.Globe, Theme.AccentSoft, Theme.Accent, "Web server", true);
            var dbCard = ServiceCardView(db, Glyph.Database, Theme.BlueSoft, Theme.Blue, "Database", false);
            var services = Ui.Columns(14, webCard, dbCard);

            var tiles = Ui.Columns(14,
                MakeTile(Glyph.Open, "Open localhost", "Your default website", () => OpenAdmin("web")),
                MakeTile(Glyph.Database, "phpMyAdmin", "Manage your databases", () => OpenAdmin("db")),
                MakeTile(Glyph.Add, "Add a site", "Create a .local domain", () => { ShowPage("sites"); siteName.Focus(); }),
                MakeTile(Glyph.Folder, "www folder", Demo ? @"C:\NAMP\www" : Paths.Www ?? "", () => Util.OpenPath(Paths.Www)));

            var card = new RoundPanel { Tag = "fill", Margin = new Padding(0), Padding = Ui.P(20, 16, 8, 12) };
            var inner = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, BackColor = Color.White };
            inner.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            inner.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var head = Ui.Lbl("Activity", Theme.H2, Theme.Text);
            head.Margin = Ui.P(0, 0, 0, 8);
            inner.Controls.Add(head, 0, 0);
            activity = new RichTextBox { Dock = DockStyle.Fill, ReadOnly = true, BorderStyle = BorderStyle.None, BackColor = Color.White, Font = Theme.Mono, DetectUrls = false, HideSelection = false };
            inner.Controls.Add(activity, 0, 1);
            card.Controls.Add(inner);

            return Ui.Stack(new Padding(0), Ui.PageTitle("Overview", "Apache, PHP and MariaDB on this PC - protected against power cuts"), services, tiles, card);
        }

        Control MakeTile(string glyph, string title, string caption, Action click)
        {
            var t = new Tile(glyph, title, caption);
            t.Click += (s, e) => click();
            return t;
        }

        Control ServiceCardView(ServiceCard sc, string glyph, Color soft, Color strong, string title, bool isWeb)
        {
            var card = new RoundPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(0) };
            var grid = new TableLayoutPanel { ColumnCount = 2, Dock = DockStyle.Top, AutoSize = true, BackColor = Color.White };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            var titles = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, BackColor = Color.White, Margin = new Padding(0) };
            var t = Ui.Lbl(title, Theme.H2, Theme.Text);
            t.Margin = new Padding(0);
            sc.Sub = Ui.Lbl("", Theme.Small, Theme.Muted);
            sc.Sub.Margin = new Padding(0);
            titles.Controls.Add(t);
            titles.Controls.Add(sc.Sub);
            var head = Ui.Row(new IconBadge(glyph, soft, strong), titles);
            grid.Controls.Add(head, 0, 0);
            sc.Status = new Pill { Anchor = AnchorStyles.Top | AnchorStyles.Right };
            grid.Controls.Add(sc.Status, 1, 0);

            sc.Detail = Ui.Lbl("", Theme.Base, Theme.Muted);
            sc.Detail.Margin = Ui.P(0, 16, 0, 16);
            grid.Controls.Add(sc.Detail, 0, 1);
            grid.SetColumnSpan(sc.Detail, 2);

            sc.Toggle = new FlatButton("Start", BtnKind.Primary, Glyph.Play);
            sc.Toggle.Click += (s, e) => ToggleService(isWeb);
            sc.Logs = new FlatButton("Logs", BtnKind.Soft, Glyph.Logs);
            sc.Logs.Click += (s, e) => OpenLogFor(isWeb ? Paths.ApacheErrorLog : Paths.MariaErrorLog);
            sc.Open = new FlatButton(isWeb ? "Open" : "phpMyAdmin", BtnKind.Soft, Glyph.Open);
            sc.Open.Click += (s, e) => OpenAdmin(isWeb ? "web" : "db");
            var buttons = Ui.Row(sc.Toggle, sc.Open, sc.Logs);
            grid.Controls.Add(buttons, 0, 2);
            grid.SetColumnSpan(buttons, 2);
            card.Controls.Add(grid);
            return card;
        }

        void ToggleService(bool isWeb)
        {
            if (isWeb)
            {
                if (apacheUp) Stack.RunOp("stop Apache", Stack.StopApache);
                else Stack.RunOp("start Apache", () => Stack.StartApache());
            }
            else
            {
                if (dbUp) Stack.RunOp("stop MariaDB", () => Stack.StopDb(true));
                else Stack.RunOp("start MariaDB", () => Stack.StartDb());
            }
        }

        void OpenAdmin(string what)
        {
            var s = Settings.Current;
            string baseUrl = Configs.HttpsActive ? "https://localhost" + (s.HttpsPort == 443 ? "" : ":" + s.HttpsPort) : s.BaseUrl;
            string url = baseUrl + (what == "db" ? "/phpmyadmin/" : "/");
            if (Stack.Apache.IsRunning && (what != "db" || Stack.Db.IsRunning)) { Util.OpenUrl(url); return; }
            Stack.RunOp("open " + what, () =>
            {
                if (what == "db" && !Stack.Db.IsRunning && !Stack.StartDb()) return;
                if (!Stack.Apache.IsRunning && !Stack.StartApache()) return;
                Util.OpenUrl((Configs.HttpsActive ? "https://localhost" + (s.HttpsPort == 443 ? "" : ":" + s.HttpsPort) : s.BaseUrl) + (what == "db" ? "/phpmyadmin/" : "/"));
            });
        }

        // ==================================================================
        // Sites
        // ==================================================================
        Control BuildSites()
        {
            var card = new RoundPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
            var grid = new TableLayoutPanel { ColumnCount = 4, Dock = DockStyle.Top, AutoSize = true, BackColor = Color.White };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 38));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 62));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var h = Ui.Lbl("Add a site", Theme.H2, Theme.Text);
            var d = Ui.Lbl("Type a name like myshop - it becomes https://myshop.local. Leave the folder empty to create one in www.", Theme.Base, Theme.Muted);
            d.Margin = Ui.P(0, 0, 0, 14);
            grid.Controls.Add(h, 0, 0); grid.SetColumnSpan(h, 4);
            grid.Controls.Add(d, 0, 1); grid.SetColumnSpan(d, 4);
            var nameBox = Ui.Input("myshop.local", out siteName);
            siteName.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; AddSite(); } };
            var folderBox = Ui.Input(@"Project folder (optional), e.g. D:\Projects\myshop", out siteFolder);
            nameBox.Dock = DockStyle.Fill;
            folderBox.Dock = DockStyle.Fill;
            var browse = new FlatButton("Browse", BtnKind.Soft, Glyph.Folder);
            browse.Click += (s, e) => { var f = PickFolder(siteFolder.Text); if (f != null) siteFolder.Text = f; };
            var add = new FlatButton("Add site", BtnKind.Primary, Glyph.Add);
            add.Margin = new Padding(0);
            add.Click += (s, e) => AddSite();
            grid.Controls.Add(nameBox, 0, 2);
            grid.Controls.Add(folderBox, 1, 2);
            grid.Controls.Add(browse, 2, 2);
            grid.Controls.Add(add, 3, 2);
            card.Controls.Add(grid);

            var listCard = new RoundPanel { Tag = "fill", Padding = Ui.P(8, 8, 8, 8), Margin = Ui.P(0, 0, 0, 12) };
            siteList = Ui.List("Site|230", "Address|280", "Project folder|300");
            siteList.DoubleClick += (s, e) => { var x = SelectedSite(true); if (x != null) OpenSite(x); };
            listCard.Controls.Add(siteList);

            var open = new FlatButton("Open", BtnKind.Dark, Glyph.Open);
            open.Click += (s, e) => { var x = SelectedSite(); if (x != null) OpenSite(x); };
            var folder = new FlatButton("Folder", BtnKind.Soft, Glyph.Folder);
            folder.Click += (s, e) => { var x = SelectedSite(); if (x != null) Util.OpenPath(x.Root); };
            var logs = new FlatButton("Logs", BtnKind.Soft, Glyph.Logs);
            logs.Click += (s, e) => { var x = SelectedSite(); if (x != null) OpenLogFor(SiteLog(x.Domain, "error")); };
            var remove = new FlatButton("Remove", BtnKind.Danger, Glyph.Delete);
            remove.Click += (s, e) => RemoveSite();
            return Ui.Stack(new Padding(0), Ui.PageTitle("Sites", "Local domains for your projects - each one gets HTTP, HTTPS and its own logs"),
                card, listCard, Ui.Row(open, folder, logs, remove));
        }

        static string SiteLog(string domain, string kind) { return Path.Combine(Paths.SiteLogs, domain + "-" + kind + ".log"); }

        DomainEntry SelectedSite(bool quiet = false)
        {
            if (siteList.SelectedItems.Count == 0 || !(siteList.SelectedItems[0].Tag is DomainEntry))
            {
                if (!quiet) MessageBox.Show(this, "Select a site in the list first.", "NAMP", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return null;
            }
            return (DomainEntry)siteList.SelectedItems[0].Tag;
        }

        void AddSite()
        {
            string domain = siteName.Text, root = siteFolder.Text;
            Stack.RunOp("add site", () =>
            {
                string err = Stack.AddDomain(domain, root);
                SafeInvoke(() =>
                {
                    if (err != null) { MessageBox.Show(this, err, "Add site", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
                    siteName.Text = "";
                    siteFolder.Text = "";
                    RefreshSites();
                });
                if (err == null && !Stack.Apache.IsRunning) Stack.StartApache();
            });
        }

        void OpenSite(DomainEntry d)
        {
            if (Stack.Apache.IsRunning) { Util.OpenUrl(Settings.Current.UrlFor(d.Domain)); return; }
            Stack.RunOp("open site", () => { if (Stack.StartApache()) Util.OpenUrl(Settings.Current.UrlFor(d.Domain)); });
        }

        void RemoveSite()
        {
            var d = SelectedSite();
            if (d == null) return;
            if (MessageBox.Show(this, "Remove " + d.Domain + "?\n\nYour project folder and its files are not deleted.", "Remove site",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            string domain = d.Domain;
            Stack.RunOp("remove site", () => { Stack.RemoveDomain(domain); SafeInvoke(RefreshSites); });
        }

        void RefreshSites()
        {
            if (siteList == null) return;
            siteList.BeginUpdate();
            siteList.Items.Clear();
            foreach (var d in Settings.Current.Domains)
            {
                var item = new ListViewItem(d.Domain) { Tag = d, Font = Theme.Semi };
                item.UseItemStyleForSubItems = false;
                var url = item.SubItems.Add(Settings.Current.UrlFor(d.Domain));
                url.ForeColor = Theme.Accent;
                string shownRoot = Demo ? @"D:\Projects\" + d.Domain.Split('.')[0] : d.Root;
                var root = item.SubItems.Add(Directory.Exists(d.Root) ? shownRoot : d.Root + "   (folder missing)");
                root.ForeColor = Directory.Exists(d.Root) ? Theme.Muted : Theme.Red;
                siteList.Items.Add(item);
            }
            if (siteList.Items.Count == 0)
                siteList.Items.Add(new ListViewItem("No sites yet - add one above.") { ForeColor = Theme.Muted });
            siteList.EndUpdate();
        }

        string PickFolder(string start)
        {
            using (var dlg = new FolderBrowserDialog())
            {
                dlg.Description = "Choose the project folder";
                dlg.ShowNewFolderButton = true;
                dlg.SelectedPath = !string.IsNullOrEmpty(start) && Directory.Exists(start) ? start : Paths.Www;
                return dlg.ShowDialog(this) == DialogResult.OK ? dlg.SelectedPath : null;
            }
        }

        // ==================================================================
        // Databases
        // ==================================================================
        Control BuildDatabases()
        {
            var card = new RoundPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
            var inner = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Dock = DockStyle.Top, BackColor = Color.White };
            inner.Controls.Add(Ui.Lbl("New database", Theme.H2, Theme.Text));
            var hint = Ui.Lbl("Connect from PHP with host 127.0.0.1, user root and an empty password.", Theme.Base, Theme.Muted);
            hint.Margin = Ui.P(0, 0, 0, 14);
            inner.Controls.Add(hint);
            var nameBox = Ui.Input("my_database", out dbName);
            nameBox.Width = Ui.S(320);
            dbName.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; CreateDatabase(); } };
            var create = new FlatButton("Create", BtnKind.Primary, Glyph.Add);
            create.Click += (s, e) => CreateDatabase();
            var pma = new FlatButton("Open phpMyAdmin", BtnKind.Soft, Glyph.Open);
            pma.Click += (s, e) => OpenAdmin("db");
            inner.Controls.Add(Ui.Row(nameBox, create, pma));
            card.Controls.Add(inner);

            var listCard = new RoundPanel { Tag = "fill", Padding = Ui.P(8, 8, 8, 8), Margin = Ui.P(0, 0, 0, 12) };
            dbList = Ui.List("Database|300", "Size|130", "Tables|100", "Last backup|200");
            dbList.DoubleClick += (s, e) => OpenDbInPma();
            listCard.Controls.Add(dbList);

            var open = new FlatButton("phpMyAdmin", BtnKind.Dark, Glyph.Open);
            open.Click += (s, e) => OpenDbInPma();
            var backup = new FlatButton("Back up now", BtnKind.Soft, Glyph.Backup);
            backup.Click += (s, e) => BackupSelected();
            var restore = new FlatButton("Restore...", BtnKind.Soft, Glyph.Restore);
            restore.Click += (s, e) => RestoreSelected();
            dbCount = Ui.Lbl("", Theme.Base, Theme.Muted);
            dbCount.Margin = Ui.P(8, 8, 0, 0);
            var actions = Ui.Row(open, backup, restore, dbCount);
            actions.Margin = Ui.P(0, 0, 0, 10);
            dbFoot = Ui.Lbl("", Theme.Small, Theme.Muted);
            return Ui.Stack(new Padding(0), Ui.PageTitle("Databases", "MariaDB " + Versions.MariaDb + " - every change is written safely to disk"),
                card, listCard, actions, dbFoot);
        }

        string SelectedDb(bool quiet = false)
        {
            if (dbList.SelectedItems.Count == 0 || !(dbList.SelectedItems[0].Tag is string))
            {
                if (!quiet) MessageBox.Show(this, "Select a database in the list first.", "NAMP", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return null;
            }
            return (string)dbList.SelectedItems[0].Tag;
        }

        void CreateDatabase()
        {
            string name = dbName.Text.Trim();
            if (!System.Text.RegularExpressions.Regex.IsMatch(name, "^[A-Za-z0-9_$-]{1,64}$"))
            {
                MessageBox.Show(this, "Use letters, digits, underscore, $ or hyphen (max 64 characters).", "New database", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            Stack.RunOp("create database", () =>
            {
                if (!Stack.Db.IsRunning && !Stack.StartDb()) return;
                string err;
                if (Stack.Db.Exec("CREATE DATABASE `" + name + "` CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci", out err))
                {
                    Log.Ok("mariadb", "Created database " + name);
                    SafeInvoke(() => { dbName.Text = ""; RefreshDatabases(); });
                }
                else SafeInvoke(() => MessageBox.Show(this, err, "New database", MessageBoxButtons.OK, MessageBoxIcon.Warning));
            });
        }

        void OpenDbInPma()
        {
            string name = SelectedDb();
            if (name == null) return;
            var s = Settings.Current;
            string baseUrl = Configs.HttpsActive ? "https://localhost" + (s.HttpsPort == 443 ? "" : ":" + s.HttpsPort) : s.BaseUrl;
            string url = baseUrl + "/phpmyadmin/index.php?route=/database/structure&db=" + Uri.EscapeDataString(name);
            if (Stack.Apache.IsRunning) Util.OpenUrl(url);
            else Stack.RunOp("open phpMyAdmin", () => { if (Stack.StartApache()) Util.OpenUrl(url); });
        }

        void BackupSelected()
        {
            string name = SelectedDb();
            if (name == null) return;
            Stack.RunOp("backup", () =>
            {
                var f = Backups.BackupDatabase(Stack.Db, name, true);
                if (f != null) Log.Ok("backup", name + " saved to " + f);
                SafeInvoke(RefreshDatabases);
            });
        }

        void RestoreSelected()
        {
            string name = SelectedDb();
            if (name == null) return;
            var files = Backups.ListBackups(name);
            if (files.Count == 0) { MessageBox.Show(this, "There are no backups of " + name + " yet.", "Restore", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            using (var dlg = new Form())
            {
                dlg.Text = "Restore " + name;
                dlg.AutoScaleMode = AutoScaleMode.None;
                dlg.Font = Theme.Base;
                dlg.BackColor = Theme.Page;
                dlg.ClientSize = new Size(Ui.S(560), Ui.S(380));
                dlg.StartPosition = FormStartPosition.CenterParent;
                dlg.FormBorderStyle = FormBorderStyle.FixedDialog;
                dlg.MinimizeBox = dlg.MaximizeBox = false;
                var info = new Label { Text = "Choose the version to restore. The current state of " + name + " is backed up first, so this can be undone.", Dock = DockStyle.Top, Height = Ui.S(52), Padding = Ui.P(14, 12, 14, 0), ForeColor = Theme.Muted };
                var list = new ListBox { Dock = DockStyle.Fill, Font = Theme.Mono, BorderStyle = BorderStyle.FixedSingle, IntegralHeight = false };
                foreach (var f in files) list.Items.Add(f.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss") + "    " + Util.FormatBytes(f.Length).PadLeft(10) + "    " + f.Name);
                list.SelectedIndex = 0;
                var ok = new Button { Text = "Restore", DialogResult = DialogResult.OK, AutoSize = true };
                var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
                var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = Ui.S(50), Padding = Ui.P(10) };
                bar.Controls.Add(cancel);
                bar.Controls.Add(ok);
                var pad = new Panel { Dock = DockStyle.Fill, Padding = Ui.P(14, 0, 14, 0) };
                pad.Controls.Add(list);
                dlg.Controls.Add(pad);
                dlg.Controls.Add(bar);
                dlg.Controls.Add(info);
                dlg.AcceptButton = ok;
                dlg.CancelButton = cancel;
                if (dlg.ShowDialog(this) != DialogResult.OK || list.SelectedIndex < 0) return;
                var file = files[list.SelectedIndex].FullName;
                Stack.RunOp("restore", () =>
                {
                    Log.Info("backup", "Saving the current state of " + name + " before restoring...");
                    Backups.BackupDatabase(Stack.Db, name, true);
                    Log.Info("backup", "Restoring " + name + " from " + Path.GetFileName(file) + "...");
                    Backups.Import(Stack.Db, file, null);
                    SafeInvoke(RefreshDatabases);
                });
            }
        }

        void RefreshDatabases()
        {
            if (dbList == null || !prepared) return;
            dbCount.Text = "Loading...";
            Task.Run(() =>
            {
                string err = null;
                List<MariaDb.DbInfo> dbs = null;
                bool running = Stack.Db.IsRunning;
                if (running) dbs = Stack.Db.ListDatabases(false, out err);
                SafeInvoke(() =>
                {
                    string keep = SelectedDb(true);
                    dbList.BeginUpdate();
                    dbList.Items.Clear();
                    if (!running) dbCount.Text = "The database is stopped - start it from Overview.";
                    else if (err != null) dbCount.Text = "Error: " + err;
                    else dbCount.Text = dbs.Count == 1 ? "1 database" : dbs.Count + " databases";
                    if (dbs != null)
                    {
                        foreach (var d in dbs)
                        {
                            var backups = Backups.ListBackups(d.Name);
                            var item = new ListViewItem(d.Name) { Tag = d.Name, Font = Theme.Semi };
                            item.SubItems.Add(Util.FormatBytes(d.Size));
                            item.SubItems.Add(d.Tables.ToString());
                            item.SubItems.Add(backups.Count > 0 ? Util.Ago(backups[0].LastWriteTime) : "not yet");
                            if (d.Name == keep) item.Selected = true;
                            dbList.Items.Add(item);
                        }
                    }
                    dbList.EndUpdate();
                    UpdateProtection();
                });
            });
        }

        // ==================================================================
        // Logs
        // ==================================================================
        Control BuildLogs()
        {
            var bar = new RoundPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = Ui.P(14, 12, 14, 10) };
            var inner = new TableLayoutPanel { ColumnCount = 2, Dock = DockStyle.Top, AutoSize = true, BackColor = Color.White };
            inner.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            inner.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            logSource = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = Ui.S(300), Font = Theme.Base, Margin = Ui.P(0, 4, 10, 0), MaxDropDownItems = 25, FlatStyle = FlatStyle.Flat };
            logSource.SelectedIndexChanged += (s, e) => LoadLog(true);
            var search = Ui.Input("Search...", out logSearch);
            search.Width = Ui.S(240);
            logSearch.TextChanged += (s, e) => RenderLog();
            var clear = new FlatButton("Clear", BtnKind.Soft, Glyph.Delete);
            clear.Click += (s, e) => ClearLog();
            var open = new FlatButton("Open file", BtnKind.Soft, Glyph.Doc);
            open.Click += (s, e) => { var src = logSource.SelectedItem as LogSource; if (src != null) Util.OpenPath(src.Path); };
            inner.Controls.Add(Ui.Row(logSource, search, clear, open), 0, 0);
            logCount = Ui.Lbl("", Theme.Small, Theme.Muted);
            logCount.Anchor = AnchorStyles.Right;
            inner.Controls.Add(logCount, 1, 0);
            logPath = Ui.Lbl("", Theme.Small, Theme.Faint);
            logPath.Margin = Ui.P(2, 8, 0, 0);
            inner.Controls.Add(logPath, 0, 1);
            inner.SetColumnSpan(logPath, 2);
            bar.Controls.Add(inner);

            var card = new RoundPanel { Tag = "fill", Padding = Ui.P(16, 12, 6, 10), Margin = new Padding(0) };
            logBox = new RichTextBox { Dock = DockStyle.Fill, ReadOnly = true, BorderStyle = BorderStyle.None, BackColor = Color.White, Font = Theme.Mono, DetectUrls = false, WordWrap = false, HideSelection = false };
            card.Controls.Add(logBox);
            return Ui.Stack(new Padding(0), Ui.PageTitle("Logs", "Live view - newest lines at the bottom"), bar, card);
        }

        void PopulateLogSources(string select)
        {
            var current = select ?? (logSource.SelectedItem is LogSource ? ((LogSource)logSource.SelectedItem).Path : Paths.ApacheErrorLog);
            var list = new List<LogSource>
            {
                new LogSource { Name = "Web server - errors", Path = Paths.ApacheErrorLog },
                new LogSource { Name = "Web server - visits", Path = Paths.ApacheAccessLog },
                new LogSource { Name = "Database", Path = Paths.MariaErrorLog },
                new LogSource { Name = "PHP errors", Path = Paths.PhpErrorLog },
                new LogSource { Name = "NAMP", Path = Paths.AppLog },
            };
            foreach (var d in Settings.Current.Domains)
            {
                list.Add(new LogSource { Name = d.Domain + " - errors", Path = SiteLog(d.Domain, "error") });
                list.Add(new LogSource { Name = d.Domain + " - PHP errors", Path = SiteLog(d.Domain, "php") });
                list.Add(new LogSource { Name = d.Domain + " - visits", Path = SiteLog(d.Domain, "access") });
            }
            logSource.BeginUpdate();
            logSource.Items.Clear();
            foreach (var s in list) logSource.Items.Add(s);
            var match = list.FirstOrDefault(s => string.Equals(s.Path, current, StringComparison.OrdinalIgnoreCase));
            logSource.SelectedItem = match ?? list[0];
            logSource.EndUpdate();
        }

        void OpenLogFor(string path)
        {
            ShowPage("logs");
            PopulateLogSources(path);
            LoadLog(true);
        }

        void LoadLog(bool force)
        {
            var src = logSource.SelectedItem as LogSource;
            if (src == null) return;
            long size = Util.FileSize(src.Path);
            if (!force && src.Path == lastLogPath && size == lastLogSize) return;
            lastLogPath = src.Path;
            lastLogSize = size;
            logPath.Text = src.Path;
            logLines = File.Exists(src.Path) && size > 0 ? Util.TailLines(src.Path, 2000) : new List<string> { "Nothing logged yet." };
            RenderLog();
        }

        [DllImport("user32.dll")]
        static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);

        void RenderLog()
        {
            string q = (logSearch.Text ?? "").Trim();
            var lines = q.Length == 0 ? logLines : logLines.Where(l => l.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            logCount.Text = lines.Count + " lines";
            SendMessage(logBox.Handle, 0x000B /* WM_SETREDRAW */, IntPtr.Zero, IntPtr.Zero);
            logBox.Clear();
            foreach (var l in lines)
            {
                Color c = Theme.Text;
                string low = l.ToLowerInvariant();
                if (low.Contains("error") || low.Contains("fatal") || low.Contains("crit") || low.Contains("emerg")) c = Theme.Red;
                else if (low.Contains("warn")) c = Theme.Amber;
                else if (low.Contains("[note]") || low.Contains("notice")) c = Theme.Muted;
                logBox.SelectionStart = logBox.TextLength;
                logBox.SelectionColor = c;
                logBox.AppendText(l + "\n");
            }
            SendMessage(logBox.Handle, 0x000B, (IntPtr)1, IntPtr.Zero);
            logBox.Invalidate();
            logBox.SelectionStart = logBox.TextLength;
            logBox.ScrollToCaret();
        }

        void ClearLog()
        {
            var src = logSource.SelectedItem as LogSource;
            if (src == null || !File.Exists(src.Path)) return;
            if (MessageBox.Show(this, "Clear this log?", "Clear log", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            try
            {
                using (var fs = new FileStream(src.Path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete)) fs.SetLength(0);
                LoadLog(true);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "This log is in use (" + ex.Message + "). Stop the service and try again.", "Clear log", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        // ==================================================================
        // Settings
        // ==================================================================
        Control BuildSettings()
        {
            httpPort = Ui.Num(1, 65535);
            httpsPort = Ui.Num(1, 65535);
            dbPort = Ui.Num(1, 65535);
            phpVersion = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = Ui.S(200), Font = Theme.Base, Margin = Ui.P(0, 2, 18, 8), FlatStyle = FlatStyle.Flat };
            phpVersion.Items.Add("PHP 8.5 (latest)");
            phpVersion.Items.Add("PHP 8.3");
            var ports = Card("Server", Ui.Row(Field("PHP version", phpVersion), Field("Web (HTTP)", httpPort), Field("Web (HTTPS)", httpsPort), Field("Database", dbPort)));

            chkHttps = Ui.Check("HTTPS for local sites (NAMP's own certificate - trusted on this PC, valid only for local names)");
            chkAutoStart = Ui.Check("Start the web server and database when NAMP opens");
            chkAutoRestart = Ui.Check("Restart a service automatically if it crashes");
            chkLan = Ui.Check("Allow phones and other computers on my network to open my sites");
            var options = Card("Options", Column(chkHttps, chkAutoStart, chkAutoRestart, chkLan));

            backupEvery = Ui.Num(0, 1440);
            backupKeep = Ui.Num(1, 500);
            chkSnapshot = Ui.Check("Keep a last-good copy of the database files every time it stops cleanly");
            var every = Ui.Lbl("Back up every database every", Theme.Base, Theme.Text);
            every.Margin = Ui.P(0, 6, 8, 0);
            var mins = Ui.Lbl("minutes (0 = off), keep", Theme.Base, Theme.Text);
            mins.Margin = Ui.P(0, 6, 8, 0);
            var vers = Ui.Lbl("versions", Theme.Base, Theme.Text);
            vers.Margin = Ui.P(0, 6, 0, 0);
            backupEvery.Margin = Ui.P(0, 2, 8, 8);
            backupKeep.Margin = Ui.P(0, 2, 8, 8);
            var backups = Card("Backups", Column(Ui.Row(every, backupEvery, mins, backupKeep, vers), chkSnapshot));

            var save = new FlatButton("Save settings", BtnKind.Primary, null);
            save.Click += (s, e) => SaveSettings();
            var folder = new FlatButton("Open NAMP folder", BtnKind.Soft, Glyph.Folder);
            folder.Click += (s, e) => Util.OpenPath(Paths.Root);
            var actions = Ui.Row(save, folder);
            actions.Margin = Ui.P(0, 0, 0, 14);
            aboutLabel = Ui.Lbl("", Theme.Small, Theme.Muted);

            var page = Ui.Stack(new Padding(0), Ui.PageTitle("Settings", null), ports, options, backups, actions, aboutLabel);
            page.AutoScroll = true;
            return page;
        }

        static Control Card(string title, Control body)
        {
            var c = new RoundPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
            var f = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Dock = DockStyle.Top, BackColor = Color.White };
            var h = Ui.Lbl(title, Theme.H2, Theme.Text);
            h.Margin = Ui.P(0, 0, 0, 10);
            f.Controls.Add(h);
            f.Controls.Add(body);
            c.Controls.Add(f);
            return c;
        }

        static Control Column(params Control[] controls)
        {
            var f = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, BackColor = Color.White, Margin = new Padding(0) };
            f.Controls.AddRange(controls);
            return f;
        }

        static Control Field(string label, Control input)
        {
            var l = Ui.Lbl(label, Theme.Small, Theme.Muted);
            l.Margin = Ui.P(0, 0, 0, 4);
            return Column(l, input);
        }

        void LoadSettingsIntoForm()
        {
            var s = Settings.Current;
            httpPort.Value = s.ApachePort;
            httpsPort.Value = s.HttpsPort;
            dbPort.Value = s.MySqlPort;
            chkHttps.Checked = s.EnableHttps;
            phpVersion.SelectedIndex = s.PhpVersion == "8.3" ? 1 : 0;
            chkAutoStart.Checked = s.AutoStart;
            chkAutoRestart.Checked = s.AutoRestart;
            chkLan.Checked = s.LanAccess;
            chkSnapshot.Checked = s.SnapshotOnStop;
            backupEvery.Value = Math.Min(1440, s.BackupIntervalMinutes);
            backupKeep.Value = Math.Max(1, Math.Min(500, s.BackupKeep));
            aboutLabel.Text = "NAMP " + Versions.Namp + "  ·  Apache " + Versions.Apache + "  ·  PHP " + Versions.Php + "  ·  MariaDB " + Versions.MariaDb +
                "  ·  phpMyAdmin " + Versions.PhpMyAdmin + "\n" + Paths.Root;
        }

        void SaveSettings()
        {
            var s = Settings.Current;
            int newHttp = (int)httpPort.Value, newHttps = (int)httpsPort.Value, newDb = (int)dbPort.Value;
            if (new[] { newHttp, newHttps, newDb }.Distinct().Count() != 3)
            {
                MessageBox.Show(this, "The three ports must be different.", "Settings", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            string newPhp = phpVersion.SelectedIndex == 1 ? "8.3" : "8.5";
            bool webChanged = newHttp != s.ApachePort || newHttps != s.HttpsPort || chkHttps.Checked != s.EnableHttps || chkLan.Checked != s.LanAccess || newPhp != s.PhpVersion;
            s.PhpVersion = newPhp;
            bool dbChanged = newDb != s.MySqlPort;
            s.ApachePort = newHttp;
            s.HttpsPort = newHttps;
            s.MySqlPort = newDb;
            s.EnableHttps = chkHttps.Checked;
            s.LanAccess = chkLan.Checked;
            s.AutoStart = chkAutoStart.Checked;
            s.AutoRestart = chkAutoRestart.Checked;
            s.SnapshotOnStop = chkSnapshot.Checked;
            s.BackupIntervalMinutes = (int)backupEvery.Value;
            s.BackupKeep = (int)backupKeep.Value;
            Stack.RunOp("save settings", () =>
            {
                s.Save();
                Configs.WriteAll();
                Log.Ok("main", "Settings saved.");
                if (dbChanged && Stack.Db.IsRunning) { Stack.StopDb(false); Stack.StartDb(); }
                if ((webChanged || dbChanged) && Stack.Apache.IsRunning) { Stack.Apache.Stop(); Stack.Apache.Start(); }
                SafeInvoke(() => { RefreshSites(); LoadSettingsIntoForm(); });
            });
        }

        // ==================================================================
        // Status
        // ==================================================================
        void Tick()
        {
            if (refreshing || closing) return;
            refreshing = true;
            Task.Run(() =>
            {
                try
                {
                    bool a = Stack.Apache.IsRunning;
                    httpsTrusted = Demo || (Configs.HttpsActive && Ssl.IsTrusted());
                    bool d = Stack.Db.IsRunning;
                    SafeInvoke(() => ApplyStatus(a, d));
                }
                catch { }
                finally { refreshing = false; }
            });
            if (currentPage == "logs") LoadLog(false);
        }

        void ApplyStatus(bool a, bool d)
        {
            apacheUp = a;
            dbUp = d;
            var s = Settings.Current;
            bool busy = Stack.Busy, enabled = prepared && !busy;

            web.Sub.Text = "Apache " + Versions.Apache + "  ·  PHP " + Versions.Php;
            web.Status.Set(a ? "Running" : "Stopped", a ? Theme.GreenSoft : Theme.GraySoft, a ? Theme.Green : Theme.Muted);
            if (a)
            {
                string http = s.BaseUrl;
                web.Detail.Text = http + (Configs.HttpsActive ? "\nhttps://localhost" + (s.HttpsPort == 443 ? "" : ":" + s.HttpsPort) + (httpsTrusted ? "  -  secure, trusted certificate" : "  -  certificate not trusted yet") : "\nHTTPS off") +
                    "\n" + s.Domains.Count + (s.Domains.Count == 1 ? " local site" : " local sites");
            }
            else web.Detail.Text = "Not running.\nStart it to open your sites.\n ";
            web.Toggle.Set(a ? "Stop" : "Start", a ? BtnKind.Soft : BtnKind.Primary, a ? Glyph.Stop : Glyph.Play);
            web.Toggle.Enabled = enabled;

            db.Sub.Text = "MariaDB " + Versions.MariaDb + " LTS  ·  phpMyAdmin " + Versions.PhpMyAdmin;
            db.Status.Set(d ? "Running" : "Stopped", d ? Theme.GreenSoft : Theme.GraySoft, d ? Theme.Green : Theme.Muted);
            var last = Backups.LastBackupRun();
            var snap = Backups.LatestSnapshot();
            db.Detail.Text = (d ? "127.0.0.1:" + s.MySqlPort + "  -  user root, no password" : "Not running - your data is safe on disk.") +
                "\nLast backup: " + (last.HasValue ? Util.Ago(last.Value) : "not yet") + "   ·   Safe copy: " + (snap != null ? Util.Ago(snap.Time) : "after first clean stop") +
                "\nPower-loss protection on";
            db.Toggle.Set(d ? "Stop" : "Start", d ? BtnKind.Soft : BtnKind.Primary, d ? Glyph.Stop : Glyph.Play);
            db.Toggle.Enabled = enabled;

            bool any = a || d;
            toggleAll.Set(any ? "Stop all" : "Start all", any ? BtnKind.Dark : BtnKind.Primary, any ? Glyph.Stop : Glyph.Play);
            toggleAll.Enabled = enabled;
            if (a && d) sideStatus.Set("All running", Theme.GreenSoft, Theme.Green);
            else if (any) sideStatus.Set("Partly running", Theme.AmberSoft, Theme.Amber);
            else sideStatus.Set("Stopped", Theme.GraySoft, Theme.Muted);
            UpdateProtection();
        }

        void UpdateProtection()
        {
            if (!prepared || dbFoot == null) return;
            var snap = Backups.LatestSnapshot();
            int every = Settings.Current.BackupIntervalMinutes;
            dbFoot.Text = "Protected: durable writes  ·  clean stop at Windows shutdown  ·  automatic repair  ·  safe copy " +
                (snap != null ? snap.Time.ToString("g") : "after first clean stop") + "  ·  " +
                (every > 0 ? "backups every " + every + " min (" + Settings.Current.BackupKeep + " kept)" : "automatic backups off");
        }

        void UpdateBusy()
        {
            busyLabel.Text = Stack.Busy ? "Working..." : "";
            Tick();
        }

        // ==================================================================
        // Activity feed
        // ==================================================================
        void AppendActivity(LogEntry e)
        {
            var rtb = activity;
            if (rtb.TextLength > 300000)
            {
                rtb.Select(0, rtb.TextLength / 2);
                rtb.SelectedText = "";
            }
            Append(rtb, e.Time.ToString("HH:mm:ss") + "   ", Theme.Faint);
            Color c = e.Level == LogLevel.Error ? Theme.Red : e.Level == LogLevel.Warn ? Theme.Amber : e.Level == LogLevel.Ok ? Theme.Green : Theme.Text;
            Append(rtb, e.Message + "\n", c);
            rtb.SelectionStart = rtb.TextLength;
            rtb.ScrollToCaret();
        }

        static void Append(RichTextBox rtb, string text, Color color)
        {
            rtb.SelectionStart = rtb.TextLength;
            rtb.SelectionLength = 0;
            rtb.SelectionColor = color;
            rtb.AppendText(text);
        }

        // ==================================================================
        // Lifecycle
        // ==================================================================
        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            foreach (var entry in Log.Recent()) AppendActivity(entry);
            Log.Added += entry => SafeInvoke(() => AppendActivity(entry));
            timer = new Timer { Interval = 1500 };
            timer.Tick += (s, ev) => Tick();
            timer.Start();
            ApplyStatus(false, false);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Log.Info("main", "NAMP " + Versions.Namp + " starting.");
            Stack.RunOp("startup", () =>
            {
                Stack.Prepare();
                prepared = true;
                SafeInvoke(() => { LoadSettingsIntoForm(); RefreshSites(); Tick(); });
                if (Settings.Current.AutoStart) Stack.StartAll();
                else Log.Info("main", "Ready. Click \"Start all\" to start.");
                SafeInvoke(() => { RefreshSites(); if (currentPage == "databases") RefreshDatabases(); });
            });
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (reallyClose || e.CloseReason == CloseReason.WindowsShutDown)
            {
                if (e.CloseReason == CloseReason.WindowsShutDown && !Stack.ShuttingDown) Stack.EmergencyStop();
                base.OnFormClosing(e);
                return;
            }
            e.Cancel = true;
            if (closing) return;
            closing = true;
            busyLabel.Text = "Stopping safely - please wait...";
            content.Enabled = false;
            toggleAll.Enabled = false;
            Stack.RunOp("shutdown", () => Stack.StopAll(true)).ContinueWith(t => SafeInvoke(() => { reallyClose = true; Close(); }));
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_QUERYENDSESSION)
            {
                // Windows is shutting down / restarting / logging off: stop the database now.
                Native.ShutdownBlockReasonCreate(Handle, "Stopping the MariaDB database safely...");
                Log.Warn("main", "Windows is shutting down - stopping services safely...");
                Stack.EmergencyStop();
                m.Result = (IntPtr)1;
                return;
            }
            if (m.Msg == Native.WM_ENDSESSION)
            {
                if (m.WParam != IntPtr.Zero)
                {
                    Stack.EmergencyStop();
                    reallyClose = true;
                }
                else
                {
                    Stack.ShuttingDown = false;
                    Log.Info("main", "Windows shutdown was cancelled. Click \"Start all\" to start the services again.");
                }
                Native.ShutdownBlockReasonDestroy(Handle);
                m.Result = IntPtr.Zero;
                return;
            }
            base.WndProc(ref m);
        }

        bool ConfirmFromWorker(string title, string message)
        {
            bool result = false;
            if (IsDisposed) return false;
            Invoke(new Action(() =>
            {
                result = MessageBox.Show(this, message, title, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button1) == DialogResult.Yes;
            }));
            return result;
        }

        void SafeInvoke(Action a)
        {
            try
            {
                if (IsDisposed || !IsHandleCreated) return;
                if (InvokeRequired) BeginInvoke(a);
                else a();
            }
            catch { }
        }
    }
}
