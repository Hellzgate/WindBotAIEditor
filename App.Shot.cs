using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace WindBotAIEditor;

/// <summary>
/// 디자인 확인용 오프스크린 스크린샷 (창을 **띄우지 않는다**).
///
/// 사용:
///   WindBotAIEditor.exe --shot &lt;폴더&gt; [--size 1560x1000] [--profile &lt;rules.json&gt;] [--dark|--light]
///       → 폴더에 화면별 PNG (01-deck.png, 02-settings.png, 03-rules-1.png …)
///   WindBotAIEditor.exe --shot &lt;파일.png&gt; [--tab "카드 DB / Lua"] [--rule N] ...
///       → 한 화면만 저장
///
/// 왜 필요한가: 데스크톱 앱은 "창을 띄우지 않는다"는 규칙 때문에 실제 렌더 결과를 볼 수 없어
/// 레이아웃 오류(빈 칸·잘림·대비 부족)를 좌표로만 추측하게 된다. 이 옵션은 UI 를 Measure/Arrange 로
/// 배치한 뒤 RenderTargetBitmap 으로 **픽셀을 그대로** 저장해, 웹 화면처럼 눈으로 보고 고칠 수 있게 한다.
/// </summary>
public partial class App
{
    private static int RunShot(string[] args)
    {
        try
        {
            var target = Path.GetFullPath(args[1]);
            var width = 1560;
            var height = 1000;
            string profile = null;
            string tab = null;
            int? rule = null;
            bool? dark = null;
            var wantFilters = false;

            for (var i = 2; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--size" when i + 1 < args.Length:
                        {
                            var parts = args[++i].Split('x', 'X');
                            if (parts.Length == 2 && int.TryParse(parts[0], out var w) && int.TryParse(parts[1], out var h) && w > 200 && h > 200)
                            {
                                width = w;
                                height = h;
                            }
                            break;
                        }
                    case "--profile" when i + 1 < args.Length:
                        profile = Path.GetFullPath(args[++i]);
                        break;
                    case "--tab" when i + 1 < args.Length:
                        tab = args[++i];
                        break;
                    case "--rule" when i + 1 < args.Length:
                        if (int.TryParse(args[++i], out var r)) rule = r;
                        break;
                    case "--dark":
                        dark = true;
                        break;
                    case "--light":
                        dark = false;
                        break;
                    case "--filters":
                        wantFilters = true;
                        break;
                }
            }

            var w0 = new MainWindow();
            if (dark.HasValue && w0.Content is FrameworkElement) ApplyThemeForShot(dark.Value);
            if (profile != null && !w0.LoadProfileFile(profile))
                Console.WriteLine("SHOT profile LOAD_FAILED " + profile);

            if (w0.Content is not FrameworkElement root)
            {
                Console.Error.WriteLine("SHOT FAIL no content");
                return 2;
            }

            var isFile = target.EndsWith(".png", StringComparison.OrdinalIgnoreCase);
            var outDir = isFile ? Path.GetDirectoryName(target) : target;
            if (!string.IsNullOrEmpty(outDir)) Directory.CreateDirectory(outDir);

            var screens = new List<(string Name, string Tab, int? Rule)>();
            if (isFile)
            {
                screens.Add(("shot", tab ?? "덱", rule));
            }
            else
            {
                screens.Add(("01-deck", "덱", null));
                for (var i = 0; i < WindBotAIEditor.MainWindow.RuleTabHeaders.Length; i++)
                    screens.Add(($"0{i + 2}-rules-{i}", "⚙ AI 설정", i));
                screens.Add(("08-bots", "봇 목록 (로비 AI)", null));
                screens.Add(("09-dialogs", "봇 대사", null));
                screens.Add(("10-cards", "카드 DB / Lua", null));
                screens.Add(("11-verify", "검증 / 실행", null));
            }

            var saved = 0;
            foreach (var (name, tabHeader, ruleIndex) in screens)
            {
                if (ruleIndex.HasValue) w0.SelectRuleTab(ruleIndex.Value);
                else if (!w0.SelectMainTab(tabHeader))
                {
                    Console.Error.WriteLine("SHOT skip (탭 없음): " + tabHeader);
                    continue;
                }

                var path = isFile ? target : Path.Combine(outDir, name + ".png");
                if (RenderToPng(w0, root, width, height, path))
                {
                    saved++;
                    Console.WriteLine("SHOT " + path + " " + width + "x" + height);
                }
            }

            if (wantFilters)
            {
                var popup = Path.Combine(isFile ? (outDir ?? ".") : outDir, "00-filters-popup.png");
                if (CaptureFilterWindow(popup)) { saved++; Console.WriteLine("SHOT " + popup); }
            }

            Console.WriteLine("SHOT OK " + saved + "개");
            return saved > 0 ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("SHOT FAIL " + ex);
            return 2;
        }
    }

    /// <summary>창을 표시하지 않고 레이아웃한 뒤 PNG 로 저장.</summary>
    private static bool RenderToPng(Window w, FrameworkElement root, int width, int height, string path)
    {
        // 루트 패널 배경이 투명이면 창 배경색으로 채운다(PNG 알파로 어둡게 보이는 것 방지)
        Panel panel = null;
        Brush restoreBrush = null;
        if (root is Panel p)
        {
            panel = p;
            restoreBrush = p.Background;
            if (p.Background == null) p.Background = w.Background;
        }

        try
        {
            for (var pass = 0; pass < 2; pass++)
            {
                root.Measure(new Size(width, height));
                root.Arrange(new Rect(0, 0, width, height));
                root.UpdateLayout();
                PumpOneFrame();
            }

            var bmp = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(root);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bmp));
            using (var fs = File.Create(path))
                encoder.Save(fs);
            return true;
        }
        finally
        {
            if (panel != null) panel.Background = restoreBrush;
        }
    }

    /// <summary>② 필터 팝업(ChoiceFiltersWindow)을 열지 않고 PNG 로 저장.</summary>
    private static bool CaptureFilterWindow(string path)
    {
        try
        {
            var rule = new Models.ChoiceRule
            {
                On = "SelectCard", Priority = 1, WhenCard = 100458026,
                ResistantMode = "on", SkipDisabled = true, SkipEffectUsed = true
            };
            var w = new ChoiceFiltersWindow(rule);
            if (w.Content is not FrameworkElement root) return false;
            root.Measure(new Size(440, double.PositiveInfinity));
            root.UpdateLayout();
            root.Measure(new Size(440, double.PositiveInfinity));   // 줄바꿈 반영 후 다시 측정
            var height = Math.Max(280, (int)Math.Ceiling(root.DesiredSize.Height) + 12);
            return RenderToPng(w, root, 440, height, path);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("SHOT filters FAIL " + ex.Message);
            return false;
        }
    }

    /// <summary>레이아웃/바인딩이 한 번 돌도록 디스패처를 잠깐 돌린다(창은 표시하지 않음).</summary>
    private static void PumpOneFrame()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static void ApplyThemeForShot(bool dark)
    {
        var dict = new ResourceDictionary
        {
            Source = new Uri(dark ? "Themes/Dark.xaml" : "Themes/Light.xaml", UriKind.Relative)
        };
        var md = Current.Resources.MergedDictionaries;
        if (md.Count > 0) md[0] = dict; else md.Add(dict);
    }
}
