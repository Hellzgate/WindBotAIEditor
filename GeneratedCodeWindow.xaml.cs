using System;
using System.Windows;

namespace WindBotAIEditor
{
    /// <summary>
    /// 생성 코드 읽기 전용 뷰어 (2026-11-02).
    /// ⑤ 루트 선택/② 카드 선택/조건 헬퍼처럼 **UI 에 그대로 드러나지 않는 생성 코드**를 눈으로 확인하기 위한 창.
    /// 검색 + 빠른 이동(루트 선택 · 카드 선택 · CountCard · AddExecutor) 지원.
    /// </summary>
    public partial class GeneratedCodeWindow : Window
    {
        private readonly string _code;
        private int _lastIndex;

        public GeneratedCodeWindow(string title, string code, string jumpToken)
        {
            InitializeComponent();
            Title = string.IsNullOrWhiteSpace(title) ? "생성 코드 (읽기 전용)" : title;
            _code = code ?? "";
            TxtCode.Text = _code;
            int lines = _code.Length == 0 ? 0 : _code.Split('\n').Length;
            TxtInfo.Text = "읽기 전용 · " + lines + "줄 — 왼쪽 위에서 찾기, 또는 [빠른 이동] 으로 해당 블록으로 이동합니다. "
                         + "(생성 코드는 규칙을 바꾸면 즉시 달라집니다)";
            Loaded += delegate
            {
                if (!string.IsNullOrWhiteSpace(jumpToken)) JumpTo(jumpToken, true);
                TxtCode.Focus();
            };
        }

        private void JumpTo(string token, bool fromStart)
        {
            if (string.IsNullOrEmpty(token)) return;
            int start = fromStart ? 0 : Math.Max(0, _lastIndex);
            int idx;
            try { idx = _code.IndexOf(token, start, StringComparison.Ordinal); }
            catch { idx = -1; }
            if (idx < 0 && start > 0) idx = _code.IndexOf(token, StringComparison.Ordinal);   // 끝까지 갔으면 처음부터
            if (idx < 0)
            {
                TxtInfo.Text = "찾지 못함: " + token;
                return;
            }
            _lastIndex = idx + token.Length;
            TxtCode.Select(idx, token.Length);
            int line = _code.Substring(0, idx).Split('\n').Length;
            TxtCode.ScrollToLine(Math.Max(1, line - 3));
            TxtInfo.Text = "이동: " + token + "  (줄 " + line + ")";
        }

        private void OnFind(object sender, RoutedEventArgs e) { JumpTo(TxtFind.Text, true); }
        private void OnFindNext(object sender, RoutedEventArgs e) { JumpTo(TxtFind.Text, false); }

        private void OnJumpRoutes(object sender, RoutedEventArgs e) { JumpTo("private void SelectRouteFirst", true); }
        private void OnJumpPick(object sender, RoutedEventArgs e) { JumpTo("OnSelectCard(IList<ClientCard>", true); }
        private void OnJumpCount(object sender, RoutedEventArgs e) { JumpTo("private static int CountCard", true); }
        private void OnJumpExec(object sender, RoutedEventArgs e) { JumpTo("AddExecutor(ExecutorType.", true); }

        private void OnCopy(object sender, RoutedEventArgs e)
        {
            try { Clipboard.SetText(_code); TxtInfo.Text = "클립보드에 복사했습니다 (" + _code.Length + "자)"; }
            catch (Exception ex) { TxtInfo.Text = "복사 실패: " + ex.Message; }
        }

        private void OnClose(object sender, RoutedEventArgs e) { Close(); }
    }
}
