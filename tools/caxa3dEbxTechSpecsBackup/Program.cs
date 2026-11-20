using System;
using System.Windows.Forms;

namespace caxa3dEbxTechSpecsBackup
{
    /// <summary>
    /// 独立 EXE 入口（进程外工具，规则 16.6）：
    /// 不加载 ICAPI、不感知 CAXA 宿主，双击即可运行；由 Host 的启动器子插件拉起。
    /// </summary>
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            Application.ThreadException += delegate(object sender, System.Threading.ThreadExceptionEventArgs e)
            {
                Diagnostics.Log("UI 线程未处理异常：" + e.Exception);
            };
            AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs e)
            {
                Diagnostics.Log("非 UI 线程未处理异常：" + (e.ExceptionObject as Exception));
            };

            Application.Run(new MainForm());
        }
    }
}
