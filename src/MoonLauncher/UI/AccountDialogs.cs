using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.Media;
using MoonLauncher.Accounts;
using MoonLauncher.Core;

namespace MoonLauncher.UI
{
    public static class AccountDialogs
    {
        static string S(string k) => k.ToLowerInvariant();

        public static async Task ManageAsync()
        {
            while (true)
            {
                var panel = new StackPanel();
                var list = new ListBox { Classes = { S("CardList") }, MaxHeight = 260 };
                foreach (var a in AccountStore.All)
                {
                    var row = new Grid { Tag = a };
                    row.ColumnDefinitions.Add(new ColumnDefinition());
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    var info = new StackPanel();
                    info.Children.Add(new TextBlock { Text = a.Name, FontWeight = FontWeight.SemiBold, FontSize = 14 });
                    info.Children.Add(new TextBlock { Text = a.TypeText + (a.Type == AccountType.Yggdrasil ? " · " + a.LoginName : "") + (LocalSkinServer.HasSkin(a) ? " · 已设皮肤" : ""), Classes = { S("Sub") } });
                    row.Children.Add(info);
                    var buttons = new StackPanel { Orientation = Orientation.Horizontal };
                    if (a.Type == AccountType.Offline)
                    {
                        var skinBtn = new Button { Classes = { S("Ghost") }, [ToolTip.TipProperty] = LocalSkinServer.HasSkin(a) ? "更换 / 清除自定义皮肤" : "给离线账号设置自定义皮肤", Content = new TextBlock { Text = "皮肤", FontSize = 12 } };
                        skinBtn.Click += async (s, e) =>
                        {
                            e.Handled = true;
                            await SetSkinAsync(a);
                            Dispatcher.UIThread.Post((Action)(() =>
                                ((TextBlock)info.Children[1]).Text = a.TypeText + (LocalSkinServer.HasSkin(a) ? " · 已设皮肤" : "")));
                        };
                        buttons.Children.Add(skinBtn);
                    }
                    var del = new Button { Classes = { S("Ghost") }, [ToolTip.TipProperty] = "删除这个账号", Content = new TextBlock { Text = "", Classes = { S("Icon") } } };
                    del.Click += async (s, e) =>
                    {
                        e.Handled = true;
                        if (await Dialogs.Confirm("删除账号", $"确定删除账号 {a.Name}（{a.TypeText}）？", "删除", danger: true))
                        {
                            AccountStore.Remove(a);
                            list.Items.Remove(list.Items.Cast<object>().FirstOrDefault(x => (x as Grid)?.Tag == a));
                        }
                    };
                    Grid.SetColumn(buttons, 1);
                    row.Children.Add(buttons);
                    list.Items.Add(row);
                    if (a == AccountStore.Selected) list.SelectedItem = row;
                }
                if (AccountStore.All.Count == 0)
                    panel.Children.Add(new TextBlock { Text = "还没有账号。离线账号可以玩单人和离线服务器；正版需要微软账号；也支持 LittleSkin 等外置登录。", Classes = { S("Body") }, Margin = new Thickness(0, 0, 0, 10) });
                else panel.Children.Add(list);
                list.SelectionChanged += (s, e) =>
                {
                    if ((list.SelectedItem as Grid)?.Tag is Account sel) AccountStore.Selected = sel;
                };

                panel.Children.Add(new TextBlock { Text = "添加账号", Classes = { S("Sub") }, Margin = new Thickness(0, 12, 0, 6) });
                panel.Children.Add(new TextBlock
                {
                    Text = "离线：无需联网，适合单人游戏和允许离线玩家的服务器。\n微软正版：适合已购买 Minecraft Java 版的微软账号，可进入正版验证服务器。\n外置登录：适合 LittleSkin、Blessing Skin 等皮肤站账号，是否可用取决于服务器设置。",
                    Classes = { S("Sub") }, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8)
                });
                var r = await Dialogs.ShowAsync("账号", panel, new[] { "完成", "离线", "微软正版", "外置登录" }, primary: 0, width: 540);
                switch (r)
                {
                    case 1: await AddOfflineAsync(); break;
                    case 2: await AddMicrosoftAsync(); break;
                    case 3: await AddYggdrasilAsync(); break;
                    default: return;
                }
            }
        }

        /// <summary>给离线账号设置 / 更换 / 清除本地自定义皮肤</summary>
        static async Task SetSkinAsync(Account a)
        {
            if (LocalSkinServer.HasSkin(a))
            {
                if (!await Dialogs.Confirm("自定义皮肤", "已设置自定义皮肤。\n\n「更换」重新选择 PNG 文件；「清除」删除皮肤。", "更换", cancel: "清除")) { /* 更换，继续往下 */ }
                else
                {
                    // 清除
                    a.LocalSkin = null;
                    Util.TryDelete(LocalSkinServer.SkinPath(a));
                    AccountStore.AddOrUpdate(a);
                    Skins.Invalidate(a);
                    Dialogs.Toast("已清除皮肤");
                    return;
                }
            }
            var file = await Pickers.PickFileAsync("选择皮肤", "PNG 图片|*.png");
            if (string.IsNullOrEmpty(file)) return;
            byte[] bytes;
            try { bytes = System.IO.File.ReadAllBytes(file); }
            catch (Exception ex) { await Dialogs.Alert("读取失败", ex.Message); return; }
            var err = Skins.ValidateSkin(bytes);
            if (err != null) { await Dialogs.Alert("不是有效的皮肤", err); return; }
            try
            {
                System.IO.File.WriteAllBytes(LocalSkinServer.SkinPath(a), bytes);
            }
            catch (Exception ex) { await Dialogs.Alert("保存失败", ex.Message); return; }
            a.LocalSkin = System.IO.Path.GetFileName(LocalSkinServer.SkinPath(a));
            AccountStore.AddOrUpdate(a);
            Skins.Invalidate(a);
            Dialogs.Toast($"已设置 {a.Name} 的皮肤", MainWindow.ToastKind.Ok);
        }

        static async Task AddOfflineAsync()
        {
            var name = await Dialogs.Prompt("添加离线账号", "玩家名（3~16 个英文字母、数字或下划线）", "", "Steve");
            if (string.IsNullOrWhiteSpace(name)) return;
            if (!OfflineAuth.IsValidName(name, out var reason)) { await Dialogs.Alert("名字不行", reason); return; }
            if (!string.IsNullOrEmpty(reason) && !await Dialogs.Confirm("名字可能有问题", reason + "\n\n仍然使用这个名字？")) return;
            var acc = OfflineAuth.Create(name);
            AccountStore.AddOrUpdate(acc);
            AccountStore.Selected = acc;
            Dialogs.Toast($"已添加离线账号 {name}", MainWindow.ToastKind.Ok);
        }

        /// <summary>重新登录当前账号；只有确实选择了新登录结果才返回 true。</summary>
        public static async Task<bool> ReloginAsync(Account account)
        {
            if (account == null) return false;
            var before = AccountStore.Selected;
            if (account.Type == AccountType.Microsoft) await AddMicrosoftAsync();
            else if (account.Type == AccountType.Yggdrasil) await AddYggdrasilAsync(account);
            else return false;
            var selected = AccountStore.Selected;
            return selected != null && !ReferenceEquals(selected, before);
        }

        static async Task AddMicrosoftAsync()
        {
            if (!MicrosoftAuth.HasClientId)
            {
                var go = await Dialogs.ShowAsync("需要先配置微软登录",
                    Dialogs.Text("微软正版登录需要一个“Azure 应用 Client ID”，并且要向 Mojang 申请 Minecraft API 权限。\n\n" +
                                 "去中心化的启动器不能共用一个会被封的 ID，所以由发布者或你自己申请：\n" +
                                 "1. 在 Azure 门户注册应用（个人 Microsoft 账户，允许公共客户端流）\n" +
                                 "2. 在 Mojang 的申请表提交 Client ID，等待审核通过\n" +
                                 "3. 把 Client ID 填到 设置 → 高级 → API\n\n" +
                                 "暂时没有 Client ID 也可以改用离线账号或外置登录。详细步骤见 docs/微软登录.md。"),
                    new[] { "取消", "去设置", "离线账号", "外置登录" }, primary: 1, width: 600);
                if (go == 1) { MainWindow.Current.GoTab("settings"); MainWindow.Current.SettingsPage.ScrollTo("api"); }
                else if (go == 2) await AddOfflineAsync();
                else if (go == 3) await AddYggdrasilAsync();
                return;
            }

            var cts = new CancellationTokenSource();
            DeviceCodeInfo info;
            try { info = await MicrosoftAuth.StartAsync(cts.Token); }
            catch (Exception ex)
            {
                await ErrorDialog.ShowAsync("微软登录失败", TaskManager.Flatten(ex), "检查网络和 Client ID 配置后重试。", ex);
                return;
            }

            var panel = new StackPanel();
            panel.Children.Add(new TextBlock { Text = "点击按钮复制代码并打开微软登录页，然后在浏览器中输入下面的代码：", Classes = { S("Body") }, TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(new TextBox { Text = info.UserCode, IsReadOnly = true, FontSize = 28, FontWeight = FontWeight.Bold, HorizontalContentAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 12, 0, 8), FontFamily = new FontFamily("Consolas") });
            var status = new TextBlock { Classes = { S("Sub") } };
            panel.Children.Add(status);
            var openBtn = new Button { Content = "复制代码并打开浏览器", Classes = { S("Primary") }, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 10, 0, 0) };
            openBtn.Click += (s, e) =>
            {
                try { _ = MainWindow.Current?.Clipboard?.SetTextAsync(info.UserCode); }
                catch (Exception ex) { Dialogs.Toast("复制失败，请手动复制代码：" + ex.Message, MainWindow.ToastKind.Error); }
                Util.OpenUrl(info.VerificationUri);
            };
            panel.Children.Add(openBtn);

            var timer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            void updateTime()
            {
                var remaining = info.ExpiresAtUtc - DateTime.UtcNow;
                status.Text = remaining > TimeSpan.Zero
                    ? $"等待浏览器确认 · 剩余 {(int)remaining.TotalMinutes:00}:{remaining.Seconds:00}"
                    : "代码已过期，正在结束登录；请重新登录获取新代码";
            }
            timer.Tick += (s, e) => updateTime();
            updateTime();
            timer.Start();

            var dialog = Dialogs.ShowAsync("微软登录", panel, new[] { "取消登录" }, primary: 0);
            var login = MicrosoftAuth.CompleteAsync(info, cts.Token);
            var done = await Task.WhenAny(dialog, login);
            if (done == dialog)
            {
                timer.Stop();
                cts.Cancel();
                try { await login; } catch { }
                Dialogs.Toast("已取消微软登录");
                return;
            }

            timer.Stop();
            CloseTop();
            try
            {
                var acc = await login;
                AccountStore.AddOrUpdate(acc);
                AccountStore.Selected = acc;
                Dialogs.Toast($"已登录 {acc.Name}", MainWindow.ToastKind.Ok);
            }
            catch (OperationCanceledException) { Dialogs.Toast("已取消微软登录"); }
            catch (Exception ex)
            {
                var message = TaskManager.Flatten(ex);
                var expired = message.IndexOf("过期", StringComparison.Ordinal) >= 0;
                await ErrorDialog.ShowAsync(expired ? "登录代码已过期" : "微软登录失败", message,
                    expired ? "重新发起微软登录，并在倒计时结束前完成浏览器授权。" : "检查网络、浏览器授权和 Client ID 配置后重试。", ex);
            }
        }

        static async Task AddYggdrasilAsync(Account existing = null)
        {
            var panel = new StackPanel();
            panel.Children.Add(new TextBlock { Text = "认证服务器（皮肤站）地址", Classes = { S("Sub") } });
            var server = new TextBox { Text = existing?.ServerUrl ?? Settings.Get("account.lastYggdrasil", "littleskin.cn"), Margin = new Thickness(0, 4, 0, 10) };
            panel.Children.Add(server);
            panel.Children.Add(new TextBlock { Text = "邮箱 / 用户名", Classes = { S("Sub") } });
            var user = new TextBox { Text = existing?.LoginName ?? "", Margin = new Thickness(0, 4, 0, 10) };
            panel.Children.Add(user);
            panel.Children.Add(new TextBlock { Text = "密码", Classes = { S("Sub") } });
            var pass = new TextBox { PasswordChar = '●',  Margin = new Thickness(0, 4, 0, 0) };
            panel.Children.Add(pass);
            panel.Children.Add(new TextBlock { Text = "使用 authlib-injector，支持 LittleSkin、Blessing Skin 等 Yggdrasil 皮肤站。密码只发给你填的服务器，不会保存。", Classes = { S("Sub") }, Margin = new Thickness(0, 10, 0, 0) });
            user.Loaded += (s, e) => user.Focus();

            if (await Dialogs.ShowAsync(existing == null ? "外置登录" : "重新外置登录", panel, new[] { "取消", "登录" }, primary: 1, width: 460) != 1) return;
            try
            {
                var api = await YggdrasilAuth.ResolveApiAsync((server.Text ?? "").Trim(), CancellationToken.None);
                Settings.Set("account.lastYggdrasil", (server.Text ?? "").Trim());
                var accounts = await YggdrasilAuth.LoginAsync(api, (user.Text ?? "").Trim(), pass.Text, CancellationToken.None);
                if (accounts.Count == 0) { await Dialogs.Alert("登录失败", "这个账号下没有角色，先去皮肤站创建一个角色吧。"); return; }
                var acc = accounts[0];
                if (accounts.Count > 1)
                {
                    var idx = await Dialogs.Choose("选择角色", "这个账号有多个角色：", accounts.Select(a => a.Name).ToList());
                    if (idx < 0) return;
                    acc = accounts[idx];
                    if (string.IsNullOrEmpty(acc.AccessToken) || accounts.Count > 1)
                        acc = await YggdrasilAuth.SelectProfileAsync(acc, acc.Uuid, acc.Name, CancellationToken.None);
                }
                AccountStore.AddOrUpdate(acc);
                AccountStore.Selected = acc;
                Dialogs.Toast($"已登录 {acc.Name}（{acc.TypeText}）", MainWindow.ToastKind.Ok);
            }
            catch (Exception ex)
            {
                await ErrorDialog.ShowAsync("外置登录失败", TaskManager.Flatten(ex), "检查认证服务器地址、账号密码和网络后重试。", ex);
            }
        }

        static void CloseTop()
        {
            if (MainWindow.Current.DialogHost.Content is Control fe) MainWindow.Current.CloseDialogContent(fe);
        }
    }
}
