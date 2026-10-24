using System.Collections.Concurrent;

namespace Nanosurf_CX.WinFormsApp
{
    /// <summary>
    /// 专门执行 COM 调用的 STA 线程。
    /// 在这个线程上创建的 COM 对象只能在这个线程上调用，所以对 CX 的所有调用都通过 Run 投递过来，排队执行。
    /// CX 卡住时只会卡住这个线程，不会卡住 UI。
    /// </summary>
    internal sealed class StaThread
    {
        private readonly BlockingCollection<Action> _queue = new();

        public StaThread(string name)
        {
            var thread = new Thread(() =>
            {
                // 队列为空时在这里等待。STA 线程上的托管等待会处理 COM 需要的消息
                foreach (Action action in _queue.GetConsumingEnumerable())
                    action();
            });
            thread.Name = name;
            thread.IsBackground = true;     // 程序退出时不等这个线程
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }

        /// <summary>
        /// 把 func 投递到 STA 线程上执行，返回它的结果。func 抛出的异常会在 await 时重新抛出。
        /// </summary>
        public Task<T> Run<T>(Func<T> func)
        {
            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            _queue.Add(() =>
            {
                try
                {
                    tcs.SetResult(func());
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
            });
            return tcs.Task;
        }

        public Task Run(Action action) => Run(() => { action(); return true; });
    }
}
