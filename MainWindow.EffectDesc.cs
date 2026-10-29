using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Threading;
using WindBotAIEditor.Services;

namespace WindBotAIEditor
{
    /// <summary>
    /// B-45: 효과번호(desc) 후보 카탈로그 — 5분 자동 갱신 + [효과번호 갱신] 버튼.
    /// 후보 = 카드 Lua(SetDescription) + duel-log 관측(activate … desc=).
    /// </summary>
    public partial class MainWindow
    {
        private DispatcherTimer? _descTimer;

        private void StartEffectDescTimer()
        {
            try
            {
                EffectDescCatalog.Instance.LoadCache();
                if (_descTimer == null)
                {
                    _descTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(5) };
                    _descTimer.Tick += delegate { RefreshEffectDescs(true); };
                    _descTimer.Start();
                }
                RefreshEffectDescs(true);
            }
            catch { /* 카탈로그 실패는 앱 동작에 영향 없음 */ }
        }

        private void OnRefreshEffectDescs(object sender, RoutedEventArgs e) => RefreshEffectDescs(false);

        private void RefreshEffectDescs(bool silent)
        {
            try
            {
                var ids = new List<int>();
                foreach (var a in _actions) if (a.Card > 0) ids.Add(a.Card);
                foreach (var c in _choices)
                {
                    if (c.Card > 0) ids.Add(c.Card);
                    if (c.WhenCard > 0) ids.Add(c.WhenCard);
                }
                foreach (var f in _forbids) if (f.Card > 0) ids.Add(f.Card);
                foreach (var t in _triggers) if (t.Card > 0) ids.Add(t.Card);
                foreach (var o in _opps)
                {
                    if (o.Card > 0) ids.Add(o.Card);
                    if (o.ResponseCard > 0) ids.Add(o.ResponseCard);
                }

                string root = App.EdoproRoot ?? "";
                EffectDescCatalog.Instance.Refresh(root, ids);
                if (!silent) Status("효과번호 후보: " + EffectDescCatalog.Instance.Status);
            }
            catch (Exception ex)
            {
                if (!silent) Status("효과번호 갱신 실패: " + ex.Message);
            }
        }
    }
}
