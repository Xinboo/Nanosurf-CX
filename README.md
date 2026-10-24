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

## 心跳用 `Version`，不要用 `IsStartingUp()`

### 现象

COM 调用移到独立的 STA 线程后，我们自己的窗口不卡了，但开着看门狗拖动 **CX 的窗口**时，CX 会卡。

### 测试

看门狗每秒心跳一次，用 `Check IsStartingUp` 和 `Check Version` 两个开关分别控制调用哪个方法，然后拖动 CX 的窗口：

| 心跳调用 | CX 窗口拖动 |
|---|---|
| `IsStartingUp()` | 卡 |
| `Version` | 不卡 |

### 原因

CX 的 COM 对象运行在 **CX 自己的 UI 线程**上。我们每发一次 COM 调用，都要占用一次 CX 的 UI 线程，而这个线程同时负责 CX 窗口的拖动和绘制：

```
我们的 COM 线程 ──IsStartingUp()──▶ CX 的 UI 线程（同时负责 CX 的窗口拖动、绘制）
```

`IsStartingUp()` 在 CX 内部要做实际的工作，大概率会去查询控制器的状态，每次都要占用一段时间，所以 CX 的窗口每秒卡一下；`Version` 只是读取一个属性值，几乎不占时间。

### 结论

- 定期轮询 CX 时（比如看门狗心跳），优先读 `Version` 这类轻量的属性。
- `IsStartingUp()` 只在 Open 之后等待 CX 启动完成时用。
- 不管我们这边怎么写，每次 COM 调用都会占用 CX 的 UI 线程，所以调用频率要控制住。

### 只读 `Version`，怎么知道 CX 是不是启动中

问题：CX 启动中也能读到版本号，所以 `Version` 判断不了 CX 是否启动完成；而 `IsStartingUp()` 又会让 CX 卡顿。

关键在于：**“启动中”是一次性的状态。** CX 每次启动只经历一次，启动完成后不会再回到启动中。所以两个方法各管一段：

| 阶段 | 用什么检查 | 原因 |
|---|---|---|
| 连接时，等待 CX 启动完成 | `IsStartingUp()` | 只有它能判断启动是否完成。这段时间 CX 本来就在启动，卡一点没关系 |
| 启动完成后，看门狗心跳 | `Version` | 只需要确认连接还活着，读版本号就够了，不会卡 |

为什么之后不用再检查“启动中”：CX 要回到启动中，只能是退出后重新启动。CX 一退出，旧的 COM 连接就失效了：

1. 心跳读 `Version` 抛出 COM 异常（比如 `RPC_E_DISCONNECTED`）；
2. 看门狗释放引用，显示“连接已断开”；
3. 自动重连调用 `ConnectAsync()`，又会用 `IsStartingUp()` 等 CX 启动完成。

这依赖一条规则：**`_cx` 不为 null，就代表 CX 已经启动完成。** 所以：

- `_cx` 只由 `ConnectAsync()` 赋值，而且要等 `IsStartingUp()` 返回 false 之后才赋值；
- 等待启动期间，新建的连接只放在局部变量 `cx` 里。如果先赋值给 `_cx` 再等待，看门狗就会在 CX 还没启动完成时读到 `Version`，误报“已连接”；
- 等待启动超时或出错时，`ConnectAsync()` 自己释放局部变量 `cx`，`_cx` 保持不变。
