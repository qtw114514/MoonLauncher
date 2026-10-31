using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Avalonia.Interactivity;
using Avalonia.Input;
using MoonLauncher.Content;
using MoonLauncher.Core;
using MoonLauncher.Game;

namespace MoonLauncher.UI.Pages
{
    public partial class ContentSearchView : UserControl, IPage
    {
        sealed class SourceSearchState
        {
            public ContentSource Source;
            public int Offset;
            public int Total;
            public bool Done;
            public string Error;
        }

        sealed class SourceSearchResult
        {
            public ContentSource Source;
            public SearchResult Result;
            public Exception Error;
        }

        readonly ContentType _type;
        readonly ObservableCollection<ContentProject> _items = new ObservableCollection<ContentProject>();
        readonly SourceSearchState _modrinth = new SourceSearchState { Source = ContentSource.Modrinth };
        readonly SourceSearchState _curseForge = new SourceSearchState { Source = ContentSource.CurseForge };
        readonly Dictionary<string, ContentProject> _sourceIds = new Dictionary<string, ContentProject>();
        readonly Dictionary<string, ContentProject> _slugs = new Dictionary<string, ContentProject>();
        readonly Dictionary<string, ContentProject> _titleAuthors = new Dictionary<string, ContentProject>();
        CancellationTokenSource _cts;
        int _session;
        bool _busy, _init, _suppress;
        Instance _target;

        public ContentSearchView(ContentType type)
        {
            InitializeComponent();
            _type = type;
            Header.Text = type switch
            {
                ContentType.Modpack => "整合包",
                ContentType.ResourcePack => "资源包",
                ContentType.Shader => "光影包",
                ContentType.DataPack => "数据包",
                _ => "Mod",
            };
            Results.ItemsSource = _items;
            LoaderPanel.IsVisible = type == ContentType.Mod || type == ContentType.Modpack ? true : false;
            TargetPanel.IsVisible = type == ContentType.Modpack ? false : true;
            SrcAll.IsVisible = type == ContentType.Mod ? true : false;
            _suppress = true;
            Sort.SelectedIndex = 0;
            Loader.SelectedIndex = 0;
            var preferred = Settings.PreferredContentSource;
            var curse = string.Equals(preferred, "curseforge", StringComparison.OrdinalIgnoreCase);
            if (type == ContentType.Mod && string.Equals(preferred, "all", StringComparison.OrdinalIgnoreCase))
            {
                SrcAll.IsChecked = true;
                _srcAll = true;
            }
            else
            {
                (curse ? SrcCurse : SrcModrinth).IsChecked = true;
                _source = curse ? ContentSource.CurseForge : ContentSource.Modrinth;
            }
            _suppress = false;
        }

        // 记住当前选中的源，而不是每次去读 IsChecked：
        // RadioButton 互斥时 Checked 事件可能早于同组按钮复位，读 IsChecked 会拿到上一轮的值。
        bool _srcAll;
        ContentSource _source = ContentSource.Modrinth;

        bool IsAll => _type == ContentType.Mod && _srcAll;
        ContentSource Source => _source;

        public void SetTarget(Instance inst)
        {
            _target = inst;
            if (_init) ApplyTargetDefaults();
        }

        public async void OnShown()
        {
            if (_init) { RefreshTargets(); return; }
            _init = true;
            _suppress = true;
            GameVersion.Items.Add(new ComboBoxItem { Content = "全部", Tag = "" });
            GameVersion.SelectedIndex = 0;
            RefreshTargets();
            ApplyTargetDefaults();
            _suppress = false;
            try
            {
                var versions = await Task.Run(() => VersionList.GetMinecraftAsync());
                _suppress = true;
                var sel = TagOf(GameVersion);
                foreach (var v in versions.Where(v => v.Type == "release"))
                    GameVersion.Items.Add(new ComboBoxItem { Content = v.Id, Tag = v.Id });
                SelectTag(GameVersion, sel);
                _suppress = false;
            }
            catch { _suppress = false; }
            _ = SearchAsync(true);
        }

        void RefreshTargets()
        {
            var keep = _target?.Id ?? (Target.SelectedItem as ComboBoxItem)?.Tag as string ?? Settings.SelectedVersion;
            var old = _suppress;
            _suppress = true;
            Target.Items.Clear();
            foreach (var i in GameRepo.Current.ListInstances().Where(i => i.IsValid))
                Target.Items.Add(new ComboBoxItem { Content = i.Id, Tag = i.Id, DataContext = i, [ToolTip.TipProperty] = i.Description });
            Target.SelectedItem = Target.Items.Cast<ComboBoxItem>().FirstOrDefault(x => (string)x.Tag == keep) ?? (Target.Items.Count > 0 ? Target.Items[0] : null);
            _target = (Target.SelectedItem as ComboBoxItem)?.DataContext as Instance;
            _suppress = old;
        }

        /// <summary>按目标版本自动设置 MC 版本和加载器筛选</summary>
        void ApplyTargetDefaults()
        {
            if (_target == null || _type == ContentType.Modpack) return;
            var old = _suppress;
            _suppress = true;
            var mc = _target.McVersion;
            if (!GameVersion.Items.Cast<ComboBoxItem>().Any(i => (string)i.Tag == mc))
                GameVersion.Items.Insert(1, new ComboBoxItem { Content = mc, Tag = mc });
            SelectTag(GameVersion, mc);
            SelectTag(Loader, LoaderInfo.Slug(_target.PrimaryLoader) ?? "");
            Target.SelectedItem = Target.Items.Cast<ComboBoxItem>().FirstOrDefault(x => (string)x.Tag == _target.Id) ?? Target.SelectedItem;
            _suppress = old;
            if (!old) _ = SearchAsync(true);
        }

        static string TagOf(ComboBox b) => (b.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
        static void SelectTag(ComboBox b, string tag) => b.SelectedItem = b.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == tag) ?? b.Items[0];

        void Source_Checked(object sender, RoutedEventArgs e)
        {
            if (_suppress) return;
            _srcAll = sender == SrcAll;
            _source = sender == SrcCurse ? ContentSource.CurseForge : ContentSource.Modrinth;
            Settings.PreferredContentSource = IsAll ? "all" : _source == ContentSource.CurseForge ? "curseforge" : "modrinth";
            _ = SearchAsync(true);
        }

        void Filter_Changed(object sender, SelectionChangedEventArgs e) { if (!_suppress && _init) _ = SearchAsync(true); }

        void Target_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppress) return;
            _target = (Target.SelectedItem as ComboBoxItem)?.DataContext as Instance;
            ApplyTargetDefaults();
        }

        void Query_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) _ = SearchAsync(true); }
        void Search_Click(object sender, RoutedEventArgs e) => _ = SearchAsync(true);

        async Task SearchAsync(bool reset)
        {
            var all = IsAll;
            if (!reset && _busy) return;
            if (reset) _session++;
            var session = _session;
            var states = all ? new[] { _modrinth, _curseForge } : new[] { StateFor(Source) };
            if (!reset && states.All(s => s.Done)) return;
            _cts?.Cancel();
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            if (reset)
            {
                ResetState(_modrinth);
                ResetState(_curseForge);
                _items.Clear();
                _sourceIds.Clear();
                _slugs.Clear();
                _titleAuthors.Clear();
            }
            _busy = true;
            if (_items.Count == 0) ShowStatus("搜索中…", true);
            var q = new SearchQuery
            {
                Text = (Query.Text ?? "").Trim(),
                Type = _type,
                GameVersion = TagOf(GameVersion),
                Loader = LoaderPanel.IsVisible == true ? TagOf(Loader) : null,
                Sort = TagOf(Sort),
                Limit = 20,
            };
            if (string.IsNullOrEmpty(q.GameVersion)) q.GameVersion = null;
            if (string.IsNullOrEmpty(q.Loader)) q.Loader = null;
            try
            {
                var tasks = states.Where(s => !s.Done).Select(s =>
                {
                    var query = q.Clone();
                    query.Offset = s.Offset;
                    return SearchOneAsync(s.Source, query, ct);
                }).ToArray();
                var results = await Task.WhenAll(tasks);
                if (session != _session || ct.IsCancellationRequested) return;
                var errors = new List<string>();
                foreach (var r in results)
                {
                    var state = StateFor(r.Source);
                    if (r.Error != null)
                    {
                        state.Done = true;
                        state.Error = r.Error.Message;
                        errors.Add(ContentService.SourceName(r.Source) + "：" + state.Error);
                        continue;
                    }
                    state.Error = null;
                    AddItems(r.Result.Items, all);
                    state.Offset += r.Result.Items.Count;
                    state.Total = r.Result.Total;
                    state.Done = r.Result.Items.Count == 0 || state.Offset >= state.Total;
                }
                if (errors.Count > 0)
                {
                    var message = string.Join("；", errors);
                    if (_items.Count == 0) ShowStatus("搜索失败：" + message + (all ? "" : "\n\n可以切换到另一个来源，或在 设置 → 下载 里调整 Mod 下载源"), false);
                    else
                    {
                        StatusPanel.IsVisible = false;
                        Dialogs.Toast((all ? "来源加载失败：" : "加载更多失败：") + message, MainWindow.ToastKind.Error);
                    }
                }
                else if (_items.Count == 0) ShowStatus("没有找到结果，换个关键词试试" + (ZhNames.ContainsCjk(q.Text) ? "（中文名不在词表里时，试试英文名）" : ""), false);
                else StatusPanel.IsVisible = false;
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                if (session != _session || ct.IsCancellationRequested) return;
                if (_items.Count == 0) ShowStatus($"搜索失败：{ex.Message}\n\n可以切换到另一个来源，或在 设置 → 下载 里调整 Mod 下载源", false);
                else Dialogs.Toast("加载更多失败：" + ex.Message, MainWindow.ToastKind.Error);
            }
            finally { if (session == _session) _busy = false; }
        }

        static async Task<SourceSearchResult> SearchOneAsync(ContentSource source, SearchQuery q, CancellationToken ct)
        {
            try
            {
                var result = await Task.Run(() => ContentService.SearchAsync(source, q, ct), ct);
                return new SourceSearchResult { Source = source, Result = result };
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { return new SourceSearchResult { Source = source, Error = ex }; }
        }

        SourceSearchState StateFor(ContentSource source) => source == ContentSource.CurseForge ? _curseForge : _modrinth;

        static void ResetState(SourceSearchState state)
        {
            state.Offset = 0;
            state.Total = 0;
            state.Done = false;
            state.Error = null;
        }

        static string Normalize(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "";
            var chars = new List<char>(value.Length);
            foreach (var c in value.Trim().ToLowerInvariant())
                if (char.IsLetterOrDigit(c)) chars.Add(c);
            return new string(chars.ToArray());
        }

        static string SourceIdKey(ContentProject p) => string.IsNullOrEmpty(p.Id) ? "" : p.Source + "\0" + p.Id;

        static string TitleAuthorKey(ContentProject p)
        {
            var title = Normalize(p.Title);
            var author = Normalize(p.Author);
            return title.Length == 0 || author.Length == 0 ? "" : title + "\0" + author;
        }

        void IndexProject(ContentProject project, ContentProject card)
        {
            var sourceId = SourceIdKey(project);
            if (sourceId.Length > 0) _sourceIds[sourceId] = card;
            var slug = Normalize(project.Slug);
            if (slug.Length > 0 && !_slugs.ContainsKey(slug)) _slugs.Add(slug, card);
            var titleAuthor = TitleAuthorKey(project);
            if (titleAuthor.Length > 0 && !_titleAuthors.ContainsKey(titleAuthor)) _titleAuthors.Add(titleAuthor, card);
        }

        void AddItems(IEnumerable<ContentProject> projects, bool mergeSources)
        {
            foreach (var p in projects)
            {
                if (p == null) continue;
                var sourceId = SourceIdKey(p);
                ContentProject card;
                if (sourceId.Length > 0 && _sourceIds.TryGetValue(sourceId, out card)) continue;
                card = null;
                var slug = Normalize(p.Slug);
                if (mergeSources && slug.Length > 0) _slugs.TryGetValue(slug, out card);
                var titleAuthor = TitleAuthorKey(p);
                if (mergeSources && card == null && titleAuthor.Length > 0) _titleAuthors.TryGetValue(titleAuthor, out card);
                if (card != null && card.Source != p.Source)
                {
                    MergeProjects(card, p);
                    IndexProject(p, card);
                    var index = _items.IndexOf(card);
                    if (index >= 0) _items[index] = card;
                    continue;
                }
                _items.Add(p);
                IndexProject(p, p);
            }
        }

        static void MergeProjects(ContentProject card, ContentProject other)
        {
            if (card.MergedSource != other.Source)
            {
                card.Downloads += other.Downloads;
                card.MergedSource = other.Source;
            }
            if (other.Updated > card.Updated) card.Updated = other.Updated;
            card.Categories = MergeValues(card.Categories, other.Categories);
            card.Loaders = MergeValues(card.Loaders, other.Loaders);
            card.GameVersions = MergeValues(card.GameVersions, other.GameVersions);
            if (string.IsNullOrEmpty(card.IconUrl)) card.IconUrl = other.IconUrl;
            if (string.IsNullOrEmpty(card.Description)) card.Description = other.Description;
        }

        static List<string> MergeValues(List<string> values, List<string> other)
        {
            values = values ?? new List<string>();
            if (other == null) return values;
            foreach (var value in other)
                if (!string.IsNullOrEmpty(value) && !values.Contains(value, StringComparer.OrdinalIgnoreCase)) values.Add(value);
            return values;
        }

        void ShowStatus(string text, bool loading)
        {
            StatusPanel.IsVisible = true;
            StatusText.Text = text;
            StatusBar.IsVisible = loading ? true : false;
            RetrySearch.IsVisible = !loading && text.StartsWith("搜索失败", StringComparison.Ordinal) ? true : false;
        }

        void RetrySearch_Click(object sender, RoutedEventArgs e) => _ = SearchAsync(true);

        void Results_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            // Avalonia 的 ScrollChangedEventArgs 只给增量，绝对位置要从 ScrollViewer 读
            if (sender is ScrollViewer sv && sv.Offset.Y > 0
                && sv.Offset.Y + sv.Viewport.Height >= sv.Extent.Height - 80) _ = SearchAsync(false);
        }

        void IntroButton_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is Button button) button.IsVisible = _type == ContentType.Mod ? true : false;
        }

        async void Intro_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (!((sender as Control)?.DataContext is ContentProject p)) return;
            p.IntroExpanded = !p.IntroExpanded;
            if (p.IntroExpanded && !p.IntroLoaded && !p.IntroLoading) await LoadIntroAsync(p);
        }

        async Task LoadIntroAsync(ContentProject p)
        {
            int session = _session;
            var ct = _cts?.Token ?? CancellationToken.None;
            p.IntroLoading = true;
            p.IntroStatus = "正在查找 MC 百科中文简介…";
            p.IntroText = p.Description;
            try
            {
                var intro = await Task.Run(() => McmodIntro.FetchAsync(p.Title, ct), ct);
                if (session != _session || ct.IsCancellationRequested) return;
                p.IntroLoaded = true;
                p.McmodUrl = intro?.Url;
                p.IntroText = intro?.Text ?? p.Description;
                p.IntroStatus = intro != null ? "中文简介来自 MC 百科" : "未找到 MC 百科中文简介，显示来源站摘要";
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                if (session != _session) return;
                p.IntroLoaded = false;
                p.IntroText = p.Description;
                p.IntroStatus = "中文简介加载失败，收起后重新展开可重试：" + ex.Message;
            }
            finally { if (session == _session) p.IntroLoading = false; }
        }

        void McmodCard_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if ((sender as Control)?.DataContext is ContentProject p && !string.IsNullOrEmpty(p.McmodUrl)) Util.OpenUrl(p.McmodUrl);
        }

        void Results_MouseUp(object sender, PointerReleasedEventArgs e)
        {
            // 卡片里的简介按钮不触发整卡跳转（MouseUp 先于 Button.Click）。
            Visual source = e.Source as Visual;
            while (source != null)
            {
                if (source is Button) { Results.SelectedItem = null; return; }
                source = (source as Visual)?.GetVisualParent();
            }
            if (Results.SelectedItem is ContentProject p)
            {
                Results.SelectedItem = null;
                MainWindow.Current.Push(new ProjectPage(p, _type, _type == ContentType.Modpack ? null : _target, TagOf(GameVersion), LoaderPanel.IsVisible == true ? TagOf(Loader) : null));
            }
        }
    }
}
