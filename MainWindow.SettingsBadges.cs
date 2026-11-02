using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using WindBotAIEditor.Models;

namespace WindBotAIEditor
{
    /// <summary>
    /// 2026-11-02 (A-6 잔여): 설정 카드의 각 행이 **기본값인지 / 변경됐는지** 표시한다.
    /// <para>· 표시 = 컨트롤 배경 **노랑 틴트** + 굵게 + 툴팁 `변경됨 (기본값: …)`</para>
    /// <para>· 검색 토큰 `@변경` / `@기본` / `@위험` 으로 필터</para>
    /// <para>· 우클릭 → **기본값으로 되돌리기**</para>
    /// 기본값의 기준은 `new AiProfile()` 의 초기값(= 모델 기본값)이다.
    /// </summary>
    public partial class MainWindow
    {
        private sealed class SettingDef
        {
            public string Name = "";
            public Func<object?> Get = () => null;
            public object? Def;
            public bool RiskWhenOff;      // 안전 기본값(true)이 꺼지면 위험
            public string Label = "";
        }

        private List<SettingDef>? _settingsMap;
        private readonly HashSet<string> _changedNames = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<FrameworkElement> _changedRows = new HashSet<FrameworkElement>();
        private readonly HashSet<FrameworkElement> _riskRows = new HashSet<FrameworkElement>();
        private bool _settingsHooked;

        private static readonly Brush ChangedBg = MakeBrush("#33E0A030");   // 반투명 노랑
        private static readonly Brush RiskBg = MakeBrush("#33EB5757");      // 반투명 빨강

        private static Brush MakeBrush(string hex)
        {
            try
            {
                var c = (Color)ColorConverter.ConvertFromString(hex);
                var b = new SolidColorBrush(c);
                b.Freeze();
                return b;
            }
            catch { return Brushes.Transparent; }
        }

        private static bool SameValue(object? a, object? b)
        {
            var s1 = a as string; var s2 = b as string;
            if (s1 != null || s2 != null)
            {
                var x = (s1 ?? "").Trim();
                var y = (s2 ?? "").Trim();
                if (x.Length == 0) x = "";
                return string.Equals(x, y, StringComparison.OrdinalIgnoreCase);
            }
            if (a == null && b == null) return true;
            if (a == null || b == null) return false;
            return a.Equals(b);
        }

        private List<SettingDef> SettingsMap()
        {
            if (_settingsMap != null) return _settingsMap;
            var list = new List<SettingDef>();

            void Chk(string name, Func<AiProfile, bool> get, bool def, bool risk = false)
                => list.Add(new SettingDef
                {
                    Name = name,
                    Label = name,
                    Def = def,
                    RiskWhenOff = risk,
                    Get = () => { var c = FindNamed(name) as CheckBox; return c != null ? (object)(c.IsChecked == true) : null; }
                });

            void Cmb(string name, Func<AiProfile, string> get, string def)
                => list.Add(new SettingDef
                {
                    Name = name,
                    Label = name,
                    Def = def,
                    Get = () => { var c = FindNamed(name) as ComboBox; return c != null ? (object?)((c.SelectedValue as string) ?? "") : null; }
                });

            void Count(string name, Func<AiProfile, int> get)
                => list.Add(new SettingDef
                {
                    Name = name,
                    Label = name,
                    Def = 0,
                    Get = () => { var p = _profile; return p == null ? (object?)null : (object)get(p); }
                });

            // ── 엔진 / 동작 ──
            Chk("ChkFreePlay", p => p.FreePlay, true);
            Chk("ChkDebugEffect", p => p.DebugEffectLog, false);
            Cmb("CmbRps", p => p.RpsHand, "random");
            Cmb("CmbTurnChoice", p => p.TurnChoice, "random");
            Cmb("CmbDialog", p => p.DialogFile, "");
            Cmb("CmbChainBasis", p => p.ChainBasis, "code");

            // ── 자유 플레이 세트(B-15) ──
            Chk("ChkFreePlaySetSpell", p => p.FreePlaySetSpell, false);
            Cmb("CmbFreePlaySetKind", p => p.FreePlaySetSpellKind, "all");
            Count("TxtFreePlaySetExclude", p => (p.FreePlaySetSpellExclude ?? new List<int>()).Count);

            // ── 안전/보호 ──
            Chk("ChkFreePlayProtectExtra", p => p.FreePlayProtectExtra, true, true);
            Count("TxtTributeGuardExcept", p => (p.FreePlayTributeGuardExcept ?? new List<int>()).Count);
            Chk("ChkAndroidSafe", p => p.AndroidSafeCheck, true, true);

            // ── 카드 목록 ──
            Count("TxtDestructibleImmune", p => (p.DestructibleImmuneCards ?? new List<int>()).Count);
            Count("TxtEffectImmune", p => (p.EffectImmuneCards ?? new List<int>()).Count);
            Count("TxtNegate", p => (p.NegateCardIds ?? new List<int>()).Count);
            Count("TxtDestroy", p => (p.DestroyCardIds ?? new List<int>()).Count);

            // ── 선택/전투/존 ──
            Chk("ChkSkipResistantGlobal", p => p.SkipResistantGlobal, true);
            Chk("ChkSkipDestructImmune", p => p.SkipDestructImmuneOnDestroy, true);
            Chk("ChkSkipEffectImmune", p => p.SkipEffectImmuneTargets, true);
            Chk("ChkSkipEffectImmuneNegate", p => p.SkipEffectImmuneNegate, true);
            Chk("ChkSkipDestructImmuneDestroy", p => p.SkipDestructImmuneDestroy, true);
            Chk("ChkAttackOnlyWinnable", p => p.AttackOnlyWinnable, true);
            Chk("ChkLinkPlace", p => p.LinkPlaceAware, false);
            Chk("ChkRouteAuthoritative", p => p.RouteAuthoritative, true);   // B-73
            Cmb("CmbDefaultPosition", p => p.DefaultSummonPosition, "attack");

            // ── 카드 DB / 기타 ──
            Chk("ChkSameNameAliases", p => p.SameNameAliases, true);
            Chk("ChkShowPreErrata", p => p.ShowPreErrata, false);

            _settingsMap = list;
            return list;
        }

        /// <summary>이름으로 컨트롤 찾기 — XAML 이름 스코프 사용(탭이 선택되지 않아 시각 트리에 없어도 찾는다)</summary>
        private Control? FindNamed(string name)
        {
            try { return FindName(name) as Control; } catch { return null; }
        }

        private static Control? FindNamedIn(DependencyObject? node, string name, int depth = 0)
        {
            if (node == null || depth > 40) return null;
            if (node is Control fe && string.Equals(fe.Name, name, StringComparison.Ordinal)) return fe;
            int n;
            try { n = VisualTreeHelper.GetChildrenCount(node); } catch { return null; }
            for (var i = 0; i < n; i++)
            {
                var hit = FindNamedIn(VisualTreeHelper.GetChild(node, i), name, depth + 1);
                if (hit != null) return hit;
            }
            return null;
        }

        /// <summary>설정 배지 갱신 — 변경/위험 표시 + 카운트</summary>
        public void UpdateSettingBadges()
        {
            try
            {
                _changedNames.Clear();
                _changedRows.Clear();
                _riskRows.Clear();
                var rows = SettingsRows();
                var risk = 0;
                foreach (var d in SettingsMap())
                {
                    var ctrl = FindNamed(d.Name);
                    if (ctrl == null) continue;
                    var cur = d.Get();
                    var changed = cur != null && !SameValue(cur, d.Def);
                    var isRisk = d.RiskWhenOff && d.Def is bool b0 && b0 && cur is bool b1 && !b1;
                    if (changed)
                    {
                        _changedNames.Add(d.Name);
                        if (isRisk) risk++;
                        var row0 = RowOf(ctrl, rows);
                        if (row0 != null)
                        {
                            _changedRows.Add(row0);
                            if (isRisk) _riskRows.Add(row0);
                        }
                    }

                    var tip = (ctrl.Tag as string);
                    if (changed)
                    {
                        if (tip == null) { ctrl.Tag = ctrl.ToolTip as string ?? ""; }
                        ctrl.Background = d.RiskWhenOff && d.Def is bool b2 && b2 && cur is bool b3 && !b3 ? RiskBg : ChangedBg;
                        ctrl.FontWeight = FontWeights.Bold;
                        var baseTip = ctrl.Tag as string ?? "";
                        var suffix = " · 변경됨 (기본값: " + DescribeDefault(d.Def) + ")";
                        ctrl.ToolTip = baseTip.EndsWith(suffix, StringComparison.Ordinal) ? baseTip : baseTip + suffix;
                    }
                    else if (ctrl.Tag is string orig)
                    {
                        ctrl.Background = null;
                        ctrl.FontWeight = FontWeights.Normal;
                        ctrl.ToolTip = orig;
                        ctrl.Tag = null;
                    }
                }

                if (TxtSettingsSearchInfo != null && string.IsNullOrWhiteSpace(TxtSettingsSearch.Text))
                    TxtSettingsSearchInfo.Text = _changedNames.Count == 0
                        ? SettingsRows().Count + "개 항목 · 모두 기본값"
                        : "변경 " + _changedNames.Count + (risk > 0 ? " · 위험 " + risk : "") + " / " + SettingsRows().Count;
            }
            catch { }
        }

        private static string DescribeDefault(object? def)
        {
            if (def is bool b) return b ? "켜짐" : "꺼짐";
            if (def is int i) return i == 0 ? "없음" : i.ToString();
            var s = def as string ?? "";
            return s.Length == 0 ? "없음" : s;
        }

        /// <summary>컨트롤이 속한 설정 행(좌/우 StackPanel 의 자식)을 찾는다 — 논리 트리 우선</summary>
        private static FrameworkElement? RowOf(DependencyObject ctrl, List<FrameworkElement> rows)
        {
            DependencyObject? cur = ctrl;
            for (var i = 0; i < 12 && cur != null; i++)
            {
                if (cur is FrameworkElement fe && rows.Contains(fe)) return fe;
                DependencyObject? next = null;
                try { next = LogicalTreeHelper.GetParent(cur); } catch { }
                if (next == null) { try { next = VisualTreeHelper.GetParent(cur); } catch { } }
                if (next == null) return null;
                cur = next;
            }
            return null;
        }

        /// <summary>행이 변경된 설정을 포함하는가 (검색 토큰 @변경 필터용)</summary>
        private bool RowIsChanged(FrameworkElement row) => _changedRows.Contains(row);

        private bool RowIsRisk(FrameworkElement row) => _riskRows.Contains(row);

        /// <summary>우클릭 → 기본값으로 되돌리기 메뉴 연결 (1회)</summary>
        private void HookSettingContextMenus()
        {
            if (_settingsHooked) return;
            _settingsHooked = true;
            try
            {
                foreach (var d in SettingsMap())
                {
                    var ctrl = FindNamed(d.Name);
                    if (ctrl == null) continue;
                    var def = d;
                    var menu = new ContextMenu();
                    var item = new MenuItem { Header = "기본값으로 되돌리기 (" + DescribeDefault(def.Def) + ")" };
                    item.Click += (s, e) => RevertSetting(def);
                    menu.Items.Add(item);
                    ctrl.ContextMenu = menu;
                }

                // 값이 바뀌면 배지 갱신 (토글/선택 전역 훅)
                PnlSettingsCard.AddHandler(ToggleButton.CheckedEvent, new RoutedEventHandler((s, e) => UpdateSettingBadges()), true);
                PnlSettingsCard.AddHandler(ToggleButton.UncheckedEvent, new RoutedEventHandler((s, e) => UpdateSettingBadges()), true);
                PnlSettingsCard.AddHandler(Selector.SelectionChangedEvent, new SelectionChangedEventHandler((s, e) => UpdateSettingBadges()), true);
            }
            catch { }
        }

        private void RevertSetting(SettingDef d)
        {
            try
            {
                switch (d.Def)
                {
                    case bool b:
                        if (FindNamed(d.Name) is CheckBox cb) cb.IsChecked = b;
                        break;
                    case string s:
                        if (FindNamed(d.Name) is ComboBox cmb) cmb.SelectedValue = s;
                        break;
                    case int:
                        // 카드 목록 계열 — 프로필 목록을 비운다
                        if (d.Name == "TxtDestructibleImmune") { _profile?.DestructibleImmuneCards?.Clear(); UpdateDestructibleImmuneText(); }
                        else if (d.Name == "TxtEffectImmune") { _profile?.EffectImmuneCards?.Clear(); UpdateEffectImmuneText(); }
                        else if (d.Name == "TxtNegate") { _profile?.NegateCardIds?.Clear(); UpdateNegateText(); }
                        else if (d.Name == "TxtDestroy") { _profile?.DestroyCardIds?.Clear(); UpdateDestroyCardText(); }
                        else if (d.Name == "TxtTributeGuardExcept") { _profile?.FreePlayTributeGuardExcept?.Clear(); UpdateTributeGuardExceptText(); }
                        break;
                }
                UpdateSettingBadges();
                if (TxtSettingsSearch != null) ApplySettingsFilter();
                Status("설정 기본값으로 되돌림: " + d.Name + " → " + DescribeDefault(d.Def));
            }
            catch { }
        }
    }
}



