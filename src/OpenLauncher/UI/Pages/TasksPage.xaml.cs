using System;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using OpenLauncher.Core;

namespace OpenLauncher.UI.Pages
{
    public partial class TasksPage : UserControl, IPage
    {
        readonly ICollectionView _view;

        public TasksPage()
        {
            InitializeComponent();
            _view = CollectionViewSource.GetDefaultView(TaskManager.Tasks);
            _view.Filter = TaskFilter;
            TaskList.ItemsSource = _view;
            TaskManager.Tasks.CollectionChanged += (s, e) => Refresh();
            TaskManager.Added += t => Refresh();
            TaskManager.Finished += t => Refresh();
            Refresh();
        }

        public void OnShown() => Refresh();

        bool TaskFilter(object item) => item is LauncherTask task && task.IsActive == (ActiveFilter.IsChecked == true);

        void Filter_Checked(object sender, RoutedEventArgs e)
        {
            if (_view != null) Refresh();
        }

        void Refresh()
        {
            if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke((Action)Refresh); return; }
            _view?.Refresh();
            int active = TaskManager.Tasks.Count(t => t.IsActive);
            int history = TaskManager.Tasks.Count - active;
            ActiveCountText.Text = active.ToString();
            HistoryCountText.Text = history.ToString();
            ClearFinishedButton.IsEnabled = history > 0;
            bool empty = _view == null || _view.IsEmpty;
            EmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
            TaskList.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
            bool showingActive = ActiveFilter.IsChecked == true;
            EmptyTitle.Text = showingActive ? "没有活动任务" : "没有历史任务";
            EmptyText.Text = showingActive
                ? "新的下载与安装会显示在这里；也可以先浏览资源。"
                : "完成的任务会保留在这里，便于查看结果和失败诊断。";
        }

        static LauncherTask TaskOf(object sender) => (sender as FrameworkElement)?.DataContext as LauncherTask;
        static bool CanRetry(LauncherTask task) => task != null && task.CanRetry && !task.Title.StartsWith("启动 ", StringComparison.Ordinal);

        void ClearTasks_Click(object sender, RoutedEventArgs e) { TaskManager.ClearFinished(); Refresh(); }
        void CancelTask_Click(object sender, RoutedEventArgs e) => TaskOf(sender)?.Cancel();
        void PauseTask_Click(object sender, RoutedEventArgs e) => TaskOf(sender)?.Pause();
        void ResumeTask_Click(object sender, RoutedEventArgs e) => TaskOf(sender)?.Resume();
        void BrowseResources_Click(object sender, RoutedEventArgs e) => MainWindow.Current?.GoTab("resources");

        void RetryTask_Click(object sender, RoutedEventArgs e)
        {
            var task = TaskOf(sender);
            if (CanRetry(task)) TaskManager.Retry(task);
        }

        void CopyTask_Click(object sender, RoutedEventArgs e)
        {
            var task = TaskOf(sender);
            if (task == null) return;
            try
            {
                Clipboard.SetText(Diagnostics(task));
                MainWindow.Current?.Toast("诊断信息已复制", MainWindow.ToastKind.Ok);
            }
            catch (Exception ex) { MainWindow.Current?.Toast("复制失败：" + ex.Message, MainWindow.ToastKind.Error); }
        }

        void TaskDetails_Click(object sender, RoutedEventArgs e)
        {
            var task = TaskOf(sender);
            if (task == null) return;
            var summary = string.IsNullOrWhiteSpace(task.Error) ? task.Detail : task.Error;
            var actions = new System.Collections.Generic.List<ErrorDialogAction>();
            if (CanRetry(task))
                actions.Add(new ErrorDialogAction("重试任务", () => TaskManager.Retry(task), primary: true));
            _ = ErrorDialog.ShowAsync(task.Title + "失败", summary,
                CanRetry(task) ? "可以直接重试；重试会创建新任务并保留这条历史记录。" : "请根据原因处理后，从原页面重新发起任务。启动任务请返回启动页处理。",
                Diagnostics(task), actions);
        }

        static string Diagnostics(LauncherTask task) =>
            $"{task.Title}\r\n状态：{task.StateText}\r\n开始时间：{task.Started:yyyy-MM-dd HH:mm:ss}\r\n{task.Detail}\r\n{task.Error}";
    }

    /// <summary>启动任务即使带有旧重试工厂，也不在下载中心提供重试。</summary>
    public sealed class TaskRetryVisibilityConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            bool canRetry = values.Length > 0 && values[0] is bool b && b;
            string title = values.Length > 1 ? values[1] as string : null;
            return canRetry && !(title ?? "").StartsWith("启动 ", StringComparison.Ordinal) ? Visibility.Visible : Visibility.Collapsed;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }
}
