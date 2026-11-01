using System;
using System.Collections.Generic;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace WindBotAIEditor
{
    /// <summary>
    /// 2026-11-02 (A-6): ⚙ AI 설정 **검색 상자**.
    /// 설정 카드의 각 행(StackPanel 의 자식)을 이름·툴팁·표시 문자열로 검색해 **매칭되는 행만** 남긴다.
    /// 검색어를 입력하면 설정 카드가 **자동으로 펼쳐진다**(접힌 상태에서는 보이지 않으므로).
    /// </summary>
    public partial class MainWindow
    {
        /// <summary>설정 카드의 행 목록 (좌/우 2열 StackPanel 의 자식들)</summary>
        private List<FrameworkElement> SettingsRows()
        {
            var rows = new List<FrameworkElement>();
            try
            {
                if (PnlSettings?.Content is not Panel grid) return rows;
                foreach (var child in grid.Children)
                {
                    // 열은 Border 로 감싸져 있고 그 안이 StackPanel(행 목록)
                    Panel? panel = child as Panel;
                    if (panel == null && child is Border b && b.Child is Panel inner) panel = inner;
                    if (panel == null) continue;
                    foreach (var row in panel.Children)
                        if (row is FrameworkElement fe) rows.Add(fe);
                }
            }
            catch { }
            return rows;
        }

        private static string SearchTextOf(DependencyObject? node, int depth = 0)
        {
            if (node == null || depth > 7) return "";
            var sb = new StringBuilder();
            if (node is TextBlock tb) sb.Append(tb.Text).Append(' ');
            if (node is ContentControl cc && cc.Content is string s0) sb.Append(s0).Append(' ');
            if (node is FrameworkElement fe && fe.ToolTip is string tip) sb.Append(tip).Append(' ');
            if (node is TextBox tx) sb.Append(tx.Text).Append(' ');
            try
            {
                var n = VisualTreeHelper.GetChildrenCount(node);
                for (var i = 0; i < n; i++) sb.Append(SearchTextOf(VisualTreeHelper.GetChild(node, i), depth + 1));
            }
            catch { }
            return sb.ToString();
        }

        private void OnSettingsSearchChanged(object sender, TextChangedEventArgs e) => ApplySettingsFilter();

        private void OnSettingsSearchKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Escape) return;
            if (TxtSettingsSearch != null) TxtSettingsSearch.Text = "";
            e.Handled = true;
        }

        /// <summary>검색어에 맞춰 설정 행 표시/숨김 — 검색어가 비면 전부 복원</summary>
        private void ApplySettingsFilter()
        {
            try
            {
                if (TxtSettingsSearch == null || TxtSettingsSearchInfo == null) return;
                var raw = (TxtSettingsSearch.Text ?? "").Trim();
                var rows = SettingsRows();

                // 2026-11-02 (A-6 잔여): 토큰 필터 — @변경 / @기본 / @위험
                var onlyChanged = raw.IndexOf("@변경", StringComparison.Ordinal) >= 0 || raw.IndexOf("@changed", StringComparison.OrdinalIgnoreCase) >= 0;
                var onlyDefault = raw.IndexOf("@기본", StringComparison.Ordinal) >= 0 || raw.IndexOf("@default", StringComparison.OrdinalIgnoreCase) >= 0;
                var onlyRisk = raw.IndexOf("@위험", StringComparison.Ordinal) >= 0 || raw.IndexOf("@risk", StringComparison.OrdinalIgnoreCase) >= 0;
                var q = raw
                    .Replace("@변경", "").Replace("@changed", "").Replace("@CHANGED", "")
                    .Replace("@기본", "").Replace("@default", "").Replace("@DEFAULT", "")
                    .Replace("@위험", "").Replace("@risk", "").Replace("@RISK", "")
                    .Trim();

                UpdateSettingBadges();
                var tokenOnly = q.Length == 0 && (onlyChanged || onlyDefault || onlyRisk);
                if (q.Length > 0 || tokenOnly) SetSettingsCollapsed(false);

                var shown = 0;
                foreach (var row in rows)
                {
                    var changed = RowIsChanged(row);
                    var risk = RowIsRisk(row);
                    var show = q.Length == 0 || SearchTextOf(row).IndexOf(q, StringComparison.CurrentCultureIgnoreCase) >= 0;
                    if (onlyChanged) show = show && changed;
                    if (onlyDefault) show = show && !changed;
                    if (onlyRisk) show = show && risk;
                    row.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
                    if (show) shown++;
                }

                if (q.Length == 0 && !tokenOnly)
                {
                    TxtSettingsSearchInfo.Text = _changedNames.Count == 0
                        ? rows.Count + "개 항목 · 모두 기본값"
                        : "변경 " + _changedNames.Count + " / " + rows.Count;
                }
                else
                {
                    var tag = onlyChanged ? "@변경 " : (onlyDefault ? "@기본 " : (onlyRisk ? "@위험 " : ""));
                    TxtSettingsSearchInfo.Text = tag + shown + " / " + rows.Count;
                }
            }
            catch { }
        }
    }
}
