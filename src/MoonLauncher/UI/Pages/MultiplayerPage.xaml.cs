using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using MoonLauncher.Accounts;
using MoonLauncher.Core;
using MoonLauncher.Game;
using MoonLauncher.Multiplayer;

namespace MoonLauncher.UI.Pages
{
    public partial class MultiplayerPage : UserControl, IPage
    {
        const string ServersKey = "multiplayer.servers";
        const string SelectedServerKey = "multiplayer.selectedServer";

        public sealed class ServerEntry
        {
            public string Name { get; set; }
            public string Address { get; set; }
        }

        readonly ObservableCollection<ServerEntry> _servers = new ObservableCollection<ServerEntry>();
        bool _refreshing;
        bool _subscribed;
        bool _launching;
        LauncherTask _activeOperation;
        TerracottaState _state;

        public MultiplayerPage()
        {
            InitializeComponent();
            ServerList.ItemsSource = _servers;
            LoadServers();
            RenderState(TerracottaService.Current);
            UpdateServerButtons();
        }

        public void OnShown()
        {
            RefreshAccounts();
            RefreshInstances();
            RenderState(TerracottaService.Current);
        }

        void Page_Loaded(object sender, RoutedEventArgs e)
        {
            if (_subscribed) return;
            _subscribed = true;
            TerracottaService.StateChanged += Terracotta_StateChanged;
            AccountStore.Changed += AccountStore_Changed;
            RefreshAccounts();
            RefreshInstances();
            RenderState(TerracottaService.Current);
        }

        void Page_Unloaded(object sender, RoutedEventArgs e)
        {
            _subscribed = false;
            TerracottaService.StateChanged -= Terracotta_StateChanged;
            AccountStore.Changed -= AccountStore_Changed;
            // 仅取消本页尚未完成的 HTTP / 安装操作。切页不能关闭陶瓦进程或房间；
            // 进程的统一退出由 App 生命周期负责。
            if (_activeOperation != null && _activeOperation.IsActive) _activeOperation.Cancel();
        }

        // ---------- 共用账户与实例 ----------
        Account SelectedAccount() => (AccountBox.SelectedItem as Account) ?? AccountStore.Selected;
        Instance SelectedInstance() => (InstanceBox.SelectedItem as Instance) ?? LaunchPage.SelectedInstance();

        void RefreshAccounts()
        {
            if (AccountBox == null) return;
            _refreshing = true;
            try
            {
                var selected = AccountStore.Selected;
                AccountBox.ItemsSource = AccountStore.All;
                AccountBox.SelectedItem = AccountStore.All.FirstOrDefault(a => selected != null && a.Id == selected.Id) ?? selected;
            }
            finally { _refreshing = false; }
        }

        void RefreshInstances()
        {
            if (InstanceBox == null) return;
            _refreshing = true;
            try
            {
                var list = GameRepo.Current.ListInstances().Where(i => i.IsValid).ToList();
                InstanceBox.ItemsSource = list;
                string id = Settings.SelectedVersion;
                InstanceBox.SelectedItem = list.FirstOrDefault(i => i.Id == id) ?? list.FirstOrDefault();
            }
            finally { _refreshing = false; }
        }

        void AccountStore_Changed()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke((Action)(() => { if (_subscribed) RefreshAccounts(); }));
                return;
            }
            if (_subscribed) RefreshAccounts();
        }

        void AccountBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_refreshing && AccountBox.SelectedItem is Account account) AccountStore.Selected = account;
        }

        void InstanceBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_refreshing && InstanceBox.SelectedItem is Instance instance) Settings.SelectedVersion = instance.Id;
        }

        async void ManageAccounts_Click(object sender, RoutedEventArgs e)
        {
            await AccountDialogs.ManageAsync();
            if (IsLoaded) RefreshAccounts();
        }

        void RefreshInstances_Click(object sender, RoutedEventArgs e) => RefreshInstances();

        // ---------- 标签 ----------
        void Tab_Checked(object sender, RoutedEventArgs e)
        {
            if (ServerPanel == null || P2PPanel == null || ServerTab == null || P2PTab == null) return;
            bool p2p = P2PTab.IsChecked == true;
            ServerPanel.Visibility = p2p ? Visibility.Collapsed : Visibility.Visible;
            P2PPanel.Visibility = p2p ? Visibility.Visible : Visibility.Collapsed;
            if (p2p) RenderState(TerracottaService.Current);
        }

        // ---------- 业务服务器列表 ----------
        ServerEntry SelectedServer() => ServerList.SelectedItem as ServerEntry;

        void LoadServers()
        {
            _servers.Clear();
            try
            {
                var nodes = Settings.GetNode(ServersKey);
                foreach (var node in nodes)
                {
                    string name = node["name"].Str?.Trim();
                    string address = node["address"].Str?.Trim();
                    if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(address))
                        _servers.Add(new ServerEntry { Name = name, Address = address });
                }
            }
            catch (Exception ex) { Log.Warn("读取业务服务器列表失败：" + ex.Message); }

            string selected = Settings.Get(SelectedServerKey);
            ServerList.SelectedItem = _servers.FirstOrDefault(s => string.Equals(s.Address, selected, StringComparison.OrdinalIgnoreCase)) ?? _servers.FirstOrDefault();
            UpdateServerButtons();
        }

        void SaveServers()
        {
            var array = JNode.Arr();
            foreach (var server in _servers)
                array.Add(JNode.Obj().Set("name", server.Name).Set("address", server.Address));
            Settings.Set(ServersKey, array);
            Settings.Set(SelectedServerKey, SelectedServer()?.Address ?? "");
        }

        async Task<ServerEntry> PromptServerAsync(ServerEntry source)
        {
            var panel = new StackPanel();
            panel.Children.Add(new TextBlock { Text = "服务器名称", Style = (Style)FindResource("Sub"), Margin = new Thickness(0, 0, 0, 4) });
            var name = new TextBox { Text = source?.Name ?? "", Tag = "例如：朋友的服务器" };
            panel.Children.Add(name);
            panel.Children.Add(new TextBlock { Text = "服务器地址", Style = (Style)FindResource("Sub"), Margin = new Thickness(0, 12, 0, 4) });
            var address = new TextBox { Text = source?.Address ?? "", Tag = "mc.example.com:25565" };
            panel.Children.Add(address);

            while (true)
            {
                int result = await Dialogs.ShowAsync(source == null ? "新增服务器" : "修改服务器", panel, new[] { "取消", "保存" }, primary: 1);
                if (result != 1) return null;
                string n = name.Text.Trim();
                string a = address.Text.Trim();
                if (string.IsNullOrEmpty(n) || string.IsNullOrEmpty(a))
                {
                    await Dialogs.Alert("信息不完整", "服务器名称和地址都不能为空。");
                    continue;
                }
                return new ServerEntry { Name = n, Address = a };
            }
        }

        async void AddServer_Click(object sender, RoutedEventArgs e)
        {
            var server = await PromptServerAsync(null);
            if (server == null) return;
            _servers.Add(server);
            ServerList.SelectedItem = server;
            SaveServers();
            UpdateServerButtons();
        }

        async void EditServer_Click(object sender, RoutedEventArgs e)
        {
            var selected = SelectedServer();
            if (selected == null) { Dialogs.Toast("请先选择要修改的服务器"); return; }
            var edited = await PromptServerAsync(selected);
            if (edited == null) return;
            selected.Name = edited.Name;
            selected.Address = edited.Address;
            ServerList.Items.Refresh();
            SaveServers();
            UpdateServerButtons();
        }

        async void DeleteServer_Click(object sender, RoutedEventArgs e)
        {
            var selected = SelectedServer();
            if (selected == null) { Dialogs.Toast("请先选择要删除的服务器"); return; }
            if (!await Dialogs.Confirm("删除服务器", $"只从列表删除“{selected.Name}”，不会影响任何游戏文件。", "删除", danger: true)) return;
            _servers.Remove(selected);
            ServerList.SelectedItem = _servers.FirstOrDefault();
            SaveServers();
            UpdateServerButtons();
        }

        void ServerList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            Settings.Set(SelectedServerKey, SelectedServer()?.Address ?? "");
            UpdateServerButtons();
        }

        void UpdateServerButtons()
        {
            if (ServerLaunchButton == null) return;
            var server = SelectedServer();
            ServerLaunchButton.IsEnabled = server != null && !_launching;
            GuestLaunchButton.IsEnabled = !_launching && !string.IsNullOrWhiteSpace(_state?.ServerUrl);
            ServerLaunchDetail.Text = server == null ? "请先选择一个服务器" : $"{server.Name} · {server.Address}";
            ServerEmpty.Visibility = _servers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        async void LaunchServer_Click(object sender, RoutedEventArgs e)
        {
            var server = SelectedServer();
            if (server == null) { Dialogs.Toast("请先选择服务器"); return; }
            await LaunchToAddressAsync(server.Address);
        }

        async void LaunchGuest_Click(object sender, RoutedEventArgs e)
        {
            string address = _state?.ServerUrl;
            if (string.IsNullOrWhiteSpace(address)) { Dialogs.Toast("只有官方 guest-ok 状态提供地址后才能进服"); return; }
            await LaunchToAddressAsync(address);
        }

        async Task LaunchToAddressAsync(string address)
        {
            address = address?.Trim();
            if (string.IsNullOrWhiteSpace(address)) { Dialogs.Toast("服务器地址不能为空", MainWindow.ToastKind.Error); return; }
            if (_launching) { Dialogs.Toast("另一个进服启动流程尚未结束"); return; }

            var account = SelectedAccount();
            if (account == null) { await Dialogs.Alert("需要账户", "请先添加并选择一个游戏账户。"); return; }
            var instance = SelectedInstance();
            if (instance == null) { await Dialogs.Alert("需要实例", "请先安装并选择一个可启动的 Minecraft 实例。"); return; }
            AccountStore.Selected = account;
            Settings.SelectedVersion = instance.Id;

            _launching = true;
            UpdateServerButtons();
            try
            {
                // 仅把地址作为本次启动的内存覆盖，绝不写入实例配置；退出或取消也不会留下临时进服目标。
                await MainWindow.Current.LaunchPage.LaunchAsync(instance, address);
            }
            catch (Exception ex)
            {
                Log.Error("一键进服启动流程异常", ex);
                if (IsLoaded) await Dialogs.Alert("一键进服失败", TaskManager.Flatten(ex));
            }
            finally
            {
                _launching = false;
                UpdateServerButtons();
            }
        }

        // ---------- Terracotta 官方状态 ----------
        void Terracotta_StateChanged(TerracottaState state)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke((Action)(() => { if (_subscribed) RenderState(state); }));
                return;
            }
            if (_subscribed) RenderState(state);
        }

        void RenderState(TerracottaState state)
        {
            if (state == null || P2PStateTitle == null) return;
            _state = state;

            string title;
            string detail;
            switch (state.State)
            {
                case TerracottaState.Waiting:
                    title = "等待创建或加入房间";
                    detail = "陶瓦服务可用，当前没有官方房间连接。";
                    break;
                case TerracottaState.HostScanning:
                case TerracottaState.HostStarting:
                    title = "房间连接中";
                    detail = "官方状态：" + state.State + "。请保持已开放局域网的存档运行。";
                    break;
                case TerracottaState.HostOk:
                    title = "房间已连接 · 已就绪";
                    detail = "官方 host-ok。邀请码和成员均来自官方状态。";
                    break;
                case TerracottaState.GuestConnecting:
                case TerracottaState.GuestStarting:
                    title = "正在连接房间";
                    detail = "官方状态：" + state.State + "，尚未就绪。";
                    break;
                case TerracottaState.GuestOk:
                    title = "房间已连接 · 已就绪";
                    detail = "官方 guest-ok。可使用下方官方 url 一键进服。";
                    break;
                case TerracottaState.Exception:
                    title = "房间连接失败";
                    detail = $"官方 exception（类型 {state.ExceptionType}）。请查看日志，或重置后重试。";
                    break;
                default:
                    title = "陶瓦服务不可用";
                    detail = state.Error ?? "本地 Terracotta 进程或 HTTP 服务不可用。";
                    break;
            }
            P2PStateTitle.Text = title;
            P2PStateDetail.Text = detail;
            UnavailablePanel.Visibility = state.IsAvailable ? Visibility.Collapsed : Visibility.Visible;

            string room = string.IsNullOrWhiteSpace(state.Room) ? null : state.Room;
            InvitationPanel.Visibility = room == null ? Visibility.Collapsed : Visibility.Visible;
            InvitationCode.Text = room ?? "";
            CopyInvitationButton.IsEnabled = room != null;

            GuestLaunchPanel.Visibility = state.IsGuestReady ? Visibility.Visible : Visibility.Collapsed;
            GuestServerUrl.Text = state.ServerUrl ?? "";
            MemberList.ItemsSource = state.Profiles;
            MemberHint.Text = state.IsReady
                ? (state.Profiles.Count == 0 ? "官方就绪状态没有返回成员" : "")
                : "只有官方 host-ok / guest-ok 状态会返回成员列表";
            MemberHint.Visibility = state.IsReady && state.Profiles.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
            UpdateServerButtons();
        }

        void TerracottaProject_Click(object sender, RoutedEventArgs e) => Util.OpenUrl(TerracottaService.ProjectUrl);

        void CopyInvitation_Click(object sender, RoutedEventArgs e)
        {
            string room = _state?.Room;
            if (string.IsNullOrWhiteSpace(room)) { Dialogs.Toast("官方尚未提供邀请码"); return; }
            try { Clipboard.SetText(room); Dialogs.Toast("已复制官方邀请码", MainWindow.ToastKind.Ok); }
            catch (Exception ex) { Dialogs.Toast("复制失败：" + ex.Message, MainWindow.ToastKind.Error); }
        }

        // ---------- TaskManager 中的陶瓦长任务 ----------
        Task<bool> RunOperationAsync(string title, Func<LauncherTask, Task> operation)
            => RunValueOperationAsync(title, async t => { await operation(t); return true; });

        async Task<T> RunValueOperationAsync<T>(string title, Func<LauncherTask, Task<T>> operation)
        {
            if (_activeOperation != null && _activeOperation.IsActive)
            {
                Dialogs.Toast("已有陶瓦操作正在进行，请等待或先取消");
                return default;
            }

            var completion = new TaskCompletionSource<T>();
            var task = TaskManager.Start(title, async t =>
            {
                try { completion.TrySetResult(await operation(t)); }
                catch (OperationCanceledException) { completion.TrySetCanceled(); throw; }
                catch (Exception ex) { completion.TrySetException(ex); throw; }
            }, canRetry: false);
            _activeOperation = task;
            UpdateOperationState();

            try { return await completion.Task; }
            catch (OperationCanceledException)
            {
                if (IsLoaded) Dialogs.Toast("已取消：" + title);
                return default;
            }
            catch (Exception ex)
            {
                if (IsLoaded) await Dialogs.Alert(title + "失败", TaskManager.Flatten(ex));
                return default;
            }
            finally
            {
                if (ReferenceEquals(_activeOperation, task)) _activeOperation = null;
                UpdateOperationState();
                RenderState(TerracottaService.Current);
            }
        }

        void UpdateOperationState()
        {
            if (P2PBusyPanel == null) return;
            bool busy = _activeOperation != null && _activeOperation.IsActive;
            P2PBusyPanel.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            P2PBusyBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            P2PBusyText.Text = busy ? _activeOperation.Title + "（也已加入任务列表）" : "";
            HostButton.IsEnabled = !busy;
            JoinButton.IsEnabled = !busy;
            InstallRetryButton.IsEnabled = !busy;
            ResetButton.IsEnabled = !busy;
        }

        void CancelOperation_Click(object sender, RoutedEventArgs e) => _activeOperation?.Cancel();

        async void InstallRetry_Click(object sender, RoutedEventArgs e)
        {
            await RunOperationAsync("安装并启动 Terracotta | 陶瓦联机", async t =>
            {
                await TerracottaService.EnsureInstalledAsync(t, t.Token);
                await TerracottaService.StartAsync(t, t.Token);
            });
        }

        async void Host_Click(object sender, RoutedEventArgs e)
        {
            var account = SelectedAccount();
            if (account == null) { await Dialogs.Alert("需要账户", "请先选择用于陶瓦联机的游戏账户。"); return; }
            if (!await Dialogs.Confirm("创建陶瓦房间", "请确认已经在 Minecraft 中进入单人存档，并已“对局域网开放”。\n\n创建后请等待官方 host-ok；启动器不会自行伪造房间码或就绪状态。", "创建房间")) return;
            await RunOperationAsync("创建 Terracotta 房间", t => TerracottaService.HostAsync(account.Name, null, t.Token));
        }

        async void Join_Click(object sender, RoutedEventArgs e)
        {
            var account = SelectedAccount();
            if (account == null) { await Dialogs.Alert("需要账户", "请先选择用于陶瓦联机的游戏账户。"); return; }
            if (!TerracottaService.TryNormalizeRoomCode(JoinRoomBox.Text, out string room))
            {
                await Dialogs.Alert("邀请码无效", "请输入官方 U/XXXX-XXXX-XXXX-XXXX 格式且校验正确的邀请码。");
                return;
            }
            JoinRoomBox.Text = room;
            await RunOperationAsync("加入 Terracotta 房间", t => TerracottaService.JoinAsync(room, account.Name, t.Token));
        }

        async void Reset_Click(object sender, RoutedEventArgs e)
        {
            if (!await Dialogs.Confirm("重置 / 关闭房间", "将请求官方 /state/ide 重置为等待状态并关闭当前房间连接，不会退出 Terracotta 进程。", "重置")) return;
            await RunOperationAsync("重置 Terracotta 房间", t => TerracottaService.ResetAsync(t.Token));
        }

        async void Log_Click(object sender, RoutedEventArgs e)
        {
            string log = await RunValueOperationAsync("获取 Terracotta 官方日志",
                t => TerracottaService.GetLogAsync(t.Token));
            if (log != null && IsLoaded)
                await Dialogs.Long("Terracotta 官方日志", string.IsNullOrEmpty(log) ? "（官方日志为空）" : log);
        }
    }
}
