# Nanosurf-CX

## 为什么 COM 调用要放在独立的 STA 线程上

### 现象

最早的看门狗把 COM 心跳直接写在 UI 线程上，每秒调用一次 `IsStartingUp()`：

```csharp
private async void BtnListen_Click(object? sender, EventArgs e)
{
    while (!cts.IsCancellationRequested)
    {
        ...
        // COM 心跳：直接在 UI 线程上调用
        status = _cx.IsStartingUp() ? "CX 启动中" : "已连接";
        ...
        await Task.Delay(1000, cts.Token);
    }
}
```

开着看门狗拖动窗口时，窗口每秒会“顿”一下。

### 原因

**COM 心跳 `_cx.IsStartingUp()` 是在 UI 线程上执行的，而它要等 CX 回复才能返回。**

1. `_cx` 是在 UI 线程上创建的，所以对它的每次调用也都在 UI 线程上执行。
2. `IsStartingUp()` 是一次跨进程调用：UI 线程把请求发给 CX.exe，然后原地等待 CX 回复。
3. CX 要等它自己的 UI 线程有空才会处理这个请求。CX 一直在刷新图表、和仪器通信，经常是忙的，一次调用可能要等几十甚至几百毫秒。
4. 等待期间，我们的 UI 线程什么都做不了，窗口没法跟着鼠标移动。
5. 看门狗每秒执行一次心跳，所以拖动时每秒都会卡一下。

`await` 会让出 UI 线程，但只对 `await` 的那几行（`Task.Run`、`Task.Delay`）有效。`_cx.IsStartingUp()` 前面没有 `await`，是同步调用，会一直占着 UI 线程直到 CX 回复。

把调用改成 `await Task.Run(() => _cx.IsStartingUp())` 也解决不了：线程池线程是 MTA，COM 会把调用封送回创建 `_cx` 的 UI 线程（STA）执行，UI 线程还是要等。

### 验证

| 操作 | 结果 | 说明 |
|---|---|---|
| 不点 Open，只点 Listen，拖动窗口 | 不卡 | `_cx` 是 null，跳过心跳，UI 线程不用等 CX |
| 先点 Open，再点 Listen，拖动窗口 | 卡 | 每秒都要在 UI 线程上等一次 CX 回复 |

### 解决

新增 `StaThread`：一个专门执行 COM 调用的 STA 线程。`_cx` 在这个线程上创建，对它的所有调用都通过 `_com.Run(...)` 投递过去执行：

```csharp
heartbeat = _com.Run(() => _cx?.IsStartingUp());

// 最多等 3 秒，超时就判定 COM 无响应
if (await Task.WhenAny(heartbeat, Task.Delay(3000, cts.Token)) != heartbeat)
    status = "COM 无响应";
```

等 CX 回复的是 COM 线程，UI 线程只 `await` 结果，等待期间照常处理拖动和绘制。CX 卡死时，卡住的也只是 COM 线程，看门狗还能通过超时检测出来。

**规则：凡是用到 `_cx` 或 `_approach` 的调用（包括 `Marshal.FinalReleaseComObject`），都写在 `_com.Run(...)` 里。**
