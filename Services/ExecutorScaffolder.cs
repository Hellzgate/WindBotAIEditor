using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using WindBotAIEditor.Models;

namespace WindBotAIEditor.Services
{
    /// <summary>
    /// 2026-11-02: WindBot 실행기(`.cs`)를 **우리 규칙 JSON**으로 스캐폴드한다.
    /// <para>· `AddExecutor(...)` → ① 행동 규칙(타입·카드·순서)</para>
    /// <para>· 커스텀 handler 본문 → `ActionRule.RawBody` (동작 보존)</para>
    /// <para>· `CardId`/필드/헬퍼/override → `AiProfile.RawMembers` (심볼 보존)</para>
    /// <para>· 단순 가드(`if (Duel.Player == 0) return false;` 등)는 조건 DSL로 옮기고 본문에서 제거</para>
    /// </summary>
    public static class ExecutorScaffolder
    {
        public sealed class Result
        {
            public AiProfile? Profile { get; set; }
            public List<string> Log { get; } = new List<string>();
            public bool Ok => Profile != null;
        }

        private static readonly string[] StandardUsings =
        {
            "System", "System.Collections.Generic", "WindBot.Game", "WindBot.Game.AI", "YGOSharp.OCGWrapper.Enums"
        };

        public static Result Scaffold(string sourcePath, string? globalIdFile = null)
        {
            var r = new Result();
            string text;
            try { text = File.ReadAllText(sourcePath); }
            catch (Exception ex) { r.Log.Add("읽기 실패: " + ex.Message); return r; }

            // ---- [Deck] ----
            var md = Regex.Match(text, "\\[Deck\\(\\s*\"(?<name>[^\"]+)\"\\s*,\\s*\"(?<file>[^\"]+)\"(?:\\s*,\\s*\"(?<level>[^\"]+)\")?\\s*\\)\\]");
            if (!md.Success) { r.Log.Add("[Deck] 속성을 찾지 못했습니다"); return r; }
            var deckName = md.Groups["name"].Value;
            var deckFile = md.Groups["file"].Value;

            var engine = deckFile.StartsWith("AI_", StringComparison.OrdinalIgnoreCase) ? deckFile.Substring(3) : deckName.Replace(" ", "");

            // ---- usings ----
            var nsIdx = text.IndexOf("namespace ", StringComparison.Ordinal);
            var header = nsIdx > 0 ? text.Substring(0, nsIdx) : text;
            var usings = new List<string>();
            foreach (Match m in Regex.Matches(header, @"^\s*using\s+([A-Za-z0-9_.]+)\s*;", RegexOptions.Multiline))
            {
                var u = m.Groups[1].Value;
                if (StandardUsings.Contains(u)) continue;
                if (!usings.Contains(u)) usings.Add(u);
            }

            // ---- 클래스 본문 ----
            // 2026-11-02 (B-70): **블록 주석 제거** — 주석 안의 AddExecutor/멤버를 실행 코드로 착각해
            //   존재하지 않는 심볼(주석 처리된 CardId 등)을 참조하는 사고를 막는다(줄 수는 보존).
            text = Regex.Replace(text, @"/\*.*?\*/", m => new string('\n', m.Value.Count(ch => ch == '\n')),
                                 RegexOptions.Singleline);

            var mcls = Regex.Match(text, @"(?:public\s+|internal\s+|private\s+)?class\s+(?<cls>\w+)\s*:\s*DefaultExecutor");
            if (!mcls.Success) { r.Log.Add("DefaultExecutor 상속 클래스를 찾지 못했습니다"); return r; }
            var cls = mcls.Groups["cls"].Value;
            var openBrace = text.IndexOf('{', mcls.Index + mcls.Length);
            var body = ExtractBraceBlock(text, openBrace);
            if (body == null) { r.Log.Add("클래스 본문 파싱 실패"); return r; }

            // ---- 카드 상수 (CardId + 기타 const) ----
            var ids = new Dictionary<string, int>();
            foreach (Match m in Regex.Matches(body, @"public\s+const\s+int\s+(?<n>\w+)\s*=\s*(?<v>\d+)\s*;"))
            {
                var n = m.Groups["n"].Value;
                if (!ids.ContainsKey(n)) ids[n] = int.Parse(m.Groups["v"].Value);
            }
            var globalIds = LoadGlobalIds(globalIdFile, r);

            // ---- 멤버 분해 ----
            var members = SplitMembers(body);
            string? ctorText = null;
            var handlerBodies = new Dictionary<string, string>();
            var rest = new List<string>();

            foreach (var mem in members)
            {
                var sig = SignatureOf(mem);
                if (Regex.IsMatch(sig, @"\bpublic\s+" + Regex.Escape(cls) + @"\s*\("))
                {
                    ctorText = mem;
                    continue;
                }
                var mm = Regex.Match(sig, @"\b(?:public|private|protected|internal)\s+(?:override\s+)?(?:static\s+)?(?:bool|void|int|ClientCard)\s+(?<name>\w+)\s*\(");
                if (mm.Success)
                {
                    var name = mm.Groups["name"].Value;
                    if (!handlerBodies.ContainsKey(name)) handlerBodies[name] = BodyOf(mem);
                    rest.Add(mem);
                    continue;
                }
                rest.Add(mem);
            }
            if (ctorText == null) { r.Log.Add("생성자(AddExecutor 등록부)를 찾지 못했습니다"); return r; }

            // ---- AddExecutor → 행동 규칙 ----
            var p = new AiProfile
            {
                Engine = engine,
                Description = "포팅 스캐폴드: " + Path.GetFileName(sourcePath),
                SchemaVersion = 13,
                ScaffoldSource = sourcePath,
                RawUsings = usings,
                DebugEffectLog = false,
                FreePlay = false,
                SameNameAliases = false   // 스캐폴드는 카드 DB 없이 생성 — 별칭 확장 끔
            };

            var usedHandlers = new HashSet<string>(StringComparer.Ordinal);
            var order = 0;
            var skipped = 0;

            foreach (Match m in Regex.Matches(ctorText, @"AddExecutor\s*\("))
            {
                var args = ReadArgs(ctorText, m.Index + m.Length - 1);
                if (args == null || args.Count == 0) continue;
                var t = args[0].Replace("ExecutorType.", "").Trim();
                if (t.Length == 0) continue;

                int card = 0;
                string? handler = null;
                var lambda = false;

                for (var i = 1; i < args.Count; i++)
                {
                    var a = args[i].Trim();
                    if (a.Length == 0) continue;
                    if (a.Contains("=>")) { lambda = true; continue; }
                    if (a.StartsWith("CardId.", StringComparison.Ordinal))
                    {
                        var sym = a.Substring("CardId.".Length);
                        if (ids.TryGetValue(sym, out var v)) card = v; else r.Log.Add("경고: CardId." + sym + " 를 찾지 못했습니다");
                    }
                    else if (a.StartsWith("_CardId.", StringComparison.Ordinal))
                    {
                        var sym = a.Substring("_CardId.".Length);
                        if (globalIds.TryGetValue(sym, out var v)) card = v; else r.Log.Add("경고: _CardId." + sym + " 를 찾지 못했습니다");
                    }
                    else if (int.TryParse(a, out var num)) card = num;
                    else handler = a;
                }

                string raw = "";
                if (handler != null)
                {
                    if (handlerBodies.TryGetValue(handler, out var hb))
                    {
                        usedHandlers.Add(handler);
                        raw = hb;
                    }
                    else if (handler.StartsWith("Default", StringComparison.Ordinal))
                    {
                        // 기반 클래스(DefaultExecutor)의 기본 핸들러 — 그대로 호출해 동작을 보존한다
                        raw = "return " + handler + "();";
                    }
                    else
                    {
                        r.Log.Add("경고: 핸들러 본문을 찾지 못했습니다: " + handler);
                    }
                }

                if (card == 0 && raw.Length == 0)
                {
                    // 카드도 본문도 없으면 전역 실행기 → 생성하면 모든 카드에 걸리므로 건너뛴다
                    if (t != "Repos" && t != "SpellSet" && t != "GoToBattlePhase" && t != "SummonOrSet")
                    {
                        if (t == "Activate" || t == "Summon" || t == "SpSummon" || t == "MonsterSet")
                        {
                            skipped++;
                            r.Log.Add("건너뜀: AddExecutor(" + t + (handler != null ? ", " + handler : "") + ") — 카드ID 미확인");
                            continue;
                        }
                    }
                }

                var action = new ActionRule
                {
                    Type = t,
                    Card = card,
                    Priority = order++ * 10,
                    RawBody = CleanBody(raw)
                };

                // ---- 단순 가드 → 조건 DSL (본문에서 제거) ----
                if (action.RawBody.Length > 0)
                {
                    action.RawBody = TryLiftGuard(action, ids, globalIds);
                }

                if (handler != null) action.Note = handler + (lambda ? " (람다)" : "");
                p.Actions.Add(action);
            }

            // 2026-11-02: 핸들러 메서드도 **남긴다** — 다른 RawBody 가 그 헬퍼를 호출할 수 있다(중복은 동작에 무해)
            var kept = rest.ToList();

            p.RawMembers = string.Join("\n\n", kept.Select(x => x.Trim()).ToArray());

            // 2026-11-02 (B-70): 신버전 실행기를 구버전에서 돌리기 위한 **호환 변환**
            //   · Callback → `...New` (구버전 시그니처는 CompatDefaultExecutor 가 다리 역할)
            //   · 신버전 전용 API → CompatShim 확장 호출
            p.CompatBase = true;
            // #region/#endregion 은 멤버를 잘라내면 짝이 맞지 않아 컴파일 오류가 된다 → 제거
            p.RawMembers = Regex.Replace(p.RawMembers, @"(?m)^\s*#(region|endregion).*\r?\n", "");
            p.RawMembers = Compatize(p.RawMembers);
            foreach (var a in p.Actions)
                if (a.RawBody.Length > 0) a.RawBody = Compatize(a.RawBody);

            r.Log.Add("호환 모드(B-70): CompatDefaultExecutor 상속 · 콜백 " +
                      (p.RawMembers.Contains("OnSelectCardNew") ? "원본 유지" : "없음"));

            if (skipped > 0) r.Log.Add("건너뛴 실행기: " + skipped + "개 (카드ID 미확인)");
            r.Log.Add("행동 " + p.Actions.Count + "개 · RawBody " + p.Actions.Count(a => a.RawBody.Length > 0) + "개 · RawMembers " +
                      (p.RawMembers.Length > 0 ? "있음" : "없음") + " · using +" + usings.Count);
            r.Profile = p;
            return r;
        }

        // ---------------- helpers ----------------

        private static readonly (string From, string To)[] CallbackMap =
        {
            ("OnSelectCard", "OnSelectCardNew"),
            ("OnSelectOption", "OnSelectOptionNew"),
            ("OnSelectYesNo", "OnSelectYesNoNew"),
            ("OnSelectPlace", "OnSelectPlaceNew"),
            ("OnSelectPendulumSummon", "OnSelectPendulumSummonNew"),
        };

        private static readonly Dictionary<string, int> MissingHintMsg = new Dictionary<string, int>
        {
            ["Disable"] = 573, ["OperateCard"] = 574, ["RITUAL"] = 1057, ["FUSION"] = 1056,
            ["SYNCHRO"] = 1063, ["XYZ"] = 1073, ["PENDULUM"] = 1074
        };

        /// <summary>
        /// 2026-11-02 (B-70): 신버전 실행기 코드를 **구버전 + CompatDefaultExecutor** 에서 컴파일되도록 바꾼다.
        /// 콜백 이름은 `...New` 로(브리지가 int 시그니처로 넘겨줌), 없는 API 는 `CompatShim` 확장 호출로.
        /// </summary>
        public static string Compatize(string? text)
        {
            if (string.IsNullOrEmpty(text)) return text ?? "";

            // 1) 콜백 이름 → 브리지
            foreach (var m in CallbackMap)
                text = Regex.Replace(text, @"(?<![\w])" + m.From + @"(?![\w])", m.To);

            // 2) long → int 로 잘라야 하는 곳
            text = text.Replace("Util.GetStringId(", "Util.StringId(");
            text = Regex.Replace(text, @"(?<![\w.])ActivateDescription(?![\w])", "((int)ActivateDescription)");

            // 3) 신버전 전용 Duel 멤버
            text = text.Replace("Duel.LastChainLocation", "CompatShim.GetLastChainLocation(Duel)");
            text = text.Replace("Duel.CurrentChainInfo", "Duel.GetCurrentChainInfo()");
            text = text.Replace("Duel.SolvingChainIndex", "CompatShim.GetSolvingChainIndex(Duel)");
            text = text.Replace("Duel.NegatedChainIndexList", "CompatShim.GetNegatedChainIndexList(Duel)");

            // 4) 속성처럼 쓰이는 신버전 멤버 → 확장 호출 (순서 중요: LastChainTargets 먼저)
            text = Regex.Replace(text, @"(\w+)\.LastChainTargets\b", "CompatShim.GetLastChainTargets($1)");
            text = Regex.Replace(text, @"(\w+)\.HintDescriptions\b", "CompatShim.GetHintDescriptions($1)");
            text = Regex.Replace(text, @"(\w+)\.LastLocation\b", "CompatShim.LastLocation($1)");

            // 5) 나머지 신버전 API
            text = text.Replace("CardContainer.CompareCardLink", "CompatShim.CompareCardLink");
            text = text.Replace("CardRace.BeastWarrior", "(CardRace)0x8000");
            text = text.Replace("Program.Rand", "CompatShim.Rand");

            // 6) 구버전 HintMsg 에 없는 상수 → 값
            foreach (var kv in MissingHintMsg)
                text = Regex.Replace(text, @"HintMsg\." + kv.Key + @"(?![\w])", kv.Value.ToString());

            return text;
        }

        private static Dictionary<string, int> LoadGlobalIds(string? path, Result r)
        {
            var d = new Dictionary<string, int>();
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return d;
            try
            {
                var t = File.ReadAllText(path);
                foreach (Match m in Regex.Matches(t, @"public\s+const\s+int\s+(?<n>\w+)\s*=\s*(?<v>\d+)\s*;"))
                {
                    var n = m.Groups["n"].Value;
                    if (!d.ContainsKey(n)) d[n] = int.Parse(m.Groups["v"].Value);
                }
            }
            catch { }
            return d;
        }

        private static string? ExtractBraceBlock(string s, int openBrace)
        {
            if (openBrace < 0 || openBrace >= s.Length) return null;
            var depth = 0;
            for (var i = openBrace; i < s.Length; i++)
            {
                if (s[i] == '{') depth++;
                else if (s[i] == '}')
                {
                    depth--;
                    if (depth == 0) return s.Substring(openBrace + 1, i - openBrace - 1);
                }
            }
            return null;
        }

        /// <summary>클래스 본문을 최상위 멤버 단위로 자른다(문자열/주석 무시).</summary>
        private static List<string> SplitMembers(string body)
        {
            var list = new List<string>();
            var depth = 0;
            var start = 0;
            var inStr = false;
            var inChar = false;
            var inLine = false;
            var inBlock = false;
            for (var i = 0; i < body.Length; i++)
            {
                var c = body[i];
                var n = i + 1 < body.Length ? body[i + 1] : '\0';
                if (inLine) { if (c == '\n') inLine = false; continue; }
                if (inBlock) { if (c == '*' && n == '/') { inBlock = false; i++; } continue; }
                if (inStr) { if (c == '\\') { i++; continue; } if (c == '"') inStr = false; continue; }
                if (inChar) { if (c == '\\') { i++; continue; } if (c == '\'') inChar = false; continue; }
                if (c == '/' && n == '/') { inLine = true; i++; continue; }
                if (c == '/' && n == '*') { inBlock = true; i++; continue; }
                if (c == '"') { inStr = true; continue; }
                if (c == '\'') { inChar = true; continue; }

                if (c == '{') depth++;
                else if (c == '}')
                {
                    depth--;
                    if (depth <= 0)
                    {
                        var chunk = body.Substring(start, i - start + 1).Trim();
                        if (chunk.Length > 0) list.Add(chunk);
                        start = i + 1;
                    }
                }
                else if (c == ';' && depth == 0)
                {
                    var chunk = body.Substring(start, i - start + 1).Trim();
                    if (chunk.Length > 0) list.Add(chunk);
                    start = i + 1;
                }
            }
            return list;
        }

        private static string SignatureOf(string member)
        {
            var b = member.IndexOf('{');
            var s = member.IndexOf(';');
            var cut = b >= 0 ? b : (s >= 0 ? s : member.Length);
            return member.Substring(0, Math.Min(cut, member.Length)).Trim();
        }

        private static string BodyOf(string member)
        {
            var b = member.IndexOf('{');
            if (b < 0) return "";
            var inner = ExtractBraceBlock(member, b);
            return inner ?? "";
        }

        /// <summary>메서드 인자 목록을 괄호 균형으로 읽는다(openParen 위치부터).</summary>
        private static List<string>? ReadArgs(string s, int openParen)
        {
            var depth = 0;
            var start = openParen + 1;
            var args = new List<string>();
            for (var i = openParen; i < s.Length; i++)
            {
                var c = s[i];
                if (c == '(') depth++;
                else if (c == ')')
                {
                    depth--;
                    if (depth == 0)
                    {
                        var last = s.Substring(start, i - start).Trim();
                        if (last.Length > 0) args.Add(last);
                        return args;
                    }
                }
                else if (c == ',' && depth == 1)
                {
                    args.Add(s.Substring(start, i - start).Trim());
                    start = i + 1;
                }
            }
            return null;
        }

        /// <summary>본문 앞뒤 공백/빈 줄 정리</summary>
        private static string CleanBody(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "";
            var lines = raw.Replace("\r\n", "\n").Split('\n').ToList();
            while (lines.Count > 0 && lines[0].Trim().Length == 0) lines.RemoveAt(0);
            while (lines.Count > 0 && lines[lines.Count - 1].Trim().Length == 0) lines.RemoveAt(lines.Count - 1);
            // 공통 들여쓰기 제거
            var min = lines.Where(l => l.Trim().Length > 0).Select(l => l.Length - l.TrimStart().Length).DefaultIfEmpty(0).Min();
            for (var i = 0; i < lines.Count; i++)
                if (lines[i].Length >= min) lines[i] = lines[i].Substring(min);
            var text = string.Join("\n", lines);
            if (text.Trim().Length == 0) return "";
            // return 이 하나도 없으면 마지막에 return true 추가(실행기 규약)
            if (!Regex.IsMatch(text, @"\breturn\b")) text += "\nreturn true;";
            return text;
        }

        /// <summary>
        /// 본문 첫 줄이 `if (단순조건) return false;` 이고 그 조건이 DSL로 옮길 수 있으면
        /// 조건을 action.Conditions 에 넣고 본문에서 그 줄을 제거한다.
        /// </summary>
        private static string TryLiftGuard(ActionRule a, Dictionary<string, int> ids, Dictionary<string, int> globalIds)
        {
            var text = a.RawBody;
            var m = Regex.Match(text, @"^\s*if\s*\((?<c>[^{};]+)\)\s*return\s+false\s*;\s*\n");
            if (!m.Success) return text;
            var cond = m.Groups["c"].Value.Trim();
            var built = TranslateCondition(cond, ids, globalIds);
            if (built == null) return text;
            a.Conditions.Add(built);
            return text.Substring(m.Length);
        }

        private static Condition? TranslateCondition(string cond, Dictionary<string, int> ids, Dictionary<string, int> gids)
        {
            cond = cond.Replace(" ", "");
            if (cond == "Duel.Player==0") return new Condition { Kind = "isMyTurn" };
            if (cond == "Card.IsDisabled()") return new Condition { Kind = "cardIsDisabled" };
            if (cond == "Enemy.GetMonsterCount()==0") return new Condition { Kind = "enemyMonsterCount", Op = "==", Value = 0 };
            if (cond == "Bot.GetMonsterCount()==0") return new Condition { Kind = "myMonsterCount", Op = "==", Value = 0 };
            var mh = Regex.Match(cond, @"^Bot\.HasInHand\((?:CardId\.)?(?<s>\w+)\)$");
            if (mh.Success)
            {
                var id = Resolve(mh.Groups["s"].Value, ids, gids);
                if (id > 0) return new Condition { Kind = "hasInHand", Card = id, Side = "me" };
            }
            var mg = Regex.Match(cond, @"^Bot\.HasInGraveyard\((?:CardId\.)?(?<s>\w+)\)$");
            if (mg.Success)
            {
                var id = Resolve(mg.Groups["s"].Value, ids, gids);
                if (id > 0) return new Condition { Kind = "hasInGraveyard", Card = id, Side = "me" };
            }
            return null;
        }

        private static int Resolve(string sym, Dictionary<string, int> ids, Dictionary<string, int> gids)
        {
            if (ids.TryGetValue(sym, out var v)) return v;
            if (gids.TryGetValue(sym, out var w)) return w;
            return 0;
        }
    }
}


