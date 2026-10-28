using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using WindBotAIEditor.Services;

namespace WindBotAIEditor
{
    /// <summary>카드 DB / Lua 스크립트 / 효과번호 뷰어.</summary>
    public partial class CardDbPanel : UserControl
    {
        public event Action<int>? ApplyCardRequested;

        /// <summary>지금 상세를 보여주는 카드 ID (0 = 없음)</summary>
        private int _shownId;

        /// <summary>B-24: 배지 문구 (--uicheck 확인용)</summary>
        public string ReleaseBadgeText => LblRelease == null ? "" : LblRelease.Text;

        public CardDbPanel()
        {
            InitializeComponent();
            // 카탈로그 스캔이 끝나면 배지를 다시 계산한다 (백그라운드 스레드 → UI 스레드)
            PreReleaseCatalog.Instance.Ready += OnReleaseCatalogReady;
            Unloaded += (_, __) => PreReleaseCatalog.Instance.Ready -= OnReleaseCatalogReady;
        }

        private void OnReleaseCatalogReady()
        {
            try
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_shownId > 0) UpdateReleaseBadge(_shownId);
                }));
            }
            catch { /* 창이 닫히는 중 */ }
        }

        /// <summary>B-24: 프리릴리즈 / 정규 배지 갱신</summary>
        private void UpdateReleaseBadge(int id)
        {
            var catalog = PreReleaseCatalog.Instance;
            var badge = catalog.Badge(id);
            LblRelease.Text = badge;
            var detail = catalog.Stats;
            if (catalog.IsReleased(id))
            {
                var cands = catalog.Candidates(id);
                detail = "같은 이름의 정규 카드: " + string.Join(", ", cands.Select(c => c.ToString()).ToArray())
                         + Environment.NewLine + detail;
            }
            LblRelease.ToolTip = detail;
        }

        private void OnSearch(object sender, RoutedEventArgs e) => RunSearch();

        private void OnSearchKey(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Enter) RunSearch();
        }

        private void RunSearch()
        {
            if (!CardDatabase.Instance.IsOpen)
            {
                TxtInfo.Text = "카드 DB 가 열려 있지 않습니다.";
                return;
            }
            var list = CardDatabase.Instance.Search(TxtSearch.Text, 200);
            GridResults.ItemsSource = list;
            LblCount.Text = list.Count + "건";
        }

        private void OnSelected(object sender, SelectionChangedEventArgs e)
        {
            if (GridResults.SelectedItem is CardDatabase.CardInfo info)
                ShowCard(info.Id);
        }

        public void ShowCard(int id)
        {
            var db = CardDatabase.Instance;
            var info = db.Info(id);
            TxtCardId.Text = id.ToString();
            _shownId = id;
            UpdateReleaseBadge(id);

            if (info == null)
            {
                TxtInfo.Text = "카드 ID " + id + " — 정보 없음";
                TxtLua.Text = "";
                ListEffects.ItemsSource = null;
                LblScript.Text = "Lua 스크립트: 없음";
                return;
            }

            string kind = ((info.Type & 0x1) != 0) ? "몬스터" : ((info.Type & 0x2) != 0) ? "마법" : ((info.Type & 0x4) != 0) ? "함정" : "?";
            TxtInfo.Text =
                "ID: " + info.Id + Environment.NewLine +
                "이름: " + info.Name + Environment.NewLine +
                "종류: " + kind + Environment.NewLine +
                "공격/수비: " + info.Atk + " / " + info.Def + Environment.NewLine +
                "레벨/랭크: " + info.Level + Environment.NewLine +
                "속성: " + info.Attribute + "  종족: " + info.Race + Environment.NewLine +
                Environment.NewLine + info.Desc;

            var root = EdoproRoot();
            var script = CardScript.Read(root, id);
            if (script == null)
            {
                TxtLua.Text = "(스크립트 없음 — 상속/기본 스크립트일 수 있음)";
                LblScript.Text = "Lua 스크립트: 없음";
                ListEffects.ItemsSource = null;
            }
            else
            {
                var summary = CardScript.Summarize(script);
                LblScript.Text = "Lua: " + CardScript.Find(root, id) +
                                 (summary.Count > 0 ? "   ·   " + string.Join(", ", summary) : "");
                TxtLua.Text = script;

                var descs = CardScript.Descriptions(script, EffectCatalog.Instance)
                    .GroupBy(d => d.Raw).Select(g => g.First()).ToList();
                ListEffects.ItemsSource = descs;
                if (descs.Count == 0)
                    ListEffects.ItemsSource = new[] { new CardScript.EffectDesc { Dynamic = false, Value = -1, Text = "(SetDescription 없음 — 기본값 -1 사용)" } };
            }

            // 시스템 효과 설명 목록 새로고침
            LoadSystemEffects();

            if (!EffectCatalog.Instance.Loaded && !string.IsNullOrEmpty(root))
                EffectCatalog.Instance.Load(root);
        }

        private void LoadSystemEffects()
        {
            var catalog = EffectCatalog.Instance;
            if (!catalog.Loaded)
            {
                var root = EdoproRoot();
                if (!string.IsNullOrEmpty(root)) catalog.Load(root);
            }

            var keyword = (TxtEffectFilter?.Text ?? "").Trim();
            var items = string.IsNullOrEmpty(keyword)
                ? catalog.EffectDescriptions()
                : catalog.Search(keyword, 300);

            ListSystem.ItemsSource = items
                .Select(kv => kv.Key + " — " + kv.Value)
                .ToList();
        }

        private void OnEffectFilterChanged(object sender, TextChangedEventArgs e) => LoadSystemEffects();

        private void OnSystemEffectClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            // 더블클릭 → 효과번호만 클립보드 복사
            if (ListSystem.SelectedItem is string s)
            {
                var num = s.Split(' ')[0];
                try { Clipboard.SetText(num); } catch { }
            }
        }

        private void OnApplyToRule(object sender, RoutedEventArgs e)
        {
            if (int.TryParse(TxtCardId.Text, out var id) && id > 0)
                ApplyCardRequested?.Invoke(id);
        }

        private static string EdoproRoot()
        {
            var root = App.WindBotRoot;
            if (string.IsNullOrEmpty(root)) return "";
            return System.IO.Directory.GetParent(root)?.FullName ?? root;
        }

        private void OnCopyId(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrWhiteSpace(TxtCardId.Text))
            {
                try { Clipboard.SetText(TxtCardId.Text); } catch { }
            }
        }
    }
}
