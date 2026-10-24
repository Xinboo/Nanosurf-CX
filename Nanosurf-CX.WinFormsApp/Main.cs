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
        // 专门执行 COM 调用的 STA 线程。_cx 和 _approach 在这个线程上创建，
        // 对它们的所有调用（包括释放）都要写在 _com.Run(...) 里，不能在 UI 线程上直接调用。
        private readonly StaThread _com = new StaThread("CX COM");

        // CX 的 COM 对象（早期绑定，类型来自 COM 引用）。
        private Application? _cx;

        // 进针模块的代理。连接成功后获取一次，关闭时和 _cx 一起释放。
        private IProxyApproach? _approach;

        // 看门狗运行时不为 null；Cancel() 它就会停止看门狗。
        private CancellationTokenSource? _watchdog;

        public Main()
        {
            InitializeComponent();
            btnOpen.Click += BtnOpen_Click;
            btnClose.Click += BtnClose_Click;
            btnListen.Click += BtnListen_Click;
            FormClosed += (s, e) => _watchdog?.Cancel();
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
                await _com.Run(() =>
                {
                    _cx = new Application();
                    _cx.Visible = true;
                    _cx.AutoExit = false;
                });

                // 2. 等待 CX 启动完成（最多 2 分钟）
                Text = "Nanosurf CX - 等待启动...";
                for (int i = 0; i < 240; i++)
                {
                    try
                    {
                        if (!await _com.Run(() => _cx!.IsStartingUp()))
                        {
                            // 3. 启动完成后获取 Approach 代理（Approach 属性返回 object，需要强制转换）
                            await _com.Run(() => _approach = (IProxyApproach)_cx!.Approach);

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
                Task release = _com.Run(() =>
                {
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
                });

                // COM 线程卡住时（CX 无响应）最多等 3 秒，然后照样往下关闭 CX
                await Task.WhenAny(release, Task.Delay(3000));

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

        private async void BtnListen_Click(object? sender, EventArgs e)
        {
            // 看门狗已经在运行：再点一次就停止
            if (_watchdog != null)
            {
                _watchdog.Cancel();
                _watchdog = null;
                btnListen.Text = "Listen CX";
                return;
            }

            var cts = new CancellationTokenSource();
            _watchdog = cts;
            btnListen.Text = "Stop Listen";

            // 最近一次投递到 COM 线程的心跳。它一直没完成，说明 COM 线程被 CX 卡住了
            Task<bool?>? heartbeat = null;

            try
            {
                // 每秒检查一次 CX 的状态
                while (!cts.IsCancellationRequested)
                {
                    string status;
                    Process[] processes = Process.GetProcessesByName("CX");

                    // 1. CX 进程还在吗？
                    if (processes.Length == 0)
                    {
                        status = "CX 未运行";
                    }
                    // 2. CX 界面有没有卡死？
                    //    Responding 内部用 SendMessageTimeout 探测主窗口，最多等 5 秒。它不经过 COM，
                    //    所以 CX 卡死时不会把我们也卡住；放到后台线程执行，不阻塞 UI。
                    else if (!await Task.Run(() => processes[0].Responding))
                    {
                        status = "CX 无响应";
                    }
                    // 3. 没有建立 COM 连接，只报告进程状态
                    else if (_cx == null)
                    {
                        status = "CX 运行中（未连接）";
                    }
                    // 4. 上一次心跳还没返回：COM 线程被卡住了，不再往它的队列里塞新的调用
                    else if (heartbeat != null && !heartbeat.IsCompleted)
                    {
                        status = "COM 无响应";
                    }
                    // 5. COM 心跳：在 COM 线程上调用一个轻量的方法，最多等 3 秒
                    else
                    {
                        heartbeat = _com.Run(() => _cx?.IsStartingUp());
                        try
                        {
                            if (await Task.WhenAny(heartbeat, Task.Delay(3000, cts.Token)) != heartbeat)
                                status = "COM 无响应";
                            else if (await heartbeat is bool starting)
                                status = starting ? "CX 启动中" : "已连接";
                            else
                                status = "CX 运行中（未连接）";    // 等待期间 _cx 被 Close 释放了
                        }
                        catch (COMException ex) when (ex.HResult == unchecked((int)0x80010001)      // RPC_E_CALL_REJECTED
                                                   || ex.HResult == unchecked((int)0x8001010A))     // RPC_E_SERVERCALL_RETRYLATER
                        {
                            // CX 弹出对话框或正在忙，连接本身没问题
                            status = "CX 正忙";
                        }
                        catch (Exception ex) when (ex is COMException || ex is InvalidComObjectException)
                        {
                            // 连接已失效（CX 退出或崩溃），释放旧的引用，之后需要重新 Open
                            await _com.Run(() =>
                            {
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
                            });
                            status = "连接已断开";
                        }
                    }

                    foreach (Process process in processes)
                        process.Dispose();

                    // 等待期间可能已经停止看门狗或关闭了窗体，这时不要再更新界面
                    if (cts.IsCancellationRequested)
                        break;

                    Text = $"Nanosurf CX - {status}（看门狗 {DateTime.Now:HH:mm:ss}）";
                    await Task.Delay(1000, cts.Token);
                }
            }
            catch (OperationCanceledException)
            {
                // Task.Delay 被取消，看门狗正常停止
            }
        }
    }
}
