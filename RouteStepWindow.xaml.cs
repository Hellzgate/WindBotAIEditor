using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using WindBotAIEditor.Models;
using WindBotAIEditor.Services;
using Condition = WindBotAIEditor.Models.Condition;

namespace WindBotAIEditor
{
    /// <summary>
    /// 2026-11-02 (B-78 / B-79): ⑤ 전개 루트 **단계 조건 팝업 편집기**.
    /// 조건 그리드가 ⑤ 탭 맨 아래에 있어 스크롤해야 했던 불편을 없애고, **그 자리에서 편집**까지 한다.
    ///  · 편집 대상: 루트 진입 조건 + 그 단계의 **순위별** 조건 (콤보로 전환)
    ///  · 항목/바인딩은 본 화면의 조건 그리드와 동일한 Condition 객체를 그대로 쓴다 → 닫으면 즉시 반영
    ///  · 순위 조건에는 카드/행동 요약과 **생성식 미리보기**를 함께 보여준다
    /// </summary>
    public partial class RouteStepWindow : Window
    {
        private sealed class Target
        {
            public string Label = "";
            public List<Condition> Conditions = new List<Condition>();
            public RouteOption? Option;   // 순위 조건이면 그 순위(배지 갱신용), 진입 조건이면 null
            public override string ToString() => Label;
        }

        private readonly RouteSet? _set;
        private readonly RouteStep? _step;
        private readonly int _stepNo;
        private readonly List<Target> _targets = new List<Target>();

        public RouteStepWindow(RouteSet? set, RouteStep? step, int stepNo, string hint, int preselectRank = 0)
        {
            InitializeComponent();
            _set = set;
            _step = step;
            _stepNo = stepNo;
            Title = "⑤ 단계 " + stepNo + " 조건 편집";

            if (!string.IsNullOrWhiteSpace(hint)) TxtInfo.Text = hint;
            TxtStepNote.Text = step != null && !string.IsNullOrWhiteSpace(step.Note) ? step.Note : "";

            BuildTargets();
            CmbCondTarget.ItemsSource = _targets;
            if (_targets.Count > 0)
            {
                int idx = 1;   // 기본: 1순위 조건
                if (preselectRank > 0)
                {
                    for (int k = 1; k < _targets.Count; k++)
                        if (_targets[k].Option != null && _targets[k].Option.Rank == preselectRank) { idx = k; break; }
                }
                CmbCondTarget.SelectedIndex = Math.Min(idx, _targets.Count - 1);
            }
        }

        private void BuildTargets()
        {
            if (_set != null)
            {
                _set.Conditions ??= new List<Condition>();
                _targets.Add(new Target { Label = "루트 진입 조건 (" + _set.Conditions.Count + "개)", Conditions = _set.Conditions, Option = null });
            }
            if (_step == null) return;
            var opts = _step.Options != null && _step.Options.Count > 0
                ? _step.Options.OrderBy(o => o.Rank).ToList()
                : new List<RouteOption> { new RouteOption { Rank = 1, Card = _step.Card, Action = _step.Action } };
            foreach (var o in opts)
            {
                o.Conditions ??= new List<Condition>();
                _targets.Add(new Target
                {
                    Label = o.Rank + "순위: " + CardText(o.Card) + " → " + ActionText(o.Action)
                            + " (" + o.Conditions.Count + "개)",
                    Conditions = o.Conditions,
                    Option = o
                });
            }
        }

        private Target? Current => CmbCondTarget.SelectedItem as Target;

        private void OnCondTargetChanged(object sender, SelectionChangedEventArgs e)
        {
            GridConds.ItemsSource = Current?.Conditions;
            RefreshGrid();
        }

        private void RefreshGrid()
        {
            GridConds.Items.Refresh();
            MainWindow.RefreshGroupBadges(GridConds.ItemsSource);   // 첫 행 연결 '—', 그룹 배지(괄호)
            GridConds.Items.Refresh();
            UpdateExpr();
        }

        /// <summary>생성식 미리보기 — 그룹(괄호)까지 확인할 수 있는 유일한 방법.</summary>
        private void UpdateExpr()
        {
            var conds = Current?.Conditions ?? new List<Condition>();
            string expr = conds.Count == 0 ? "true   (조건 없음 → 항상 실행)" : RuleCodeGen.PreviewConditions(conds);
            TxtExpr.Text = "조건 " + conds.Count + "개 · " + expr;
        }

        private void OnGridSelectionChanged(object sender, EventArgs e) => UpdateExpr();

        private void OnAddCond(object sender, RoutedEventArgs e)
        {
            var t = Current;
            if (t == null) return;
            CommitEdit();
            t.Conditions.Add(new Condition { Kind = "enemyMonsterCount", Op = ">=", Value = 1, Side = "enemy", Join = "and" });
            NotifyOwner(t);
            SyncLabel(t);
            RefreshGrid();
            if (GridConds.Items.Count > 0) GridConds.SelectedIndex = GridConds.Items.Count - 1;
        }

        private void OnRemoveCond(object sender, RoutedEventArgs e)
        {
            var t = Current;
            if (t == null || GridConds.SelectedItem is not Condition c) return;
            CommitEdit();
            t.Conditions.Remove(c);
            NotifyOwner(t);
            SyncLabel(t);
            RefreshGrid();
        }

        /// <summary>조건 개수 배지 등 화면 표시를 즉시 갱신한다(본 화면과 동일한 객체를 고치므로).</summary>
        private void NotifyOwner(Target t)
        {
            try
            {
                if (t.Option != null) t.Option.NotifyConditions();
                _set?.NotifyConditions();
            }
            catch { /* 표시 갱신 실패는 무해 */ }
        }

        private void CommitEdit()
        {
            try
            {
                GridConds.CommitEdit(DataGridEditingUnit.Row, true);
            }
            catch { /* 편집 중이 아니면 무시 */ }
        }

        private void SyncLabel(Target t)
        {
            int idx = _targets.IndexOf(t);
            string head = t.Label;
            int cut = head.LastIndexOf(" (", StringComparison.Ordinal);
            if (cut > 0) head = head.Substring(0, cut);
            t.Label = head + " (" + t.Conditions.Count + "개)";
            CmbCondTarget.Items.Refresh();
            if (idx >= 0) CmbCondTarget.SelectedIndex = idx;
        }

        private void OnCopy(object sender, RoutedEventArgs e)
        {
            try
            {
                CommitEdit();
                Clipboard.SetText(BuildBody(_set, _step, _stepNo));
                TxtInfo.Text = "이 단계 요약을 클립보드에 복사했습니다";
            }
            catch (Exception ex) { TxtInfo.Text = "복사 실패: " + ex.Message; }
        }

        private void OnClose(object sender, RoutedEventArgs e)
        {
            CommitEdit();
            Close();
        }

        /// <summary>
        /// 단계 전체(진입 조건 + 순위별 카드/행동/조건)를 **사람이 읽는 텍스트**로 만든다(복사·검토용).
        /// </summary>
        public static string BuildBody(RouteSet? set, RouteStep? step, int stepNo)
        {
            var sb = new StringBuilder();
            if (set != null)
            {
                sb.AppendLine("루트: " + (string.IsNullOrWhiteSpace(set.Name) ? "(이름 없음)" : set.Name)
                              + (set.IsDefault ? "   [기본 루트]" : ""));
                var entry = set.Conditions ?? new List<Condition>();
                sb.AppendLine("진입 조건: " + (entry.Count == 0 ? "없음 (항상 이 루트를 선택)" : entry.Count + "개"));
                foreach (var c in entry) sb.AppendLine("   · " + DescribeCondition(c));
                sb.AppendLine("   생성식: " + RuleCodeGen.PreviewConditions(entry));
                sb.AppendLine(new string('─', 60));
            }
            if (step == null) return sb.ToString();

            string note = string.IsNullOrWhiteSpace(step.Note) ? "" : "  — " + step.Note;
            sb.AppendLine("단계 " + stepNo + note);

            var opts = step.Options != null && step.Options.Count > 0
                ? step.Options.OrderBy(o => o.Rank).ToList()
                : new List<RouteOption> { new RouteOption { Rank = 1, Card = step.Card, Action = step.Action } };

            foreach (var o in opts)
            {
                var conds = o.Conditions ?? new List<Condition>();
                sb.AppendLine();
                sb.AppendLine("   " + o.Rank + "순위: " + CardText(o.Card) + " → " + ActionText(o.Action)
                              + (conds.Count > 0 ? "   (조건 " + conds.Count + "개)" : "   (조건 없음 · 항상 실행)"));
                foreach (var c in conds) sb.AppendLine("        · " + DescribeCondition(c));
                if (conds.Count > 0) sb.AppendLine("        생성식: " + RuleCodeGen.PreviewConditions(conds));
            }
            sb.AppendLine();
            sb.AppendLine(new string('─', 60));
            sb.AppendLine("※ 순위는 낮을수록 먼저 검사합니다. 조건이 불충족이면 다음 순위로 넘어가고,");
            sb.AppendLine("   모두 불충족이면 그 턴의 전개를 끝냅니다(이후 카드는 ① 행동으로 자유롭게).");
            return sb.ToString();
        }

        private static string CardText(int id)
        {
            if (id <= 0) return "(카드 없음)";
            string name = "";
            try { name = CardDatabase.Instance.NameOf(id); } catch { name = ""; }
            return string.IsNullOrWhiteSpace(name) || name == id.ToString() ? id.ToString() : id + " " + name;
        }

        private static string ActionText(string action)
        {
            if (string.IsNullOrWhiteSpace(action)) return "(행동 없음)";
            var t = ExecutorTypeOption.All.FirstOrDefault(x => x.Id == action);
            return t != null ? t.Label + " (" + action + ")" : action;
        }

        /// <summary>조건 1개를 사람이 읽는 문장으로.</summary>
        public static string DescribeCondition(Condition c)
        {
            if (c == null) return "(빈 조건)";
            var kind = ConditionKind.All.FirstOrDefault(k => k.Id == c.Kind);
            string label = kind != null ? kind.Label : c.Kind;
            var sb = new StringBuilder();
            if (!string.IsNullOrEmpty(c.Join) && c.Join != "and") sb.Append("or ");
            if (c.Not) sb.Append("NOT ");
            sb.Append(label);
            if (kind == null || kind.NeedsValue) sb.Append(" " + c.Op + " " + c.Value);
            if ((kind == null || kind.NeedsCard) && c.Card > 0) sb.Append("  [카드 " + CardText(c.Card) + "]");
            if (kind != null && kind.NeedsSide && !string.IsNullOrEmpty(c.Side))
                sb.Append(c.Side == "enemy" ? "  [상대]" : "  [내]");
            if (c.Count > 0) sb.Append("  (장수 " + c.Count + ")");
            if (c.MonsterOnly) sb.Append("  [몬스터만]");
            if (c.ThemeCode > 0) sb.Append("  [테마 " + c.ThemeCode + "]");
            if (c.Group > 0) sb.Append("  (그룹 " + c.Group + ")");
            return sb.ToString();
        }
    }
}
