using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using WindBotAIEditor.Models;
using WindBotAIEditor.Services;
using Condition = WindBotAIEditor.Models.Condition;

namespace WindBotAIEditor
{
    public partial class MainWindow : Window
    {
        private WindBotProject? _project;
        private AiProfile _profile = new AiProfile();
        private string[] _allDecks = Array.Empty<string>();

        private readonly ObservableCollection<BotEntry> _bots = new();
        private readonly ObservableCollection<ActionRule> _actions = new();
        private readonly ObservableCollection<ChoiceRule> _choices = new();
        private readonly ObservableCollection<ForbidRule> _forbids = new();
        private readonly ObservableCollection<FinishTrigger> _triggers = new();
        private readonly ObservableCollection<OppTrigger> _opps = new();
        private readonly ObservableCollection<RouteSet> _routes = new();
        private readonly ObservableCollection<RouteSet> _routesSecond = new();

        /// <summary>⑤ 탭에서 지금 편집 중인 트랙 (false = 선공, true = 후공)</summary>
        private bool _routeSecondTrack;

        /// <summary>왼쪽 목록에서 고른 루트 1벌 (오른쪽 단계 카드·진입 조건의 대상)</summary>
        private RouteSet? _selectedRouteSet;

        /// <summary>조건을 편집 중인 순위 (null 이면 선택한 루트의 진입 조건을 편집)</summary>
        private RouteOption? _selectedRouteOption;

        /// <summary>⑤ 탭 UI 재구성 중 재진입 방지 (ItemsSource 재바인딩 → SelectionChanged)</summary>
        private bool _routeUiBusy;

        // ===== 콤보박스 바인딩용 정적 목록 =====
        public static List<string> ExecutorTypes => PluginBuilder.ExecutorTypes.ToList();
        public static List<ConditionKind> ConditionKinds => Models.ConditionKind.All;
        public static List<PickPolicy> PickPolicies => PickPolicy.All;
        public static List<ChoiceKind> ChoiceKinds => ChoiceKind.All;
        public static List<ForbidAction> ForbidActions => ForbidAction.All;
        public static List<TurnScopeOption> TurnScopes => TurnScopeOption.All;
        public static List<FinishZone> FinishZones => FinishZone.All;
        public static List<FinishAction> FinishActions => FinishAction.All;
        public static List<string> Ops => new() { ">=", "<=", "==", "!=", ">", "<" };
        public static List<ChoiceOption> Sides => new()
        {
            new ChoiceOption { Id = "me", Label = "나(AI)" },
            new ChoiceOption { Id = "enemy", Label = "상대" },
        };
        public static List<string> Joins => new() { "and", "or" };
        public static List<ConditionModeOption> ConditionModes => ConditionModeOption.All;
        public static List<RaceOption> Races => RaceOption.All;
        public static ObservableCollection<ThemeOption> Themes => ThemeCatalog.Instance.All;
        public static List<ChainBasisOption> ChainBases => ChainBasisOption.All;
        public static List<SummonZoneOption> SummonZones => SummonZoneOption.All;
        public static List<PositionOption> Positions => PositionOption.All;

        private static readonly List<ChoiceOption> RpsChoices = new()
        {
            new ChoiceOption { Id = "random", Label = "랜덤 (기본)" },
            new ChoiceOption { Id = "1",      Label = "1 고정" },
            new ChoiceOption { Id = "2",      Label = "2 고정" },
            new ChoiceOption { Id = "3",      Label = "3 고정" },
        };

        private static readonly List<ChoiceOption> TurnChoices = new()
        {
            new ChoiceOption { Id = "random", Label = "랜덤 (기본)" },
            new ChoiceOption { Id = "first",  Label = "선공" },
            new ChoiceOption { Id = "second", Label = "후공" },
        };

        public MainWindow()
        {
            InitializeComponent();

            GridBots.ItemsSource = _bots;
            GridActions.ItemsSource = _actions;
            GridChoices.ItemsSource = _choices;
            GridForbids.ItemsSource = _forbids;
            GridTriggers.ItemsSource = _triggers;
            GridOpps.ItemsSource = _opps;
            UpdateRouteTrackUi();

            CardPanel.ApplyCardRequested += OnApplyCardFromDb;

            CmbRps.ItemsSource = RpsChoices;
            CmbRps.SelectedValue = "random";
            CmbTurnChoice.ItemsSource = TurnChoices;
            CmbTurnChoice.SelectedValue = "random";
            CmbChainBasis.ItemsSource = ChainBases;
            CmbChainBasis.SelectedValue = "code";

            ChkDarkMode.IsChecked = AppSettings.Load().DarkMode;
            Loaded += (_, __) =>
            {
                UpdateProfilePath();
                var spec = Environment.GetEnvironmentVariable("WB_AUTO");
                if (!string.IsNullOrWhiteSpace(spec))
                {
                    var parts = spec.Split(':');
                    var deck = "AI_" + parts[0] + ".ydk";
                    if (ListDecks.ItemsSource is IEnumerable<string> items && items.Contains(deck))
                        ListDecks.SelectedItem = deck;
                    if (parts.Length > 1 && int.TryParse(parts[1], out var t1)) Tabs.SelectedIndex = t1;
                    if (parts.Length > 2 && int.TryParse(parts[2], out var t2)) SelectRuleTab(t2);
                }
            };
            ApplyTheme(ChkDarkMode.IsChecked == true);

            UpdateRuleSummary();
            TryAutoDetect(silent: true);
        }

        // ================= 테마 =================

        private void OnThemeChanged(object sender, RoutedEventArgs e)
            => ApplyTheme(ChkDarkMode.IsChecked == true);

        private void ApplyTheme(bool dark)
        {
            var dict = new ResourceDictionary
            {
                Source = new Uri(dark ? "Themes/Dark.xaml" : "Themes/Light.xaml", UriKind.Relative)
            };
            var md = Application.Current.Resources.MergedDictionaries;
            if (md.Count > 0) md[0] = dict; else md.Add(dict);

            var s = AppSettings.Load();
            s.DarkMode = dark;
            s.Save();
        }

        // ================= 시작 / 경로 =================

        private void TryAutoDetect(bool silent)
        {
            var saved = AppSettings.Load();
            if (!string.IsNullOrWhiteSpace(saved.EdoproPath) && AppSettings.LooksLikeEdopro(saved.EdoproPath))
            {
                OpenEdopro(saved.EdoproPath);
                return;
            }

            string[] candidates = { @"D:\edopro", @"C:\edopro", @"D:\EDOPro", @"C:\EDOPro" };
            foreach (var c in candidates)
            {
                if (AppSettings.LooksLikeEdopro(c)) { OpenEdopro(c); return; }
            }

            PromptForEdopro();
        }

        private void PromptForEdopro()
        {
            var dlg = new OpenFolderDialog { Title = "EDOPro 폴더를 선택하세요 (EDOPro.exe 가 있는 폴더)" };
            if (dlg.ShowDialog() != true) return;

            if (!AppSettings.LooksLikeEdopro(dlg.FolderName))
            {
                var r = MessageBox.Show("이 폴더에서 EDOPro.exe 또는 WindBot 폴더를 찾지 못했습니다." + Environment.NewLine + "그래도 사용할까요?",
                    "EDOPro 폴더", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (r != MessageBoxResult.Yes) return;
            }
            OpenEdopro(dlg.FolderName);
        }

        private void OnAutoDetect(object sender, RoutedEventArgs e) => PromptForEdopro();

        private void OnBrowseRoot(object sender, RoutedEventArgs e) => PromptForEdopro();

        private void OnOpenRoot(object sender, RoutedEventArgs e) => OpenEdopro(TxtRoot.Text);

        private void OpenEdopro(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            {
                MessageBox.Show("폴더가 존재하지 않습니다:" + Environment.NewLine + path, "열기");
                return;
            }

            var edopro = path;
            var windbot = Path.Combine(path, "WindBot");
            if (!File.Exists(Path.Combine(windbot, "WindBot.exe")))
            {
                if (File.Exists(Path.Combine(path, "WindBot.exe")))
                {
                    windbot = path;
                    edopro = Directory.GetParent(path)?.FullName ?? path;
                }
                else
                {
                    MessageBox.Show("이 폴더에서 WindBot\\WindBot.exe 를 찾지 못했습니다." + Environment.NewLine +
                                    "EDOPro.exe 가 있는 폴더를 선택해 주세요.", "EDOPro 폴더");
                    return;
                }
            }

            App.EdoproRoot = edopro;
            App.WindBotRoot = windbot;
            _project = new WindBotProject(windbot);
            TxtRoot.Text = edopro;

            var settings = AppSettings.Load();
            settings.EdoproPath = edopro;
            settings.Save();

            OpenCardDatabase(edopro);
            ThemeCatalog.Instance.Load(edopro);
            UpdateProfilePath();
            LoadBots();
            LoadDecks();
            RefreshDialogFiles();
            Status("EDOPro 열림: " + edopro + " · 테마 " + ThemeCatalog.Instance.All.Count + "개");
        }

        private void OpenCardDatabase(string edoproRoot)
        {
            var windBotRoot = Path.Combine(edoproRoot, "WindBot");
            if (!Directory.Exists(windBotRoot)) windBotRoot = edoproRoot;

            EffectCatalog.Instance.Load(edoproRoot);

            var candidates = CardDatabase.FindCandidates(windBotRoot);
            if (candidates.Count == 0)
            {
                TxtCdb.Text = "카드DB: 없음";
                Status("cards.cdb 를 찾지 못했습니다.");
                return;
            }

            CardDatabase.Instance.OpenMany(candidates);
            var opened = CardDatabase.Instance.Paths.Count;
            var first = CardDatabase.Instance.Paths.FirstOrDefault() ?? "";
            TxtCdb.Text = $"카드DB {opened}개 · {Path.GetFileName(Path.GetDirectoryName(first))}/{Path.GetFileName(first)}";
            Status($"카드 DB {opened}개 연결 (repositories/languages 포함)");
        }

        private void OnReloadCdb(object sender, RoutedEventArgs e)
        {
            var root = string.IsNullOrEmpty(App.EdoproRoot) ? App.WindBotRoot : App.EdoproRoot;
            if (!string.IsNullOrEmpty(root)) OpenCardDatabase(root);
        }

        // ================= 프로필 =================

        private string ProfilePath =>
            _project == null ? "" : Path.Combine(_project.ExecutorsDir, "_src", RuleCodeGen.ClassName(_profile.Engine) + ".rules.json");

        private void UpdateProfilePath()
        {
            if (TxtProfilePath == null) return;
            TxtProfilePath.Text = _project == null ? "" : "→ " + ProfilePath;
        }

        private void OnEngineNameChanged(object sender, TextChangedEventArgs e) => UpdateProfilePath();

        private AiProfile CurrentProfile()
        {
            _profile.Engine = (TxtEngineName.Text ?? "").Trim();
            _profile.Description = (TxtEngineDesc.Text ?? "").Trim();
            _profile.FreePlay = ChkFreePlay.IsChecked == true;
            _profile.DebugEffectLog = ChkDebugEffect.IsChecked == true;
            _profile.RpsHand = (CmbRps.SelectedValue as string) ?? "random";
            _profile.TurnChoice = (CmbTurnChoice.SelectedValue as string) ?? "random";
            _profile.DialogFile = (CmbDialog.SelectedValue as string) ?? "";
            _profile.ChainBasis = (CmbChainBasis.SelectedValue as string) ?? "code";
            _profile.AvoidColumnCard = CardAvoidColumn?.CardId ?? 0;
            _profile.Actions = _actions.ToList();
            _profile.Choices = _choices.ToList();
            _profile.Forbids = _forbids.ToList();
            _profile.Triggers = _triggers.ToList();
            _profile.OppTriggers = _opps.ToList();
            _profile.Routes = _routes;
            _profile.RoutesSecond = _routesSecond;
            _profile.Route = null;          // 구형 단일 루트는 더 이상 쓰지 않는다
            _profile.RouteSecond = null;
            return _profile;
        }

        private void ApplyProfile(AiProfile p)
        {
            _profile = p;
            TxtEngineName.Text = p.Engine;
            TxtEngineDesc.Text = p.Description;
            ChkFreePlay.IsChecked = p.FreePlay;
            ChkDebugEffect.IsChecked = p.DebugEffectLog;
            CmbRps.SelectedValue = string.IsNullOrWhiteSpace(p.RpsHand) ? "random" : p.RpsHand;
            CmbTurnChoice.SelectedValue = string.IsNullOrWhiteSpace(p.TurnChoice) ? "random" : p.TurnChoice;
            CmbDialog.SelectedValue = string.IsNullOrWhiteSpace(p.DialogFile) ? "" : p.DialogFile;
            CmbChainBasis.SelectedValue = string.IsNullOrWhiteSpace(p.ChainBasis) ? "code" : p.ChainBasis;
            if (CardAvoidColumn != null) CardAvoidColumn.CardId = p.AvoidColumnCard;

            _actions.Clear(); foreach (var x in p.Actions) _actions.Add(x);
            _choices.Clear(); foreach (var x in p.Choices) _choices.Add(x);
            _forbids.Clear(); foreach (var x in p.Forbids ?? new List<ForbidRule>()) _forbids.Add(x);
            _triggers.Clear(); foreach (var x in p.Triggers ?? new List<FinishTrigger>()) _triggers.Add(x);
            _opps.Clear(); foreach (var x in p.OppTriggers ?? new List<OppTrigger>()) _opps.Add(x);
            _routes.Clear(); foreach (var x in p.Routes ?? new ObservableCollection<RouteSet>()) _routes.Add(x);
            _routesSecond.Clear(); foreach (var x in p.RoutesSecond ?? new ObservableCollection<RouteSet>()) _routesSecond.Add(x);

            _selectedRouteSet = null;
            RenumberRoute();
            SelectRouteOption(null);
            UpdateRouteTrackUi();
            GridActions.SelectedIndex = _actions.Count > 0 ? 0 : -1;
            GridChoices.SelectedIndex = _choices.Count > 0 ? 0 : -1;
            OnChoiceSelected(null, null);
            SetSettingsCollapsed(true);          // B-9: 규칙 프로필을 읽으면 설정 카드는 기본(접힘)으로
            UpdateProfilePath();
        }

        private void TryLoadProfile(string engine)
        {
            if (_project == null || string.IsNullOrWhiteSpace(engine)) return;
            var path = Path.Combine(_project.ExecutorsDir, "_src", RuleCodeGen.ClassName(engine) + ".rules.json");
            if (!File.Exists(path))
            {
                _actions.Clear(); _choices.Clear(); _forbids.Clear();
                _triggers.Clear(); _opps.Clear();
                _routes.Clear(); _routesSecond.Clear();
                _selectedRouteSet = null;
                RenumberRoute();
                SelectRouteOption(null);
                UpdateRouteTrackUi();
                GridActions.SelectedIndex = -1;
                return;
            }
            try
            {
                var p = JsonSerializer.Deserialize<AiProfile>(File.ReadAllText(path), WindBotProject.JsonOpts);
                if (p != null) { RuleMigration.Normalize(p); ApplyProfile(p); }
                Status($"규칙 불러옴: {Path.GetFileName(path)} (행동 {_actions.Count} / 금지 {_forbids.Count} / " +
                       $"선공 루트 {RouteSetCount(false)}벌 {RouteStepCount(false)}단계 / 후공 루트 {RouteSetCount(true)}벌 {RouteStepCount(true)}단계)");
            }
            catch (Exception ex) { Status("규칙 불러오기 실패: " + ex.Message); }
        }

        private void OnSaveProfile(object sender, RoutedEventArgs e)
        {
            if (_project == null) { MessageBox.Show("EDOPro 폴더를 먼저 여세요."); return; }
            CommitGrids();
            try
            {
                var p = CurrentProfile();
                var path = ProfilePath;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, JsonSerializer.Serialize(p, WindBotProject.JsonOpts));
                Status("규칙 저장: " + path);
                MessageBox.Show("저장했습니다." + Environment.NewLine + path, "규칙 저장");
            }
            catch (Exception ex) { MessageBox.Show("저장 실패: " + ex.Message, "오류"); }
        }

        private void OnLoadProfile(object sender, RoutedEventArgs e)
        {
            if (_project == null) { MessageBox.Show("EDOPro 폴더를 먼저 여세요."); return; }
            var path = ProfilePath;
            if (!File.Exists(path)) { MessageBox.Show("저장된 규칙이 없습니다:" + Environment.NewLine + path, "불러오기"); return; }
            TryLoadProfile((TxtEngineName.Text ?? "").Trim());
        }

        private void OnPreviewSource(object sender, RoutedEventArgs e)
        {
            CommitGrids();
            try { TxtBuildLog.Text = RuleCodeGen.Generate(CurrentProfile()); }
            catch (Exception ex) { TxtBuildLog.Text = "생성 실패: " + ex.Message; }
        }

        private void CommitGrids()
        {
            GridActions.CommitEdit(DataGridEditingUnit.Row, true);
            GridConds.CommitEdit(DataGridEditingUnit.Row, true);
            GridChoices.CommitEdit(DataGridEditingUnit.Row, true);
            GridChoiceConds.CommitEdit(DataGridEditingUnit.Row, true);
            GridForbids.CommitEdit(DataGridEditingUnit.Row, true);
            GridForbidConds.CommitEdit(DataGridEditingUnit.Row, true);
            GridTriggers.CommitEdit(DataGridEditingUnit.Row, true);
            GridOpps.CommitEdit(DataGridEditingUnit.Row, true);
            GridRouteConds.CommitEdit(DataGridEditingUnit.Row, true);
        }

        private void OnBuildEngine(object sender, RoutedEventArgs e)
        {
            if (_project == null) { MessageBox.Show("EDOPro 폴더를 먼저 여세요."); return; }
            CommitGrids();

            var p = CurrentProfile();
            if (!PluginBuilder.IsValidIdentifier(RuleCodeGen.ClassName(p.Engine)))
            {
                MessageBox.Show("엔진 이름은 영문/숫자/밑줄만 사용할 수 있습니다 (예: MyDeck).", "이름 오류");
                return;
            }

            string source;
            try { source = RuleCodeGen.Generate(p); }
            catch (Exception ex) { MessageBox.Show("소스 생성 실패: " + ex.Message); return; }

            var res = PluginBuilder.BuildFromSource(_project, RuleCodeGen.ClassName(p.Engine), source);

            var note = "";
            if (p.FreePlay && p.Actions.Count == 0 && p.Triggers.Count == 0)
                note = "[자유 플레이] 행동 규칙이 없어 기본 AI 동작을 자동 추가했습니다. 금지 규칙만 제약으로 적용됩니다." + Environment.NewLine + Environment.NewLine;

            TxtBuildLog.Text = note + res.Log + Environment.NewLine + Environment.NewLine +
                               (res.Ok ? "OK  → " + res.OutputPath : "FAILED");

            if (res.Ok)
            {
                try
                {
                    var path = ProfilePath;
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    File.WriteAllText(path, JsonSerializer.Serialize(p, WindBotProject.JsonOpts));
                }
                catch { }
                Status("엔진 빌드 성공: " + Path.GetFileName(res.OutputPath) + "  (WindBot 재시작 필요)");
                UpdateProfilePath();
            }
            else Status("엔진 빌드 실패 — 아래 로그 확인");
        }

        // ================= 규칙 편집 =================

        private void OnAddAction(object sender, RoutedEventArgs e)
        {
            _actions.Add(new ActionRule { Type = "Activate", Card = 0 });
            GridActions.SelectedIndex = _actions.Count - 1;
        }

        private void OnRemoveAction(object sender, RoutedEventArgs e)
        {
            if (GridActions.SelectedItem is ActionRule a) _actions.Remove(a);
        }

        private void OnActionSelected(object sender, SelectionChangedEventArgs e)
        {
            if (GridActions.SelectedItem is ActionRule a)
            {
                GridConds.ItemsSource = a.Conditions;
                TxtCondHeader.Text = $"조건 — {a.Type}" + (a.Card > 0 ? $" / {a.Card}" : " / 카드 무관");
            }
            else
            {
                GridConds.ItemsSource = null;
                TxtCondHeader.Text = "조건 — 모두 만족";
            }
        }

        private void OnAddCondition(object sender, RoutedEventArgs e)
        {
            if (GridActions.SelectedItem is not ActionRule a)
            {
                MessageBox.Show("먼저 행동 규칙을 선택하세요.", "조건 추가");
                return;
            }
            a.Conditions.Add(new Condition { Kind = "enemyMonsterCount", Op = ">=", Value = 1, Side = "enemy", Join = "and" });
            GridConds.Items.Refresh();
        }

        private void OnRemoveCondition(object sender, RoutedEventArgs e)
        {
            if (GridActions.SelectedItem is ActionRule a && GridConds.SelectedItem is Condition c)
            {
                a.Conditions.Remove(c);
                GridConds.Items.Refresh();
            }
        }

        private void OnAddChoice(object sender, RoutedEventArgs e)
        {
            _choices.Add(new ChoiceRule { On = "SelectCard", Pick = "enemyHighestAttack" });
            GridChoices.SelectedIndex = _choices.Count - 1;
            OnChoiceSelected(sender, null);
        }

        private void OnRemoveChoice(object sender, RoutedEventArgs e)
        {
            if (GridChoices.SelectedItem is ChoiceRule c) _choices.Remove(c);
            OnChoiceSelected(sender, null);
        }

        // ---- 선택 규칙의 조건 (B-6) ----

        private ChoiceRule? SelectedChoice => GridChoices.SelectedItem as ChoiceRule;

        private void OnChoiceSelected(object sender, SelectionChangedEventArgs? e)
        {
            var c = SelectedChoice;

            // 규칙이 바뀌기 전에 조건 그리드의 편집 내용을 먼저 커밋 (입력 유실 방지)
            if (!ReferenceEquals(GridChoiceConds.ItemsSource, c?.Conditions))
                GridChoiceConds.CommitEdit(DataGridEditingUnit.Row, true);

            if (c != null)
            {
                c.Conditions ??= new List<Condition>();
                GridChoiceConds.ItemsSource = c.Conditions;
                ChoiceCondTitle.Text = $"규칙: {c.OnName.Label} · 우선 {c.Priority}" +
                                       (c.WhenCard > 0 ? $" · 발동카드 {c.WhenCard}" : " · 발동카드 무관");
            }
            else
            {
                GridChoiceConds.ItemsSource = null;
                ChoiceCondTitle.Text = "선택 규칙 행을 먼저 선택하세요";
            }
            GridChoiceConds.Items.Refresh();
            c?.NotifyConditions();
        }

        private void OnAddChoiceCond(object sender, RoutedEventArgs e)
        {
            var c = SelectedChoice;
            if (c == null)
            {
                MessageBox.Show("먼저 선택 규칙 행을 선택하세요.", "조건 추가");
                return;
            }
            c.Conditions ??= new List<Condition>();
            c.Conditions.Add(new Condition { Kind = "myLp", Op = "<=", Value = 4000, Side = "me", Join = "and" });
            GridChoiceConds.Items.Refresh();
            if (GridChoiceConds.Items.Count > 0) GridChoiceConds.SelectedIndex = GridChoiceConds.Items.Count - 1;
            c.NotifyConditions();
        }

        private void OnRemoveChoiceCond(object sender, RoutedEventArgs e)
        {
            var c = SelectedChoice;
            if (c != null && GridChoiceConds.SelectedItem is Condition cond)
            {
                c.Conditions.Remove(cond);
                GridChoiceConds.Items.Refresh();
                c.NotifyConditions();
            }
        }

        private void OnPickChoiceCondCard(object sender, RoutedEventArgs e)
        {
            if (GridChoiceConds.SelectedItem is not Condition cond)
            {
                MessageBox.Show("조건 행을 먼저 선택하세요.", "카드 선택");
                return;
            }
            if (PickCard(cond.Card) is int v) { cond.Card = v; GridChoiceConds.Items.Refresh(); }
        }

        private void OnAddForbid(object sender, RoutedEventArgs e)
        {
            _forbids.Add(new ForbidRule { Card = 0, Action = "Activate", Priority = 100, EffectDesc = -1, TurnScope = "any" });
            GridForbids.SelectedIndex = _forbids.Count - 1;
        }

        private void OnRemoveForbid(object sender, RoutedEventArgs e)
        {
            if (GridForbids.SelectedItem is ForbidRule f) _forbids.Remove(f);
            OnForbidSelected(sender, null);
        }

        // ---- 금지 규칙의 예외 조건 ----

        private ForbidRule? SelectedForbid => GridForbids.SelectedItem as ForbidRule;

        private void OnForbidSelected(object sender, SelectionChangedEventArgs? e)
        {
            var f = SelectedForbid;
            if (f != null)
            {
                f.Conditions ??= new List<Condition>();
                GridForbidConds.ItemsSource = f.Conditions;
                ForbidCondTitle.Text = $"대상: 카드 {f.Card} · {f.ActionName}";
            }
            else
            {
                GridForbidConds.ItemsSource = null;
                ForbidCondTitle.Text = "금지 규칙 행을 먼저 선택하세요";
            }
            GridForbidConds.Items.Refresh();
        }

        private void OnAddForbidCond(object sender, RoutedEventArgs e)
        {
            var f = SelectedForbid;
            if (f == null)
            {
                MessageBox.Show("먼저 금지 규칙 행을 선택하세요.", "예외 조건 추가");
                return;
            }
            f.Conditions ??= new List<Condition>();
            f.Conditions.Add(new Condition { Kind = "enemyMonsterCount", Op = ">=", Value = 1, Side = "me", Join = "and" });
            GridForbidConds.Items.Refresh();
            if (GridForbidConds.Items.Count > 0) GridForbidConds.SelectedIndex = GridForbidConds.Items.Count - 1;
        }

        private void OnRemoveForbidCond(object sender, RoutedEventArgs e)
        {
            var f = SelectedForbid;
            if (f != null && GridForbidConds.SelectedItem is Condition c)
            {
                f.Conditions.Remove(c);
                GridForbidConds.Items.Refresh();
            }
        }

        private void OnPickForbidCondCard(object sender, RoutedEventArgs e)
        {
            if (GridForbidConds.SelectedItem is not Condition c)
            {
                MessageBox.Show("예외 조건 행을 먼저 선택하세요.", "카드 선택");
                return;
            }
            if (PickCard(c.Card) is int v) { c.Card = v; GridForbidConds.Items.Refresh(); }
        }

        /// <summary>같은 카드의 행동 규칙 조건을 예외 조건으로 복사 (그룹끼리는 OR).</summary>
        private void OnCopyForbidCondFromAction(object sender, RoutedEventArgs e)
        {
            var f = SelectedForbid;
            if (f == null)
            {
                MessageBox.Show("먼저 금지 규칙 행을 선택하세요.", "행동 조건 복사");
                return;
            }

            var sources = _actions.Where(a => a.Card > 0 && a.Card == f.Card && a.Conditions.Count > 0).ToList();
            if (sources.Count == 0)
            {
                MessageBox.Show($"카드 {f.Card} 에 조건이 설정된 행동 규칙이 없습니다." + Environment.NewLine +
                                "① 행동 · 조건 탭에서 같은 카드의 행동과 조건을 먼저 만드세요.", "행동 조건 복사");
                return;
            }

            var list = new List<Condition>();
            for (int g = 0; g < sources.Count; g++)
            {
                foreach (var c in sources[g].Conditions)
                    list.Add(new Condition { Kind = c.Kind, Op = c.Op, Value = c.Value, Card = c.Card, Side = c.Side, Join = c.Join });
                // 그룹 경계: 두 번째 그룹부터는 OR 로 연결
                int first = list.Count - sources[g].Conditions.Count;
                if (g > 0) list[first].Join = "or";
            }

            f.Conditions = list;
            GridForbidConds.ItemsSource = f.Conditions;
            GridForbidConds.Items.Refresh();
            Status($"예외 조건 {list.Count}개를 행동 규칙에서 복사했습니다.");
        }

        private void OnAddTrigger(object sender, RoutedEventArgs e)
        {
            _triggers.Add(new FinishTrigger { Card = 0, Zone = "myField", WhenDone = "battle", StopOthers = true, Priority = 10, Group = 1 });
            GridTriggers.SelectedIndex = _triggers.Count - 1;
        }

        private void OnRemoveTrigger(object sender, RoutedEventArgs e)
        {
            if (GridTriggers.SelectedItem is FinishTrigger t) _triggers.Remove(t);
        }

        private void OnAddOpp(object sender, RoutedEventArgs e)
        {
            _opps.Add(new OppTrigger { Card = 0, ResponseCard = 0, Priority = 100 });
            GridOpps.SelectedIndex = _opps.Count - 1;
        }

        private void OnRemoveOpp(object sender, RoutedEventArgs e)
        {
            if (GridOpps.SelectedItem is OppTrigger o) _opps.Remove(o);
        }

        // ================= 전개 루트 (선공 / 후공 × N벌) =================

        /// <summary>지금 편집 중인 트랙의 루트 목록</summary>
        private ObservableCollection<RouteSet> ActiveRouteSets => _routeSecondTrack ? _routesSecond : _routes;

        /// <summary>선택한 루트의 단계 목록 (루트가 없으면 빈 목록)</summary>
        private ObservableCollection<RouteStep> ActiveSteps =>
            _selectedRouteSet?.Steps ?? new ObservableCollection<RouteStep>();

        private string TrackName => _routeSecondTrack ? "후공" : "선공";

        private void OnRouteTrackFirst(object sender, RoutedEventArgs e) => SetRouteTrack(false);
        private void OnRouteTrackSecond(object sender, RoutedEventArgs e) => SetRouteTrack(true);

        private void SetRouteTrack(bool second)
        {
            if (_routeSecondTrack != second)
            {
                CommitGrids();              // 편집 중이던 값을 먼저 커밋 (입력 유실 금지)
                _routeSecondTrack = second;
                _selectedRouteSet = null;   // 트랙이 바뀌면 루트 선택도 새로
                SelectRouteOption(null);
            }
            UpdateRouteTrackUi();
        }

        /// <summary>⑤ 탭 전체(트랙 버튼 · 루트 목록 · 단계 카드 · 안내 문구)를 다시 그린다.</summary>
        private void UpdateRouteTrackUi()
        {
            _routeUiBusy = true;
            try
            {
                var sets = ActiveRouteSets;
                if (_selectedRouteSet == null || !sets.Contains(_selectedRouteSet))
                    _selectedRouteSet = sets.Count > 0 ? sets[0] : null;

                var accent = (Brush)FindResource(_routeSecondTrack ? "RouteSecondBrush" : "RouteFirstBrush");

                ListRouteSets.ItemsSource = sets;
                ListRouteSets.Tag = accent;
                ListRouteSets.SelectedItem = _selectedRouteSet;

                ItemsRoute.ItemsSource = _selectedRouteSet?.Steps;
                ItemsRoute.Tag = accent;

                StyleTrackButton(BtnRouteFirst, !_routeSecondTrack, "RouteFirstBrush", "선공 전개");
                StyleTrackButton(BtnRouteSecond, _routeSecondTrack, "RouteSecondBrush", "후공 전개");

                TxtRouteStatus.Text = sets.Count == 0
                    ? "현재: " + TrackName + " 전개 (비어 있음)"
                    : "현재: " + TrackName + " 전개 (" + sets.Count + "벌)";
                TxtRouteStatus.Foreground = accent;

                PnlRouteNotice.BorderBrush = accent;
                TxtRouteNotice.Foreground = accent;
                TxtRouteNotice.Text = RouteNoticeText(sets);

                if (TxtRouteSetsEmpty != null)
                {
                    TxtRouteSetsEmpty.Text = "루트가 없습니다 — 위의 [+ 루트] 로 추가하세요";
                    TxtRouteSetsEmpty.Visibility = sets.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                }
                if (TxtRouteEmpty != null)
                {
                    TxtRouteEmpty.Text = _selectedRouteSet == null
                        ? "루트를 선택하면 단계가 표시됩니다"
                        : "이 루트에 단계가 없습니다 — 위의 [+ 단계 추가] 로 추가하세요";
                    TxtRouteEmpty.Visibility = (_selectedRouteSet == null || _selectedRouteSet.Steps.Count == 0)
                        ? Visibility.Visible
                        : Visibility.Collapsed;
                }
            }
            finally { _routeUiBusy = false; }
            UpdateRouteCondHeader();
            UpdateRuleSummary();
        }

        /// <summary>⑤ 탭 안내 문구 — 트랙 설명 + 루트 선택 규칙 경고.</summary>
        private string RouteNoticeText(IList<RouteSet> sets)
        {
            var lines = new List<string>();
            lines.Add(_routeSecondTrack
                ? "BP 진입 가능 — 선공과 순서가 다를 수 있음"
                : "선공 전개 — 첫 턴에 실행할 순서입니다");

            if (!_routeSecondTrack && _routesSecond.Count == 0 && sets.Count > 0)
                lines.Add("후공 트랙이 비어 있습니다 — 이 선공 루트를 후공에서도 사용합니다");

            if (sets.Count == 0)
                lines.Add("루트가 없습니다 — 이 트랙에서는 전개하지 않습니다");
            else
            {
                if (sets.All(s => !s.IsDefault))
                    lines.Add("기본 루트가 없습니다 — 진입 조건을 만족하는 루트가 없으면 그 턴에는 전개하지 않습니다");

                var noCond = sets.Where(s => !s.IsDefault && (s.Conditions == null || s.Conditions.Count == 0)).ToList();
                if (noCond.Count > 0)
                    lines.Add("진입 조건이 없는 루트는 항상 선택됩니다: " + string.Join(", ", noCond.Select(s => s.DisplayName)));

                var noStep = sets.Where(s => s.Steps == null || s.Steps.Count == 0).ToList();
                if (noStep.Count > 0)
                    lines.Add("단계가 없는 루트는 실행되지 않습니다: " + string.Join(", ", noStep.Select(s => s.DisplayName)));
            }
            return string.Join(Environment.NewLine, lines);
        }

        private void StyleTrackButton(Button btn, bool selected, string brushKey, string label)
        {
            btn.Content = (selected ? "■ " : "   ") + label;
            btn.FontWeight = selected ? FontWeights.Bold : FontWeights.Normal;
            // B-8: 색은 테마 리소스만 사용한다(하드코딩 금지). 라이트/다크 모두 읽히도록 선택 = 강조색 + RouteTrackOnFg,
            //      비선택 = 카드 배경 + 기본 텍스트색.
            btn.SetResourceReference(Control.BackgroundProperty, selected ? brushKey : "CardBg");
            btn.SetResourceReference(Control.BorderBrushProperty, selected ? brushKey : "BorderBrush2");
            btn.SetResourceReference(Control.ForegroundProperty, selected ? "RouteTrackOnFg" : "TextFg");
        }

        /// <summary>⑤ 트랙 버튼이 실제로 쓰는 테마 리소스 키 (--uicheck 보고용)</summary>
        public string TrackButtonKeys(bool selected, bool second) =>
            (selected ? "RouteTrackOnFg" : "TextFg") + "/" + (selected ? (second ? "RouteSecondBrush" : "RouteFirstBrush") : "CardBg");

        /// <summary>버튼 전경/배경의 실제 색 (--uicheck 대비 확인용)</summary>
        public static string BrushColorText(Brush brush)
        {
            if (brush is SolidColorBrush sb) return "#" + sb.Color.R.ToString("X2") + sb.Color.G.ToString("X2") + sb.Color.B.ToString("X2");
            return brush == null ? "(null)" : brush.ToString();
        }

        /// <summary>전경/배경 색 밝기 차이 (0~1). 0.25 미만이면 읽기 어렵다고 본다.</summary>
        public static double ContrastGap(Brush fg, Brush bg)
        {
            if (fg is not SolidColorBrush f || bg is not SolidColorBrush b) return 1;
            Func<Color, double> lum = c => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;
            return Math.Abs(lum(f.Color) - lum(b.Color));
        }

        private static RouteStep? RouteStepOf(object sender) =>
            (sender as FrameworkElement)?.DataContext as RouteStep;

        private static RouteOption? RouteOptionOf(object sender) =>
            (sender as FrameworkElement)?.DataContext as RouteOption;

        // ---- 루트 1벌 관리 ----

        private void OnRouteSetSelected(object sender, SelectionChangedEventArgs e)
        {
            if (_routeUiBusy) return;
            var set = ListRouteSets.SelectedItem as RouteSet;
            if (ReferenceEquals(set, _selectedRouteSet)) return;

            CommitGrids();                 // 이전 루트의 조건 편집 내용 커밋 (유실 금지)
            _selectedRouteSet = set;
            SelectRouteOption(null);       // 순위 선택 해제 → 루트 진입 조건 표시
            UpdateRouteTrackUi();
        }

        private void OnAddRouteSet(object sender, RoutedEventArgs e) => AddRouteSet();

        private RouteSet AddRouteSet()
        {
            CommitGrids();
            var sets = ActiveRouteSets;
            var set = new RouteSet
            {
                Name = "루트 " + (sets.Count + 1),
                IsDefault = sets.Count == 0,      // 트랙의 첫 루트는 기본으로
            };
            sets.Add(set);
            AddRouteStep(set);                    // 바로 쓸 수 있게 1단계 + 1순위
            _selectedRouteSet = set;
            RenumberRoute();
            UpdateRouteTrackUi();
            return set;
        }

        private void OnRemoveRouteSet(object sender, RoutedEventArgs e) => RemoveRouteSet(_selectedRouteSet);

        private void RemoveRouteSet(RouteSet? set)
        {
            if (set == null) { Status("삭제할 루트를 먼저 선택하세요."); return; }
            CommitGrids();

            var sets = ActiveRouteSets;
            if (!sets.Contains(set)) return;

            var wasDefault = set.IsDefault;
            sets.Remove(set);
            if (wasDefault && sets.Count > 0) sets[0].IsDefault = true;   // 기본 루트는 1개 유지

            if (ReferenceEquals(_selectedRouteSet, set)) _selectedRouteSet = null;
            RenumberRoute();
            UpdateRouteTrackUi();
            SelectRouteOption(null);
            Status("루트 삭제 — " + TrackName + " 전개 " + sets.Count + "벌");
        }

        private void OnMoveRouteSetUp(object sender, RoutedEventArgs e) => MoveRouteSet(-1);
        private void OnMoveRouteSetDown(object sender, RoutedEventArgs e) => MoveRouteSet(+1);

        private void MoveRouteSet(int delta)
        {
            var set = _selectedRouteSet;
            if (set == null) { Status("옮길 루트를 먼저 선택하세요."); return; }
            CommitGrids();

            var sets = ActiveRouteSets;
            var i = sets.IndexOf(set);
            var j = i + delta;
            if (i < 0 || j < 0 || j >= sets.Count) return;
            sets.Move(i, j);
            UpdateRouteTrackUi();
        }

        private void OnSetDefaultRouteSet(object sender, RoutedEventArgs e)
        {
            var set = _selectedRouteSet;
            if (set == null) { Status("기본으로 지정할 루트를 먼저 선택하세요."); return; }
            foreach (var s in ActiveRouteSets) s.IsDefault = ReferenceEquals(s, set);
            UpdateRouteTrackUi();
            Status(TrackName + " 기본 루트 = " + set.DisplayName + " (조건을 만족하는 루트가 없을 때 실행)");
        }

        private void OnRenameRouteSet(object sender, RoutedEventArgs e)
        {
            var set = _selectedRouteSet;
            if (set == null) { Status("이름을 바꿀 루트를 먼저 선택하세요."); return; }
            var name = PromptText("루트 이름", "루트 이름 (예: A콤보):", set.Name);
            if (name == null) return;
            set.Name = name.Trim();
            UpdateRouteTrackUi();
        }

        // ---- 단계 카드 ----

        private void OnAddRoute(object sender, RoutedEventArgs e) => AddRouteStep();

        private RouteStep AddRouteStep()
        {
            var set = _selectedRouteSet;
            if (set == null)
            {
                Status("먼저 [+ 루트] 로 루트를 추가하거나 왼쪽 목록에서 루트를 선택하세요.");
                return null!;
            }
            return AddRouteStep(set);
        }

        private RouteStep AddRouteStep(RouteSet set)
        {
            var step = new RouteStep { Note = "" };
            step.Options.Add(new RouteOption { Rank = 1, Card = 0, Action = "Summon" });
            set.Steps.Add(step);
            set.NotifySteps();
            RenumberRoute();
            SelectRouteOption(step.Options[0]);
            UpdateRouteTrackUi();
            return step;
        }

        private void OnRemoveRouteStep(object sender, RoutedEventArgs e)
        {
            var step = RouteStepOf(sender);
            if (step != null) RemoveRouteStep(step);
        }

        private void RemoveRouteStep(RouteStep step)
        {
            var set = _selectedRouteSet;
            if (set == null) return;
            if (_selectedRouteOption != null && step.Options.Contains(_selectedRouteOption)) SelectRouteOption(null);
            set.Steps.Remove(step);
            set.NotifySteps();
            RenumberRoute();
            UpdateRouteTrackUi();
        }

        private void OnMoveRouteStepUp(object sender, RoutedEventArgs e) => MoveRouteStep(RouteStepOf(sender), -1);
        private void OnMoveRouteStepDown(object sender, RoutedEventArgs e) => MoveRouteStep(RouteStepOf(sender), +1);

        private void MoveRouteStep(RouteStep? step, int delta)
        {
            if (step == null) return;
            var list = ActiveSteps;
            var i = list.IndexOf(step);
            var j = i + delta;
            if (i < 0 || j < 0 || j >= list.Count) return;
            list.Move(i, j);
            RenumberRoute();
        }

        private void OnAddRouteOption(object sender, RoutedEventArgs e)
        {
            var step = RouteStepOf(sender);
            if (step != null) AddRouteOption(step);
        }

        private RouteOption AddRouteOption(RouteStep step)
        {
            var opt = new RouteOption { Rank = step.Options.Count + 1, Card = 0, Action = "Summon" };
            step.Options.Add(opt);
            RenumberRoute();
            SelectRouteOption(opt);
            return opt;
        }

        private void OnRemoveRouteOption(object sender, RoutedEventArgs e)
        {
            var opt = RouteOptionOf(sender);
            if (opt != null) RemoveRouteOption(opt);
        }

        private void RemoveRouteOption(RouteOption opt)
        {
            foreach (var step in ActiveSteps)
            {
                if (!step.Options.Contains(opt)) continue;
                if (step.Options.Count <= 1)
                {
                    Status("한 단계에는 순위가 최소 1개 필요합니다 — 단계 자체를 삭제하세요.");
                    return;
                }
                if (ReferenceEquals(opt, _selectedRouteOption)) SelectRouteOption(null);
                step.Options.Remove(opt);
                break;
            }
            RenumberRoute();
        }

        private void OnEditRouteOptionConditions(object sender, RoutedEventArgs e)
        {
            var opt = RouteOptionOf(sender);
            if (opt != null) SelectRouteOption(opt);
        }

        /// <summary>
        /// 조건 편집 대상을 고른다.
        /// 순위를 고르면 그 순위의 조건, null 이면 선택한 루트의 <b>진입 조건</b>을 편집한다.
        /// </summary>
        private void SelectRouteOption(RouteOption? opt)
        {
            if (_selectedRouteOption != null && !ReferenceEquals(_selectedRouteOption, opt))
                _selectedRouteOption.IsEditing = false;
            _selectedRouteOption = opt;
            if (opt != null)
            {
                opt.Conditions ??= new List<Condition>();
                opt.IsEditing = true;
            }

            foreach (var step in ActiveSteps)
                foreach (var o in step.Options)
                    o.NotifyConditions();
            _selectedRouteSet?.NotifyConditions();

            RefreshRouteCondGrid();
            UpdateRouteCondHeader();
        }

        /// <summary>아래 조건 그리드에 연결된 조건 목록 (순위 조건 우선, 없으면 루트 진입 조건)</summary>
        private List<Condition>? ConditionTarget =>
            _selectedRouteOption?.Conditions ?? _selectedRouteSet?.Conditions;

        private void RefreshRouteCondGrid()
        {
            GridRouteConds.ItemsSource = ConditionTarget;
            GridRouteConds.Items.Refresh();
        }

        private void UpdateRouteCondHeader()
        {
            if (_selectedRouteOption != null)
            {
                var stepNo = 0;
                var steps = ActiveSteps;
                for (var i = 0; i < steps.Count; i++)
                    if (steps[i].Options.Contains(_selectedRouteOption)) { stepNo = i + 1; break; }

                TxtRouteCondHeader.Text = "순위 조건 — " + TrackName + " · " + (_selectedRouteSet?.DisplayName ?? "?") +
                                          " · " + (stepNo > 0 ? stepNo + "단계" : "?단계") + " · " + _selectedRouteOption.Rank + "순위" +
                                          (_selectedRouteOption.Card > 0 ? " · 카드 " + _selectedRouteOption.Card : " · 카드 미지정");
                return;
            }

            if (_selectedRouteSet != null)
            {
                TxtRouteCondHeader.Text = "루트 진입 조건 — " + TrackName + " · " + _selectedRouteSet.DisplayName +
                                          " (비어 있으면 항상 이 루트를 선택)";
                return;
            }

            TxtRouteCondHeader.Text = "루트를 선택하면 진입 조건을, 순위 [조건]을 누르면 그 순위 조건을 편집합니다";
        }

        private void OnAddRouteCond(object sender, RoutedEventArgs e) => AddRouteCondition();

        private Condition AddRouteCondition()
        {
            var target = ConditionTarget;
            if (target == null)
            {
                Status("먼저 루트를 선택하세요 (루트 진입 조건) 또는 순위의 [조건] 버튼을 누르세요 (순위 조건).");
                return null!;
            }
            var c = new Condition { Kind = "enemyMonsterCount", Op = ">=", Value = 1, Side = "enemy", Join = "and" };
            target.Add(c);
            _selectedRouteOption?.NotifyConditions();
            _selectedRouteSet?.NotifyConditions();
            RefreshRouteCondGrid();
            if (GridRouteConds.Items.Count > 0) GridRouteConds.SelectedIndex = GridRouteConds.Items.Count - 1;
            return c;
        }

        private void OnRemoveRouteCond(object sender, RoutedEventArgs e)
        {
            var target = ConditionTarget;
            if (target == null || GridRouteConds.SelectedItem is not Condition c) return;
            target.Remove(c);
            _selectedRouteOption?.NotifyConditions();
            _selectedRouteSet?.NotifyConditions();
            RefreshRouteCondGrid();
        }

        private void OnPickRouteCondCard(object sender, RoutedEventArgs e)
        {
            if (GridRouteConds.SelectedItem is not Condition c)
            {
                MessageBox.Show("조건 행을 먼저 선택하세요.", "카드 선택");
                return;
            }
            if (PickCard(c.Card) is int v) { c.Card = v; GridRouteConds.Items.Refresh(); }
        }

        // ================= 리플레이(.yrpX) → 전개 루트 (B-3) =================

        /// <summary>
        /// [리플레이 가져오기] — 파일 선택 → 미리보기(내 쪽 선택) → [적용] 시 선공/후공 루트를 교체한다.
        /// </summary>
        private void OnImportReplay(object sender, RoutedEventArgs e)
        {
            // 카드명 표시를 위해 cdb 가 닫혀 있으면 조용히 다시 연다
            if (!CardDatabase.Instance.IsOpen)
            {
                var root = string.IsNullOrEmpty(App.EdoproRoot) ? App.WindBotRoot : App.EdoproRoot;
                if (!string.IsNullOrEmpty(root)) OpenCardDatabase(root);
            }

            var dlg = new OpenFileDialog
            {
                Title = "패 테스트 등에서 전개 루트만 기록한 리플레이를 요구합니다.",
                Filter = "EDOPro 리플레이 (*.yrpX)|*.yrpX|모든 파일 (*.*)|*.*",
                CheckFileExists = true,
                Multiselect = false
            };
            var replayDir = string.IsNullOrEmpty(App.EdoproRoot) ? "" : Path.Combine(App.EdoproRoot, "replay");
            if (Directory.Exists(replayDir)) dlg.InitialDirectory = replayDir;
            if (dlg.ShowDialog() != true) return;

            ReplayFile replay;
            try
            {
                replay = ReplayParser.Parse(dlg.FileName);
            }
            catch (ReplayFormatException ex)
            {
                MessageBox.Show(ex.Message, "리플레이 가져오기", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            catch (Exception ex)
            {
                MessageBox.Show("리플레이를 읽지 못했습니다: " + ex.Message, "리플레이 가져오기",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var win = new ReplayImportWindow(replay) { Owner = this };
            if (win.ShowDialog() != true) return;

            ApplyReplayRoutes(win.RouteFirst, win.RouteSecond);
        }

        /// <summary>
        /// 추출한 단계를 <b>현재 선택된 트랙</b>의 루트로 넣는다 (B-5).
        /// 그 트랙의 루트를 모두 지우고 Name="리플레이"/IsDefault=true 인 1벌로 교체한다.
        /// </summary>
        private void ApplyReplayRoutes(List<RouteStep> first, List<RouteStep> second)
        {
            CommitGrids();
            SelectRouteOption(null);

            var steps = _routeSecondTrack ? second : first;
            if (steps == null || steps.Count == 0)
            {
                MessageBox.Show("이 리플레이에는 " + TrackName + " 전개의 행동이 없습니다." + Environment.NewLine +
                                "트랙 토글로 다른 쪽을 선택하거나 다른 리플레이를 사용하세요.",
                    "리플레이 가져오기", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var sets = ActiveRouteSets;
            if (sets.Count > 0 &&
                MessageBox.Show("현재 [" + TrackName + " 전개] 루트 " + sets.Count + "벌을 모두 지우고," + Environment.NewLine +
                                "리플레이 결과 1벌(Name=\"리플레이\", 기본)로 교체합니다." + Environment.NewLine + Environment.NewLine +
                                "계속할까요?", "리플레이 가져오기",
                                MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
                return;

            _selectedRouteSet = null;
            sets.Clear();

            var set = new RouteSet { Name = "리플레이", IsDefault = true };
            foreach (var s in steps) set.Steps.Add(s);
            sets.Add(set);

            RenumberRoute();
            UpdateRouteTrackUi();
            SelectRouteOption(null);
            Status("리플레이 적용 — " + TrackName + " 전개 루트를 '리플레이' 1벌(" + set.Steps.Count + "단계)로 교체");
        }

        private void RenumberRoute()
        {
            foreach (var set in _routes) RenumberRouteSet(set);
            foreach (var set in _routesSecond) RenumberRouteSet(set);
            UpdateRouteCondHeader();
        }

        private static void RenumberRouteSet(RouteSet set)
        {
            RuleMigration.NormalizeRoute(set.Steps);   // Order + 순위 1..N 재정렬
            set.NotifySteps();
        }

        // ================= B-7: 탭 분리 / 가시성 =================

        /// <summary>⚙ AI 설정 탭 하단의 현재 규칙 요약 (행동/선택/금지/트리거/루트 벌 수).</summary>
        private void UpdateRuleSummary()
        {
            if (TxtRuleSummary == null) return;
            var p = CurrentProfile();
            var forbids = p.Forbids == null ? 0 : p.Forbids.Count;
            var triggers = (p.Triggers == null ? 0 : p.Triggers.Count) + (p.OppTriggers == null ? 0 : p.OppTriggers.Count);
            TxtRuleSummary.Text = "행동 " + p.Actions.Count
                                  + " · 선택 " + p.Choices.Count
                                  + " · 금지 " + forbids
                                  + " · 트리거 " + triggers
                                  + " · 선공 루트 " + RouteSetCount(false) + "벌 / 후공 " + RouteSetCount(true) + "벌";
        }

        /// <summary>바깥 탭이 바뀌면 요약을 갱신한다 (안쪽 TabControl/ComboBox 의 이벤트는 무시).</summary>
        private void OnMainTabChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!ReferenceEquals(e.OriginalSource, Tabs)) return;
            SetSettingsCollapsed(true);          // B-9: ⚙ AI 설정 탭은 항상 '설정 접힘' 상태로 열린다
            UpdateRuleSummary();
        }

        /// <summary>B-9: 설정 카드(2열)를 접거나 펼친다 — 기본은 접힘(규칙 서브탭 공간 확보).</summary>
        private void SetSettingsCollapsed(bool collapsed)
        {
            if (PnlSettings == null) return;
            PnlSettings.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
            if (BtnToggleSettings != null) BtnToggleSettings.Content = collapsed ? "설정 펼치기" : "설정 접기";
        }

        /// <summary>설정 카드 접기/펼치기 버튼.</summary>
        private void OnToggleSettingsPanel(object sender, RoutedEventArgs e)
        {
            if (PnlSettings == null) return;
            SetSettingsCollapsed(PnlSettings.Visibility == Visibility.Visible);
        }

        // ================= ⑤ 탭 자기검사 (--uicheck, 화면 표시 없음) =================

        public int RouteSetCount(bool second) => (second ? _routesSecond : _routes).Count;

        /// <summary>바깥 탭 개수 (B-7 검사용)</summary>
        public int MainTabCount => Tabs.Items.Count;

        /// <summary>B-7 검사: 이름으로 찾은 컨트롤 수 / 검사한 컨트롤 수</summary>
        public int LayoutControlsFound { get; private set; }
        public int LayoutControlsChecked { get; private set; }

        /// <summary>최소 크기에서 측정한 주요 폭/높이 (--uicheck 숫자 보고용)</summary>
        public string LayoutMetrics { get; private set; } = "";

        /// <summary>현재 규칙 요약 문구 (--uicheck 숫자 보고용)</summary>
        public string RuleSummaryText => TxtRuleSummary == null ? "" : TxtRuleSummary.Text;

        /// <summary>
        /// --uicheck 용: 규칙 JSON 을 읽어 화면 상태로 반영한다 (파일 쓰기 없음 · 규칙 파일은 읽기 전용).
        /// </summary>
        public bool LoadProfileFile(string path)
        {
            try
            {
                var p = JsonSerializer.Deserialize<AiProfile>(File.ReadAllText(path), WindBotProject.JsonOpts);
                if (p == null) return false;
                RuleMigration.Normalize(p);
                ApplyProfile(p);
                UpdateRuleSummary();
                return true;
            }
            catch { return false; }
        }

        /// <summary>바깥 탭에 해당 헤더가 있는지</summary>
        public bool HasMainTab(string header) => MainTabIndex(header) >= 0;

        private int MainTabIndex(string header)
        {
            for (var i = 0; i < Tabs.Items.Count; i++)
                if (Tabs.Items[i] is TabItem ti && string.Equals(Convert.ToString(ti.Header), header, StringComparison.Ordinal))
                    return i;
            return -1;
        }

        /// <summary>B-9: 최상위 탭 순서 (고정 — 6개)</summary>
        public static readonly string[] MainTabHeaders =
        {
            "덱", "⚙ AI 설정", "봇 목록 (로비 AI)", "봇 대사", "카드 DB / Lua", "검증 / 실행"
        };

        /// <summary>B-9: ⚙ AI 설정 안의 규칙 서브탭 헤더 (①~⑤)</summary>
        public static readonly string[] RuleTabHeaders =
        {
            "① 행동 · 조건", "② 선택 규칙", "③ 금지 규칙", "④ 트리거", "⑤ 전개 루트"
        };

        /// <summary>규칙 서브탭(0=① … 4=⑤)을 ⚙ AI 설정 안에서 선택한다.</summary>
        public bool SelectRuleTab(int index)
        {
            if (index < 0 || index >= RuleTabHeaders.Length) return false;
            if (RuleTabs == null || RuleTabs.Items.Count <= index) return false;
            var setIdx = MainTabIndex("⚙ AI 설정");
            if (setIdx < 0) return false;
            if (Tabs.SelectedIndex != setIdx) Tabs.SelectedIndex = setIdx;
            RuleTabs.SelectedIndex = index;
            return true;
        }

        /// <summary>B-9 검사: ①~⑤ 가 ⚙ AI 설정 하위 서브탭 5개인지</summary>
        public bool RuleSubTabsOk { get; private set; }

        /// <summary>규칙 서브탭 개수 (--uicheck 보고용)</summary>
        public int RuleSubTabCount => RuleTabs == null ? 0 : RuleTabs.Items.Count;

        /// <summary>B-9 검사: 설정 카드가 기본으로 접혀 있는지</summary>
        public bool SettingsCollapsedByDefault { get; private set; }

        /// <summary>B-9 검사: 1100x700 에서 접힌/펼친 설정 카드 높이와 규칙 서브탭 영역 높이 (px)</summary>
        public double SettingsCollapsedHeight { get; private set; }
        public double SettingsExpandedHeight { get; private set; }
        public double RuleTabsHeight { get; private set; }

        /// <summary>B-9 검사: 1100x700 에서 ⑤ 루트 목록 / 단계 영역 높이 (px)</summary>
        public double RouteListHeight { get; private set; }
        public double StepsHeight { get; private set; }
        public double RouteListHeightDefault { get; private set; }
        public double StepsHeightDefault { get; private set; }

        /// <summary>B-9 검사: ⑤ 트랙 버튼 전경/배경 색과 테마 키 (대비 확인용)</summary>
        public string TrackButtonMetrics { get; private set; } = "";

        /// <summary>
        /// B-9 자기검사: 최상위 6탭 + ⚙ AI 설정 하위 규칙 서브탭 5개 · 이동한 컨트롤(FindName) · 창 최소 크기 ·
        /// 설정 카드 기본 접힘/펼침 높이 · 공통 DataGrid 스타일 · 1100x700/1560x1000 에서 ⑤ 루트 목록/단계 높이 ·
        /// 트랙 버튼 대비를 확인한다. 창은 표시하지 않고, 끝나면 원래 탭/크기로 되돌린다.
        /// </summary>
        public List<string> VerifyLayoutUi()
        {
            var problems = new List<string>();
            var oldTab = Tabs.SelectedIndex;
            var oldRuleTab = RuleTabs == null ? -1 : RuleTabs.SelectedIndex;

            // 1) 최상위 탭: 개수(6) + 순서(고정) — ①~⑤ 는 여기에 없다
            var deckIdx = MainTabIndex("덱");
            var setIdx = MainTabIndex("⚙ AI 설정");
            if (MainTabCount != MainTabHeaders.Length)
                problems.Add("최상위 탭 수가 " + MainTabHeaders.Length + "이 아님: " + MainTabCount);
            for (var i = 0; i < MainTabHeaders.Length; i++)
            {
                var actual = i < Tabs.Items.Count && Tabs.Items[i] is TabItem ti ? Convert.ToString(ti.Header) : "(없음)";
                if (!string.Equals(actual, MainTabHeaders[i], StringComparison.Ordinal))
                    problems.Add("탭 순서 불일치 [" + i + "] 기대=" + MainTabHeaders[i] + " 실제=" + actual);
            }
            if (deckIdx < 0) problems.Add("'덱' 탭이 없음");
            if (setIdx < 0) problems.Add("'⚙ AI 설정' 탭이 없음");
            if (deckIdx >= 0 && setIdx >= 0 && setIdx != deckIdx + 1)
                problems.Add("'⚙ AI 설정' 탭이 '덱' 바로 다음이 아님 (" + deckIdx + " → " + setIdx + ")");

            // 2) B-9: ①~⑤ 는 ⚙ AI 설정 안의 서브탭(RuleTabs) 5개여야 한다
            RuleSubTabsOk = true;
            if (RuleTabs == null) { RuleSubTabsOk = false; problems.Add("규칙 서브탭(RuleTabs)이 없음"); }
            else
            {
                if (RuleTabs.Items.Count != RuleTabHeaders.Length)
                {
                    RuleSubTabsOk = false;
                    problems.Add("규칙 서브탭 수가 " + RuleTabHeaders.Length + "이 아님: " + RuleTabs.Items.Count);
                }
                for (var i = 0; i < RuleTabHeaders.Length && i < RuleTabs.Items.Count; i++)
                {
                    var actual = RuleTabs.Items[i] is TabItem ti ? Convert.ToString(ti.Header) : "(없음)";
                    if (!string.Equals(actual, RuleTabHeaders[i], StringComparison.Ordinal))
                    {
                        RuleSubTabsOk = false;
                        problems.Add("규칙 서브탭 순서 불일치 [" + i + "] 기대=" + RuleTabHeaders[i] + " 실제=" + actual);
                    }
                    if (MainTabIndex(RuleTabHeaders[i]) >= 0)
                    {
                        RuleSubTabsOk = false;
                        problems.Add("규칙 탭이 아직 최상위에 있음: " + RuleTabHeaders[i]);
                    }
                }
            }
            // 중첩 TabControl 이어도 같은 XAML 네임스코프이므로 이름으로 찾을 수 있어야 한다
            if (FindName("GridActions") == null || FindName("ListRouteSets") == null)
            {
                RuleSubTabsOk = false;
                problems.Add("규칙 탭 내용이 창 네임스코프에 없음(이름/중첩 구조 의심)");
            }

            // 2) 이동한 컨트롤이 창 네임스코프에 실제로 있는지 (x:Name 변경 금지 확인)
            string[] names =
            {
                "TxtEngineName", "TxtEngineDesc", "ChkFreePlay", "ChkDebugEffect", "CmbRps", "CmbTurnChoice",
                "CmbDialog", "CmbChainBasis", "CardAvoidColumn", "BtnToggleSettings", "PnlSettings", "PnlSettingsCard",
                "TxtRuleSummary", "TxtProfilePath", "TxtBuildLog",
                "ListDecks", "TxtDeck", "TxtDeckCardFilter", "ListDeckCards", "TxtDeckInfo",
                "GridActions", "GridConds", "GridChoices", "GridChoiceConds", "GridForbids", "GridForbidConds",
                "GridTriggers", "GridOpps", "GridRouteConds", "ItemsRoute", "ListRouteSets", "CmbDialogFile",
                "BtnRouteFirst", "BtnRouteSecond", "TxtRouteStatus", "TxtRouteNotice", "PnlRouteNotice",
                "TxtRouteEmpty", "TxtRouteSetsEmpty", "ScrollRouteSteps"
            };
            LayoutControlsChecked = names.Length;
            LayoutControlsFound = 0;
            foreach (var n in names)
            {
                if (FindName(n) != null) LayoutControlsFound++;
                else problems.Add("컨트롤을 찾을 수 없음(이름 변경 의심): " + n);
            }

            // 3) 창 최소 크기 (좁은 창에서 오른쪽이 잘리지 않도록)
            if (MinWidth < 1000) problems.Add("MinWidth 가 1000 미만: " + MinWidth);
            if (MinHeight < 650) problems.Add("MinHeight 가 650 미만: " + MinHeight);

            // 4) 가시성 공통 스타일 (App.xaml 암묵적 스타일)
            if (Application.Current != null && Application.Current.Resources[typeof(DataGrid)] is Style dgStyle)
            {
                var rowH = StyleSetterValue(dgStyle, DataGrid.RowHeightProperty);
                var headH = StyleSetterValue(dgStyle, DataGrid.ColumnHeaderHeightProperty);
                var font = StyleSetterValue(dgStyle, Control.FontSizeProperty);
                if (rowH < 29) problems.Add("DataGrid RowHeight 가 29 미만: " + rowH);
                if (headH < 31) problems.Add("DataGrid ColumnHeaderHeight 가 31 미만: " + headH);
                if (font < 13) problems.Add("DataGrid FontSize 가 13 미만: " + font);
            }
            else problems.Add("App.xaml 에 DataGrid 암묵적 스타일이 없음");

            // 5) 최소 크기에서 실제 레이아웃 측정 (잘림/예외 확인)
            var minW = Math.Max(MinWidth, 1000);
            var minH = Math.Max(MinHeight, 650);

            if (deckIdx >= 0)
            {
                Tabs.SelectedIndex = deckIdx;
                MeasureAt(minW, minH);
                if (ListDecks.ActualWidth < 150)
                    problems.Add("최소 폭에서 덱 목록이 너무 좁음: " + Math.Round(ListDecks.ActualWidth));
                if (TxtDeck.ActualWidth < 280)
                    problems.Add("최소 폭에서 덱 텍스트가 너무 좁음: " + Math.Round(TxtDeck.ActualWidth));
                LayoutMetrics = "deckList=" + Math.Round(ListDecks.ActualWidth) +
                                " deckText=" + Math.Round(TxtDeck.ActualWidth) + "x" + Math.Round(TxtDeck.ActualHeight);
            }

            // 6) ⚙ AI 설정: 기본 = 설정 카드 접힘 + 규칙 서브탭(*) + 요약 + 빌드 로그  (B-9)
            if (setIdx >= 0)
            {
                Tabs.SelectedIndex = setIdx;
                SetSettingsCollapsed(true);
                MeasureAt(minW, minH);

                SettingsCollapsedByDefault = PnlSettings.Visibility == Visibility.Collapsed
                                             && Convert.ToString(BtnToggleSettings.Content) == "설정 펼치기";
                SettingsCollapsedHeight = PnlSettingsCard.ActualHeight;
                RuleTabsHeight = RuleTabs == null ? 0 : RuleTabs.ActualHeight;
                if (!SettingsCollapsedByDefault) problems.Add("설정 카드가 기본으로 접혀 있지 않음");
                if (SettingsCollapsedHeight > 60)
                    problems.Add("접힌 설정 카드가 너무 큼: " + Math.Round(SettingsCollapsedHeight));
                if (TxtBuildLog.ActualHeight < 60)
                    problems.Add("최소 높이에서 빌드 로그가 너무 낮음: " + Math.Round(TxtBuildLog.ActualHeight));
                if (RuleTabsHeight < 300)
                    problems.Add("최소 높이에서 규칙 서브탭 영역이 너무 낮음: " + Math.Round(RuleTabsHeight));

                LayoutMetrics += " ruleTabsH=" + Math.Round(RuleTabsHeight) +
                                 " buildLogH=" + Math.Round(TxtBuildLog.ActualHeight) +
                                 " settingsCollapsedH=" + Math.Round(SettingsCollapsedHeight);

                UpdateRuleSummary();
                if (TxtRuleSummary.Text.IndexOf("행동", StringComparison.Ordinal) < 0)
                    problems.Add("규칙 요약 문구 없음: " + TxtRuleSummary.Text);
                if (TxtRuleSummary.Text.IndexOf("선공 루트", StringComparison.Ordinal) < 0)
                    problems.Add("규칙 요약에 루트 벌 수가 없음: " + TxtRuleSummary.Text);

                // 펼쳤을 때 수치 → 다시 접으면 원래대로 (설정 카드가 규칙 영역을 잠식하지 않는지)
                SetSettingsCollapsed(false);
                MeasureAt(minW, minH);
                SettingsExpandedHeight = PnlSettingsCard.ActualHeight;
                var expandedRuleTabsH = RuleTabs == null ? 0 : RuleTabs.ActualHeight;
                var settingsSize = Math.Round(PnlSettings.ActualWidth) + "x" + Math.Round(PnlSettings.ActualHeight);
                if (SettingsExpandedHeight <= SettingsCollapsedHeight)
                    problems.Add("설정 펼치기가 동작하지 않음 (" + Math.Round(SettingsCollapsedHeight) +
                                 " → " + Math.Round(SettingsExpandedHeight) + ")");
                SetSettingsCollapsed(true);
                MeasureAt(minW, minH);
                if (PnlSettings.Visibility != Visibility.Collapsed) problems.Add("설정 카드 복원(접힘) 실패");
                if (PnlSettingsCard.ActualHeight > 60)
                    problems.Add("다시 접은 뒤 설정 카드가 큼: " + Math.Round(PnlSettingsCard.ActualHeight));

                LayoutMetrics += " expandedSettingsH=" + Math.Round(SettingsExpandedHeight) +
                                 " expandedRuleTabsH=" + Math.Round(expandedRuleTabsH) +
                                 " settings=" + settingsSize;
            }

            // 7) ⑤ 전개 루트: 최소 크기와 기본 크기 모두에서 루트 목록/단계 영역이 실제로 보여야 한다 (B-9: 서브탭)
            if (SelectRuleTab(4))
            {
                UpdateRouteTrackUi();               // 트랙 버튼 색/문구 갱신
                MeasureAt(minW, minH);
                RouteListHeight = ListRouteSets.ActualHeight;
                StepsHeight = ScrollRouteSteps.ActualHeight;
                var condsH = GridRouteConds.ActualHeight;
                if (RouteListHeight <= 0) problems.Add("1100x700 에서 ⑤ 루트 목록 높이가 0: " + Math.Round(RouteListHeight));
                else if (RouteListHeight < 120) problems.Add("1100x700 에서 ⑤ 루트 목록이 너무 낮음: " + Math.Round(RouteListHeight));
                if (StepsHeight <= 0) problems.Add("1100x700 에서 ⑤ 단계 영역 높이가 0: " + Math.Round(StepsHeight));
                else if (StepsHeight < 120) problems.Add("1100x700 에서 ⑤ 단계 영역이 너무 낮음: " + Math.Round(StepsHeight));
                if (condsH > 200) problems.Add("⑤ 조건 그리드가 너무 큼(목록을 밀어냄): " + Math.Round(condsH));

                MeasureAt(1560, 1000);
                RouteListHeightDefault = ListRouteSets.ActualHeight;
                StepsHeightDefault = ScrollRouteSteps.ActualHeight;
                if (RouteListHeightDefault <= 0 || StepsHeightDefault <= 0)
                    problems.Add("1560x1000 에서 ⑤ 영역이 0 (" + Math.Round(RouteListHeightDefault) + " / " + Math.Round(StepsHeightDefault) + ")");

                LayoutMetrics += " routeListH=" + Math.Round(RouteListHeight) + " stepsH=" + Math.Round(StepsHeight) +
                                 " routeCondsH=" + Math.Round(condsH) +
                                 " default(1560x1000)=routeListH " + Math.Round(RouteListHeightDefault) +
                                 "/stepsH " + Math.Round(StepsHeightDefault);

                // 트랙 버튼: 선택/비선택 모두 전경·배경 대비가 확보되는지 (색은 테마 리소스만 사용)
                var gapFirst = ContrastGap(BtnRouteFirst.Foreground, BtnRouteFirst.Background);
                var gapSecond = ContrastGap(BtnRouteSecond.Foreground, BtnRouteSecond.Background);
                if (gapFirst < 0.25)
                    problems.Add("⑤ 선공 버튼 대비 부족: " + BrushColorText(BtnRouteFirst.Foreground) + " on " + BrushColorText(BtnRouteFirst.Background));
                if (gapSecond < 0.25)
                    problems.Add("⑤ 후공 버튼 대비 부족: " + BrushColorText(BtnRouteSecond.Foreground) + " on " + BrushColorText(BtnRouteSecond.Background));
                TrackButtonMetrics = "first(" + BrushColorText(BtnRouteFirst.Foreground) + " on " + BrushColorText(BtnRouteFirst.Background) + ")"
                                     + " second(" + BrushColorText(BtnRouteSecond.Foreground) + " on " + BrushColorText(BtnRouteSecond.Background) + ")"
                                     + " keys sel=" + TrackButtonKeys(true, _routeSecondTrack) + " off=" + TrackButtonKeys(false, false)
                                     + " " + CheckTrackThemeContrast(problems);
            }

            Tabs.SelectedIndex = oldTab;
            if (RuleTabs != null && oldRuleTab >= 0 && oldRuleTab < RuleTabs.Items.Count) RuleTabs.SelectedIndex = oldRuleTab;
            SetSettingsCollapsed(true);
            Relayout();
            return problems;
        }

        /// <summary>
        /// 라이트/다크 테마 사전을 각각 읽어 ⑤ 트랙 버튼 글자/배경 대비를 검사한다 (테마 상태는 건드리지 않음).
        /// </summary>
        private string CheckTrackThemeContrast(List<string> problems)
        {
            var parts = new List<string>();
            var first = Resources["RouteFirstBrush"] as Brush;
            var second = Resources["RouteSecondBrush"] as Brush;
            foreach (var theme in new[] { "Light", "Dark" })
            {
                try
                {
                    var dict = new ResourceDictionary { Source = new Uri("Themes/" + theme + ".xaml", UriKind.Relative) };
                    var onFg = dict["RouteTrackOnFg"] as Brush;
                    var textFg = dict["TextFg"] as Brush;
                    var cardBg = dict["CardBg"] as Brush;

                    var gFirst = ContrastGap(onFg, first);
                    var gSecond = ContrastGap(onFg, second);
                    var gOff = ContrastGap(textFg, cardBg);
                    parts.Add(theme + "(sel " + gFirst.ToString("0.00") + "/" + gSecond.ToString("0.00") +
                              " off " + gOff.ToString("0.00") + ")");
                    if (gFirst < 0.25) problems.Add(theme + " 테마: 선공 선택 대비 부족 (RouteTrackOnFg on RouteFirstBrush) " + gFirst.ToString("0.00"));
                    if (gSecond < 0.25) problems.Add(theme + " 테마: 후공 선택 대비 부족 (RouteTrackOnFg on RouteSecondBrush) " + gSecond.ToString("0.00"));
                    if (gOff < 0.25) problems.Add(theme + " 테마: 비선택 대비 부족 (TextFg on CardBg) " + gOff.ToString("0.00"));
                }
                catch (Exception ex)
                {
                    problems.Add(theme + " 테마 리소스 확인 실패: " + ex.Message);
                }
            }
            return "themeContrast " + string.Join(" ", parts);
        }

        /// <summary>스타일 Setter 의 double 값 읽기 (없으면 0)</summary>
        private static double StyleSetterValue(Style style, DependencyProperty prop)
        {
            foreach (var setterBase in style.Setters)
                if (setterBase is Setter s && s.Property == prop && s.Value != null)
                {
                    try { return Convert.ToDouble(s.Value); } catch { return 0; }
                }
            return 0;
        }

        /// <summary>지정 크기로 레이아웃만 측정 (창 표시 없음)</summary>
        private void MeasureAt(double w, double h)
        {
            if (Content is not FrameworkElement root) return;
            root.Measure(new Size(w, h));
            root.Arrange(new Rect(0, 0, w, h));
            root.UpdateLayout();
        }

        public int RouteStepCount(bool second) =>
            (second ? _routesSecond : _routes).Sum(s => s.Steps == null ? 0 : s.Steps.Count);

        /// <summary>창을 표시하지 않고 레이아웃만 갱신 (템플릿/바인딩 생성)</summary>
        private void Relayout()
        {
            if (Content is not FrameworkElement root) return;
            root.Measure(new Size(1560, 1000));
            root.Arrange(new Rect(0, 0, 1560, 1000));
            root.UpdateLayout();
        }

        private static T? FindChild<T>(DependencyObject? parent) where T : DependencyObject
        {
            if (parent == null) return null;
            var n = VisualTreeHelper.GetChildrenCount(parent);
            for (var i = 0; i < n; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T hit) return hit;
                var found = FindChild<T>(child);
                if (found != null) return found;
            }
            return null;
        }

        /// <summary>
        /// 창을 띄우지 않고 ⑤ 전개 루트 UI 를 조작해 트랙/루트/단계가 서로 섞이지 않는지 검사한다.
        /// 추가한 루트·단계·조건은 모두 제거해 원상 복구한다 (파일 저장 없음).
        /// </summary>
        public List<string> VerifyRouteUi()
        {
            var problems = new List<string>();
            int beforeSetsFirst = _routes.Count, beforeSetsSecond = _routesSecond.Count;
            int beforeStepsFirst = RouteStepCount(false), beforeStepsSecond = RouteStepCount(true);
            var oldTab = Tabs.SelectedIndex;
            var oldTrack = _routeSecondTrack;

            // B-9: 규칙 ①~⑤ 는 ⚙ AI 설정 하위 서브탭이다. ⑤ 서브탭을 열어야 내용이 측정되어 카드가 생성된다.
            // 0) ⑤ 탭을 실제로 열어 카드 템플릿까지 생성되는지 확인 (XAML 템플릿 오류 검출)
            SelectRuleTab(4);
            Relayout();

            // 1) 선공 트랙: 루트 추가 → 단계/순위/조건 추가
            SetRouteTrack(false);
            var set = AddRouteSet();
            Relayout();
            if (_routes.Count != beforeSetsFirst + 1) problems.Add("선공 루트 추가 실패");
            if (beforeSetsFirst == 0 && !set.IsDefault) problems.Add("트랙의 첫 루트가 기본으로 지정되지 않음");
            if (!ReferenceEquals(ListRouteSets.SelectedItem, set)) problems.Add("새 루트가 목록에서 선택되지 않음");
            if (!ReferenceEquals(ItemsRoute.ItemsSource, set.Steps)) problems.Add("단계 목록이 선택한 루트로 교체되지 않음");
            if (set.Steps.Count != 1) problems.Add("새 루트의 단계 수가 1이 아님 (" + set.Steps.Count + ")");
            if (set.Steps.Count > 0 && set.Steps[0].Options.Count != 1) problems.Add("새 단계의 순위 수가 1이 아님");

            // 루트/단계가 있는 상태에서도 최소 크기(1100x700)에 목록·단계 영역이 보이는지 (B-8)
            MeasureAt(1100, 700);
            if (ListRouteSets.ActualHeight <= 0)
                problems.Add("루트가 있을 때 ⑤ 루트 목록 높이가 0");
            if (ScrollRouteSteps.ActualHeight <= 0)
                problems.Add("단계가 있을 때 ⑤ 단계 영역 높이가 0");

            var stepCard = ItemsRoute.ItemContainerGenerator.ContainerFromIndex(set.Steps.Count - 1);
            if (stepCard == null) problems.Add("⑤ 단계 카드가 생성되지 않음 (템플릿/바인딩 오류 의심)");
            else if (FindChild<Controls.CardNameBox>(stepCard) == null) problems.Add("⑤ 순위 행(카드 검색 상자)이 생성되지 않음");

            var step = set.Steps[0];
            AddRouteOption(step);
            if (step.Options.Count != 2) problems.Add("순위 추가 실패 (" + step.Options.Count + ")");
            if (step.Options[0].Rank != 1 || step.Options[1].Rank != 2) problems.Add("순위 번호 1..N 재정렬 실패");

            SelectRouteOption(step.Options[1]);
            AddRouteCondition();                                    // 순위 조건
            if (step.Options[1].Conditions.Count != 1) problems.Add("순위 조건 추가 실패");
            if (!step.Options[1].ConditionBadge.Contains("조건 1")) problems.Add("순위 조건 배지 갱신 실패: " + step.Options[1].ConditionBadge);

            SelectRouteOption(null);                                // 루트 진입 조건
            if (!ReferenceEquals(GridRouteConds.ItemsSource, set.Conditions)) problems.Add("루트 진입 조건 그리드 연결 실패");
            AddRouteCondition();
            if (set.Conditions.Count != 1) problems.Add("루트 진입 조건 추가 실패");
            if (!set.ConditionBadge.Contains("조건 1")) problems.Add("루트 진입 조건 배지 갱신 실패: " + set.ConditionBadge);
            if (TxtRouteCondHeader.Text.IndexOf("진입 조건", StringComparison.Ordinal) < 0) problems.Add("루트 진입 조건 헤더 문구 없음: " + TxtRouteCondHeader.Text);

            // 2) 기본 루트 라디오(트랙당 1개) + 벌 수 상태 텍스트
            var set2 = AddRouteSet();
            set.Name = "A콤보";
            foreach (var s in _routes) s.IsDefault = ReferenceEquals(s, set2);
            if (set.IsDefault || !set2.IsDefault) problems.Add("기본 루트 라디오(1개만) 실패");
            if (TxtRouteStatus.Text.IndexOf((beforeSetsFirst + 2) + "벌", StringComparison.Ordinal) < 0)
                problems.Add("상태 텍스트에 벌 수가 없음: " + TxtRouteStatus.Text);

            var pFirst = CurrentProfile();
            if (pFirst.Routes.Count != beforeSetsFirst + 2) problems.Add("프로필(선공 루트) 반영 실패");
            if (pFirst.RoutesSecond.Count != beforeSetsSecond) problems.Add("선공 편집이 후공 목록에 섞임");
            if (pFirst.Route != null || pFirst.RouteSecond != null) problems.Add("구형 route/routeSecond 가 비워지지 않음");

            // 3) 코드 생성: 루트 선택 + 벌/단계 이름 + 루트 인덱스 게이트
            var src = RuleCodeGen.Generate(pFirst);
            if (src.IndexOf("SelectRouteFirst", StringComparison.Ordinal) < 0) problems.Add("SelectRouteFirst 미생성");
            if (src.IndexOf("RouteFirst0Step0_R1", StringComparison.Ordinal) < 0) problems.Add("선공 0벌 1단계 메서드 미생성");
            if (src.IndexOf("RouteFirst1Step0_R1", StringComparison.Ordinal) < 0) problems.Add("선공 1벌 1단계 메서드 미생성");
            if (src.IndexOf("_routeSetFirst", StringComparison.Ordinal) < 0) problems.Add("_routeSetFirst 필드 미생성");
            if (src.IndexOf("_routeSetFirst != 0", StringComparison.Ordinal) < 0) problems.Add("루트 인덱스 게이트 미생성");
            if (src.IndexOf("_routeSetFirst = -2", StringComparison.Ordinal) >= 0) problems.Add("기본 루트가 있는데 -2(전개 안 함) 분기가 생성됨");

            // 4) 후공 트랙으로 전환 → 별개 목록
            SetRouteTrack(true);
            if (!ReferenceEquals(ListRouteSets.ItemsSource, _routesSecond)) problems.Add("후공 전환 시 루트 목록 교체 실패");
            if (_routesSecond.Count != beforeSetsSecond) problems.Add("후공 전환에서 목록이 섞임");
            if (TxtRouteStatus.Text.IndexOf("후공 전개", StringComparison.Ordinal) < 0) problems.Add("상태 텍스트가 후공으로 바뀌지 않음: " + TxtRouteStatus.Text);
            if (TxtRouteNotice.Text.IndexOf("BP 진입 가능", StringComparison.Ordinal) < 0) problems.Add("후공 배지 누락");

            var set3 = AddRouteSet();
            if (_routesSecond.Count != beforeSetsSecond + 1) problems.Add("후공 루트 추가 실패");
            if (_routes.Count != beforeSetsFirst + 2) problems.Add("후공 편집이 선공 목록에 섞임");

            var src2 = RuleCodeGen.Generate(CurrentProfile());
            if (src2.IndexOf("SelectRouteSecond", StringComparison.Ordinal) < 0) problems.Add("SelectRouteSecond 미생성");
            if (src2.IndexOf("RouteSecond0Step0_R1", StringComparison.Ordinal) < 0) problems.Add("후공 0벌 1단계 메서드 미생성");
            if (src2.IndexOf("OnRouteSecond", StringComparison.Ordinal) < 0) problems.Add("후공 게이트 미생성");
            if (src2.IndexOf("_routeSetSecond", StringComparison.Ordinal) < 0) problems.Add("후공 루트 상태 필드 미생성");

            // 5) 원상 복구
            RemoveRouteSet(set3);
            SetRouteTrack(false);
            RemoveRouteSet(set2);
            RemoveRouteSet(set);
            RenumberRoute();
            SelectRouteOption(null);
            UpdateRouteTrackUi();
            SelectRuleTab(4);            // ⑤ 로 복귀 (원래 탭 인덱스는 아래에서 복원)
            SetRouteTrack(oldTrack);
            Relayout();
            if (_routes.Count != beforeSetsFirst || _routesSecond.Count != beforeSetsSecond) problems.Add("원상 복구 실패(루트 벌 수)");
            if (RouteStepCount(false) != beforeStepsFirst || RouteStepCount(true) != beforeStepsSecond) problems.Add("원상 복구 실패(단계 수)");

            // 6) 후공 트랙이 비어 있으면 선공 루트를 후공 게이트로 한 번 더 만든다
            if (_routes.Count > 0 && _routesSecond.Count == 0)
            {
                var fb = RuleCodeGen.Generate(CurrentProfile());
                if (fb.IndexOf("SelectRouteSecond", StringComparison.Ordinal) < 0) problems.Add("후공 빈 트랙 대체(SelectRouteSecond) 미생성");
                if (fb.IndexOf("RouteSecond0Step0_R1", StringComparison.Ordinal) < 0) problems.Add("후공 빈 트랙 대체 단계 메서드 미생성");
            }

            // 7) 리플레이 미리보기 창 XAML/추출 파이프라인 (창은 표시하지 않음)
            try
            {
                var fake = new ReplayFile { PlayerNames = new[] { "[AI] TestBot", "Human" } };
                var preview = new ReplayImportWindow(fake);
                if (preview.SummaryText.Length == 0) problems.Add("리플레이 미리보기 요약이 비어 있음");
                if (preview.Title.Length == 0) problems.Add("리플레이 미리보기 창 제목 없음");
            }
            catch (Exception ex)
            {
                problems.Add("리플레이 미리보기 창 생성 실패: " + ex.Message);
            }
            // 8) ② 선택 규칙 조건(B-6): 그리드 연결 · 배지 · 코드 게이트 · JSON 왕복
            int beforeChoices = _choices.Count;
            SelectRuleTab(1);
            Relayout();
            OnAddChoice(null, null);
            var addedChoice = SelectedChoice;
            if (addedChoice == null)
            {
                problems.Add("② 선택 규칙 추가 후 행이 선택되지 않음");
            }
            else
            {
                if (!ReferenceEquals(GridChoiceConds.ItemsSource, addedChoice.Conditions))
                    problems.Add("② 선택 규칙 조건 그리드 연결 실패");
                if (addedChoice.ConditionBadge != "조건 없음")
                    problems.Add("② 조건 배지(없음) 실패: " + addedChoice.ConditionBadge);

                OnAddChoiceCond(null, null);
                if (addedChoice.Conditions.Count != 1) problems.Add("② 선택 규칙 조건 추가 실패");
                if (!addedChoice.ConditionBadge.Contains("조건 1"))
                    problems.Add("② 조건 배지 갱신 실패: " + addedChoice.ConditionBadge);
                if (ChoiceCondTitle.Text.IndexOf("우선", StringComparison.Ordinal) < 0)
                    problems.Add("② 조건 헤더 문구 없음: " + ChoiceCondTitle.Text);

                var withCond = RuleCodeGen.Generate(CurrentProfile());
                if (withCond.IndexOf("Bot.LifePoints <= 4000", StringComparison.Ordinal) < 0)
                    problems.Add("② 선택 규칙 조건 게이트 미생성");

                // JSON 왕복: 조건 1개가 저장 → 재읽기에서 유지
                var json = JsonSerializer.Serialize(CurrentProfile(), WindBotProject.JsonOpts);
                var back = JsonSerializer.Deserialize<AiProfile>(json, WindBotProject.JsonOpts);
                if (back == null || back.Choices == null || !back.Choices.Any(x => x.Conditions != null && x.Conditions.Count == 1))
                    problems.Add("② 선택 규칙 조건 JSON 왕복 실패");

                OnRemoveChoiceCond(null, null);
                if (addedChoice.Conditions.Count != 0) problems.Add("② 선택 규칙 조건 삭제 실패");
                OnRemoveChoice(null, null);
            }
            if (_choices.Count != beforeChoices) problems.Add("② 원상 복구 실패(선택 규칙 수)");
            SelectRuleTab(4);
            Tabs.SelectedIndex = oldTab;      // 호출 전 탭으로 복원
            Relayout();

            return problems;
        }

        // ================= 카드 선택 =================

        private int? PickCard(int initial = 0)
        {
            if (!CardDatabase.Instance.IsOpen)
            {
                MessageBox.Show("카드 DB 가 열려 있지 않습니다.", "카드 선택");
                return null;
            }
            var dlg = new CardPickerWindow(initial) { Owner = this };
            return dlg.ShowDialog() == true ? dlg.SelectedCardId : null;
        }

        /// <summary>같은 카드/행동으로 조건만 다른 규칙을 복제 (조건 분기)</summary>
        private void OnBranchAction(object sender, RoutedEventArgs e)
        {
            if (GridActions.SelectedItem is not ActionRule src)
            {
                MessageBox.Show("행동 규칙을 먼저 선택하세요.", "분기 복제");
                return;
            }

            var copy = new ActionRule
            {
                Type = src.Type,
                Card = src.Card,
                Priority = src.Priority + 1,
                Note = string.IsNullOrWhiteSpace(src.Note) ? "분기" : src.Note + " (분기)"
            };
            foreach (var c in src.Conditions)
                copy.Conditions.Add(new Condition { Kind = c.Kind, Op = c.Op, Value = c.Value, Card = c.Card, Side = c.Side, Join = c.Join });

            _actions.Add(copy);
            GridActions.SelectedIndex = _actions.Count - 1;
            Status("분기 규칙 추가 — 조건을 수정하세요 (우선순위로 검사 순서 결정)");
        }

        private void OnPickActionCard(object sender, RoutedEventArgs e)
        {
            if (GridActions.SelectedItem is not ActionRule a) { MessageBox.Show("행동 규칙을 먼저 선택하세요."); return; }
            if (PickCard(a.Card) is int v) { a.Card = v; GridActions.Items.Refresh(); }
        }

        private void OnPickConditionCard(object sender, RoutedEventArgs e)
        {
            if (GridConds.SelectedItem is not Condition c) { MessageBox.Show("조건 행을 먼저 선택하세요."); return; }
            if (PickCard(c.Card) is int v) { c.Card = v; GridConds.Items.Refresh(); }
        }

        private void OnPickChoiceCard(object sender, RoutedEventArgs e)
        {
            if (GridChoices.SelectedItem is not ChoiceRule c) { MessageBox.Show("선택 규칙 행을 먼저 선택하세요."); return; }
            if (PickCard(c.WhenCard) is int v) { c.WhenCard = v; GridChoices.Items.Refresh(); }
        }

        private void OnApplyCardFromDb(int id)
        {
            if (GridActions.SelectedItem is ActionRule a) { a.Card = id; GridActions.Items.Refresh(); Status($"행동 규칙 카드 = {id}"); return; }
            if (GridConds.SelectedItem is Condition c) { c.Card = id; GridConds.Items.Refresh(); Status($"조건 카드 = {id}"); return; }
            if (GridForbids.SelectedItem is ForbidRule f) { f.Card = id; GridForbids.Items.Refresh(); Status($"금지 규칙 카드 = {id}"); return; }
            if (GridChoices.SelectedItem is ChoiceRule ch) { ch.WhenCard = id; GridChoices.Items.Refresh(); Status($"선택 규칙 발동카드 = {id}"); return; }
            if (GridTriggers.SelectedItem is FinishTrigger tg) { tg.Card = id; GridTriggers.Items.Refresh(); Status($"트리거 카드 = {id}"); return; }
            if (GridOpps.SelectedItem is OppTrigger og) { og.Card = id; GridOpps.Items.Refresh(); Status($"상대 트리거 카드 = {id}"); return; }
            if (_selectedRouteOption is RouteOption ro) { ro.Card = id; Status($"루트 순위 카드 = {id}"); return; }
            MessageBox.Show("먼저 규칙 행을 하나 선택하세요.", "카드 채우기");
        }

        private void OnSyncEngineToDeck(object sender, RoutedEventArgs e)
        {
            if (ListDecks.SelectedItem is string file)
                TxtEngineName.Text = StripDeckName(file);
        }

        // ================= 덱 =================

        private static string StripDeckName(string file)
        {
            var n = file.EndsWith(".ydk", StringComparison.OrdinalIgnoreCase) ? file[..^4] : file;
            if (n.StartsWith("AI_", StringComparison.OrdinalIgnoreCase)) n = n[3..];
            return n;
        }

        private void LoadDecks()
        {
            if (_project == null) return;
            var selected = ListDecks.SelectedItem as string;
            _allDecks = _project.ListDecks();
            ApplyDeckFilter();
            if (selected != null && _allDecks.Contains(selected)) ListDecks.SelectedItem = selected;
            Status($"덱 {_allDecks.Length}개");
        }

        private void ApplyDeckFilter()
        {
            var keyword = (TxtDeckFilter.Text ?? "").Trim();
            PhDeckFilter.Visibility = keyword.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

            var view = keyword.Length == 0
                ? _allDecks
                : _allDecks.Where(d => StripDeckName(d).IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0).ToArray();

            ListDecks.ItemsSource = view;
            LblDeckList.Text = keyword.Length == 0
                ? $"덱 목록 ({_allDecks.Length})"
                : $"덱 목록 — '{keyword}' {view.Length}건";
        }

        private void OnDeckFilterChanged(object sender, TextChangedEventArgs e) => ApplyDeckFilter();

        private void OnRefreshDecks(object sender, RoutedEventArgs e) => LoadDecks();

        private void OnDeckSelected(object sender, SelectionChangedEventArgs e)
        {
            if (_project == null || ListDecks.SelectedItem is not string file) return;
            try
            {
                var name = file.EndsWith(".ydk", StringComparison.OrdinalIgnoreCase) ? file[..^4] : file;
                TxtDeck.Text = _project.ReadDeck(name);
                var (m, x, s) = WindBotProject.CountDeck(TxtDeck.Text);
                TxtDeckInfo.Text = $"{file} — 메인 {m} / 엑스트라 {x} / 사이드 {s}";

                var engine = StripDeckName(file);
                TxtEngineName.Text = engine;
                TryLoadProfile(engine);
            }
            catch (Exception ex) { Status("덱 읽기 실패: " + ex.Message); }
        }

        private void OnDeckTextChanged(object sender, TextChangedEventArgs e) => RefreshDeckCards();

        private void OnSaveDeck(object sender, RoutedEventArgs e)
        {
            if (_project == null || ListDecks.SelectedItem is not string file)
            {
                MessageBox.Show("덱을 먼저 선택하세요.", "덱 저장");
                return;
            }
            try
            {
                var name = file.EndsWith(".ydk", StringComparison.OrdinalIgnoreCase) ? file[..^4] : file;
                _project.WriteDeck(name, TxtDeck.Text);
                var (m, x, s) = WindBotProject.CountDeck(TxtDeck.Text);
                TxtDeckInfo.Text = $"{file} — 메인 {m} / 엑스트라 {x} / 사이드 {s}  (저장 {DateTime.Now:HH:mm:ss})";
                Status("덱 저장: " + file + "  (WindBot 재시작 필요)");
            }
            catch (Exception ex) { MessageBox.Show("덱 저장 실패: " + ex.Message, "오류"); }
        }

        private void OnNewDeck(object sender, RoutedEventArgs e)
        {
            if (_project == null) return;
            var name = (TxtNewDeck.Text ?? "").Trim();
            if (name.Length == 0) { MessageBox.Show("덱 이름을 입력하세요."); return; }
            var path = _project.DeckPath(name);
            if (File.Exists(path)) { MessageBox.Show("이미 존재합니다: " + Path.GetFileName(path)); return; }

            _project.WriteDeck(name, "#created by WindBot AI Editor\n#main\n\n#extra\n\n!side\n");
            LoadDecks();
            ListDecks.SelectedItem = Path.GetFileName(path);
            TxtEngineName.Text = StripDeckName(Path.GetFileName(path));
            TxtNewDeck.Text = "";
            Status("새 덱 생성: " + Path.GetFileName(path));
        }

        private void OnImportDeck(object sender, RoutedEventArgs e)
        {
            if (_project == null) return;
            var dlg = new OpenFileDialog
            {
                Title = "가져올 .ydk 파일 선택",
                Filter = "Yu-Gi-Oh 덱 (*.ydk)|*.ydk|모든 파일 (*.*)|*.*"
            };
            if (dlg.ShowDialog() != true) return;
            try
            {
                var dest = _project.DeckPath(Path.GetFileNameWithoutExtension(dlg.FileName));
                if (File.Exists(dest))
                {
                    if (MessageBox.Show("같은 이름의 덱이 있습니다. 덮어쓸까요?" + Environment.NewLine + Path.GetFileName(dest),
                            "가져오기", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
                }
                Directory.CreateDirectory(_project.DecksDir);
                File.Copy(dlg.FileName, dest, true);
                LoadDecks();
                ListDecks.SelectedItem = Path.GetFileName(dest);
                TxtEngineName.Text = StripDeckName(Path.GetFileName(dest));
                Status("덱 가져옴: " + Path.GetFileName(dest));
            }
            catch (Exception ex) { MessageBox.Show("가져오기 실패: " + ex.Message, "오류"); }
        }

        private void OnDeleteDeck(object sender, RoutedEventArgs e)
        {
            if (_project == null || ListDecks.SelectedItem is not string file) return;
            if (MessageBox.Show("삭제할까요?" + Environment.NewLine + file, "확인", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            try
            {
                File.Delete(Path.Combine(_project.DecksDir, file));
                TxtDeck.Text = "";
                LoadDecks();
            }
            catch (Exception ex) { MessageBox.Show("삭제 실패: " + ex.Message); }
        }

        private void OnAddCardToDeck(object sender, RoutedEventArgs e)
        {
            if (_project == null || ListDecks.SelectedItem is not string file) return;
            var id = PickCard();
            if (id is not int cardId) return;

            var lines = (TxtDeck.Text ?? "").Replace("\r\n", "\n").Split('\n').ToList();
            int insertAt = lines.FindIndex(l => l.Trim().StartsWith("#extra", StringComparison.OrdinalIgnoreCase));
            if (insertAt < 0) insertAt = lines.FindIndex(l => WindBotProject.SectionOf(l) == "side");
            if (insertAt < 0) insertAt = lines.Count;

            lines.Insert(insertAt, cardId.ToString());
            TxtDeck.Text = string.Join("\r\n", lines);

            var name = file.EndsWith(".ydk", StringComparison.OrdinalIgnoreCase) ? file[..^4] : file;
            _project.WriteDeck(name, TxtDeck.Text);
            var (m, x, s) = WindBotProject.CountDeck(TxtDeck.Text);
            TxtDeckInfo.Text = $"{file} — 메인 {m} / 엑스트라 {x} / 사이드 {s}  (추가됨)";
            Status($"덱에 카드 추가: {cardId} ({CardDatabase.Instance.NameOf(cardId)})");
        }

        private void OnConvertNamesToIds(object sender, RoutedEventArgs e)
        {
            if (!CardDatabase.Instance.IsOpen) { MessageBox.Show("카드 DB 가 없습니다."); return; }
            var lines = (TxtDeck.Text ?? "").Replace("\r\n", "\n").Split('\n');
            int converted = 0, failed = 0;
            var sb = new StringBuilder();
            foreach (var raw in lines)
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#") || int.TryParse(line, out _))
                {
                    sb.AppendLine(raw);
                    continue;
                }
                var id = CardDatabase.Instance.FindIdByName(line);
                if (id > 0) { sb.AppendLine(id.ToString()); converted++; }
                else { sb.AppendLine(raw); failed++; }
            }
            TxtDeck.Text = sb.ToString();
            MessageBox.Show($"이름→ID 변환: {converted}건 성공, {failed}건 실패", "변환");
        }

        // ---- 덱 내 카드 검색 ----

        private List<DeckCardItem> _deckCardItems = new();

        private void OnDeckCardFilterChanged(object sender, TextChangedEventArgs e) => RefreshDeckCards();

        private void RefreshDeckCards()
        {
            if (ListDeckCards == null) return;
            _deckCardItems = ParseDeckCards(TxtDeck.Text ?? "");
            var kw = (TxtDeckCardFilter.Text ?? "").Trim();
            var view = kw.Length == 0
                ? _deckCardItems
                : _deckCardItems.Where(i => i.Name.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0
                                            || i.Id.ToString().Contains(kw)).ToList();
            ListDeckCards.ItemsSource = view;
            if (LblDeckCardCount != null)
                LblDeckCardCount.Text = $"고유 {_deckCardItems.Count}종" + (kw.Length > 0 ? $" · 검색 {view.Count}건" : "");
        }

        private static List<DeckCardItem> ParseDeckCards(string text)
        {
            var map = new Dictionary<string, DeckCardItem>();
            var section = "main";
            foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0) continue;
                var sec = WindBotProject.SectionOf(line);
                if (sec != null) { section = sec; continue; }
                if (WindBotProject.IsCommentOrMarker(line)) continue;
                if (!int.TryParse(line, out var id)) continue;
                var key = section + ":" + id;
                if (!map.TryGetValue(key, out var item))
                {
                    item = new DeckCardItem { Id = id, Section = section, Name = CardDatabase.Instance.NameOf(id) };
                    map[key] = item;
                }
                item.Count++;
            }
            return map.Values.OrderByDescending(x => x.Count).ThenBy(x => x.Id).ToList();
        }

        private void OnRemoveDeckCard(object sender, RoutedEventArgs e) => RemoveOneDeckCard();

        private void OnDeckCardDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e) => RemoveOneDeckCard();

        private void RemoveOneDeckCard()
        {
            if (ListDeckCards.SelectedItem is not DeckCardItem item) return;
            var lines = (TxtDeck.Text ?? "").Replace("\r\n", "\n").Split('\n').ToList();
            var section = "main";
            var removedAt = -1;
            for (var i = 0; i < lines.Count; i++)
            {
                var t = lines[i].Trim();
                var sec = WindBotProject.SectionOf(t);
                if (sec != null) { section = sec; continue; }
                if (WindBotProject.IsCommentOrMarker(t)) continue;
                if (section == item.Section && t == item.Id.ToString()) { removedAt = i; break; }
            }
            if (removedAt < 0) return;

            lines.RemoveAt(removedAt);
            TxtDeck.Text = string.Join("\r\n", lines);
            RefreshDeckCards();
            Status($"덱에서 1장 제거: {item.Name} ({item.Id})");
        }

        // ================= 봇 =================

        private void LoadBots()
        {
            if (_project == null) return;
            _bots.Clear();
            try
            {
                foreach (var b in _project.LoadBots()) _bots.Add(b);
            }
            catch (Exception ex) { Status("bots.json 로드 실패: " + ex.Message); }
        }

        private void OnAddBot(object sender, RoutedEventArgs e)
        {
            var deck = ListDecks.SelectedItem is string f ? StripDeckName(f) : "MyDeck";
            _bots.Add(new BotEntry { Name = "새 AI", Deck = deck, Difficulty = 1, MasterRules = new() { 5 } });
            GridBots.SelectedIndex = _bots.Count - 1;
        }

        private void OnAddBotFromDeck(object sender, RoutedEventArgs e)
        {
            if (ListDecks.SelectedItem is not string f) { MessageBox.Show("덱을 먼저 선택하세요."); return; }
            var deck = StripDeckName(f);
            if (_bots.Any(b => string.Equals(b.Deck, deck, StringComparison.OrdinalIgnoreCase)))
            {
                MessageBox.Show("이미 등록된 덱입니다: " + deck);
                return;
            }
            _bots.Add(new BotEntry { Name = deck, Deck = deck, Difficulty = 1, MasterRules = new() { 5 } });
            GridBots.SelectedIndex = _bots.Count - 1;
            Status("봇 항목 추가: " + deck + " (저장 필요)");
        }

        private void OnRemoveBot(object sender, RoutedEventArgs e)
        {
            if (GridBots.SelectedItem is BotEntry b) _bots.Remove(b);
        }

        private void OnDuplicateBot(object sender, RoutedEventArgs e)
        {
            if (GridBots.SelectedItem is BotEntry b)
            {
                var copy = b.Clone();
                copy.Name = b.Name + " (사본)";
                _bots.Add(copy);
                GridBots.SelectedIndex = _bots.Count - 1;
            }
        }

        private void OnSaveBots(object sender, RoutedEventArgs e)
        {
            if (_project == null) return;
            GridBots.CommitEdit(DataGridEditingUnit.Row, true);
            try
            {
                BackupBots();
                _project.SaveBots(_bots);
                Status("bots.json 저장 완료");
                MessageBox.Show("저장했습니다." + Environment.NewLine + "EDOPro 로비에서 다시 열면 반영됩니다.", "저장");
            }
            catch (Exception ex) { MessageBox.Show("저장 실패: " + ex.Message, "오류"); }
        }

        private void BackupBots()
        {
            if (_project == null || !File.Exists(_project.BotsJson)) return;
            var dir = Path.Combine(_project.Root, "_backup");
            Directory.CreateDirectory(dir);
            File.Copy(_project.BotsJson, Path.Combine(dir, "bots_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".json"), true);
        }

        private void OnBackupBots(object sender, RoutedEventArgs e)
        {
            BackupBots();
            Status("bots.json 백업 완료");
        }

        // ================= 검증 / 실행 =================

        private void OnValidate(object sender, RoutedEventArgs e)
        {
            if (_project == null) { MessageBox.Show("EDOPro 폴더를 먼저 여세요."); return; }
            ListIssues.ItemsSource = null;
            ListIssues.ItemsSource = _project.Validate();
            Status("검증 완료 — 카드DB " + CardDatabase.Instance.Paths.Count + "개 연결");
        }

        private void OnRunWindBot(object sender, RoutedEventArgs e)
        {
            if (_project == null) return;
            try
            {
                var deck = ListDecks.SelectedItem is string f
                    ? StripDeckName(f)
                    : (TxtEngineName.Text ?? "").Trim();

                var args = "Name=WindBotAIEditor Host=127.0.0.1 Port=7911";
                if (!string.IsNullOrWhiteSpace(deck)) args = "Deck=" + deck + " " + args;
                var dialogName = CmbDialogFile.SelectedItem as string;
                if (!string.IsNullOrWhiteSpace(dialogName) && !string.Equals(dialogName, "default", StringComparison.OrdinalIgnoreCase))
                    args = "Dialog=" + dialogName + " " + args;

                Process.Start(new ProcessStartInfo
                {
                    FileName = _project.WindBotExe,
                    Arguments = args,
                    WorkingDirectory = _project.Root,
                    UseShellExecute = true
                });
                TxtRunHint.Text = "WindBot 실행 (Deck=" + (string.IsNullOrWhiteSpace(deck) ? "랜덤" : deck) + ")";
            }
            catch (Exception ex) { MessageBox.Show("실행 실패: " + ex.Message); }
        }

        private void OnRunEDOPro(object sender, RoutedEventArgs e)
        {
            var root = string.IsNullOrEmpty(App.EdoproRoot) ? App.WindBotRoot : App.EdoproRoot;
            var exe = Path.Combine(root, "EDOPro.exe");
            if (!File.Exists(exe)) { MessageBox.Show("EDOPro.exe 를 찾지 못했습니다: " + exe); return; }
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = exe,
                    WorkingDirectory = root,
                    UseShellExecute = true
                });
                TxtRunHint.Text = "EDOPro 실행됨";
            }
            catch (Exception ex) { MessageBox.Show("실행 실패: " + ex.Message); }
        }

        private void Status(string msg) => TxtStatus.Text = msg;

        // ================= 봇 대사 (Dialogs/*.json) =================

        private sealed class DialogKeyItem
        {
            public string Key { get; init; } = "";
            public string Label { get; init; } = "";
            public override string ToString() => Label;
        }

        private static readonly (string Key, string Label)[] DialogKeyDefs =
        {
            ("welcome",           "환영 인사"),
            ("deckerror",         "덱 오류"),
            ("duelstart",         "듀얼 시작"),
            ("newturn",           "내 턴 시작"),
            ("endturn",           "턴 종료"),
            ("directattack",      "직접 공격"),
            ("attack",            "몬스터 공격"),
            ("ondirectattack",    "상대에게 직공 당할 때"),
            ("facedownmonstername","세트 몬스터 표기"),
            ("activate",          "카드 발동"),
            ("summon",            "소환"),
            ("setmonster",        "몬스터 세트"),
            ("chaining",          "체인"),
            ("surrender",         "항복"),
            ("custom",            "커스텀 (스크립트 연동)"),
        };

        private static readonly Dictionary<string, string[]> DialogDefaults = new()
        {
            ["welcome"] = new[] { "안녕하세요! AI와 듀얼을 시작합니다." },
            ["deckerror"] = new[] { "덱에 {0} 이(가) 너무 많은 것 같아요." },
            ["duelstart"] = new[] { "듀얼 시작!", "잘 부탁드립니다." },
            ["newturn"] = new[] { "제 턴입니다. 드로우!", "내 턴, 드로우!" },
            ["endturn"] = new[] { "턴을 종료합니다.", "제 턴은 여기까지." },
            ["directattack"] = new[] { "{0}, 직접 공격!", "{0}, 상대 라이프에 직접 공격!" },
            ["attack"] = new[] { "{0}으로 {1}을(를) 공격!", "{0}, 저 {1}을(를) 파괴해!" },
            ["ondirectattack"] = new[] { "크윽...", "{0} 정도는 두렵지 않아!" },
            ["facedownmonstername"] = new[] { "몬스터" },
            ["activate"] = new[] { "{0} 발동!", "{0}의 효과를 사용합니다." },
            ["summon"] = new[] { "{0} 소환!", "나와라, {0}!" },
            ["setmonster"] = new[] { "몬스터를 세트합니다.", "이 몬스터는 무엇일까요?" },
            ["chaining"] = new[] { "가만히 있지 않겠어! {0} 발동!", "체인! {0}!" },
            ["surrender"] = new[] { "항복합니다...", "제 패배입니다." },
            ["custom"] = new[] { "" },
        };

        private JsonObject? _dialog;
        private string? _dialogKey;

        /// <summary>대사 JSON 저장 옵션 (트리밍 환경에서도 동작하도록 리졸버를 명시)</summary>
        private static readonly JsonSerializerOptions DialogJsonOpts = new()
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
        };

        /// <summary>Dialogs 의 facedownmonstername 은 배열이 아니라 단일 문자열이다.</summary>
        private static JsonNode DefaultNode(string key)
        {
            if (key == "facedownmonstername")
                return JsonValue.Create(DialogDefaults[key].FirstOrDefault() ?? "몬스터")!;
            var arr = new JsonArray();
            foreach (var s in DialogDefaults[key]) arr.Add(s);
            return arr;
        }

        private string DialogDir => _project == null ? "" : Path.Combine(_project.Root, "Dialogs");

        private void RefreshDialogFiles(string? select = null)
        {
            if (CmbDialogFile == null) return;
            var names = new List<string>();
            try
            {
                if (Directory.Exists(DialogDir))
                    names.AddRange(Directory.GetFiles(DialogDir, "*.json").Select(f => Path.GetFileNameWithoutExtension(f) ?? ""));
            }
            catch { }
            names.Sort(StringComparer.OrdinalIgnoreCase);

            var keep = select ?? CmbDialogFile.SelectedItem as string;
            CmbDialogFile.SelectionChanged -= OnDialogFileChanged;
            CmbDialogFile.ItemsSource = names;
            if (names.Count == 0) { CmbDialogFile.SelectedIndex = -1; _dialog = null; _dialogKey = null; ListDialogKeys.ItemsSource = null; TxtDialogLines.Text = ""; }
            else
            {
                var pick = keep != null && names.Contains(keep) ? keep : (names.Contains("default") ? "default" : names[0]);
                CmbDialogFile.SelectedItem = pick;
                LoadDialogFile(pick);
            }
            CmbDialogFile.SelectionChanged += OnDialogFileChanged;
            RefreshDialogChoices();
        }

        /// <summary>엔진 헤더의 [대사] 콤보 목록을 갱신한다.</summary>
        private void RefreshDialogChoices()
        {
            if (CmbDialog == null) return;
            var keep = (CmbDialog.SelectedValue as string) ?? _profile.DialogFile ?? "";
            var opts = new List<ChoiceOption> { new ChoiceOption { Id = "", Label = "(사용 안 함)" } };
            try
            {
                if (Directory.Exists(DialogDir))
                {
                    foreach (var f in Directory.GetFiles(DialogDir, "*.json").OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                    {
                        var n = Path.GetFileNameWithoutExtension(f) ?? "";
                        if (n.Length > 0) opts.Add(new ChoiceOption { Id = n, Label = n });
                    }
                }
            }
            catch { }
            CmbDialog.ItemsSource = opts;
            CmbDialog.SelectedValue = opts.Any(o => o.Id == keep) ? keep : "";
        }

        private void OnEditEngineDialog(object sender, RoutedEventArgs e)
        {
            if (_project == null) return;
            var id = (CmbDialog.SelectedValue as string) ?? "";
            if (string.IsNullOrWhiteSpace(id))
            {
                MessageBox.Show("먼저 이 엔진이 사용할 대사 파일을 선택하세요 (왼쪽 [대사] 콤보)." + Environment.NewLine +
                                "새 파일은 [봇 대사] 탭에서 만들 수 있습니다.", "대사 편집");
                return;
            }
            foreach (var item in Tabs.Items)
                if (item is TabItem ti && (ti.Header as string) == "봇 대사") Tabs.SelectedItem = ti;
            RefreshDialogFiles(id);
        }

        private void LoadDialogFile(string name)
        {
            if (_project == null || string.IsNullOrWhiteSpace(name)) return;
            Directory.CreateDirectory(DialogDir);
            var path = Path.Combine(DialogDir, name + ".json");
            JsonObject obj;
            try
            {
                obj = File.Exists(path)
                    ? (JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? new JsonObject())
                    : new JsonObject();
            }
            catch (Exception ex)
            {
                MessageBox.Show("대사 파일을 읽지 못했습니다: " + ex.Message, "봇 대사");
                return;
            }

            // 누락된 키는 기본값으로 채운다 (WindBot 은 배열이 null 이면 오류)
            foreach (var (key, _) in DialogKeyDefs)
            {
                if (obj.ContainsKey(key)) continue;
                obj[key] = DefaultNode(key);
            }

            _dialog = obj;
            TxtDialogPath.Text = "→ " + path;
            FillDialogKeys(name);
        }

        private void FillDialogKeys(string? fileName)
        {
            var items = DialogKeyDefs.Select(d => new DialogKeyItem { Key = d.Key, Label = d.Label }).ToList();
            _dialogKey = null;
            ListDialogKeys.ItemsSource = items;
            ListDialogKeys.SelectedIndex = 0;
            TxtDialogHelp.Text = "한 줄에 하나씩 = 대사 후보 (그중 하나가 랜덤으로 출력됩니다). {0} {1} 은 카드 이름으로 바뀝니다. " +
                                 "파일: " + (fileName ?? "") + ".json · 비워두면 그 상황에는 말하지 않습니다.";
        }

        private void OnDialogFileChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CmbDialogFile.SelectedItem is string name && _dialog != null && !string.Equals(name, _currentDialogName, StringComparison.OrdinalIgnoreCase))
                LoadDialogFile(name);
        }

        private string? _currentDialogName => Path.GetFileNameWithoutExtension(TxtDialogPath.Text.Replace("→ ", "").Trim());

        private void CommitDialogText()
        {
            if (_dialog == null || _dialogKey == null) return;
            var lines = (TxtDialogLines.Text ?? "").Replace("\r\n", "\n").Split('\n')
                        .Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
            if (_dialogKey == "facedownmonstername")
            {
                _dialog[_dialogKey] = JsonValue.Create(lines.Count > 0 ? lines[0] : "몬스터");
            }
            else
            {
                var arr = new JsonArray();
                if (lines.Count == 0) arr.Add("");
                else foreach (var l in lines) arr.Add(l);
                _dialog[_dialogKey] = arr;
            }
        }

        private void OnDialogKeyChanged(object sender, SelectionChangedEventArgs e)
        {
            CommitDialogText();
            if (_dialog == null || ListDialogKeys.SelectedItem is not DialogKeyItem item) { _dialogKey = null; return; }

            _dialogKey = item.Key;
            var node = _dialog[item.Key];
            if (node is JsonArray arr)
                TxtDialogLines.Text = string.Join(Environment.NewLine, arr.Select(x => x is JsonValue jv && jv.TryGetValue<string>(out var s) ? s : ""));
            else if (node is JsonValue v && v.TryGetValue<string>(out var single))
                TxtDialogLines.Text = single;
            else
                TxtDialogLines.Text = "";
        }

        private void OnSaveDialog(object sender, RoutedEventArgs e)
        {
            if (_project == null) { MessageBox.Show("EDOPro 폴더를 먼저 여세요."); return; }
            if (_dialog == null || string.IsNullOrWhiteSpace(_currentDialogName))
            {
                MessageBox.Show("먼저 대사 파일을 선택하거나 [새 파일] 로 만드세요.", "봇 대사");
                return;
            }
            CommitDialogText();
            try
            {
                Directory.CreateDirectory(DialogDir);
                var path = Path.Combine(DialogDir, _currentDialogName + ".json");
                File.WriteAllText(path, _dialog.ToJsonString(DialogJsonOpts), new UTF8Encoding(false));
                Status("봇 대사 저장: " + path);
            }
            catch (Exception ex) { MessageBox.Show("저장 실패: " + ex.Message, "봇 대사"); }
        }

        private void OnRefreshDialogFiles(object sender, RoutedEventArgs e)
        {
            if (_project == null) return;
            var keep = _currentDialogName;
            if (_dialog != null && !string.IsNullOrWhiteSpace(keep)) OnSaveDialog(sender, e);
            RefreshDialogFiles(keep);
        }

        private void OnNewDialogFile(object sender, RoutedEventArgs e)
        {
            if (_project == null) { MessageBox.Show("EDOPro 폴더를 먼저 여세요."); return; }
            var name = PromptText("새 대사 파일", "파일 이름 (영문/숫자/밑줄):", "mybot");
            if (string.IsNullOrWhiteSpace(name)) return;
            name = name.Trim();
            if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Contains(' '))
            {
                MessageBox.Show("파일 이름에는 공백이나 특수문자를 쓸 수 없습니다.", "봇 대사");
                return;
            }
            if (File.Exists(Path.Combine(DialogDir, name + ".json")))
            {
                MessageBox.Show("같은 이름의 파일이 이미 있습니다.", "봇 대사");
                return;
            }
            try
            {
                Directory.CreateDirectory(DialogDir);
                var obj = new JsonObject();
                foreach (var (key, _) in DialogKeyDefs)
                {
                    obj[key] = DefaultNode(key);
                }
                File.WriteAllText(Path.Combine(DialogDir, name + ".json"),
                    obj.ToJsonString(DialogJsonOpts), new UTF8Encoding(false));
                RefreshDialogFiles(name);
                Status("새 대사 파일: " + name + ".json");
            }
            catch (Exception ex) { MessageBox.Show("생성 실패: " + ex.Message, "봇 대사"); }
        }

        private void OnApplyDialogAsDefault(object sender, RoutedEventArgs e)
        {
            if (_project == null || _dialog == null || string.IsNullOrWhiteSpace(_currentDialogName)) return;
            if (string.Equals(_currentDialogName, "default", StringComparison.OrdinalIgnoreCase))
            {
                OnSaveDialog(sender, e);
                return;
            }
            if (MessageBox.Show("현재 대사를 default.json 으로 복사할까요?" + Environment.NewLine +
                                "EDOPro 가 실행하는 봇은 별도 설정이 없으면 default.json 을 사용합니다." + Environment.NewLine +
                                "(기존 default.json 은 default.json.bak 으로 백업)",
                                "default.json 에 적용", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
            try
            {
                CommitDialogText();
                var def = Path.Combine(DialogDir, "default.json");
                if (File.Exists(def)) File.Copy(def, def + ".bak", true);
                File.WriteAllText(def, _dialog.ToJsonString(DialogJsonOpts), new UTF8Encoding(false));
                RefreshDialogFiles("default");
                Status("default.json 갱신 완료");
            }
            catch (Exception ex) { MessageBox.Show("적용 실패: " + ex.Message, "봇 대사"); }
        }

        /// <summary>간단한 텍스트 입력 대화상자</summary>
        private string? PromptText(string title, string label, string initial)
        {
            var win = new Window
            {
                Title = title,
                Width = 380,
                Height = 150,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                ResizeMode = ResizeMode.NoResize,
                Background = Background,
            };
            var sp = new StackPanel { Margin = new Thickness(12) };
            sp.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 6) });
            var box = new TextBox { Text = initial, Padding = new Thickness(4) };
            sp.Children.Add(box);
            var ok = new Button { Content = "확인", Width = 80, Margin = new Thickness(0, 12, 0, 0), HorizontalAlignment = HorizontalAlignment.Right, IsDefault = true };
            ok.Click += (_, __) => { win.DialogResult = true; win.Close(); };
            sp.Children.Add(ok);
            win.Content = sp;
            return win.ShowDialog() == true ? box.Text : null;
        }
    }
}


















