using System;
using System.Windows;
using System.Windows.Input;
using WindBotAIEditor.Models;
using WindBotAIEditor.Services;

namespace WindBotAIEditor
{
    /// <summary>
    /// 2026-11-02: 포팅 스캐폴드의 **원본 코드(rawBody) 편집** — [검증]으로 csc v4.0 컴파일을 통과해야 저장된다.
    /// </summary>
    public partial class MainWindow
    {
        private void OnOpenRawBody(object sender, RoutedEventArgs e) => OpenRawBodyWindow(GridActions?.SelectedItem as ActionRule);

        private void OnActionRowDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (GridActions == null) return;
            if (GridActions.SelectedItem is ActionRule a && !string.IsNullOrWhiteSpace(a.RawBody))
                OpenRawBodyWindow(a);
        }

        private void OpenRawBodyWindow(ActionRule? a)
        {
            if (a == null) { Status("행을 먼저 선택하세요"); return; }
            if (string.IsNullOrWhiteSpace(a.RawBody))
            {
                Status("이 행은 일반 규칙입니다 (원본 코드 rawBody 없음)");
                return;
            }
            if (_project == null) { Status("EDOPro 폴더를 먼저 여세요"); return; }

            var w = new RawBodyWindow(a.Type + " / 카드 " + a.Card, a.RawBody) { Owner = this };
            w.Verify = text => VerifyRawBody(a, text);
            if (w.ShowDialog() == true)
            {
                a.RawBody = w.Body;
                Status("원본 코드 수정: " + a.Type + " / " + a.Card + " — [규칙 저장] 후 [생성 + 컴파일] 하세요");
            }
        }

        /// <summary>2026-11-02: Android 안전 검사 토글</summary>
        private void OnAndroidSafeChanged(object sender, RoutedEventArgs e)
        {
            bool on = ChkAndroidSafe.IsChecked == true;
            if (_profile != null) _profile.AndroidSafeCheck = on;
            Status("Android 안전 검사: " + (on ? "ON" : "OFF"));
        }

        /// <summary>본문을 임시로 갈아끼우고 **생성 코드 전체**를 csc v4.0 으로 검증한다.</summary>
        private CSharp5Verifier.Result VerifyRawBody(ActionRule a, string text)
        {
            var old = a.RawBody;
            a.RawBody = text;
            try
            {
                var p = _profile ?? CurrentProfile();
                var src = RuleCodeGen.Generate(p);
                return CSharp5Verifier.Verify(_project, src, RuleCodeGen.ClassName(p.Engine));
            }
            catch (Exception ex)
            {
                var r = new CSharp5Verifier.Result();
                r.Items.Add(new CSharp5Verifier.Diag { Code = "GEN", Message = ex.Message });
                return r;
            }
            finally { a.RawBody = old; }
        }

        /// <summary>2026-11-02: 빌드 **전에** C#5 검증 — 실패하면 DLL 을 건드리지 않고 진단만 보여준다.</summary>
        private bool PreBuildVerify(AiProfile p, string source, out string report)
        {
            report = "";
            try
            {
                var r = CSharp5Verifier.Verify(_project, source, RuleCodeGen.ClassName(p.Engine));
                var bad = CSharp5Verifier.ScanForbiddenApis(source);
                var sb = new System.Text.StringBuilder();
                if (!r.Ok)
                {
                    sb.AppendLine("[사전 검증] C#5 오류 " + r.Items.Count + "건 — 빌드를 중단했습니다");
                    foreach (var d in r.Items) sb.AppendLine("  " + d);
                }
                // 2026-11-02: Windows/Android **둘 다** 같은 DLL 로 돌아가야 하므로 기본은 차단
                if (bad.Count > 0)
                {
                    sb.AppendLine((p.AndroidSafeCheck ? "[안드로이드 차단] " : "[안드로이드 경고] ") +
                                  "트리밍 BCL 에 없는 API " + bad.Count + "건" +
                                  (p.AndroidSafeCheck ? " — 빌드를 중단했습니다 (⚙ AI 설정에서 검사를 끌 수 있습니다)" : ""));
                    foreach (var b in bad) sb.AppendLine("  · " + b);
                }
                report = sb.ToString().TrimEnd();
                return r.Ok && (!p.AndroidSafeCheck || bad.Count == 0);
            }
            catch (Exception ex)
            {
                report = "[사전 검증] 실패: " + ex.Message;
                return true;   // 검증기 자체 오류로 빌드를 막지는 않는다
            }
        }
    }
}

