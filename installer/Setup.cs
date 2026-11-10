using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace CoCofeSetup
{
    // ============================================================
    //  公共常量与工具
    // ============================================================
    static class C
    {
        public const string AppName = "Co_cofe建模看图工具";
        public const string Version = "1.0.0";
        public const string Publisher = "冷咖啡制作";
        public const string ExeName = "Co_cofe建模看图工具.exe";
        public const string UninstName = "unins000.exe";
        public const string RegKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Co_cofeWebPin";
        public const string LinkName = "Co_cofe建模看图工具";

        // 资源名 -> 目标文件名
        public static readonly string[,] Payload = new string[,]
        {
            { "APP_EXE",   ExeName },
            { "WELCOME",   "welcome.jpg" },
            { "HELP_TXT",  "使用说明.txt" },
            { "QUICK_TXT", "快速上手.txt" },
            { "APP_ICO",   "app.ico" },
            { "LOGO_PNG",  "logo.png" },
        };

        public static string DefaultDir
        {
            get
            {
                string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                return Path.Combine(pf, AppName);
            }
        }

        public static string PublicDesktop
        {
            get { return Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory); }
        }

        public static string StartMenuDir
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
                    LinkName);
            }
        }

        public static string InstalledDir()
        {
            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(RegKey))
                {
                    if (k != null)
                    {
                        string v = k.GetValue("InstallLocation") as string;
                        if (!string.IsNullOrEmpty(v) && Directory.Exists(v)) return v;
                    }
                }
            }
            catch { }
            return null;
        }

        public static void KillRunningApp()
        {
            string pn = Path.GetFileNameWithoutExtension(ExeName);
            foreach (Process p in Process.GetProcessesByName(pn))
            {
                try { p.Kill(); p.WaitForExit(4000); }
                catch { }
            }
        }

        // ---------- 快捷方式（走 WScript.Shell，免 COM 接口声明） ----------
        public static void CreateShortcut(string lnkPath, string target, string workDir, string icon, string desc)
        {
            Type ws = Type.GetTypeFromProgID("WScript.Shell");
            object shell = Activator.CreateInstance(ws);
            object lnk = ws.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell,
                new object[] { lnkPath });
            Type lt = lnk.GetType();
            lt.InvokeMember("TargetPath", BindingFlags.SetProperty, null, lnk, new object[] { target });
            lt.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, lnk, new object[] { workDir });
            lt.InvokeMember("IconLocation", BindingFlags.SetProperty, null, lnk, new object[] { icon + ",0" });
            lt.InvokeMember("Description", BindingFlags.SetProperty, null, lnk, new object[] { desc });
            lt.InvokeMember("Save", BindingFlags.InvokeMethod, null, lnk, null);
            System.Runtime.InteropServices.Marshal.ReleaseComObject(lnk);
            System.Runtime.InteropServices.Marshal.ReleaseComObject(shell);
        }

        public static void SafeDeleteFile(string path)
        {
            try { if (File.Exists(path)) File.SetAttributes(path, FileAttributes.Normal); } catch { }
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        public static void SafeDeleteDir(string path)
        {
            try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { }
        }

        public static long DirSize(string dir)
        {
            long n = 0;
            try
            {
                foreach (string f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                    try { n += new FileInfo(f).Length; } catch { }
            }
            catch { }
            return n;
        }

        public static void SelfDeleteLater(string exePath)
        {
            try
            {
                string cmd = string.Format("/c ping -n 3 127.0.0.1 >nul & del /f /q \"{0}\"", exePath);
                var psi = new ProcessStartInfo("cmd.exe", cmd);
                psi.WindowStyle = ProcessWindowStyle.Hidden;
                psi.CreateNoWindow = true;
                psi.UseShellExecute = false;
                Process.Start(psi);
            }
            catch { }
        }
    }

    // ============================================================
    //  安装程序
    // ============================================================
    class SetupForm : Form
    {
        TextBox txtDir;
        CheckBox chkDesk, chkStart, chkRun;
        Button btnInstall, btnBrowse;
        ProgressBar bar;
        Label lblStatus;

        public SetupForm()
        {
            Text = C.AppName + " 安装程序";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(560, 336);
            Font = new Font("Microsoft YaHei UI", 9f);
            BackColor = Color.FromArgb(250, 250, 252);
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            // 头部
            var head = new Panel();
            head.Dock = DockStyle.Top;
            head.Height = 74;
            head.BackColor = Color.FromArgb(247, 226, 240);
            Controls.Add(head);

            var lblTitle = new Label();
            lblTitle.Text = C.AppName;
            lblTitle.Font = new Font("Microsoft YaHei UI", 15f, FontStyle.Bold);
            lblTitle.ForeColor = Color.FromArgb(62, 38, 72);
            lblTitle.AutoSize = true;
            lblTitle.Location = new Point(20, 14);
            head.Controls.Add(lblTitle);

            var lblSub = new Label();
            lblSub.Text = "版本 " + C.Version + "    ·    " + C.Publisher +
                          "    ·    把图纸钉在所有窗口最上方";
            lblSub.ForeColor = Color.FromArgb(110, 78, 122);
            lblSub.AutoSize = true;
            lblSub.Location = new Point(22, 46);
            head.Controls.Add(lblSub);

            // 安装目录
            var lblDir = new Label();
            lblDir.Text = "安装位置：";
            lblDir.AutoSize = true;
            lblDir.Location = new Point(24, 98);
            Controls.Add(lblDir);

            txtDir = new TextBox();
            txtDir.Location = new Point(96, 95);
            txtDir.Width = 356;
            txtDir.Text = C.DefaultDir;
            Controls.Add(txtDir);

            btnBrowse = new Button();
            btnBrowse.Text = "浏览…";
            btnBrowse.Location = new Point(460, 93);
            btnBrowse.Width = 76;
            btnBrowse.Click += delegate { Browse(); };
            Controls.Add(btnBrowse);

            // 选项
            chkDesk = new CheckBox();
            chkDesk.Text = "创建桌面快捷方式";
            chkDesk.Checked = true;
            chkDesk.AutoSize = true;
            chkDesk.Location = new Point(96, 134);
            Controls.Add(chkDesk);

            chkStart = new CheckBox();
            chkStart.Text = "创建开始菜单快捷方式";
            chkStart.Checked = true;
            chkStart.AutoSize = true;
            chkStart.Location = new Point(96, 160);
            Controls.Add(chkStart);

            chkRun = new CheckBox();
            chkRun.Text = "安装完成后立即运行";
            chkRun.Checked = true;
            chkRun.AutoSize = true;
            chkRun.Location = new Point(96, 186);
            Controls.Add(chkRun);

            // 提示
            var lblTip = new Label();
            lblTip.Text = "说明：程序不含联网功能，不装服务，只会在 %APPDATA%\\SwPin 记录窗口位置。";
            lblTip.ForeColor = Color.FromArgb(120, 120, 120);
            lblTip.AutoSize = false;
            lblTip.Location = new Point(24, 216);
            lblTip.Width = 512;
            lblTip.Height = 34;
            Controls.Add(lblTip);

            // 进度
            bar = new ProgressBar();
            bar.Location = new Point(24, 256);
            bar.Width = 512;
            bar.Height = 8;
            bar.Style = ProgressBarStyle.Continuous;
            Controls.Add(bar);

            lblStatus = new Label();
            lblStatus.Text = "准备就绪";
            lblStatus.ForeColor = Color.FromArgb(110, 78, 122);
            lblStatus.AutoSize = false;
            lblStatus.Location = new Point(24, 270);
            lblStatus.Width = 340;
            Controls.Add(lblStatus);

            btnInstall = new Button();
            btnInstall.Text = "安装";
            btnInstall.Location = new Point(452, 292);
            btnInstall.Width = 84;
            btnInstall.Height = 30;
            btnInstall.Click += delegate { DoInstall(); };
            Controls.Add(btnInstall);

            var btnCancel = new Button();
            btnCancel.Text = "取消";
            btnCancel.Location = new Point(360, 292);
            btnCancel.Width = 84;
            btnCancel.Height = 30;
            btnCancel.Click += delegate { Close(); };
            Controls.Add(btnCancel);
        }

        void Browse()
        {
            using (var dlg = new FolderBrowserDialog())
            {
                dlg.Description = "选择安装位置";
                dlg.SelectedPath = txtDir.Text;
                if (dlg.ShowDialog() == DialogResult.OK) txtDir.Text = dlg.SelectedPath;
            }
        }

        void DoInstall()
        {
            string dir = txtDir.Text.Trim();
            if (dir.Length == 0) { MessageBox.Show("请填写安装位置。", Text); return; }
            try { dir = Path.GetFullPath(dir); }
            catch { MessageBox.Show("安装位置不合法。", Text); return; }

            if (dir.Length < 3 || dir.TrimEnd('\\').Length <= 2)
            {
                MessageBox.Show("不能直接装到盘符根目录，请选一个子文件夹。", Text);
                return;
            }

            btnInstall.Enabled = false;
            btnBrowse.Enabled = false;
            bar.Value = 2;

            string err = null;
            var th = new Thread(delegate ()
            {
                try { InstallCore(dir); }
                catch (Exception ex) { err = ex.Message; }
            });
            th.IsBackground = true;
            th.Start();

            // 简单等待（安装很快）
            while (th.IsAlive)
            {
                Application.DoEvents();
                Thread.Sleep(30);
            }

            if (err != null)
            {
                MessageBox.Show("安装失败：\r\n" + err, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
                btnInstall.Enabled = true;
                btnBrowse.Enabled = true;
                bar.Value = 0;
                lblStatus.Text = "安装失败";
                return;
            }

            bar.Value = 100;
            lblStatus.Text = "安装完成";
            MessageBox.Show("安装完成！\r\n\r\n程序位置：" + dir, Text, MessageBoxButtons.OK, MessageBoxIcon.Information);

            if (chkRun.Checked)
            {
                try { Process.Start(new ProcessStartInfo(Path.Combine(dir, C.ExeName)) { WorkingDirectory = dir }); }
                catch { }
            }
            Close();
        }

        void InstallCore(string dir)
        {
            Engine.Install(dir, chkDesk.Checked, chkStart.Checked, SetStatus);
        }

        void SetStatus(string text, int percent)
        {
            try
            {
                Invoke((MethodInvoker)delegate
                {
                    lblStatus.Text = text;
                    bar.Value = Math.Min(100, Math.Max(0, percent));
                    Application.DoEvents();
                });
            }
            catch { }
        }
    }

    // ============================================================
    //  安装核心（UI 和静默模式共用）
    // ============================================================
    static class Engine
    {
        public static void Install(string dir, bool desk, bool start, Action<string, int> status)
        {
            Action<string, int> S = delegate (string t, int p) { if (status != null) status(t, p); };

            S("正在创建目录…", 10);
            Directory.CreateDirectory(dir);

            Assembly asm = Assembly.GetExecutingAssembly();
            int total = C.Payload.GetLength(0);
            for (int r = 0; r < total; r++)
            {
                string res = C.Payload[r, 0];
                string name = C.Payload[r, 1];
                S("正在复制文件… " + (r + 1) + "/" + total, 20 + (int)(38.0 * (r + 1) / total));
                using (Stream s = asm.GetManifestResourceStream(res))
                {
                    if (s == null) throw new Exception("安装包损坏，缺少资源：" + res);
                    string dst = Path.Combine(dir, name);
                    C.SafeDeleteFile(dst);
                    using (FileStream fs = new FileStream(dst, FileMode.Create, FileAccess.Write))
                    {
                        byte[] buf = new byte[81920];
                        int n;
                        while ((n = s.Read(buf, 0, buf.Length)) > 0) fs.Write(buf, 0, n);
                    }
                }
            }

            S("正在写入卸载程序…", 62);
            using (Stream s = asm.GetManifestResourceStream("UNINS_EXE"))
            {
                if (s == null) throw new Exception("安装包损坏，缺少卸载程序。");
                string dst = Path.Combine(dir, C.UninstName);
                C.SafeDeleteFile(dst);
                using (FileStream fs = new FileStream(dst, FileMode.Create, FileAccess.Write))
                {
                    byte[] buf = new byte[81920];
                    int n;
                    while ((n = s.Read(buf, 0, buf.Length)) > 0) fs.Write(buf, 0, n);
                }
            }

            string exe = Path.Combine(dir, C.ExeName);
            string icon = Path.Combine(dir, "app.ico");

            S("正在创建快捷方式…", 72);
            if (start)
            {
                string sm = C.StartMenuDir;
                Directory.CreateDirectory(sm);
                C.CreateShortcut(Path.Combine(sm, C.LinkName + ".lnk"), exe, dir, icon, C.AppName);
            }
            if (desk)
            {
                C.CreateShortcut(Path.Combine(C.PublicDesktop, C.LinkName + ".lnk"), exe, dir, icon, C.AppName);
            }

            S("正在写入注册表…", 84);
            using (RegistryKey k = Registry.LocalMachine.CreateSubKey(C.RegKey))
            {
                k.SetValue("DisplayName", C.AppName);
                k.SetValue("DisplayVersion", C.Version);
                k.SetValue("Publisher", C.Publisher);
                k.SetValue("DisplayIcon", exe + ",0");
                k.SetValue("InstallLocation", dir);
                k.SetValue("UninstallString", "\"" + Path.Combine(dir, C.UninstName) + "\"");
                k.SetValue("QuietUninstallString", "\"" + Path.Combine(dir, C.UninstName) + "\" /SILENT");
                k.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
                k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                k.SetValue("EstimatedSize", (int)(C.DirSize(dir) / 1024), RegistryValueKind.DWord);
            }

            S("完成", 100);
        }
    }

    static class SetupProgram
    {
        [STAThread]
        static void Main(string[] args)
        {
            string dir = null;
            bool silent = false;
            bool noDesk = false, noStart = false;

            foreach (string a in args)
            {
                if (a.StartsWith("/DIR=", StringComparison.OrdinalIgnoreCase))
                {
                    dir = a.Substring(5).Trim('"');
                }
                else if (a.Equals("/SILENT", StringComparison.OrdinalIgnoreCase) ||
                         a.Equals("/S", StringComparison.OrdinalIgnoreCase)) silent = true;
                else if (a.Equals("/NODESKTOP", StringComparison.OrdinalIgnoreCase)) noDesk = true;
                else if (a.Equals("/NOSTARTMENU", StringComparison.OrdinalIgnoreCase)) noStart = true;

            }

            if (silent)
            {
                if (string.IsNullOrEmpty(dir)) dir = C.DefaultDir;
                try
                {
                    Engine.Install(dir, !noDesk, !noStart, null);
                }
                catch (Exception ex)
                {
                    try
                    {
                        File.WriteAllText(Path.Combine(Path.GetTempPath(), "cofe_setup_err.txt"),
                            DateTime.Now + "\r\n" + ex, Encoding.UTF8);
                    }
                    catch { }
                    Environment.ExitCode = 1;
                }
                return;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new SetupForm());
        }
    }

    // ============================================================
    //  卸载程序
    // ============================================================
    static class UninstallProgram
    {
        [STAThread]
        static void Main(string[] args)
        {
            bool silent = false;
            foreach (string a in args)
                if (a.Equals("/SILENT", StringComparison.OrdinalIgnoreCase) ||
                    a.Equals("/S", StringComparison.OrdinalIgnoreCase)) silent = true;

            string exePath = Application.ExecutablePath;
            string dir = Path.GetDirectoryName(exePath);

            // ---------- 第二段：从临时目录回来做真正的清理 ----------
            if (args.Length >= 2 && args[0].Equals("/CLEAN", StringComparison.OrdinalIgnoreCase))
            {
                dir = args[1];
                C.KillRunningApp();
                Thread.Sleep(600);

                // 短链
                try { File.Delete(Path.Combine(C.PublicDesktop, C.LinkName + ".lnk")); } catch { }
                C.SafeDeleteDir(C.StartMenuDir);

                // 注册表
                try { Registry.LocalMachine.DeleteSubKeyTree(C.RegKey, false); } catch { }

                // 目录（重试几次，等文件句柄释放）
                for (int i = 0; i < 10; i++)
                {
                    try
                    {
                        if (Directory.Exists(dir)) Directory.Delete(dir, true);
                        if (!Directory.Exists(dir)) break;
                    }
                    catch { }
                    Thread.Sleep(400);
                }

                C.SelfDeleteLater(exePath);
                if (!silent)
                    MessageBox.Show("卸载完成。\r\n\r\n用户配置文件（%APPDATA%\\SwPin）已保留，\r\n如需彻底清除请手动删除该文件夹。",
                        C.AppName + " 卸载", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            // ---------- 第一段：正常卸载入口 ----------
            if (!silent)
            {
                DialogResult r = MessageBox.Show(
                    "确定要卸载 " + C.AppName + " 吗？\r\n\r\n" +
                    "安装位置：" + dir + "\r\n" +
                    "（桌面和开始菜单的快捷方式也会一并删除）",
                    C.AppName + " 卸载", MessageBoxButtons.YesNo, MessageBoxIcon.Question,
                    MessageBoxDefaultButton.Button2);
                if (r != DialogResult.Yes) return;
            }

            C.KillRunningApp();
            Thread.Sleep(400);

            // 复制自己到临时目录再清理，避免"自己删自己"问题
            try
            {
                string tmp = Path.Combine(Path.GetTempPath(), "cofe_unins_" + Guid.NewGuid().ToString("N") + ".exe");
                File.Copy(exePath, tmp, true);
                var psi = new ProcessStartInfo(tmp, "/CLEAN \"" + dir + "\"" + (silent ? " /SILENT" : ""));
                psi.UseShellExecute = false;
                Process.Start(psi);
            }
            catch (Exception ex)
            {
                MessageBox.Show("卸载启动失败：" + ex.Message, C.AppName + " 卸载");
            }
        }
    }
}
