using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using WindBotAIEditor.Models;

namespace WindBotAIEditor.Services
{
    /// <summary>
    /// 한 봇(엔진)을 다른 사람에게 넘길 수 있는 ZIP 으로 묶는다.
    /// 구성: WindBot\Executors\*.dll, WindBot\Decks\AI_*.ydk, WindBot\Dialogs\*.json,
    ///       bots-entry.json, install.ps1, 설치방법.txt
    /// </summary>
    public static class BotPackageExporter
    {
        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
        };

        public sealed class Result
        {
            public string ZipPath { get; init; } = "";
            public List<string> Files { get; } = new();
            public string Error { get; init; } = "";
            public bool Ok => Error.Length == 0;
        }

        public static Result Export(WindBotProject project, BotEntry bot, string zipPath)
        {
            var res = new Result { ZipPath = zipPath };
            var engine = (bot.Deck ?? "").Trim();
            if (engine.Length == 0) return Fail("봇에 덱(엔진) 이름이 없습니다.");

            var cls = RuleCodeGen.ClassName(engine);
            var dll = Path.Combine(project.ExecutorsDir, cls + ".dll");
            if (!File.Exists(dll)) return Fail("실행기가 없습니다: " + dll + " (먼저 [생성 + 컴파일])");

            var deck = Path.Combine(project.DecksDir, "AI_" + engine + ".ydk");
            if (!File.Exists(deck)) return Fail("덱 파일이 없습니다: " + deck);

            var dialogFile = ReadDialogFile(project, engine);
            var dialog = dialogFile.Length > 0 ? Path.Combine(project.DialogsDir, dialogFile + ".json") : "";
            if (dialog.Length > 0 && !File.Exists(dialog)) dialog = "";
            if (dialog.Length == 0) dialogFile = "";

            var temp = Path.Combine(Path.GetTempPath(), "wbbot_" + Guid.NewGuid().ToString("N"));
            try
            {
                var wb = Path.Combine(temp, "WindBot");
                Directory.CreateDirectory(Path.Combine(wb, "Executors"));
                Directory.CreateDirectory(Path.Combine(wb, "Decks"));
                if (dialogFile.Length > 0) Directory.CreateDirectory(Path.Combine(wb, "Dialogs"));

                File.Copy(dll, Path.Combine(wb, "Executors", cls + ".dll"));
                res.Files.Add("WindBot/Executors/" + cls + ".dll");
                File.Copy(deck, Path.Combine(wb, "Decks", "AI_" + engine + ".ydk"));
                res.Files.Add("WindBot/Decks/AI_" + engine + ".ydk");
                if (dialogFile.Length > 0)
                {
                    File.Copy(dialog, Path.Combine(wb, "Dialogs", dialogFile + ".json"));
                    res.Files.Add("WindBot/Dialogs/" + dialogFile + ".json");
                }

                var entry = new
                {
                    name = bot.Name,
                    deck = engine,
                    difficulty = bot.Difficulty,
                    masterRules = bot.MasterRules ?? new List<int>(),
                };
                WriteText(Path.Combine(temp, "bots-entry.json"), JsonSerializer.Serialize(entry, JsonOpts));
                WriteText(Path.Combine(temp, "install.ps1"), InstallScript(bot.Name, engine, dialogFile));
                WriteText(Path.Combine(temp, "설치방법.txt"), Readme(bot.Name, engine, dialogFile));
                res.Files.Add("bots-entry.json");
                res.Files.Add("install.ps1");
                res.Files.Add("설치방법.txt");

                // 설치.exe (선택) — 있으면 패키지에 포함한다(더블클릭 설치, PowerShell 불필요)
                var installer = FindInstallerExe();
                if (installer != null)
                {
                    File.Copy(installer, Path.Combine(temp, "설치.exe"));
                    res.Files.Add("설치.exe");
                }

                var dir = Path.GetDirectoryName(Path.GetFullPath(zipPath));
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                if (File.Exists(zipPath)) File.Delete(zipPath);
                ZipFile.CreateFromDirectory(temp, zipPath, CompressionLevel.Optimal, false);
                return res;
            }
            catch (Exception ex)
            {
                return Fail(ex.Message);
            }
            finally
            {
                try { if (Directory.Exists(temp)) Directory.Delete(temp, true); } catch { }
            }
        }

        private static Result Fail(string msg) => new() { Error = msg };

        /// <summary>
        /// 내보내기 전 경고 — DLL 이 **현재 규칙 파일 내용**으로 빌드된 것인지 확인한다.
        /// (파일 수정시각 비교는 규칙을 '저장만' 해도 오탐이 나서, 빌드 스탬프의 해시와 비교한다)
        /// </summary>
        public static string StaleWarning(WindBotProject project, BotEntry bot)
        {
            try
            {
                var cls = RuleCodeGen.ClassName(bot.Deck);
                var workDir = Path.Combine(project.ExecutorsDir, "_src");
                var stampPath = Path.Combine(workDir, cls + ".build.json");
                var rulesPath = Path.Combine(workDir, cls + ".rules.json");
                var dllPath = Path.Combine(project.ExecutorsDir, cls + ".dll");
                if (!File.Exists(dllPath) || !File.Exists(rulesPath)) return "";
                if (!File.Exists(stampPath))
                    return "실행기 DLL 의 빌드 기록이 없습니다(구버전). [생성 + 컴파일] 후 다시 내보내세요.";
                var stamp = File.ReadAllText(stampPath, Encoding.UTF8);
                var m = System.Text.RegularExpressions.Regex.Match(stamp, "\"rulesSha256\"\\s*:\\s*\"([0-9A-Fa-f]*)\"");
                var stamped = m.Success ? m.Groups[1].Value : "";
                var current = PluginBuilder.Sha256File(rulesPath);
                if (stamped.Length == 0 || !string.Equals(stamped, current, StringComparison.OrdinalIgnoreCase))
                    return "규칙 파일이 실행기 DLL 보다 나중에 변경되었습니다. [생성 + 컴파일] 후 다시 내보내세요.";
            }
            catch { }
            return "";
        }

        private static string ReadDialogFile(WindBotProject project, string engine)
        {
            try
            {
                var rules = Path.Combine(project.ExecutorsDir, "_src", RuleCodeGen.ClassName(engine) + ".rules.json");
                if (!File.Exists(rules)) return "";
                using var doc = JsonDocument.Parse(File.ReadAllText(rules));
                if (doc.RootElement.TryGetProperty("dialogFile", out var d)) return d.GetString() ?? "";
            }
            catch { }
            return "";
        }

        public static string SafeFileName(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder();
            foreach (var ch in name) sb.Append(invalid.Contains(ch) ? '_' : ch);
            var s = sb.ToString().Trim();
            return s.Length == 0 ? "bot" : s;
        }

        private static void WriteText(string path, string text) =>
            File.WriteAllText(path, text, new UTF8Encoding(false));

        /// <summary>
        /// install.ps1 생성.
        ///  · 파일 복사 + bots.json **원본 보존 삽입(S1)** — 마지막 ']' 앞에 한 항목만 끼워 넣는다(전체 재작성 금지)
        ///  · 삽입 전 구조 검사 → .bak 백업 → 삽입 → 재검사 → 실패하면 원본 복구 + 수기 안내
        ///  · 근거: D:\rea-out\windbot-ai-editor\19-bots-json-merge-verification.md
        /// </summary>
        private static string InstallScript(string botName, string engine, string dialogFile)
        {
            var dialogLine = dialogFile.Length > 0
                ? "Write-Host '대사 파일: WindBot\\Dialogs\\" + dialogFile + ".json'"
                : "";
            var deckRx = "'\"deck\"\\s*:\\s*\"" + engine + "\"'";
            return string.Join("\r\n", new[]
            {
                "# " + botName + " 설치 스크립트 (WindBot AI Editor 로 생성)",
                "# 사용법: 이 ZIP 을 아무 곳에나 풀고, install.ps1 우클릭 → [PowerShell에서 실행]",
                "#  · 파일 복사 + bots.json 원본 보존 삽입(마지막 ] 앞에 한 줄) + 실패 시 원본 복구",
                "#  · 실행 정책 오류가 나면: Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass",
                "$ErrorActionPreference = 'Stop'",
                "$here = Split-Path -Parent $MyInvocation.MyCommand.Path",
                "",
                "$edopro = Read-Host 'EDOPro 폴더 경로를 입력하세요 (EDOPro.exe 가 있는 폴더, 예: D:\\edopro)'",
                "$edopro = $edopro.Trim('\"').Trim()",
                "if (-not (Test-Path (Join-Path $edopro 'EDOPro.exe'))) { Write-Host 'EDOPro.exe 를 찾을 수 없습니다.' -ForegroundColor Red; Read-Host; exit 1 }",
                "$wb = Join-Path $edopro 'WindBot'",
                "if (-not (Test-Path $wb)) { Write-Host 'WindBot 폴더가 없습니다.' -ForegroundColor Red; Read-Host; exit 1 }",
                "",
                "# 1) 파일 복사 (실행기/덱/대사)",
                "Copy-Item (Join-Path $here 'WindBot\\*') $wb -Recurse -Force",
                "Write-Host ('파일 복사 완료: ' + $wb) -ForegroundColor Green",
                "",
                "# 2) bots.json — 원본 보존 삽입 (파일 전체를 다시 쓰지 않는다)",
                "$botsPath = Join-Path $wb 'bots.json'",
                "$entryPath = Join-Path $here 'bots-entry.json'",
                "$enc = New-Object System.Text.UTF8Encoding($false)",
                "$done = $false",
                "if ((Test-Path $botsPath) -and (Test-Path $entryPath)) {",
                "    $t = [System.IO.File]::ReadAllText($botsPath, $enc)",
                "    if ($t -match " + deckRx + ") {",
                "        Write-Host 'bots.json: 이미 항목이 있습니다 → 수정하지 않았습니다.' -ForegroundColor Yellow",
                "        $done = $true",
                "    } elseif ((-not $t.TrimStart().StartsWith('[')) -or (-not $t.TrimEnd().EndsWith(']'))) {",
                "        Write-Host 'bots.json 구조가 예상과 달라 자동 수정하지 않았습니다(원본 유지).' -ForegroundColor Yellow",
                "    } else {",
                "        $entry = (([System.IO.File]::ReadAllText($entryPath, $enc)) -replace '\\s+', ' ').Trim()",
                "        $close = $t.LastIndexOf(']')",
                "        $head = $t.Substring(0, $close).TrimEnd()",
                "        $tail = $t.Substring($close)",
                "        $trim = $head",
                "        $comma = ''",
                "        if (($trim.Length -gt 0) -and ($trim[$trim.Length - 1] -ne '[')) { $comma = ',' }",
                "        $ins = $comma + [char]13 + [char]10 + '  ' + $entry + [char]13 + [char]10",
                "        $bak = $botsPath + '.bak-' + (Get-Date -Format 'yyyyMMdd-HHmmss')",
                "        Copy-Item $botsPath $bak -Force",
                "        [System.IO.File]::WriteAllText($botsPath, ($head + $ins + $tail), $enc)",
                "        $chk = [System.IO.File]::ReadAllText($botsPath, $enc)",
                "        if (($chk -match " + deckRx + ") -and $chk.TrimEnd().EndsWith(']')) {",
                "            Write-Host ('bots.json: 항목 추가 완료 (백업: ' + (Split-Path -Leaf $bak) + ')') -ForegroundColor Green",
                "            $done = $true",
                "        } else {",
                "            Copy-Item $bak $botsPath -Force",
                "            Write-Host '추가 후 검사에 실패해 원본으로 되돌렸습니다. 설치방법.txt [방법 A] 를 따라 주세요.' -ForegroundColor Red",
                "        }",
                "    }",
                "}",
                "if (-not $done) {",
                "    $manual = (([System.IO.File]::ReadAllText($entryPath, $enc)) -replace '\\s+', ' ').Trim()",
                "    try { Set-Clipboard -Value $manual } catch { }",
                "    Write-Host ''",
                "    Write-Host '▶ 아래 한 줄을 bots.json 의 마지막 ] 앞에 직접 붙여넣으세요(클립보드에 복사됨):' -ForegroundColor Cyan",
                "    Write-Host ('   ' + $manual)",
                "}",
                "",
                "Write-Host ''",
                "Write-Host '설치 완료. EDOPro 와 WindBot 을 완전히 종료했다가 다시 실행하세요.' -ForegroundColor Cyan",
                dialogLine,
                "Read-Host 'Enter 키를 누르면 종료'",
            });
        }

        /// <summary>
        /// 패키지에 넣을 설치.exe 를 찾는다(없으면 null → exe 없이 6파일 패키지).
        /// </summary>
        private static string? FindInstallerExe()
        {
            var names = new[] { "설치.exe", "BotInstaller.exe", "install.exe" };
            var bases = new List<string> { AppContext.BaseDirectory };
            var up = AppContext.BaseDirectory;
            for (int i = 0; i < 6 && !string.IsNullOrEmpty(up); i++)
            {
                up = Path.GetDirectoryName(up.TrimEnd(Path.DirectorySeparatorChar)) ?? "";
                if (!string.IsNullOrEmpty(up)) bases.Add(up);
            }
            foreach (var b in bases)
            {
                foreach (var n in names)
                {
                    var p1 = Path.Combine(b, "install-assets", n);
                    if (File.Exists(p1)) return p1;
                    var p2 = Path.Combine(b, n);
                    if (File.Exists(p2)) return p2;
                }
            }
            return null;
        }

        private static string Readme(string botName, string engine, string dialogFile)
        {
            var cls = RuleCodeGen.ClassName(engine);
            var dlg = dialogFile.Length > 0 ? "WindBot\\Dialogs\\" + dialogFile + ".json" : "(대사 파일 없음)";
            var entryOneLine = "{ \"name\": \"" + botName + "\", \"deck\": \"" + engine + "\", \"difficulty\": 3, \"masterRules\": [ 5 ] }";
            var lines = new List<string>
            {
                "[" + botName + "] 봇 배포 패키지 — 설치 안내 (초보자용)",
                "================================================================",
                "",
                "이 패키지를 설치하면 EDOPro 로비의 AI 목록에 \"" + botName + "\" 이 추가됩니다.",
                "설치 방법은 3가지입니다. 위에서부터 편한 것을 고르세요.",
                "  A. 설치.exe 더블클릭 (가장 쉬움 · 권장)",
                "  B. install.ps1 실행 (PowerShell 자동 설치)",
                "  C. 손으로 복사 + bots.json 한 줄 추가 (가장 확실)",
                "",
                "■ 이 ZIP 안에 들어있는 것",
                "  설치.exe                            ← (있으면) 더블클릭 설치 프로그램",
                "  WindBot\\Executors\\" + cls + ".dll      ← AI 두뇌(실행기)",
                "  WindBot\\Decks\\AI_" + engine + ".ydk" + new string(' ', Math.Max(1, 26 - ("AI_" + engine + ".ydk").Length)) + "← 봇이 쓰는 덱",
                "  " + dlg.PadRight(40) + "← 봇 대사(선택)",
                "  bots-entry.json                     ← bots.json 에 넣을 한 줄",
                "  install.ps1                         ← 자동 설치 스크립트(선택)",
                "  설치방법.txt                        ← 지금 읽고 있는 이 파일",
                "",
                "■ 준비: EDOPro 폴더 확인",
                "  EDOPro.exe 가 있는 폴더를 찾으세요. 보통 D:\\edopro 입니다.",
                "  그 안에 'WindBot' 폴더가 반드시 있어야 합니다. (없으면 WindBot 이 설치되지 않은 것)",
                "",
                "",
                "================ 방법 A. 설치.exe (가장 쉬움) ================",
                "",
                "  A-1. ZIP 을 풉니다 (마우스 오른쪽 → [압축 풀기]).",
                "  A-2. 설치.exe 를 더블클릭합니다.",
                "  A-3. [폴더 선택…] 을 눌러 EDOPro 폴더(EDOPro.exe 가 있는 폴더)를 고릅니다.",
                "  A-4. [설치] 를 누릅니다.",
                "       - 실행기·덱·대사 파일이 복사됩니다.",
                "       - bots.json 에 항목이 자동으로 추가됩니다(원본 서식 유지 · 자동 백업 · 실패 시 원복).",
                "  A-5. '재시작할까요?' → [예] 를 누르면 EDOPro 가 다시 켜집니다.",
                "       (진행 중인 듀얼이 있으면 그 판은 사라지니, 듀얼 중이면 [아니오] 후 직접 재시작하세요.)",
                "  A-6. 로비 AI 목록에서 \"" + botName + "\" 을 확인합니다.",
                "",
                "  ▶ 'Windows의 PC 보호' 경고가 뜨면 [추가 정보] → [실행] 을 누르세요.",
                "    (서명되지 않은 프로그램이라 나오는 경고입니다.)",
                "",
                "",
                "================ 방법 B. install.ps1 (PowerShell) ================",
                "",
                "  B-1. install.ps1 파일을 마우스 오른쪽 → [PowerShell에서 실행]",
                "       (Windows 11 은 [추가 옵션 표시] → [PowerShell에서 실행])",
                "  B-2. EDOPro 폴더 경로를 입력합니다. 예) D:\\edopro",
                "  B-3. 자동으로 복사 + bots.json 항목 추가(원본 보존 삽입 · .bak 백업)가 됩니다.",
                "  B-4. EDOPro 재시작 → 로비에서 확인",
                "",
                "  ▶ '스크립트를 실행할 수 없습니다'(실행 정책) 오류가 나면:",
                "     시작 → PowerShell 을 열고 아래를 한 줄 입력 후 Enter, 그다음 B-1 재시도",
                "       Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass",
                "",
                "  ▶ 자동 추가가 실패하면 화면에 붙여넣을 한 줄이 표시됩니다(클립보드 복사됨)",
                "     → 그 줄을 bots.json 의 마지막 ] 앞에 붙여넣으면 됩니다(아래 방법 C-3).",
                "",
                "",
                "================ 방법 C. 손으로 설치 (가장 확실) ================",
                "",
                "C-1. 파일 3개 복사",
                "     풀린 폴더 안의 WindBot 폴더를 통째로  <EDOPro>\\WindBot  위에 끌어다 놓습니다.",
                "     → '덮어쓰시겠습니까?' 가 나오면 [예].",
                "        <EDOPro>\\WindBot\\Executors\\" + cls + ".dll",
                "        <EDOPro>\\WindBot\\Decks\\AI_" + engine + ".ydk",
                "        <EDOPro>\\WindBot\\Dialogs\\" + (dialogFile.Length > 0 ? dialogFile + ".json" : "(없음)"),
                "",
                "C-2. bots.json 열기",
                "     (1) 메모장을 엽니다.",
                "     (2) <EDOPro>\\WindBot\\bots.json 파일을 메모장 창으로 끌어다 놓습니다.",
                "         - 파일이 안 보이면: 폴더 창 위쪽 [보기] → [파일 확장명] 체크",
                "         - '열기' 창에서 열려면 파일 형식을 [모든 파일] 로 바꿔야 합니다.",
                "     (3) 파일의 맨 마지막 부분을 봅니다. 대괄호  ]  하나로 끝나 있습니다.",
                "",
                "C-3. 한 줄 추가  ★가장 중요한 단계★",
                "     (1) 아래 한 줄을 복사합니다(bots-entry.json 과 같은 내용).",
                "",
                "         " + entryOneLine,
                "",
                "     (2)  ]  바로 '앞'에 붙여넣습니다.",
                "         - 앞 항목이 이미 있으면  }  와 새 항목 사이에  ,  (쉼표)를 꼭 넣습니다.",
                "         - 예시(맨 끝 부분):",
                "              { \"name\": \"ABC\", \"deck\": \"ABC\", \"difficulty\": 3, \"masterRules\": [ 5 ] },",
                "              " + entryOneLine,
                "           ]",
                "         -  ]  뒤에는 아무것도 넣지 않습니다.",
                "     (3) 저장: [파일] → [저장] (Ctrl+S). 인코딩을 물으면 UTF-8 을 고르세요.",
                "",
                "     ※ 이미 \"" + botName + "\" 항목이 있으면 아무것도 추가하지 마세요(중복 금지).",
                "     ※ 확실하지 않으면 원본을 먼저 복사해 두세요: bots.json → bots.json.backup",
                "",
                "C-4. EDOPro 를 완전히 종료(창 닫기)한 뒤 다시 실행합니다.",
                "C-5. 로비 AI 목록에 \"" + botName + "\" 이 보이면 성공입니다.",
                "     듀얼을 시작하면 검은 콘솔 창에  Deck found, loading AI_" + engine + "  가 뜹니다.",
                "",
                "",
                "================ 업데이트 (새 버전으로 바꾸기) ================",
                "",
                "  ▶ 이미 설치한 적이 있으면 **bots.json 은 다시 건드리지 않습니다.**",
                "    한 번 넣은 항목은 그대로 두고, **파일만 최신으로** 바꾸면 됩니다.",
                "",
                "  U-1. 새 ZIP 을 아무 곳에나 풉니다.",
                "  U-2. 풀린 폴더의 **WindBot 폴더만**  <EDOPro>\\WindBot  위에 덮어씁니다.",
                "         - WindBot\\Executors\\" + cls + ".dll   (AI 두뇌 교체)",
                "         - WindBot\\Decks\\AI_" + engine + ".ydk  (덱 갱신)",
                "         - WindBot\\Dialogs\\" + (dialogFile.Length > 0 ? dialogFile + ".json" : "(없음)") + "  (대사 갱신)",
                "  U-3. EDOPro 를 **완전히 종료**했다가 다시 실행합니다.",
                "       (WindBot 은 시작할 때 실행기·덱을 한 번만 읽습니다. 재시작 없이는 옛 DLL 그대로입니다.)",
                "  U-4. 듀얼을 시작해 콘솔에  Deck found, loading AI_" + engine + "  이 뜨는지 확인합니다.",
                "",
                "  ※ 설치.exe / install.ps1 을 다시 실행해도 **안전**합니다.",
                "     이미 항목이 있으면 '이미 항목 있음 → 건드리지 않음' 이라고만 표시됩니다.",
                "  ※ Android 도 동일: 파일 관리자로 WindBot 폴더의 위 3개 파일만 덮어쓰면 됩니다.",
                "  ※ 봇 이름(" + botName + ")이나 덱 이름(" + engine + ")을 바꾼 경우에만 bots.json 항목도 고쳐야 합니다.",
                "",
                "",
                "================ 안드로이드(Android) EDOPro ================",
                "",
                "  ※ 안드로이드도 **같은 파일**을 씁니다. (설치.exe / install.ps1 은 Windows 전용이므로 쓰지 않습니다.)",
                "",
                "  D-1. 파일 관리자 앱으로 EDOPro 의 WindBot 폴더를 찾습니다.",
                "       - 기준: **bots.json 이 들어 있는 폴더** (보통 Android/data/<EDOPro 패키지>/files/WindBot)",
                "       - Android 11 이상은 PC와 USB로 연결해 접근하는 편이 쉽습니다.",
                "  D-2. ZIP 안의 파일을 같은 구조로 복사합니다.",
                "         <WindBot>\\Executors\\" + cls + ".dll",
                "         <WindBot>\\Decks\\AI_" + engine + ".ydk",
                "         <WindBot>\\Dialogs\\" + (dialogFile.Length > 0 ? dialogFile + ".json" : "(없음)"),
                "  D-3. bots.json 에 한 줄 추가 (방법 C-3 과 동일).",
                "       - 안드로이드 텍스트 편집기로 열고, 마지막 ] 앞에 붙여넣고 저장(UTF-8).",
                "  D-4. EDOPro 앱을 **완전히 종료**했다가 다시 실행합니다.",
                "  D-5. 로비 AI 목록에서 \"" + botName + "\" 을 선택합니다.",
                "",
                "  ▶ 대사(툴팁/대사)가 안 나오면: Dialogs 폴더에 대사 json 이 있는지 확인하세요.",
                "     실행기 DLL 과 같은 WindBot 폴더에 Dialogs 가 있어야 합니다.",
                "",
                "",
                "================ 설치가 안 될 때 체크리스트 ================",
                "",
                "  1) 로비 AI 목록에 안 보인다",
                "     - bots.json 에 \"" + botName + "\" 이 있는지 (따옴표/쉼표 확인)",
                "     - 저장 후 EDOPro 를 완전히 껐다 켰는지",
                "     - 그래도 안 되면  <EDOPro>\\error.log  끝부분에",
                "       'Failed to load WindBot Ignite config json' 이 있는지 확인",
                "       → 있으면 bots.json 문법이 깨진 것입니다. 백업(.bak-날짜)으로 되돌리고 C-3 을 다시 하세요.",
                "",
                "  2) 목록에는 보이는데 덱이 랜덤으로 나온다",
                "     - WindBot 콘솔 메시지를 보세요.",
                "       'Deck not found, loading random: " + engine + "'  → 덱 파일이 없거나 이름이 다름",
                "         → <EDOPro>\\WindBot\\Decks\\AI_" + engine + ".ydk  가 있는지 확인(대소문자 포함)",
                "       'Failed to load deck'                          → 파일은 있으나 읽기 실패(잠금/권한)",
                "     - 실행기 DLL 이 <EDOPro>\\WindBot\\Executors\\ 에 있는지 확인",
                "",
                "  3) 봇이 예전 규칙대로 움직인다",
                "     - WindBot 은 시작할 때 실행기/덱을 한 번만 읽습니다. EDOPro 를 완전히 재시작하세요.",
                "",
                "  4) 대사가 나오지 않는다",
                "     - 대사 파일(" + (dialogFile.Length > 0 ? dialogFile + ".json" : "없음") + ")이 Dialogs 폴더에 있는지 확인하세요.",
                "",
                "",
                "================ 주의사항 ================",
                "",
                "  * 봇 이름(" + botName + ")과 덱 이름(" + engine + ")을 바꾸지 마세요.",
                "    실행기 DLL 안에 덱 이름이 들어 있어서, 이름을 바꾸면 덱을 못 찾습니다.",
                "  * bots.json 을 편집할 때 메모장의 '자동 서식' 기능은 쓰지 마세요.",
                "  * 여러 봇을 넣을 때는 항목 사이에 쉼표(,)만 정확히 넣으면 됩니다.",
                "  * masterRules 5 = 현재 마스터룰(MR2020) 입니다. 그대로 두세요.",
                "",
                "문의/버그: 듀얼 로그(%LOCALAPPDATA%\\WindBotAIEditor\\duel-log.txt)와",
                "           <EDOPro>\\error.log 를 함께 보내주시면 빠르게 확인할 수 있습니다.",
            };
            // txt 이므로 마크다운 강조(**)는 제거한다
            return string.Join("\r\n", lines).Replace("**", "");
        }
    }
}



