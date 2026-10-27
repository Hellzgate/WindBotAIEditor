using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using WindBotAIEditor.Models;
using WindBotAIEditor.Services;

namespace WindBotAIEditor
{
    /// <summary>
    /// 리플레이(.yrpX) 미리보기 — "내 쪽"을 고르면 그 플레이어의 행동만 턴/트랙별로 보여주고,
    /// [적용] 하면 선공/후공 전개 루트를 각각 교체할 단계 목록을 돌려준다.
    /// </summary>
    public partial class ReplayImportWindow : Window
    {
        private readonly ReplayFile _replay;
        private ReplayExtraction? _extraction;
        private bool _ready;

        /// <summary>[적용] 시 선공 루트로 쓸 단계 (조건 없음, 순위 1개)</summary>
        public List<RouteStep> RouteFirst { get; private set; } = new List<RouteStep>();

        /// <summary>[적용] 시 후공 루트로 쓸 단계</summary>
        public List<RouteStep> RouteSecond { get; private set; } = new List<RouteStep>();

        /// <summary>"선공 N단계 / 후공 M단계"</summary>
        public string SummaryText => _extraction?.Summary ?? "";

        public ReplayImportWindow(ReplayFile replay)
        {
            InitializeComponent();
            _replay = replay;

            TxtFile.Text = replay.FilePath;
            TxtReplayInfo.Text = "패킷 " + replay.PacketCount + "개 · 턴 " + replay.TurnCount + "개 · " +
                                 "플레이어 " + replay.PlayerSummary +
                                 (replay.TrailingBytes > 0 ? " · 잔여 " + replay.TrailingBytes + "B(손상 의심)" : "");

            var guess = ReplayRouteExtractor.GuessMySide(replay);
            RbSide0.Content = "플레이어 0 — " + replay.NameOf(0);
            RbSide1.Content = "플레이어 1 — " + replay.NameOf(1);
            RbSide1.Visibility = replay.PlayerNames.Length > 1 ? Visibility.Visible : Visibility.Collapsed;

            if (guess == 1 && replay.PlayerNames.Length > 1) RbSide1.IsChecked = true;
            else RbSide0.IsChecked = true;

            _ready = true;
            Refresh();
        }

        private int SelectedSide => RbSide1.IsChecked == true ? 1 : 0;

        private void OnSideChanged(object sender, RoutedEventArgs e)
        {
            if (!_ready) return;
            Refresh();
        }

        private void Refresh()
        {
            var result = ReplayRouteExtractor.Extract(_replay, SelectedSide);
            _extraction = result;
            GridActions.ItemsSource = result.Actions;
            TxtSummary.Text = result.Summary +
                              (result.MyTurnCount == 0 ? "  (내 턴이 없습니다)" : "");

            var warn = "";
            if (result.MyTurnCount == 0)
                warn = "이 플레이어의 턴이 리플레이에 없습니다 — 다른 쪽을 선택해 보세요.";
            else if (result.IgnoredTurnCount > 0)
                warn = "내 " + (result.MyTurnCount) + "번째 턴이 있어 " + result.IgnoredTurnCount +
                       "개 턴은 기록하지 않았습니다 (루트는 1·2번째 턴만).";
            else if (result.IWasFirstPlayer && result.SecondCount > 0)
                warn = "내가 선공인 리플레이입니다 — 후공 전개 루트는 후공일 때만 실행되므로, " +
                       "후공으로 들어간 " + result.SecondCount + "단계는 실제 듀얼에서 실행되지 않습니다.";
            TxtWarn.Text = warn;
            TxtWarn.Visibility = warn.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        }

        private void OnApply(object sender, RoutedEventArgs e)
        {
            var result = _extraction;
            if (result == null) return;
            RouteFirst = ReplayRouteExtractor.ToRouteSteps(result.OfTrack("first"));
            RouteSecond = ReplayRouteExtractor.ToRouteSteps(result.OfTrack("second"));
            DialogResult = true;
        }
    }
}
