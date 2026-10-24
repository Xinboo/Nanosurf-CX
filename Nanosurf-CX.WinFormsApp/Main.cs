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

        // 用户想不想保持连接：Open 设为 true，Close 设为 false。只有为 true 时看门狗才会自动重连
        private bool _wantConnected;

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
            _wantConnected = true;
            try
            {
                Text = "Nanosurf CX - 等待启动...";
                Text = await ConnectAsync() ? "Nanosurf CX - 已连接" : "Nanosurf CX - 启动超时";
            }
            catch (Exception ex)
            {
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
            _wantConnected = false;     // 用户主动断开，看门狗不再自动重连
            try
            {
                // 1. 释放 COM 引用（CX 没有 Quit 方法）。
                //    COM 线程卡住时（CX 无响应）最多等 3 秒，然后照样往下关闭 CX
                await Task.WhenAny(ReleaseCxAsync(), Task.Delay(3000));

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
            Task<string?>? heartbeat = null;

            // 正在进行的自动重连。它没完成之前不发起新的重连
            Task<bool>? reconnect = null;

            // 自动重连失败后，到这个时间之前不再重试
            DateTime nextReconnect = DateTime.MinValue;

            try
            {
                // 每秒检查一次 CX 的状态
                while (!cts.IsCancellationRequested)
                {
                    string status;
                    Process[] processes = Process.GetProcessesByName("CX");

                    // 上一次自动重连已经结束：失败的话 10 秒后再试（连了一半的连接 ConnectAsync 已经释放了）
                    if (reconnect != null && reconnect.IsCompleted)
                    {
                        if (!reconnect.IsCompletedSuccessfully || !reconnect.Result)
                            nextReconnect = DateTime.Now.AddSeconds(10);
                        reconnect = null;
                    }

                    // 1. CX 进程还在吗？不在的话只等待，不去启动 CX
                    if (processes.Length == 0)
                    {
                        status = _wantConnected ? "CX 未运行（CX 启动后自动重连）" : "CX 未运行";
                    }
                    // 2. CX 界面有没有卡死？
                    //    Responding 内部用 SendMessageTimeout 探测主窗口，最多等 5 秒。它不经过 COM，
                    //    所以 CX 卡死时不会把我们也卡住；放到后台线程执行，不阻塞 UI。
                    else if (!await Task.Run(() => processes[0].Responding))
                    {
                        status = "CX 无响应";
                    }
                    // 3. 自动重连正在进行
                    else if (reconnect != null)
                    {
                        status = "正在重连...";
                    }
                    // 4. 没有 COM 连接：满足条件就自动重连，否则只报告进程状态
                    else if (_cx == null)
                    {
                        // CX 刚启动的前 15 秒不重连：这时 CX 可能还没向 COM 注册类工厂，
                        // new Application() 找不到正在运行的 CX，会再启动一个新的 CX
                        bool startedLongEnough;
                        try
                        {
                            startedLongEnough = DateTime.Now - processes[0].StartTime > TimeSpan.FromSeconds(15);
                        }
                        catch (Win32Exception)
                        {
                            startedLongEnough = false;      // 读不到启动时间（比如 CX 以管理员身份运行），不重连
                        }

                        if (_wantConnected && startedLongEnough && DateTime.Now >= nextReconnect)
                        {
                            reconnect = ConnectAsync();
                            status = "正在重连...";
                        }
                        else
                        {
                            status = "CX 运行中（未连接）";
                        }
                    }
                    // 5. 上一次心跳还没返回：COM 线程被卡住了，不再往它的队列里塞新的调用
                    else if (heartbeat != null && !heartbeat.IsCompleted)
                    {
                        status = "COM 无响应";
                    }
                    // 6. COM 心跳：在 COM 线程上读取 Version，最多等 3 秒。
                    //    只读 Version，不调用 IsStartingUp()：后者会让 CX 的窗口卡顿（见 README）。
                    //    也不需要判断“启动中”：_cx 只由 ConnectAsync() 在 CX 启动完成后才赋值；
                    //    CX 重启时旧连接会失效，这里读 Version 会抛 COM 异常，然后走断开、重连的流程
                    else
                    {
                        heartbeat = _com.Run(() => _cx?.Version);
                        try
                        {
                            if (await Task.WhenAny(heartbeat, Task.Delay(3000, cts.Token)) != heartbeat)
                                status = "COM 无响应";
                            else if (await heartbeat is string version)
                                status = $"已连接（CX {version}）";
                            else
                                status = "CX 运行中（未连接）";    // null：等待期间 _cx 被 Close 释放了
                        }
                        catch (COMException ex) when (ex.HResult == unchecked((int)0x80010001)      // RPC_E_CALL_REJECTED
                                                   || ex.HResult == unchecked((int)0x8001010A))     // RPC_E_SERVERCALL_RETRYLATER
                        {
                            // CX 弹出对话框或正在忙，连接本身没问题
                            status = "CX 正忙";
                        }
                        catch (Exception ex) when (ex is COMException || ex is InvalidComObjectException)
                        {
                            // 连接已失效（CX 退出或崩溃），释放旧的引用。
                            // 没有点过 Close 的话，CX 重新运行后会在第 4 步自动重连
                            await ReleaseCxAsync();
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

        /// <summary>
        /// 连接 CX，等待启动完成后获取子对象。Open 和看门狗的自动重连共用。
        /// 返回 false 表示等待启动超时。超时或出错时，这次新建的连接会在这里释放掉。
        /// </summary>
        private async Task<bool> ConnectAsync()
        {
            // 新建的连接先放在局部变量里，等 CX 启动完成才赋值给 _cx。
            // 这样看门狗只要看到 _cx 不为 null，CX 就一定已经启动完成
            Application? cx = null;
            try
            {
                // 1. 连接 CX：已经在运行就连上它，没有运行就启动一个。
                //    CX 注册类工厂时用的是 REGCLS_MULTIPLEUSE（一个进程服务所有客户端），
                //    不是 REGCLS_SINGLEUSE（每个客户端各起一个进程），所以不会重复启动 CX。
                await _com.Run(() =>
                {
                    cx = new Application();
                    cx.Visible = true;
                    cx.AutoExit = false;
                });

                // 2. 等待 CX 启动完成（最多 2 分钟）
                for (int i = 0; i < 240; i++)
                {
                    try
                    {
                        if (!await _com.Run(() => cx!.IsStartingUp()))
                        {
                            // 3. 启动完成：先获取子对象（属性返回 object，需要强制转换），最后才赋值 _cx。
                            //    以后加了 _scan 等其他代理，也要在这里获取，重连时才会一起重新获取
                            await _com.Run(() =>
                            {
                                _approach = (IProxyApproach)cx!.Approach;
                                _cx = cx;
                            });
                            cx = null;      // 已经交给 _cx 了，下面的 finally 不要释放它
                            return true;
                        }
                    }
                    catch (COMException)
                    {
                        // CX 正忙，稍后重试
                    }
                    await Task.Delay(500);
                }

                return false;
            }
            finally
            {
                // 启动超时或出错：释放这次新建的连接
                if (cx != null)
                    await _com.Run(() => Marshal.FinalReleaseComObject(cx!));
            }
        }

        /// <summary>
        /// 在 COM 线程上释放所有 COM 引用。先释放子对象，再释放 _cx。
        /// </summary>
        private Task ReleaseCxAsync()
        {
            return _com.Run(() =>
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
        }
    }
}
