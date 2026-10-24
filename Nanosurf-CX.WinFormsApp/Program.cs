namespace Nanosurf_CX.WinFormsApp
{
    internal static class Program
    {
        /// <summary>
        ///  The main entry point for the application.
        /// </summary>
        [STAThread]
        static void Main()
        {
            // To customize application configuration such as set high DPI settings or default font,
            // see https://aka.ms/applicationconfiguration.
            ApplicationConfiguration.Initialize();

            // 测试：同一个进程里同时打开两个窗体，各自连接 CX。
            // Application.Run 会阻塞到窗体关闭，所以第二个窗体要先用 Show() 显示，
            // 否则要等第一个窗体关掉，第二个才会出现。关闭 form1 时程序退出。
            var form1 = new Main();
            //var form2 = new Main();
            //form2.Show();

            // CX 的类型库里也有一个 Application，这里写全名避免冲突
            System.Windows.Forms.Application.Run(form1);
        }
    }
}