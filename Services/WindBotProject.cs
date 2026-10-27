using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using WindBotAIEditor.Models;

namespace WindBotAIEditor.Services
{
    /// <summary>
    /// WindBot 설치 폴더를 다룬다.
    /// 확인된 실제 구조:
    ///   WindBot/bots.json          → EDOPro 로비 AI 리스트 (name/deck/difficulty/masterRules)
    ///   WindBot/Decks/AI_*.ydk     → 덱 레시피
    ///   WindBot/Executors/*.dll    → 커스텀 AI 엔진 (Executor 파생 클래스)
    ///   WindBot/Dialogs/*.json     → 대사
    /// </summary>
    public class WindBotProject
    {
        public string Root { get; }

        public WindBotProject(string root) => Root = root;

        public string BotsJson => Path.Combine(Root, "bots.json");
        public string DecksDir => Path.Combine(Root, "Decks");
        public string ExecutorsDir => Path.Combine(Root, "Executors");
        public string DialogsDir => Path.Combine(Root, "Dialogs");
        public string WindBotExe => Path.Combine(Root, "WindBot.exe");
        public string ExecutorBaseDll => Path.Combine(Root, "ExecutorBase.dll");
        public string YgoSharpOcgDll => Path.Combine(Root, "YGOSharp.OCGWrapper.dll");
        public string YgoSharpNetDll => Path.Combine(Root, "YGOSharp.Network.dll");

        public static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        // ---------- bots.json ----------

        public List<BotEntry> LoadBots()
        {
            if (!File.Exists(BotsJson))
                throw new FileNotFoundException("bots.json 을 찾을 수 없습니다.", BotsJson);
            var text = File.ReadAllText(BotsJson, Encoding.UTF8);
            return JsonSerializer.Deserialize<List<BotEntry>>(text, JsonOpts) ?? new List<BotEntry>();
        }

        public void SaveBots(IEnumerable<BotEntry> bots)
        {
            var list = bots.ToList();
            var json = JsonSerializer.Serialize(list, JsonOpts);
            // 원본과 동일한 개행 스타일 유지 (CRLF)
            File.WriteAllText(BotsJson, json.Replace("\n", "\r\n"), new UTF8Encoding(false));
        }

        // ---------- Decks ----------

        public string[] ListDecks()
        {
            if (!Directory.Exists(DecksDir)) return Array.Empty<string>();
            return Directory.GetFiles(DecksDir, "*.ydk")
                .Select(Path.GetFileName)
                .Where(n => n != null)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToArray()!;
        }

        /// <summary>"ABC" → AI_ABC.ydk 경로</summary>
        public string DeckPath(string deckName)
        {
            var name = deckName.EndsWith(".ydk", StringComparison.OrdinalIgnoreCase)
                ? deckName
                : (deckName.StartsWith("AI_", StringComparison.OrdinalIgnoreCase) ? deckName : "AI_" + deckName) + ".ydk";
            return Path.Combine(DecksDir, name);
        }

        public string ReadDeck(string deckName) => File.ReadAllText(DeckPath(deckName), Encoding.UTF8);

        public void WriteDeck(string deckName, string content)
        {
            Directory.CreateDirectory(DecksDir);
            File.WriteAllText(DeckPath(deckName), content.Replace("\r\n", "\n").Replace("\n", "\r\n"), new UTF8Encoding(false));
        }

        /// <summary>
        /// ydk 섹션 마커 판정. 실제 ydk 는 "#main / #extra / !side" 이다.
        /// (예전 코드가 "#!side" 만 검사해서 사이드 덱이 엑스트라로 집계되던 버그가 있었다)
        /// </summary>
        public static string? SectionOf(string line)
        {
            var t = (line ?? "").Trim();
            if (t.Length == 0) return null;
            var l = t.ToLowerInvariant();
            if (l.StartsWith("#main")) return "main";
            if (l.StartsWith("#extra")) return "extra";
            if (l.StartsWith("!side") || l.StartsWith("#!side") || l.StartsWith("#side")) return "side";
            return null;
        }

        /// <summary>카드 ID 가 아닌 주석/마커 줄인지</summary>
        public static bool IsCommentOrMarker(string line)
        {
            var t = (line ?? "").Trim();
            if (t.Length == 0) return true;
            return t.StartsWith("#") || t.StartsWith("!");
        }

        /// <summary>ydk 문자열에서 카드 ID 개수(중복 포함) 세기</summary>
        public static (int main, int extra, int side) CountDeck(string content)
        {
            int main = 0, extra = 0, side = 0;
            var section = "main";
            foreach (var raw in content.Replace("\r\n", "\n").Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0) continue;
                var sec = SectionOf(line);
                if (sec != null) { section = sec; continue; }
                if (IsCommentOrMarker(line)) continue;
                if (!int.TryParse(line, out _)) continue;
                if (section == "extra") extra++;
                else if (section == "side") side++;
                else main++;
            }
            return (main, extra, side);
        }

        // ---------- Executors ----------

        public string[] ListExecutors()
        {
            if (!Directory.Exists(ExecutorsDir)) return Array.Empty<string>();
            return Directory.GetFiles(ExecutorsDir, "*.dll")
                .Select(Path.GetFileName)
                .Where(n => n != null)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToArray()!;
        }

        public void EnsureExecutorsDir() => Directory.CreateDirectory(ExecutorsDir);

        // ---------- Dialogs ----------

        public string[] ListDialogs()
        {
            if (!Directory.Exists(DialogsDir)) return Array.Empty<string>();
            return Directory.GetFiles(DialogsDir, "*.json")
                .Select(Path.GetFileName)
                .Where(n => n != null)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToArray()!;
        }

        // ---------- 검증 ----------

        public class Issue
        {
            public string Level { get; set; } = "info";   // error | warn | info | ok
            public string Message { get; set; } = "";
            public override string ToString() => $"[{Level}] {Message}";
        }

        public List<Issue> Validate()
        {
            var issues = new List<Issue>();

            if (!File.Exists(WindBotExe))
                issues.Add(new Issue { Level = "error", Message = $"WindBot.exe 없음: {WindBotExe}" });
            else
                issues.Add(new Issue { Level = "ok", Message = "WindBot.exe 확인" });

            if (!File.Exists(ExecutorBaseDll))
                issues.Add(new Issue { Level = "error", Message = "ExecutorBase.dll 없음 — 커스텀 엔진 컴파일에 필요" });
            else
                issues.Add(new Issue { Level = "ok", Message = "ExecutorBase.dll 확인" });

            List<BotEntry> bots;
            try
            {
                bots = LoadBots();
                issues.Add(new Issue { Level = "ok", Message = $"bots.json 로드: {bots.Count}개 항목" });
            }
            catch (Exception ex)
            {
                issues.Add(new Issue { Level = "error", Message = "bots.json 로드 실패: " + ex.Message });
                return issues;
            }

            var executors = ListExecutors();
            issues.Add(new Issue
            {
                Level = executors.Length > 0 ? "ok" : "warn",
                Message = $"Executors: {executors.Length}개" + (executors.Length > 0 ? " → " + string.Join(", ", executors) : " (없음 — 기본 51개 엔진만 동작)")
            });

            foreach (var b in bots)
            {
                if (string.IsNullOrWhiteSpace(b.Name))
                    issues.Add(new Issue { Level = "warn", Message = $"이름이 빈 항목 (deck={b.Deck})" });

                var deckPath = DeckPath(b.Deck);
                if (!File.Exists(deckPath))
                    issues.Add(new Issue { Level = "error", Message = $"'{b.Name}' 덱 파일 없음: {Path.GetFileName(deckPath)}" });
            }

            var deckFiles = ListDecks();
            issues.Add(new Issue { Level = "info", Message = $"Decks: {deckFiles.Length}개 파일" });

            // 커스텀 엔진(Executors/*.dll) ↔ bots.json deck 이름 연결 검사
            // WindBot 은 "<deck>Executor" 타입을 찾아 덱을 구성하므로 두 이름이 일치해야 한다.
            foreach (var dll in executors)
            {
                var baseName = Path.GetFileNameWithoutExtension(dll);
                if (baseName == null) continue;
                if (baseName.EndsWith("Executor", StringComparison.OrdinalIgnoreCase))
                    baseName = baseName[..^"Executor".Length];

                if (!bots.Any(b => string.Equals(b.Deck, baseName, StringComparison.OrdinalIgnoreCase)))
                    issues.Add(new Issue
                    {
                        Level = "warn",
                        Message = $"엔진 '{dll}' 에 대응하는 bots.json 항목이 없습니다 (deck=\"{baseName}\" 필요)"
                    });
                else
                    issues.Add(new Issue { Level = "ok", Message = $"엔진 '{dll}' ↔ bot deck \"{baseName}\" 연결됨" });
            }


            // 봇이 참조하지 않는 덱
            var usedDecks = bots.Select(b => Path.GetFileName(DeckPath(b.Deck))).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var d in deckFiles.Where(d => !usedDecks.Contains(d)))
                issues.Add(new Issue { Level = "info", Message = $"봇 미등록 덱: {d}" });

            return issues;
        }
    }
}

