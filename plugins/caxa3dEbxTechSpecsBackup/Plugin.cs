using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using Caxa3dMyTools.Contracts;

namespace caxa3dEbxTechSpecsBackup
{
    /// <summary>
    /// 「2. 系统工具」列：工程图技术要求备份（外部工具启动器）。
    ///
    /// 2026-11-19 由「进程内业务子插件」转制为独立 EXE（规则 16.6 / 第 17 节）：
    ///   备份/还原只读写用户目录下的 NSpecLib.xml，不需要 CAXA 宿主。
    ///   本类不实现业务逻辑、不引用 ICAPI、不建窗口、不注册 Ribbon，
    ///   只负责「找到 EXE -> 已有实例则前台化，否则启动」。
    /// </summary>
    public sealed class EbxTechSpecsBackupLauncherPlugin : IPlugin
    {
        private const string ExeName     = "caxa3dEbxTechSpecsBackup.exe";
        private const string ProcessName = "caxa3dEbxTechSpecsBackup";
        private const int    SW_RESTORE  = 9;
        private const int    MaxParentLevels = 6;

        private IPluginHost _host;
        private string _exePath;   // 首次激活时解析并缓存

        public string Id { get { return "caxa3dEbxTechSpecsBackup"; } }

        public string DisplayName { get { return "工程图技术要求备份"; } }

        /// <summary>独立 EXE 方式运行，归入「2. 系统工具」列。</summary>
        public PluginCategory Category { get { return PluginCategory.SystemTool; } }

        /// <summary>Host 加载时只做准备：解析 EXE 路径并写日志，绝不启动进程。</summary>
        public void Initialize(IPluginHost host)
        {
            _host = host;
            _exePath = ResolveExePath();
            Log(_exePath != null
                ? "独立工具已就位：" + _exePath
                : "未找到 " + ExeName + "（部署目录缺少该 EXE 时点击会给出明确提示）");
        }

        /// <summary>工具箱点击：已有实例切前台，否则启动独立 EXE。</summary>
        public void Activate()
        {
            if (_exePath == null || !File.Exists(_exePath)) _exePath = ResolveExePath();
            if (_exePath == null)
            {
                throw new FileNotFoundException(
                    "找不到独立工具 " + ExeName + "。\n" +
                    "部署目录应包含：<CAXA bin>\\caxa3dmytoolsaddin\\plugins\\caxa3dEbxTechSpecsBackup\\" + ExeName + "\n" +
                    "请重新运行 安装.bat（或 build.ps1 后重新部署）。");
            }

            // 备份/还原会写同一个 NSpecLib.xml，多开会互相踩：已有实例只做前台化
            if (ActivateExistingInstance()) return;

            ProcessStartInfo psi = new ProcessStartInfo
            {
                FileName         = _exePath,
                WorkingDirectory = Path.GetDirectoryName(_exePath),
                UseShellExecute  = true   // 独立进程、独立身份，不继承 CAXA 的加载上下文
            };
            Process.Start(psi);
            Log("已启动独立工具：" + _exePath);
        }

        /// <summary>
        /// Host 卸载时不结束外部进程：用户可能正在备份/还原中途，
        /// 强行结束会留下半成品备份包。启动器只解除引用。
        /// </summary>
        public void Shutdown()
        {
            _host = null;
        }

        // ================================================================ 内部实现

        /// <summary>
        /// 解析 EXE 路径，顺序：
        ///   1) 插件目录（部署布局：plugins\caxa3dEbxTechSpecsBackup\caxa3dEbxTechSpecsBackup.exe）
        ///   2) 启动器 DLL 所在目录（单独 dotnet build 的输出目录）
        ///   3) 仓库开发布局：逐级向上找 tools\caxa3dEbxTechSpecsBackup\bin\x64\Release\net48
        /// </summary>
        private string ResolveExePath()
        {
            if (_host != null && !string.IsNullOrEmpty(_host.PluginDirectory))
            {
                string p = Path.Combine(_host.PluginDirectory, ExeName);
                if (File.Exists(p)) return p;
            }

            try
            {
                string asmDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                if (!string.IsNullOrEmpty(asmDir))
                {
                    string p = Path.Combine(asmDir, ExeName);
                    if (File.Exists(p)) return p;

                    DirectoryInfo dir = new DirectoryInfo(asmDir);
                    for (int level = 0; level < MaxParentLevels && dir != null; level++)
                    {
                        string dev = Path.Combine(dir.FullName,
                            @"tools\caxa3dEbxTechSpecsBackup\bin\x64\Release\net48\" + ExeName);
                        if (File.Exists(dev)) return dev;
                        dir = dir.Parent;
                    }
                }
            }
            catch (Exception ex)
            {
                Log("解析独立工具路径失败：" + ex.Message);
            }
            return null;
        }

        /// <summary>已有实例则还原并置前，返回 true 表示无需再启动。</summary>
        private bool ActivateExistingInstance()
        {
            Process[] all;
            try { all = Process.GetProcessesByName(ProcessName); }
            catch (Exception ex) { Log("枚举 " + ProcessName + " 进程失败：" + ex.Message); return false; }

            bool found = false;
            foreach (Process proc in all)
            {
                try
                {
                    found = true;
                    IntPtr hwnd = proc.MainWindowHandle;
                    if (hwnd != IntPtr.Zero)
                    {
                        ShowWindow(hwnd, SW_RESTORE);
                        SetForegroundWindow(hwnd);
                    }
                }
                catch (Exception ex)
                {
                    Log("前台化已有实例失败：" + ex.Message);
                }
                finally
                {
                    proc.Dispose();
                }
            }

            if (found) Log("已有 " + ProcessName + " 实例在运行，已切到前台（不重复启动）");
            return found;
        }

        private void Log(string message)
        {
            if (_host != null) _host.Log(message);
        }

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);
    }
}
