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
        /// install.ps1 생성 — 템플릿(install-assets\install-template.ps1)을 읽어 치환한다.
        /// 봇 항목(bots.json)은 **자동 수정하지 않고** 붙여넣기만 안내한다(EDOPro 파서가 깨진 사례).
        /// </summary>
        private static string InstallScript(string botName, string engine, string dialogFile)
        {
            var dialogLine = dialogFile.Length > 0
                ? "Write-Host '대사 파일: WindBot\\Dialogs\\" + dialogFile + ".json'"
                : "";
            return string.Join("\r\n", new[]
            {
                "# " + botName + " 설치 스크립트 (WindBot AI Editor 로 생성)",
                "# 사용법: 이 ZIP 을 아무 곳에나 풀고, install.ps1 우클릭 → [PowerShell에서 실행]",
                "$ErrorActionPreference = 'Stop'",
                "$here = Split-Path -Parent $MyInvocation.MyCommand.Path",
                "",
                "$edopro = Read-Host 'EDOPro 폴더 경로를 입력하세요 (EDOPro.exe 가 있는 폴더, 예: D:\\edopro)'",
                "$edopro = $edopro.Trim('\"')",
                "if (-not (Test-Path (Join-Path $edopro 'EDOPro.exe'))) { Write-Host 'EDOPro.exe 를 찾을 수 없습니다.' -ForegroundColor Red; Read-Host; exit 1 }",
                "$wb = Join-Path $edopro 'WindBot'",
                "if (-not (Test-Path $wb)) { Write-Host 'WindBot 폴더가 없습니다.' -ForegroundColor Red; Read-Host; exit 1 }",
                "",
                "# 1) 파일 복사",
                "Copy-Item (Join-Path $here 'WindBot\\*') $wb -Recurse -Force",
                "Write-Host ('파일 복사 완료: ' + $wb) -ForegroundColor Green",
                "",
                "# 2) bots.json 병합 (백업 후)",
                "$botsPath = Join-Path $wb 'bots.json'",
                "if (Test-Path $botsPath) { Copy-Item $botsPath ($botsPath + '.bak') -Force }",
                "$entry = Get-Content (Join-Path $here 'bots-entry.json') -Raw | ConvertFrom-Json",
                "$list = New-Object System.Collections.ArrayList",
                "if (Test-Path $botsPath) {",
                "    $raw = Get-Content $botsPath -Raw",
                "    if ($raw -ne $null -and $raw.Trim().Length -gt 2 -and $raw.Trim() -ne '[]') {",
                "        foreach ($b in ($raw | ConvertFrom-Json)) {",
                "            if ($null -eq $b) { continue }",
                "            if ($b.PSObject.Properties.Name -contains 'name' -and $b.name -eq $entry.name) { continue }",
                "            [void]$list.Add($b)",
                "        }",
                "    }",
                "}",
                "[void]$list.Add($entry)",
                "$json = ConvertTo-Json -InputObject $list.ToArray() -Depth 8",
                "if (-not $json.TrimStart().StartsWith('[')) { $json = '[' + [char]13 + [char]10 + $json + [char]13 + [char]10 + ']' }",
                "[System.IO.File]::WriteAllText($botsPath, $json, (New-Object System.Text.UTF8Encoding($false)))",
                "Write-Host 'bots.json 병합 완료 (백업: bots.json.bak)' -ForegroundColor Green",
                "",
                "Write-Host ''",
                "Write-Host '설치 완료. EDOPro 와 WindBot 을 완전히 종료했다가 다시 실행하세요.' -ForegroundColor Cyan",
                dialogLine,
                "Read-Host 'Enter 키를 누르면 종료'",
            });
        }

        private static string Readme(string botName, string engine, string dialogFile)
        {
            var cls = RuleCodeGen.ClassName(engine);
            var lines = new List<string>
            {
                "[" + botName + "] 봇 배포 패키지",
                "================================================",
                "",
                "필요한 것: EDOPro (WindBot 폴더 포함) 가 설치된 PC",
                "",
                "■ 방법 1 — 자동 설치 (권장)",
                "  1) 이 ZIP 을 아무 곳에나 풉니다",
                "  2) install.ps1 우클릭 → [PowerShell에서 실행]",
                "  3) EDOPro 폴더 경로 입력 (예: D:\\edopro)",
                "  4) EDOPro / WindBot 재시작 → 로비 AI 목록에서 \"" + botName + "\" 선택",
                "",
                "■ 방법 2 — 수동 설치",
                "  1) ZIP 안의 WindBot 폴더를 <EDOPro>\\WindBot 에 덮어쓰기",
                "     - WindBot\\Executors\\" + cls + ".dll",
                "     - WindBot\\Decks\\AI_" + engine + ".ydk",
                (dialogFile.Length > 0 ? "     - WindBot\\Dialogs\\" + dialogFile + ".json" : "     - (대사 파일 없음)"),
                "  2) bots-entry.json 내용을 <EDOPro>\\WindBot\\bots.json 배열에 추가",
                "  3) EDOPro / WindBot 재시작",
                "",
                "■ 확인",
                "  - 로비 AI 목록에 \"" + botName + "\" 이 보이면 성공",
                "  - WindBot 콘솔: 'Deck found, loading AI_" + engine + "'",
                "",
                "※ 봇 이름/덱 이름을 바꾸지 마세요. 실행기 DLL 안에 덱 이름이 들어 있습니다.",
            };
            return string.Join("\r\n", lines);
        }
    }
}



