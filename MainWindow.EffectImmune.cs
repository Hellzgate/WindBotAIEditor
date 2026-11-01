using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows;
using WindBotAIEditor.Services;

namespace WindBotAIEditor
{
    /// <summary>
    /// 2026-11-02: **효과 면역 카드** 목록 — `Unaffected by other cards' effects` 처럼
    /// 이쪽 효과(뵐러·포영·파괴 등)가 통하지 않는 카드. 대상 선택에서 제외한다(릴리스는 예외).
    /// </summary>
    public partial class MainWindow
    {
        /// <summary>목록 표시 갱신</summary>
        private void UpdateEffectImmuneText()
        {
            if (TxtEffectImmune == null) return;
            var ids = _profile?.EffectImmuneCards;
            if (ids == null || ids.Count == 0) { TxtEffectImmune.Text = "(없음)"; return; }
            var sb = new StringBuilder();
            foreach (var id in ids)
            {
                if (sb.Length > 0) sb.Append(", ");
                var nm = CardDatabase.Instance.NameOf(id);
                sb.Append(id).Append(nm.Length > 0 ? "(" + nm + ")" : "");
            }
            TxtEffectImmune.Text = sb.ToString();
        }

        private void OnAddEffectImmune(object sender, RoutedEventArgs e)
            => AddEffectImmune(CardEffectImmune?.CardId ?? 0);

        private void OnClearEffectImmune(object sender, RoutedEventArgs e) => ClearEffectImmune();

        private bool AddEffectImmune(int id)
        {
            if (id <= 0) { Status("카드를 먼저 선택하세요"); return false; }
            if (_profile == null) return false;
            _profile.EffectImmuneCards ??= new List<int>();
            if (!_profile.EffectImmuneCards.Contains(id))
                _profile.EffectImmuneCards.Add(id);
            if (CardEffectImmune != null) CardEffectImmune.CardId = 0;
            UpdateEffectImmuneText();
            Status("효과 면역 카드 추가: " + id + " (총 " + _profile.EffectImmuneCards.Count + "개)");
            return true;
        }

        private void ClearEffectImmune()
        {
            _profile?.EffectImmuneCards?.Clear();
            if (CardEffectImmune != null) CardEffectImmune.CardId = 0;
            UpdateEffectImmuneText();
            Status("효과 면역 카드 목록을 비웠습니다");
        }

        /// <summary>2026-11-02: 효과 면역 카드 대상 제외 — 전역 토글</summary>
        private void OnSkipEffectImmuneChanged(object sender, RoutedEventArgs e)
        {
            bool on = ChkSkipEffectImmune.IsChecked == true;
            if (_profile != null) _profile.SkipEffectImmuneTargets = on;
            Status("효과 면역 카드 대상 제외(릴리스 제외): " + (on ? "ON" : "OFF"));
        }
        /// <summary>2026-11-02: 무효화 카드 목록 표시 갱신</summary>
        private void UpdateNegateText()
        {
            if (TxtNegate == null) return;
            var ids = _profile?.NegateCardIds;
            if (ids == null || ids.Count == 0) { TxtNegate.Text = "(없음)"; return; }
            var sb = new StringBuilder();
            foreach (var id in ids)
            {
                if (sb.Length > 0) sb.Append(", ");
                var nm = CardDatabase.Instance.NameOf(id);
                sb.Append(id).Append(nm.Length > 0 ? "(" + nm + ")" : "");
            }
            TxtNegate.Text = sb.ToString();
        }

        private void OnAddNegate(object sender, RoutedEventArgs e) => AddNegate(CardNegate?.CardId ?? 0);

        private void OnClearNegate(object sender, RoutedEventArgs e)
        {
            _profile?.NegateCardIds?.Clear();
            if (CardNegate != null) CardNegate.CardId = 0;
            UpdateNegateText();
            Status("무효화 카드 목록을 비웠습니다");
        }

        /// <summary>기본값(뵐러·무한포영) 채우기</summary>
        private void OnNegateDefaults(object sender, RoutedEventArgs e)
        {
            AddNegate(97268402);
            AddNegate(10045474);
        }

        private bool AddNegate(int id)
        {
            if (id <= 0) { Status("카드를 먼저 선택하세요"); return false; }
            if (_profile == null) return false;
            _profile.NegateCardIds ??= new List<int>();
            if (!_profile.NegateCardIds.Contains(id)) _profile.NegateCardIds.Add(id);
            if (CardNegate != null) CardNegate.CardId = 0;
            UpdateNegateText();
            Status("무효화 카드 추가: " + id + " (총 " + _profile.NegateCardIds.Count + "개)");
            return true;
        }

        /// <summary>2026-11-02: 무효화 카드 체인 금지 — 전역 토글</summary>
        private void OnSkipEffectImmuneNegateChanged(object sender, RoutedEventArgs e)
        {
            bool on = ChkSkipEffectImmuneNegate.IsChecked == true;
            if (_profile != null) _profile.SkipEffectImmuneNegate = on;
            Status("무효화 카드 효과 면역 체인 금지: " + (on ? "ON" : "OFF"));
        }
        /// <summary>2026-11-02: 파괴 카드 목록 표시 갱신</summary>
        private void UpdateDestroyCardText()
        {
            if (TxtDestroy == null) return;
            var ids = _profile?.DestroyCardIds;
            if (ids == null || ids.Count == 0) { TxtDestroy.Text = "(없음)"; return; }
            var sb = new StringBuilder();
            foreach (var id in ids)
            {
                if (sb.Length > 0) sb.Append(", ");
                var nm = CardDatabase.Instance.NameOf(id);
                sb.Append(id).Append(nm.Length > 0 ? "(" + nm + ")" : "");
            }
            TxtDestroy.Text = sb.ToString();
        }

        private void OnAddDestroyCard(object sender, RoutedEventArgs e) => AddDestroyCard(CardDestroy?.CardId ?? 0);

        private void OnClearDestroyCard(object sender, RoutedEventArgs e)
        {
            _profile?.DestroyCardIds?.Clear();
            if (CardDestroy != null) CardDestroy.CardId = 0;
            UpdateDestroyCardText();
            Status("파괴 카드 목록을 비웠습니다");
        }

        /// <summary>기본값(유령토끼 2종) 채우기</summary>
        private void OnDestroyDefaults(object sender, RoutedEventArgs e)
        {
            AddDestroyCard(59438930);
            AddDestroyCard(59438931);
        }

        private bool AddDestroyCard(int id)
        {
            if (id <= 0) { Status("카드를 먼저 선택하세요"); return false; }
            if (_profile == null) return false;
            _profile.DestroyCardIds ??= new List<int>();
            if (!_profile.DestroyCardIds.Contains(id)) _profile.DestroyCardIds.Add(id);
            if (CardDestroy != null) CardDestroy.CardId = 0;
            UpdateDestroyCardText();
            Status("파괴 카드 추가: " + id + " (총 " + _profile.DestroyCardIds.Count + "개)");
            return true;
        }

        /// <summary>2026-11-02: 파괴 카드 체인 금지 — 전역 토글</summary>
        private void OnSkipDestructImmuneDestroyChanged(object sender, RoutedEventArgs e)
        {
            bool on = ChkSkipDestructImmuneDestroy.IsChecked == true;
            if (_profile != null) _profile.SkipDestructImmuneDestroy = on;
            Status("파괴 카드 파괴내성 체인 금지: " + (on ? "ON" : "OFF"));
        }
    }
}




