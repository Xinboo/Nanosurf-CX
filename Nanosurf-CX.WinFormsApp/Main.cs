using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Nanosurf_CX.WinFormsApp
{
    public partial class Main : Form
    {
        // CX 的 COM 对象（早期绑定，类型来自 COM 引用）。只在 UI 线程上使用。
        private Application? _cx;

        // 进针模块的代理。连接成功后获取一次，关闭时和 _cx 一起释放。
        private IProxyApproach? _approach;

        public Main()
        {
            InitializeComponent();
            btnOpen.Click += BtnOpen_Click;
            btnClose.Click += BtnClose_Click;
            Text = "Nanosurf CX - 未连接";
        }

        private async void BtnOpen_Click(object? sender, EventArgs e)
        {
            btnOpen.Enabled = false;
            try
            {
                // 1. 连接 CX：已经在运行就连上它，没有运行就启动一个。
                //    CX 注册类工厂时用的是 REGCLS_MULTIPLEUSE（一个进程服务所有客户端），
                //    不是 REGCLS_SINGLEUSE（每个客户端各起一个进程），所以不会重复启动 CX。
                _cx = new Application();
                _cx.Visible = true;
                _cx.AutoExit = false;

                // 2. 等待 CX 启动完成（最多 2 分钟）
                Text = "Nanosurf CX - 等待启动...";
                for (int i = 0; i < 240; i++)
                {
                    try
                    {
                        if (!_cx.IsStartingUp())
                        {
                            // 3. 启动完成后获取 Approach 代理（Approach 属性返回 object，需要强制转换）
                            _approach = (IProxyApproach)_cx.Approach;

                            Text = "Nanosurf CX - 已连接";
                            return;
                        }
                    }
                    catch (COMException)
                    {
                        // CX 正忙，稍后重试
                    }
                    await Task.Delay(500);
                }

                Text = "Nanosurf CX - 启动超时";
            }
            catch (Exception ex)
            {
                _approach = null;
                _cx = null;
                Text = "Nanosurf CX - 连接失败";
                MessageBox.Show(this, ex.Message, "Open CX");
            }
            finally
            {
                btnOpen.Enabled = true;
            }
        }

        private async void BtnClose_Click(object? sender, EventArgs e)
        {
            btnClose.Enabled = false;
            try
            {
                // 1. 释放 COM 引用（CX 没有 Quit 方法）。先释放子对象，再释放 _cx
                if (_approach != null)
                {
                    Marshal.FinalReleaseComObject(_approach);
                    _approach = null;
                }
                if (_cx != null)
                {
                    Marshal.FinalReleaseComObject(_cx);
                    _cx = null;
                }

                // 2. 让 CX 关闭主窗口，相当于点右上角的 ×
                foreach (Process process in Process.GetProcessesByName("CX"))
                {
                    Text = "Nanosurf CX - 正在关闭...";
                    process.CloseMainWindow();

                    // 3. 15 秒内没退出，询问是否强制结束
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                    try
                    {
                        await process.WaitForExitAsync(cts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        var answer = MessageBox.Show(this, "CX 15 秒内没有退出，是否强制结束？\n可能丢失未保存的数据。",
                            "Close CX", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
                        if (answer == DialogResult.Yes)
                            process.Kill();
                    }
                }

                Text = "Nanosurf CX - 未连接";
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Close CX");
            }
            finally
            {
                btnClose.Enabled = true;
            }
        }
    }
}
