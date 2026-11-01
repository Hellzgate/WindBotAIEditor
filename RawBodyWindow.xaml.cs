using System;
using System.Windows;

namespace WindBotAIEditor
{
    /// <summary>
    /// 2026-11-02: 포팅 스캐폴드의 `rawBody`(원본 C# handler 본문) 편집 창.
    /// **검증(csc v4.0)을 통과해야 저장**된다 — C#6+ 문법이나 구버전에 없는 API 를 막기 위해.
    /// </summary>
    public partial class RawBodyWindow : Window
    {
        private readonly string _original;
        public string Body { get; private set; } = "";

        /// <summary>본문 검증기 주입 (생성 코드 전체를 컴파일해 결과를 돌려준다)</summary>
        public Func<string, Services.CSharp5Verifier.Result>? Verify { get; set; }

        public RawBodyWindow(string title, string body)
        {
            InitializeComponent();
            _original = body ?? "";
            TxtBody.Text = _original;
            Body = _original;
            if (TxtTitle != null && !string.IsNullOrWhiteSpace(title))
                TxtTitle.Text = title + " — 조건은 ① 그리드에서, 본문은 여기서 편집합니다";
            SetState("검증 후 저장할 수 있습니다", "#A0A4AA");
        }

        private void SetState(string text, string color)
        {
            if (TxtState == null) return;
            TxtState.Text = text;
            try { TxtState.Foreground = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(color)); }
            catch { }
        }

        private void OnVerify(object sender, RoutedEventArgs e)
        {
            if (Verify == null) { SetState("검증기를 사용할 수 없습니다", "#E0A030"); return; }
            Cursor = System.Windows.Input.Cursors.Wait;
            try
            {
                var r = Verify(TxtBody.Text ?? "");
                if (TxtDiag != null) TxtDiag.Text = r.Ok ? "" : string.Join(Environment.NewLine, r.Items.ConvertAll(d => d.ToString()).ToArray());
                if (BtnSave != null) BtnSave.IsEnabled = r.Ok;
                SetState(r.Ok ? "C#5 OK — 저장할 수 있습니다" : r.Summary + " — 수정 후 다시 검증하세요",
                         r.Ok ? "#6FCF97" : "#EB5757");
            }
            catch (Exception ex)
            {
                SetState("검증 실패: " + ex.Message, "#EB5757");
            }
            finally { Cursor = System.Windows.Input.Cursors.Arrow; }
        }

        private void OnSave(object sender, RoutedEventArgs e)
        {
            Body = TxtBody.Text ?? "";
            DialogResult = true;
        }

        private void OnRevert(object sender, RoutedEventArgs e)
        {
            TxtBody.Text = _original;
            if (TxtDiag != null) TxtDiag.Text = "";
            if (BtnSave != null) BtnSave.IsEnabled = false;
            SetState("원래 본문으로 되돌렸습니다", "#A0A4AA");
        }

        private void OnClose(object sender, RoutedEventArgs e) { DialogResult = false; }
    }
}
