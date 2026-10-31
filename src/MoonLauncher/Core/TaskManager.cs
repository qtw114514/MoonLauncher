using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace MoonLauncher.Core
{
    /// <summary>
    /// UI 线程调度抽象。
    /// 业务层只认这个接口，不引用任何 UI 框架（WPF / Avalonia 各自的 Dispatcher）。
    /// 由启动代码（App）实现并注入；没有注入时（单元测试、纯后端进程）全部同步执行。
    /// </summary>
    public interface IUiDispatcher
    {
        /// <summary>当前线程是否就是 UI 线程</summary>
        bool IsOnUiThread { get; }

        /// <summary>异步投递到 UI 线程，不等待</summary>
        void Post(Action action);

        /// <summary>投递到 UI 线程并等它执行完</summary>
        Task InvokeAsync(Action action);
    }

    public enum TaskState { Running, Paused, Done, Failed, Canceled }

    /// <summary>一个后台任务（安装版本、下载 Mod、导入整合包……），界面上的任务列表直接绑定它</summary>
    public class LauncherTask : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;
        void On([CallerMemberName] string p = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));

        public CancellationTokenSource Cts { get; } = new CancellationTokenSource();
        public CancellationToken Token => Cts.Token;
        public DateTime Started { get; } = DateTime.Now;
        internal Func<LauncherTask, Task> RetryFactory { get; set; }

        readonly SemaphoreSlim _pauseGate = new SemaphoreSlim(1, 1);
        readonly object _pauseSync = new object();
        string _title, _detail, _error;
        double _progress;
        long _speed;
        TaskState _state = TaskState.Running;

        public string Title { get => _title; set { _title = value; On(); } }
        public string Detail { get => _detail; set { _detail = value; On(); } }
        public string Error { get => _error; set { _error = value; On(); } }
        /// <summary>0~1；小于 0 表示不确定进度</summary>
        public double Progress { get => _progress; set { _progress = Math.Max(-1, Math.Min(1, value)); On(); On(nameof(ProgressText)); On(nameof(Indeterminate)); } }
        public long Speed { get => _speed; set { _speed = value; On(); On(nameof(SpeedText)); } }
        public TaskState State { get => _state; set { _state = value; On(); On(nameof(IsActive)); On(nameof(StateText)); On(nameof(CanRetry)); On(nameof(CanPause)); On(nameof(CanResume)); On(nameof(CanCancel)); } }

        public bool IsActive => State == TaskState.Running || State == TaskState.Paused;
        public bool CanRetry => (State == TaskState.Failed || State == TaskState.Canceled) && RetryFactory != null;
        public bool CanPause => State == TaskState.Running;
        public bool CanResume => State == TaskState.Paused;
        public bool CanCancel => IsActive;
        public bool Indeterminate => _progress < 0;
        public string ProgressText => _progress < 0 ? "" : $"{_progress * 100:0}%";
        public string SpeedText => _speed > 0 ? Util.FormatSize(_speed) + "/s" : "";
        public string StateText => State switch
        {
            TaskState.Paused => "已暂停",
            TaskState.Done => "已完成",
            TaskState.Failed => "失败",
            TaskState.Canceled => "已取消",
            _ => "进行中"
        };

        public void Report(double progress, string detail = null)
        {
            Progress = progress;
            if (detail != null) Detail = detail;
        }

        public void Pause()
        {
            lock (_pauseSync)
            {
                if (State != TaskState.Running || !_pauseGate.Wait(0)) return;
                State = TaskState.Paused;
                Speed = 0;
            }
        }

        public void Resume()
        {
            lock (_pauseSync)
            {
                if (State != TaskState.Paused) return;
                State = TaskState.Running;
                _pauseGate.Release();
            }
        }

        /// <summary>在下载等可暂停操作的安全点调用；暂停和取消都能唤醒等待。</summary>
        public async Task WaitIfPausedAsync(CancellationToken cancellationToken = default(CancellationToken))
        {
            await _pauseGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            _pauseGate.Release();
        }

        public void Cancel() { try { Cts.Cancel(); } catch { } }
    }

    public static class TaskManager
    {
        public static ObservableCollection<LauncherTask> Tasks { get; } = new ObservableCollection<LauncherTask>();

        /// <summary>UI 线程调度器，由启动代码注入；为 null 时所有回调就地在调用线程执行</summary>
        public static IUiDispatcher Ui { get; set; }

        /// <summary>任务结束（成功/失败/取消）时触发，在 UI 线程上</summary>
        public static event Action<LauncherTask> Finished;
        public static event Action<LauncherTask> Added;

        public static LauncherTask Start(string title, Func<LauncherTask, Task> body, bool canRetry = true)
        {
            var t = new LauncherTask { Title = title, Progress = -1, Detail = "准备中…", RetryFactory = canRetry ? body : null };
            OnUi(() => { Tasks.Insert(0, t); Added?.Invoke(t); });
            Task.Run(async () =>
            {
                try
                {
                    await body(t).ConfigureAwait(false);
                    t.Progress = 1;
                    t.Detail = "完成";
                    t.State = TaskState.Done;
                    Log.Info($"任务完成：{title}");
                }
                catch (OperationCanceledException)
                {
                    t.State = TaskState.Canceled;
                    t.Detail = "已取消";
                }
                catch (Exception ex)
                {
                    var msg = Flatten(ex);
                    t.Error = msg;
                    t.Detail = msg;
                    t.State = TaskState.Failed;
                    Log.Error($"任务失败：{title}", ex);
                }
                t.Speed = 0;
                OnUi(() => Finished?.Invoke(t));
            });
            return t;
        }

        /// <summary>用原任务工厂创建全新任务和取消令牌，旧任务保留在列表中。</summary>
        public static LauncherTask Retry(LauncherTask task)
        {
            if (task == null) throw new ArgumentNullException(nameof(task));
            if (!task.CanRetry) throw new InvalidOperationException("只有失败或已取消的任务可以重试。");
            return Start(task.Title, task.RetryFactory);
        }

        public static string Flatten(Exception ex)
        {
            if (ex is AggregateException ag && ag.InnerExceptions.Count > 0) ex = ag.InnerExceptions[0];
            while (ex is System.Reflection.TargetInvocationException && ex.InnerException != null) ex = ex.InnerException;
            return ex.Message;
        }

        public static void OnUi(Action a)
        {
            if (a == null) return;
            if (Ui == null || Ui.IsOnUiThread) a();
            else Ui.Post(a);
        }

        /// <summary>在 UI 线程上执行并等它完成（没有 UI 时直接执行）</summary>
        public static Task OnUiAsync(Action a)
        {
            if (a == null) return Task.CompletedTask;
            if (Ui == null || Ui.IsOnUiThread) { a(); return Task.CompletedTask; }
            return Ui.InvokeAsync(a);
        }

        public static void ClearFinished()
        {
            for (int i = Tasks.Count - 1; i >= 0; i--)
                if (!Tasks[i].IsActive) Tasks.RemoveAt(i);
        }

    }
}
