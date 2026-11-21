using System;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace Caxa3dToolboxMaker
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            Diagnostics.Initialize();

            // 自检 / 界面验证：WinExe 无控制台，把输出重定向到 _dump 下的文本文件
            if (args != null && args.Length > 0 &&
                (args[0] == "--selftest" || args[0] == "--uitest"))
            {
                RunHeadless(args);
                return;
            }

            // UI 铁律：不调用 Application.EnableVisualStyles()，保持 Win2000 经典外观
            Application.SetCompatibleTextRenderingDefault(false);

            Application.ThreadException += delegate (object sender, System.Threading.ThreadExceptionEventArgs e)
            {
                Diagnostics.Exception("UI 线程未处理异常", e.Exception);
            };
            AppDomain.CurrentDomain.UnhandledException += delegate (object sender, UnhandledExceptionEventArgs e)
            {
                Diagnostics.Exception("非 UI 线程未处理异常", e.ExceptionObject as Exception);
            };

            Application.Run(new MainForm(args));
        }

        private static void RunHeadless(string[] args)
        {
            string name = args[0].Trim('-');
            string dumpDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "_dump");
            Directory.CreateDirectory(dumpDir);
            string log = Path.Combine(dumpDir, name + ".txt");
            int rc;
            using (StreamWriter sw = new StreamWriter(log, false, new UTF8Encoding(false)))
            {
                TextWriter old = Console.Out;
                Console.SetOut(sw);
                try
                {
                    rc = args[0] == "--uitest" ? UiTest.Run(args) : SelfTest.Run(args);
                }
                finally
                {
                    Console.SetOut(old);
                }
            }
            Environment.ExitCode = rc;
        }
    }
}
