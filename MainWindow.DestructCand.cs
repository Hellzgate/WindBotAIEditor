using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows;
using WindBotAIEditor.Services;

namespace WindBotAIEditor
{
    /// <summary>
    /// 2026-11-01: 관찰된 "파괴 실패" 후보 — duel-log 의 `destruct-fail` 을 모아 보여주고,
    /// 사용자가 [후보 추가] 로 [파괴 내성 카드] 목록에 넣는다(자동 등록 없음).
    /// </summary>
    public partial class MainWindow
    {
        private void RefreshDestructCandidates()
        {
            try
            {
                DestructFailCatalog.Instance.Refresh(_profile?.DestructibleImmuneCards);
                UpdateDestructCandText();
            }
            catch { /* 후보 수집 실패는 앱 동작에 영향 없음 */ }
        }

        /// <summary>설정 카드의 후보 안내 문구 갱신</summary>
        private void UpdateDestructCandText()
        {
            if (TxtDestructCand == null) return;
            var items = DestructFailCatalog.Instance.Items;
            if (items == null || items.Count == 0)
            {
                TxtDestructCand.Text = "(관찰된 후보 없음 — 듀얼 후 자동으로 모입니다)";
                return;
            }
            var sb = new StringBuilder("관찰된 후보: ");
            for (var i = 0; i < items.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(items[i].CardId);
                if (!string.IsNullOrWhiteSpace(items[i].Name)) sb.Append("(").Append(items[i].Name).Append(")");
                sb.Append(" ").Append(items[i].Count).Append("회");
            }
            TxtDestructCand.Text = sb.ToString();
        }

        /// <summary>[후보 추가] — 관찰된 후보를 전부 [파괴 내성 카드] 목록에 넣는다</summary>
        private void OnAddDestructCandidates(object sender, RoutedEventArgs e)
        {
            var items = DestructFailCatalog.Instance.Items;
            if (items == null || items.Count == 0)
            {
                Status("관찰된 파괴 실패 후보가 없습니다 (듀얼 후 자동으로 모입니다)");
                return;
            }

            var added = 0;
            foreach (var it in items.ToList())
                if (AddDestructibleImmune(it.CardId)) added++;

            DestructFailCatalog.Instance.Refresh(_profile?.DestructibleImmuneCards);
            UpdateDestructCandText();
            Status("파괴 내성 카드 " + added + "장 추가 — [규칙 저장] 후 [생성 + 컴파일] 하세요");
        }

        /// <summary>[무시] — 현재 후보를 다시 보지 않는다</summary>
        private void OnIgnoreDestructCandidates(object sender, RoutedEventArgs e)
        {
            var n = DestructFailCatalog.Instance.Items.Count;
            DestructFailCatalog.Instance.IgnoreAll(_profile?.DestructibleImmuneCards);
            UpdateDestructCandText();
            Status("관찰된 후보 " + n + "장을 무시 목록에 넣었습니다");
        }
    }
}
