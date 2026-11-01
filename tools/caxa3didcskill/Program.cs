using System;
using System.Windows.Forms;

namespace caxa3didcskill
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            Diagnostics.Initialize();

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            Application.ThreadException += delegate (object sender, System.Threading.ThreadExceptionEventArgs e)
            {
                Diagnostics.Exception("UI 线程未处理异常", e.Exception);
            };
            AppDomain.CurrentDomain.UnhandledException += delegate (object sender, UnhandledExceptionEventArgs e)
            {
                Diagnostics.Exception("非 UI 线程未处理异常", e.ExceptionObject as Exception);
            };

            Application.Run(new MainForm());
        }
    }
}
