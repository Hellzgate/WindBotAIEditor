using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using WindBotAIEditor.Models;

namespace WindBotAIEditor.Services
{
    /// <summary>
    /// AiProfile(유희왕식 규칙) → C# Executor 소스 생성.
    /// 생성 코드는 .NET Framework 4.0 csc(C# 5)와 호환되어야 한다.
    /// </summary>
    public static class RuleCodeGen
    {
        public static string ClassName(string engine) =>
            engine.EndsWith("Executor", StringComparison.OrdinalIgnoreCase) ? engine : engine + "Executor";

        private static int PolicyId(string pick) => pick switch
        {
            "enemyHighestAttack" => 1,
            "enemyLowestAttack" => 2,
            "enemyHighestDefense" => 3,
            "lowestLevel" => 4,
            "codeMatch" => 5,
            _ => 0
        };

        /// <summary>턴 조건식 (Duel.Player 는 로컬 기준: 0=내 턴, 1=상대 턴)</summary>
        private static string TurnExpr(ForbidRule f) => f.TurnScope switch
        {
            "my" => "Duel.Player == 0",
            "opponent" => "Duel.Player != 0",
            _ => "true"
        };

        private static bool HasTurnLimit(ForbidRule f) => f.TurnScope == "my" || f.TurnScope == "opponent";

        /// <summary>조건 해석이 "조건 충족 시에만 금지"인가</summary>
        private static bool IsDeny(ForbidRule f) =>
            string.Equals(f.ConditionMode, "deny", StringComparison.OrdinalIgnoreCase);

        /// <summary>예외 조건이 있으면 " && !(예외)" (deny 모드면 " && (조건)") 문자열 반환</summary>
        private static string Exc(ForbidRule f)
        {
            var exc = ExceptionExpr(f);
            if (exc == null) return "";
            return IsDeny(f) ? " && (" + exc + ")" : " && !(" + exc + ")";
        }

        /// <summary>금지 AddExecutor 용 Func 식: 금지 조건이 아니면 허용(true)</summary>
        private static string FuncExpr(ForbidRule f)
        {
            var exc = ExceptionExpr(f);
            var turn = TurnExpr(f);
            if (exc == null) return "() => !(" + turn + ")";
            return IsDeny(f)
                ? "() => (!(" + turn + ") || !(" + exc + "))"
                : "() => (!(" + turn + ") || (" + exc + "))";
        }

        /// <summary>예외 조건식 (없으면 null)</summary>
        private static string? ExceptionExpr(ForbidRule f) =>
            (f.Conditions != null && f.Conditions.Count > 0) ? ConditionExpression(f.Conditions) : null;

        /// <summary>
        /// 선택 규칙(B-6)의 조건 게이트 식. 조건이 없으면 null (= 기존과 완전히 동일하게 무조건 적용).
        /// 기존 적용 조건(On/WhenCard/WhenMax)과 AND 로 합성된다.
        /// </summary>
        private static string? ChoiceGate(ChoiceRule c) =>
            (c.Conditions != null && c.Conditions.Count > 0) ? ConditionExpression(c.Conditions) : null;

        public static string Generate(AiProfile p)
        {
            var cls = ClassName(p.Engine);
            var sb = new StringBuilder();

            // ---- B-12: 같은 이름 카드(대체 일러/재록) 별칭 ----
            Warnings.Clear();
            _aliasMemo.Clear();
            _aliasesOn = p.SameNameAliases;
            if (_aliasesOn && !CardDatabase.Instance.IsOpen)
                Warnings.Add("카드 DB 가 열려 있지 않아 같은 이름 카드를 해석하지 못했습니다 (카드 자기 자신만 판정)");
            var aliasGroups = AliasGroupsOf(p);

            var actions = p.Actions.OrderBy(a => a.Priority).ToList();
            var choices = (p.Choices ?? new List<ChoiceRule>()).Where(c => c != null).OrderBy(c => c.Priority).ToList();

            // 단일 결과 콜백(SelectYesNo/Option/Announce/Place/Position)은 우선순위 순서로 조건을 검사한다 (B-6)
            var yesNoRules = choices.Where(c => c.On == "SelectYesNo").ToList();
            var optionRules = choices.Where(c => c.On == "SelectOption").ToList();
            var announceRules = choices.Where(c => c.On == "AnnounceCard").ToList();
            var placeRules = choices.Where(c => c.On == "SelectPlace").ToList();
            var positionRules = choices.Where(c => c.On == "SelectPosition").ToList();
            // B-14: 선택 정책 "테마 카드 우선" 규칙 (우선순위 순서 = PickByTheme_<n> 번호)
            var themePickRules = choices.Where(c => c.Pick == "themeMatch").ToList();
            // B-17: 선택 정책 "발동 중인 카드(체인)" 규칙 (우선순위 순서 = PickChainCard_<n> 번호)
            var chainPickRules = choices.Where(c => c.Pick == "chainCard").ToList();
            // B-22: 제외 카드(excludeCards)가 있는 SelectCard 규칙 — 정책과 무관하게 후보를 먼저 거른다
            //   (우선순위 순서 = ExcludeCodes_<n> 번호, 중복 정의 방지 위해 참조 중복 제거)
            var excludePickRules = new List<ChoiceRule>();
            foreach (var c in choices)
            {
                if (c.On != "SelectCard") continue;
                if (c.ExcludeCards == null || !c.ExcludeCards.Any(id => id > 0)) continue;
                if (!excludePickRules.Contains(c)) excludePickRules.Add(c);
            }

            var forbids = (p.Forbids ?? new List<ForbidRule>()).OrderBy(f => f.Priority)
                // B-26: 테마 일반소환·세트 금지는 카드 ID 없이 테마로만 지정한다
                .Where(f => f.Card > 0 || f.Action == "ChainLimit" || (f.Action == "SummonSetTheme" && f.ThemeCode > 0)).ToList();
            var triggers = (p.Triggers ?? new List<FinishTrigger>()).OrderBy(t => t.Priority).ToList();
            var opps = (p.OppTriggers ?? new List<OppTrigger>()).OrderBy(o => o.Priority).ToList();

            // ---- 전개 루트: 선공(Routes) / 후공(RoutesSecond) — 트랙마다 N벌 (B-5) ----
            var firstSets = RouteSetsOf(p.Routes, p.Route);
            var secondSets = RouteSetsOf(p.RoutesSecond, p.RouteSecond);
            bool hasFirstRoute = firstSets.Any(s => RouteSteps(s).Count > 0);
            bool hasSecondRoute = secondSets.Any(s => RouteSteps(s).Count > 0);
            // 후공 트랙이 비어 있으면 선공 트랙을 그대로 쓴다 (B-4 동작 유지)
            bool secondUsesFirst = hasFirstRoute && !hasSecondRoute;
            bool hasAnyRoute = hasFirstRoute || hasSecondRoute;
            // 후공 트랙이 실제로 만들 루트 목록 (비었으면 선공 목록을 복제)
            var secondEmitSets = secondUsesFirst ? firstSets : secondSets;

            var forbidActivate = forbids.Where(f => f.Action == "Activate" || f.Action == "ActivateOnField"
                                                 || f.Action == "ActivateHand" || f.Action == "ActivateGrave").ToList();
            var forbidSummon = forbids.Where(f => f.Action == "Summon").ToList();
            var forbidNoUse = forbids.Where(f => f.Action == "SummonSet").ToList();
            // B-26: 테마 단위 일반소환·세트 금지 (특수소환은 건드리지 않는다)
            var forbidThemeSummon = forbids.Where(f => f.Action == "SummonSetTheme" && f.ThemeCode > 0).ToList();
            var forbidAttack = forbids.Where(f => f.Action == "Attack").ToList();
            var forbidTarget = forbids.Where(f => f.Action == "Target").ToList();
            var forbidRelease = forbids.Where(f => f.Action == "Release").ToList();
            var forbidNoTargetField = forbids.Where(f => f.Action == "NoTargetMyField").ToList();
            var forbidNoTargetCard = forbids.Where(f => f.Action == "NoTargetMyCard").ToList();
            var forbidNoTargetEnemy = forbids.Where(f => f.Action == "NoTargetEnemyCard").ToList();
            // B-13: 내 카드에 체인해서 발동하는 것만 금지 (상대 카드에 체인은 허용)
            var forbidChainToMine = forbids.Where(f => f.Action == "ActivateChainToMine").ToList();
            var effectBlocks = forbids.Where(f => f.Action == "BlockEffect").ToList();
            var chainLimits = forbids.Where(f => f.Action == "ChainLimit").ToList();

            bool stopOthers = triggers.Any(t => t.StopOthers);
            int avoidColumnCard = p.AvoidColumnCard;   // 마함 열 회피(무한포영 등)
            bool protectExtra = p.FreePlayProtectExtra;   // B-16: 릴리스/제물에서 엑스트라 덱 몬스터 제외
            // B-18: 자유 플레이 Summon 실행기가 실제로 배출될 때만 "제물 여력" 가드/헬퍼를 넣는다.
            //   ① 행동으로 명시한 Summon 은 건드리지 않는다(자유 플레이 전용).
            bool needTributeGuard = protectExtra && p.FreePlay && !actions.Any(a =>
                string.Equals(a.Type, "Summon", StringComparison.OrdinalIgnoreCase)
                || string.Equals(a.Type, "SummonOrSet", StringComparison.OrdinalIgnoreCase));
            // B-46: 제물 여력 가드 예외 카드 (무제물 일반소환 카드) — 별칭 확장 포함
            List<int> tributeGuardExcept = new List<int>();
            if (needTributeGuard && p.FreePlayTributeGuardExcept != null)
                foreach (var cid in p.FreePlayTributeGuardExcept)
                    foreach (var aid in AliasIds(cid))
                        if (!tributeGuardExcept.Contains(aid)) tributeGuardExcept.Add(aid);

            // ActivatingCard() 는 NoTarget 금지 또는 whenCard 선택규칙에서 필요하다
            // codeMatch(특정 카드 우선) 규칙이 있으면 HasCode 헬퍼가 필요하다 (없으면 다음 규칙으로 넘어감)
            bool needHasCode = choices.Any(ch => ch.Pick == "codeMatch" && ch.Card > 0);

            bool needActivatingCard = forbidNoTargetField.Count > 0 || forbidNoTargetCard.Count > 0
                                      || forbidNoTargetEnemy.Count > 0
                                      || choices.Any(c => c.WhenCard > 0);

            sb.AppendLine("// WindBot AI Editor — rule-generated executor (schema v11)");
            sb.AppendLine("// engine : " + cls);
            if (!string.IsNullOrWhiteSpace(p.Description))
                sb.AppendLine("// note   : " + OneLine(p.Description));
            sb.AppendLine("// turn  : Duel.Player == 0 은 '내 턴', != 0 은 '상대 턴'");
            if (aliasGroups.Count > 0)
            {
                sb.AppendLine("// alias  : 같은 이름 카드(대체 일러/재록)를 함께 판정 — 설정 sameNameAliases=ON");
                foreach (var g in aliasGroups)
                    sb.AppendLine("//          " + g.Key + " = " + string.Join(", ", g.Value.Select(x => x.ToString()).ToArray()));
            }
            sb.AppendLine("using System;");
            sb.AppendLine("using System.Collections.Generic;");
            sb.AppendLine("using WindBot.Game;");
            sb.AppendLine("using WindBot.Game.AI;");
            sb.AppendLine("using YGOSharp.OCGWrapper.Enums;");
            if (!string.IsNullOrWhiteSpace(p.DialogFile))
            {
                sb.AppendLine("using System.IO;");
                sb.AppendLine("using System.Reflection;");
                sb.AppendLine("using System.Runtime.Serialization;");
                sb.AppendLine("using System.Runtime.Serialization.Json;");
            }
            sb.AppendLine();
            sb.AppendLine("namespace WindBot.Game.AI.Decks");
            sb.AppendLine("{");
            sb.AppendLine($"    [Deck(\"{p.Engine}\", \"AI_{p.Engine}\", \"Normal\")]");
            sb.AppendLine("    public class " + cls + " : DefaultExecutor");
            sb.AppendLine("    {");
            sb.AppendLine("        public " + cls + "(GameAI ai, Duel duel) : base(ai, duel)");
            sb.AppendLine("        {");
            if (!string.IsNullOrWhiteSpace(p.DialogFile))
                sb.AppendLine("            ApplyDialogs(AI);   // 이 엔진 전용 대사 적용");

            // ---- 전개 루트 (가장 먼저) ----
            if (hasFirstRoute)
                EmitRouteRegistrations(sb, firstSets, false);
            if (hasSecondRoute || secondUsesFirst)
                EmitRouteRegistrations(sb, secondEmitSets, true);

            // ---- 완성 트리거 ----
            foreach (var g in triggers.GroupBy(t => new { t.Group, t.WhenDone }).OrderBy(x => x.Min(t => t.Priority)))
            {
                var members = g.OrderBy(t => t.Priority).ToList();
                var notes = string.Join(" + ", members.Where(m => !string.IsNullOrWhiteSpace(m.Note)).Select(m => OneLine(m.Note)));
                if (notes.Length > 0)
                    sb.AppendLine("            // [트리거 그룹" + g.Key.Group + "] " + notes);
                var gcond = GroupExpression(members);
                if (g.Key.WhenDone == "battle")
                    sb.AppendLine("            AddExecutor(ExecutorType.GoToBattlePhase, () => " + gcond + ");");
                else if (g.Key.WhenDone == "end")
                    sb.AppendLine("            AddExecutor(ExecutorType.GoToEndPhase, () => " + gcond + ");");
            }

            // ---- 상대 발동 트리거 ----
            foreach (var o in opps)
            {
                if (!string.IsNullOrWhiteSpace(o.Note))
                    sb.AppendLine("            // [상대트리거 P" + o.Priority + "] " + OneLine(o.Note));
                if (o.ResponseCard > 0 && o.Card > 0)
                    foreach (var rid in AliasIds(o.ResponseCard))
                        sb.AppendLine("            AddExecutor(ExecutorType.Activate, " + rid + ", () => _oppUsed_" + o.Card + ");");
            }

            // ---- 행동 규칙 ----
            foreach (var a in actions)
            {
                if (!string.IsNullOrWhiteSpace(a.Note))
                    sb.AppendLine("            // " + OneLine(a.Note) + "   [P" + a.Priority + "]");
                var cardIds = a.Card > 0 ? AliasIds(a.Card) : new int[0];
                if (cardIds.Count > 1)   // B-12: 별칭마다 1개씩 등록
                    sb.AppendLine("            //   같은 이름 카드 " + cardIds.Count + "개: " + string.Join(", ", cardIds.Select(x => x.ToString()).ToArray()));
                if (a.Card <= 0)
                {
                    sb.AppendLine("            " + ActionCall(a, 0, stopOthers, effectBlocks.Count > 0) + ";");
                }
                else
                {
                    foreach (var cid in cardIds)
                        sb.AppendLine("            " + ActionCall(a, cid, stopOthers, effectBlocks.Count > 0) + ";");
                }
            }

            // ---- 자유 플레이 ----
            if (p.FreePlay)
                EmitFreePlay(sb, actions, triggers, stopOthers, p.DebugEffectLog, effectBlocks.Count > 0,
                    forbidSummon.Count > 0, forbidNoUse.Count > 0,
                    p.FreePlaySetSpell, p.FreePlaySetSpellKind, p.FreePlaySetSpellExclude, needTributeGuard,
                    forbidThemeSummon.Count > 0, tributeGuardExcept);

            // ---- 소환 금지 (턴 조건 반영) ----
            foreach (var f in forbidSummon)
                foreach (var cid in AliasIds(f.Card))
                    sb.AppendLine($"            AddExecutor(ExecutorType.Summon, {cid}, {FuncExpr(f)});");
            foreach (var f in forbidNoUse)
            {
                var guard = FuncExpr(f);
                foreach (var cid in AliasIds(f.Card))
                {
                    sb.AppendLine($"            AddExecutor(ExecutorType.Summon, {cid}, {guard});");
                    sb.AppendLine($"            AddExecutor(ExecutorType.SummonOrSet, {cid}, {guard});");
                    sb.AppendLine($"            AddExecutor(ExecutorType.MonsterSet, {cid}, {guard});");
                    sb.AppendLine($"            AddExecutor(ExecutorType.SpSummon, {cid}, {guard});");
                }
            }

            sb.AppendLine("        }");

            // ---- 가위바위보 / 선후공 ----
            int rps = 0;
            if (int.TryParse(p.RpsHand, out rps) && rps >= 1 && rps <= 3)
            {
                sb.AppendLine();
                sb.AppendLine("        // 가위바위보: 1/2/3 고정");
                sb.AppendLine("        public override int OnRockPaperScissors()");
                sb.AppendLine("        {");
                sb.AppendLine("            return " + rps + ";");
                sb.AppendLine("        }");
            }
            if (p.TurnChoice == "first" || p.TurnChoice == "second")
            {
                sb.AppendLine();
                sb.AppendLine("        // 가위바위보 승리 시 진영 선택 (true = 선공)");
                sb.AppendLine("        public override bool OnSelectHand()");
                sb.AppendLine("        {");
                sb.AppendLine("            return " + (p.TurnChoice == "first" ? "true" : "false") + ";");
                sb.AppendLine("        }");
            }

            // ---- 이 엔진 전용 대사 ----
            // ---- B-27: 결정 로그 (debugEffectLog ON 일 때만) — 콘솔 + 파일 ----
            if (p.DebugEffectLog)
            {
                sb.AppendLine();
                sb.AppendLine("        // B-27: 결정 로그 — 콘솔 + %LOCALAPPDATA%\\WindBotAIEditor\\duel-log.txt (2MB 넘으면 새로 시작)");
                sb.AppendLine("        // B-42: 안드로이드 Mono BCL 은 ILLink 로 트리밍되어 일부 API 가 **없다**. 아래 조합만 사용한다.");
                sb.AppendLine("        //   없음: File.AppendAllText / File.WriteAllText / File.ReadAllText / Path.GetTempPath / FileSystemInfo.Delete");
                sb.AppendLine("        //   있음: File.AppendText · StreamWriter.WriteLine · FileInfo.Length · File.Delete · Path.Combine · Directory.*");
                sb.AppendLine("        private static readonly object _logLock = new object();");
                sb.AppendLine("        private static void LogLine(string msg)");
                sb.AppendLine("        {");
                sb.AppendLine("            try { Console.WriteLine(msg); } catch { }");
                sb.AppendLine("            try { LogToFile(msg); } catch { }   // B-42: 파일 로그는 어떤 예외도 밖으로 내보내지 않는다");
                sb.AppendLine("        }");
                sb.AppendLine();
                sb.AppendLine("        // B-42: 트리밍된 BCL 에서 누락 메서드가 있으면 JIT 시점에 죽으므로, 검증된 API 만 쓰는 별도 메서드로 분리");
                sb.AppendLine("        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]");
                sb.AppendLine("        private static void LogToFile(string msg)");
                sb.AppendLine("        {");
                sb.AppendLine("            string baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);");
                sb.AppendLine("            string dir = System.IO.Path.Combine(baseDir, \"WindBotAIEditor\");");
                sb.AppendLine("            if (!System.IO.Directory.Exists(dir)) System.IO.Directory.CreateDirectory(dir);");
                sb.AppendLine("            string path = System.IO.Path.Combine(dir, \"duel-log.txt\");");
                sb.AppendLine("            lock (_logLock)");
                sb.AppendLine("            {");
                sb.AppendLine("                System.IO.FileInfo fi = new System.IO.FileInfo(path);");
                sb.AppendLine("                if (fi.Exists && fi.Length > 2097152) System.IO.File.Delete(path);");
                sb.AppendLine("                using (System.IO.StreamWriter w = System.IO.File.AppendText(path))");
                sb.AppendLine("                    w.WriteLine(DateTime.Now.ToString(\"HH:mm:ss\") + \" \" + msg);");
                sb.AppendLine("            }");
                sb.AppendLine("        }");
                sb.AppendLine();
                sb.AppendLine("        // B-27: SelectCard 결과 로그 (누가/몇 장/힌트/무엇을 골랐는지)");
                sb.AppendLine("        private void LogSelectCard(int min, int max, long hint, IList<ClientCard> picked)");
                sb.AppendLine("        {");
                sb.AppendLine("            string ids = \"\";");
                sb.AppendLine("            if (picked != null)");
                sb.AppendLine("            {");
                sb.AppendLine("                for (int i = 0; i < picked.Count; i++)");
                sb.AppendLine("                {");
                sb.AppendLine("                    if (i > 0) ids += \",\";");
                sb.AppendLine("                    ids += (picked[i] == null ? \"null\" : picked[i].Id.ToString());");
                sb.AppendLine("                }");
                sb.AppendLine("            }");
            sb.AppendLine("            string chainStr = \"\";   // B-34: 현재 체인 링크 카드들 (앞=link1)");
            sb.AppendLine("            if (Duel.CurrentChain != null)");
            sb.AppendLine("            {");
            sb.AppendLine("                for (int i = 0; i < Duel.CurrentChain.Count; i++)");
            sb.AppendLine("                {");
            sb.AppendLine("                    ClientCard lc = Duel.CurrentChain[i];");
            sb.AppendLine("                    if (lc == null) continue;");
            sb.AppendLine("                    if (chainStr.Length > 0) chainStr += \",\";");
            sb.AppendLine("                    chainStr += lc.Id.ToString();");
            sb.AppendLine("                }");
            sb.AppendLine("            }");
            sb.AppendLine("            LogLine(\"[AI-EDITOR] select card=\" + (Card != null ? Card.Id.ToString() : \"-\")");
            sb.AppendLine("                + \" min=\" + min.ToString() + \" max=\" + max.ToString() + \" hint=\" + hint.ToString()");
            sb.AppendLine("                + \" chain=[\" + chainStr + \"]\"   // B-34: 체인 목록");
            sb.AppendLine("                + \" -> \" + ids);");
                sb.AppendLine("        }");
            }

            if (!string.IsNullOrWhiteSpace(p.DialogFile))
                EmitDialogs(sb, p.DialogFile, cls);

            // ---- 존 지정 (소환 존 / 마함 열 회피) ----
            var zoneActions = actions.Where(a => a.Card > 0 && (a.SummonZone == "main" || a.SummonZone == "extra")).ToList();
            if (zoneActions.Count > 0 || avoidColumnCard > 0 || placeRules.Count > 0 || p.LinkPlaceAware)   // N-5: 링크 존 선택 시에도 생성
            {
                sb.AppendLine();
                if (avoidColumnCard > 0)
                {
                    sb.AppendLine("        private int _avoidColumn = -1;   // 이번 턴 무효 열 — **내 쪽 인덱스** (세트에서 발동된 " + avoidColumnCard + ")");
                    sb.AppendLine("        private int _avoidTurn = -1;");
                    sb.AppendLine();
                    // B-47: 코어는 상대 마함존을 좌우 반전(4-seq)으로 취급한다 → 같은 '세로열'을 내 인덱스로 환산
                    sb.AppendLine("        // B-47: 상대 필드는 좌우가 뒤집혀 보인다 → 같은 세로열 = 4 - seq (상대 카드일 때)");
                    sb.AppendLine("        private static int AvoidColumnOf(ClientCard c)");
                    sb.AppendLine("        {");
                    sb.AppendLine("            if (c == null) return -1;");
                    sb.AppendLine("            int col = c.Sequence;");
                    sb.AppendLine("            if (col < 0 || col > 4) return -1;");
                    sb.AppendLine("            if (c.Controller != 0) col = 4 - col;");
                    sb.AppendLine("            return col;");
                    sb.AppendLine("        }");
                    sb.AppendLine();
                }
                sb.AppendLine("        // ---- 존 지정 (소환 존 / 마함 열 회피) ----");
                sb.AppendLine("        public override int OnSelectPlace(long cardId, int player, CardLocation location, int available)");
                sb.AppendLine("        {");
                if (placeRules.Count > 0)
                {
                    sb.AppendLine("            int ruleZone = SelectZoneByRule(cardId, player, location, available);");
                    sb.AppendLine("            if (ruleZone >= 0) return ruleZone;   // ② 선택 규칙(조건 포함)이 먼저");
                }
                if (zoneActions.Count > 0)
                {
                    var defZone = zoneActions[0].SummonZone;
                    sb.AppendLine("            if (player == 0 && location == CardLocation.MonsterZone)");
                    sb.AppendLine("            {");
                    sb.AppendLine("                int want = 0;");
                    foreach (var a in zoneActions)
                        sb.AppendLine("                if (" + IdEqOr("cardId", a.Card) + ") want = " +
                                      (a.SummonZone == "extra" ? "Zones.ExtraMonsterZones" : "Zones.MainMonsterZones") + ";");
                    sb.AppendLine("                if (want == 0) want = " +
                                  (defZone == "extra" ? "Zones.ExtraMonsterZones" : "Zones.MainMonsterZones") + ";  // 기본값");
                    sb.AppendLine("                int z = PreferZone(available & want);");
                    sb.AppendLine("                if (z != 0) return z;");
                    sb.AppendLine("            }");
                }
                if (avoidColumnCard > 0)
                {
                    sb.AppendLine("            if (player == 0 && location == CardLocation.SpellZone)");
                    sb.AppendLine("            {");
                    sb.AppendLine("                // " + avoidColumnCard + " 가 있는 열을 피한다. 그 카드를 놓을 때는 내 다른 마함이 있는 열도 피한다.");
                    sb.AppendLine("                int bad = 0;");
                    sb.AppendLine("                bool placing = " + IdEqOr("cardId", avoidColumnCard) + ";");
                    sb.AppendLine("                if (Bot.SpellZone != null)");
                    sb.AppendLine("                {");
                    sb.AppendLine("                    for (int i = 0; i < Bot.SpellZone.Length && i < 5; i++)");
                    sb.AppendLine("                    {");
                    sb.AppendLine("                        ClientCard c = Bot.SpellZone[i];");
                    sb.AppendLine("                        if (c == null) continue;");
                    sb.AppendLine("                        if (" + IsCodeOr("c", avoidColumnCard) + " || placing) bad |= (1 << i);");
                    sb.AppendLine("                    }");
                    sb.AppendLine("                }");
                    sb.AppendLine("                if (_avoidTurn == Duel.Turn && _avoidColumn >= 0 && _avoidColumn < 5) bad |= (1 << _avoidColumn);");
                    sb.AppendLine("                if (bad != 0)");
                    sb.AppendLine("                {");
                    sb.AppendLine("                    int avail = available & Zones.SpellZones;");
                    sb.AppendLine("                    for (int i = 0; i < 5; i++)");
                    sb.AppendLine("                    {");
                    sb.AppendLine("                        int bit = 1 << i;");
                    sb.AppendLine("                        if ((avail & bit) != 0 && (bad & bit) == 0) return bit;");
                    sb.AppendLine("                    }");
                    sb.AppendLine("                }");
                    sb.AppendLine("            }");
                }
                // N-5: 링크 마커 기반 존 점수 (상대 링크 존 회피 → 내 링크 존 선호)
                if (p.LinkPlaceAware)
                {
                    sb.AppendLine("            if (player == 0 && location == CardLocation.MonsterZone)");
                    sb.AppendLine("            {");
                    sb.AppendLine("                // N-5: 상대 링크가 걸린 존은 -2, 내 링크 존은 +1 (GetLinkedZones 는 WindBot 이 계산)");
                    sb.AppendLine("                int enemyLinked = 0;");
                    sb.AppendLine("                if (Enemy.MonsterZone != null)");
                    sb.AppendLine("                    for (int i = 0; i < Enemy.MonsterZone.Length; i++)");
                    sb.AppendLine("                    {");
                    sb.AppendLine("                        ClientCard e = Enemy.MonsterZone[i];");
                    sb.AppendLine("                        if (e != null && (e.Type & 0x4000000) != 0) enemyLinked |= e.GetLinkedZones();");
                    sb.AppendLine("                    }");
                    sb.AppendLine("                int ourLinked = 0;");
                    sb.AppendLine("                if (Bot.MonsterZone != null)");
                    sb.AppendLine("                    for (int i = 0; i < Bot.MonsterZone.Length; i++)");
                    sb.AppendLine("                    {");
                    sb.AppendLine("                        ClientCard m = Bot.MonsterZone[i];");
                    sb.AppendLine("                        if (m != null && (m.Type & 0x4000000) != 0) ourLinked |= m.GetLinkedZones();");
                    sb.AppendLine("                    }");
                    sb.AppendLine("                int bestBit = 0;");
                    sb.AppendLine("                int bestScore = -99;");
                    sb.AppendLine("                for (int i = 0; i < 7; i++)");
                    sb.AppendLine("                {");
                    sb.AppendLine("                    int bit = 1 << i;");
                    sb.AppendLine("                    if ((available & bit) == 0) continue;");
                    sb.AppendLine("                    int score = 0;");
                    sb.AppendLine("                    if ((enemyLinked & bit) != 0) score -= 2;");
                    sb.AppendLine("                    if ((ourLinked & bit) != 0) score += 1;");
                    sb.AppendLine("                    if (score > bestScore) { bestScore = score; bestBit = bit; }");
                    sb.AppendLine("                }");
                    sb.AppendLine("                if (bestBit != 0) return bestBit;");
                    sb.AppendLine("            }");
                }
                sb.AppendLine("            return base.OnSelectPlace(cardId, player, location, available);");
                sb.AppendLine("        }");
                if (zoneActions.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine("        private static int PreferZone(int available)");
                    sb.AppendLine("        {");
                    sb.AppendLine("            if ((available & Zones.z2) != 0) return Zones.z2;");
                    sb.AppendLine("            if ((available & Zones.z1) != 0) return Zones.z1;");
                    sb.AppendLine("            if ((available & Zones.z3) != 0) return Zones.z3;");
                    sb.AppendLine("            if ((available & Zones.z0) != 0) return Zones.z0;");
                    sb.AppendLine("            if ((available & Zones.z4) != 0) return Zones.z4;");
                    sb.AppendLine("            if ((available & Zones.z6) != 0) return Zones.z6;");
                    sb.AppendLine("            if ((available & Zones.z5) != 0) return Zones.z5;");
                    sb.AppendLine("            return 0;");
                    sb.AppendLine("        }");
                }
            }
            // ---- 소환 시 표시형식 (공격/수비) ----
            var posActions = actions.Where(a => a.Card > 0 && (a.SummonPosition == "attack" || a.SummonPosition == "defense")).ToList();
            // B-40: 토큰은 수비 표시로 — 규칙 유무와 무관하게 이 오버라이드는 항상 생성한다
            {
                sb.AppendLine();
                sb.AppendLine("        // B-40: 필드의 토큰 판정 (Data.Type 0x4000)");
                sb.AppendLine("        private bool IsTokenOnField(int cardId)");
                sb.AppendLine("        {");
                sb.AppendLine("            foreach (var c in Bot.MonsterZone)");
                sb.AppendLine("                if (c != null && c.Id == cardId && (c.Type & 0x4000) != 0) return true;");
                sb.AppendLine("            foreach (var c in Enemy.MonsterZone)");
                sb.AppendLine("                if (c != null && c.Id == cardId && (c.Type & 0x4000) != 0) return true;");
                sb.AppendLine("            return false;");
                sb.AppendLine("        }");
                sb.AppendLine();
                sb.AppendLine("        // ---- 소환 시 표시형식 (공격/수비) ----");
                sb.AppendLine("        public override CardPosition OnSelectPosition(int cardId, IList<CardPosition> positions)");
                sb.AppendLine("        {");
                sb.AppendLine("            // B-40: 토큰은 항상 수비 (공격으로 세울 이유가 없다)");
                sb.AppendLine("            if (IsTokenOnField(cardId) && positions != null)");
                sb.AppendLine("            {");
                sb.AppendLine("                if (positions.Contains(CardPosition.FaceUpDefence)) return CardPosition.FaceUpDefence;");
                sb.AppendLine("                if (positions.Contains(CardPosition.FaceDownDefence)) return CardPosition.FaceDownDefence;");
                sb.AppendLine("            }");
                if (positionRules.Count > 0)
                {
                    sb.AppendLine("            int rulePos = SelectPositionByRule(cardId, positions);");
                    sb.AppendLine("            if (rulePos >= 0) return (CardPosition)rulePos;   // ② 선택 규칙(조건 포함)이 먼저");
                }
                sb.AppendLine("            int want = 0;   // 1=공격 2=수비");
                foreach (var a in posActions)
                {
                    if (a.SummonPosition == "any") continue;   // B-31: 자동 = 전역 기본값
                    sb.AppendLine("            if (" + IdEqOr("cardId", a.Card) + ") want = " + (a.SummonPosition == "attack" ? "1" : "2") + ";");
                }
                // B-31: 기본 표시형식 — 프로필 설정(기본 attack)
                var globalPos = string.IsNullOrWhiteSpace(p.DefaultSummonPosition) ? "attack" : p.DefaultSummonPosition;
                var fallbackPos = (globalPos == "defense") ? "2" : "1";
                sb.AppendLine("            if (want == 0) want = " + fallbackPos + ";  // 기본값(B-31)");
                sb.AppendLine("            if (want == 1 && positions != null && positions.Contains(CardPosition.FaceUpAttack)) return CardPosition.FaceUpAttack;");
                sb.AppendLine("            if (want == 2 && positions != null)");
                sb.AppendLine("            {");
                sb.AppendLine("                if (positions.Contains(CardPosition.FaceUpDefence)) return CardPosition.FaceUpDefence;");
                sb.AppendLine("                if (positions.Contains(CardPosition.FaceDownDefence)) return CardPosition.FaceDownDefence;");
                sb.AppendLine("            }");
                sb.AppendLine("            // B-31: 요청한 표시형식이 불가하면 가능한 것 중 하나(세트는 뒷면 수비)");
                sb.AppendLine("            if (positions != null)");
                sb.AppendLine("            {");
                sb.AppendLine("                if (positions.Contains(CardPosition.FaceUpDefence)) return CardPosition.FaceUpDefence;");
                sb.AppendLine("                if (positions.Contains(CardPosition.FaceDownDefence)) return CardPosition.FaceDownDefence;");
                sb.AppendLine("                if (positions.Contains(CardPosition.FaceUpAttack)) return CardPosition.FaceUpAttack;");
                sb.AppendLine("                if (positions.Contains(CardPosition.FaceDownAttack)) return CardPosition.FaceDownAttack;");
                sb.AppendLine("            }");
                sb.AppendLine("            return base.OnSelectPosition(cardId, positions);");
                sb.AppendLine("        }");
            }
            // ---- 상태 필드 / OnChaining / OnNewTurn ----
            bool emitsSecondRoute = hasSecondRoute || secondUsesFirst;
            if (opps.Count > 0 || hasAnyRoute || avoidColumnCard > 0)
            {
                sb.AppendLine();
                foreach (var o in opps)
                    sb.AppendLine("        private bool _oppUsed_" + o.Card + ";");
                if (hasFirstRoute)
                {
                    sb.AppendLine("        private int _routeSetFirst = -1;   // 선공: -1 미선택 / 0..N-1 선택된 루트 / -2 없음(전개 안 함)");
                    sb.AppendLine("        private int _routeStep = 0;        // 선택된 루트 안의 단계 커서 (-1 = 이번 턴 전개 종료)");
                }
                if (emitsSecondRoute)
                {
                    sb.AppendLine("        private int _routeSetSecond = -1;  // 후공: -1 미선택 / 0..N-1 / -2 없음(전개 안 함)");
                    sb.AppendLine("        private int _routeStepSecond = 0;  // 후공 단계 커서 (-1 = 이번 턴 전개 종료)");
                }
                sb.AppendLine();

            // ---- B-36: 이길 수 있는 상대가 없으면 공격하지 않는다 ----
            if (p.AttackOnlyWinnable)
            {
                sb.AppendLine();
                sb.AppendLine("        // B-36: 모든 상대를 비교해 이길 수 있는 대상이 없으면 배틀 페이즈를 종료한다");
                sb.AppendLine("        public override BattlePhaseAction OnBattle(IList<ClientCard> attackers, IList<ClientCard> defenders)");
                sb.AppendLine("        {");
                sb.AppendLine("            if (attackers == null || attackers.Count == 0) return base.OnBattle(attackers, defenders);");
                sb.AppendLine("            if (defenders == null || defenders.Count == 0) return base.OnBattle(attackers, defenders);   // 직접 공격은 기본 로직");
                sb.AppendLine("            ClientCard target = null;");
                sb.AppendLine("            int bestValue = -1;");
                sb.AppendLine("            for (int j = 0; j < defenders.Count; j++)");
                sb.AppendLine("            {");
                sb.AppendLine("                ClientCard d = defenders[j];");
                sb.AppendLine("                if (d == null) continue;");
                sb.AppendLine("                int defPower = d.IsDefense() ? d.Defense : d.Attack;");
                sb.AppendLine("                bool canBeat = false;");
                sb.AppendLine("                for (int i = 0; i < attackers.Count; i++)");
                sb.AppendLine("                {");
                sb.AppendLine("                    ClientCard a = attackers[i];");
                sb.AppendLine("                    if (a != null && a.Attack > defPower) { canBeat = true; break; }");
                sb.AppendLine("                }");
                sb.AppendLine("                if (!canBeat) continue;   // 이 조합은 못 이김");
                sb.AppendLine("                if (d.Attack > bestValue) { bestValue = d.Attack; target = d; }   // 가장 위협적인 상대");
                sb.AppendLine("            }");
                sb.AppendLine("            if (target != null)");
                sb.AppendLine("            {");
                sb.AppendLine("                int need = target.IsDefense() ? target.Defense : target.Attack;");
                sb.AppendLine("                ClientCard chosen = null;   // 그를 이길 수 있는 가장 약한 내 몬스터");
                sb.AppendLine("                for (int i = 0; i < attackers.Count; i++)");
                sb.AppendLine("                {");
                sb.AppendLine("                    ClientCard a = attackers[i];");
                sb.AppendLine("                    if (a == null || a.Attack <= need) continue;");
                sb.AppendLine("                    if (chosen == null || a.Attack < chosen.Attack) chosen = a;");
                sb.AppendLine("                }");
                sb.AppendLine("                if (chosen != null) return AI.Attack(chosen, target);");
                sb.AppendLine("            }");
                sb.AppendLine("            // 이길 수 있는 상대가 하나도 없다 → 공격하지 않는다");
                sb.AppendLine("            if (Battle != null && Battle.CanMainPhaseTwo) return new BattlePhaseAction(BattlePhaseAction.BattleAction.ToMainPhaseTwo);");
                sb.AppendLine("            return new BattlePhaseAction(BattlePhaseAction.BattleAction.ToEndPhase);");
                sb.AppendLine("        }");
                sb.AppendLine();
            }
                if (opps.Count > 0 || avoidColumnCard > 0)
                {
                    sb.AppendLine("        public override void OnChaining(int player, ClientCard card)");
                    sb.AppendLine("        {");
                    foreach (var o in opps)
                        sb.AppendLine("            if (player != 0 && card != null && " + IsCodeOr("card", o.Card) + ") _oppUsed_" + o.Card + " = true;");
                    if (avoidColumnCard > 0)
                    {
                        // OnChaining 은 양쪽 발동에서 호출된다(player 0=나 / 1=상대).
                        // 세트에서 발동한 회피 카드는 그 열의 다른 마함을 이 턴 무효로 만든다.
                        // 발동 시점의 position 은 코어가 이미 앞면(0x5)으로 보고하므로(리플레이 41,747건 실측:
                        // 뒷면 0건) 위치가 아니라 "SpellZone 에서 발동" 으로 판정한다.
                        sb.AppendLine("            // " + avoidColumnCard + " 가 마함존에서 발동하면(=세트에서 발동) 그 열의 다른 마함이 이 턴 무효 → 열을 기억");
                        sb.AppendLine("            if (card != null && " + IsCodeOr("card", avoidColumnCard) + " && card.Location == CardLocation.SpellZone)");
                        sb.AppendLine("            {");
                        sb.AppendLine("                int avoidCol = AvoidColumnOf(card);");
                        sb.AppendLine("                if (avoidCol >= 0) { _avoidColumn = avoidCol; _avoidTurn = Duel.Turn; }");
                        if (p.DebugEffectLog)
                            sb.AppendLine("                LogLine(\"[AI-EDITOR] avoid-column col=\" + _avoidColumn.ToString() + \" who=\" + player.ToString() + \" turn=\" + _avoidTurn.ToString());");   // B-47: 파일 로그
                        sb.AppendLine("            }");
                    }
                    sb.AppendLine("            base.OnChaining(player, card);");
                    sb.AppendLine("        }");
                    sb.AppendLine();
                }

                if (opps.Count > 0 || hasAnyRoute)
                {
                    sb.AppendLine("        public override void OnNewTurn()");
                    sb.AppendLine("        {");
                    foreach (var o in opps)
                        sb.AppendLine("            _oppUsed_" + o.Card + " = false;");
                    if (hasFirstRoute)
                    {
                        sb.AppendLine("            _routeSetFirst = -1;   // 선공 루트 다시 고르기");
                        sb.AppendLine("            _routeStep = 0;        // 선공 루트 처음부터");
                    }
                    if (emitsSecondRoute)
                    {
                        sb.AppendLine("            _routeSetSecond = -1;  // 후공 루트 다시 고르기");
                        sb.AppendLine("            _routeStepSecond = 0;  // 후공 루트 처음부터");
                    }
                    sb.AppendLine("            base.OnNewTurn();");
                    sb.AppendLine("        }");
                }
            }

            // ---- 전개 루트: 선공/후공 게이트 + 루트 선택 + 단계 메서드 ----
            if (hasAnyRoute)
            {
                sb.AppendLine();
                sb.AppendLine("        // ---- 전개 루트 선공/후공 게이트 (amFirst / amSecond 와 같은 판정) ----");
                if (hasFirstRoute)
                {
                    sb.AppendLine("        private bool OnRouteFirst()");
                    sb.AppendLine("        {");
                    sb.AppendLine("            return Duel.IsFirst;");
                    sb.AppendLine("        }");
                }
                if (emitsSecondRoute)
                {
                    if (hasFirstRoute) sb.AppendLine();
                    sb.AppendLine("        private bool OnRouteSecond()");
                    sb.AppendLine("        {");
                    sb.AppendLine("            return !Duel.IsFirst;");
                    sb.AppendLine("        }");
                }
            }
            if (hasFirstRoute)
                EmitRouteSelect(sb, firstSets, false);
            if (emitsSecondRoute)
            {
                if (secondUsesFirst)
                {
                    sb.AppendLine();
                    sb.AppendLine("        // 후공 트랙이 비어 있다 → 선공 트랙의 루트 목록을 후공 게이트로 한 번 더 만든다");
                }
                EmitRouteSelect(sb, secondEmitSets, true);
            }
            if (hasFirstRoute)
                EmitRouteMethods(sb, firstSets, false);
            if (emitsSecondRoute)
                EmitRouteMethods(sb, secondEmitSets, true);

            // ---- 발동 금지 ----
            bool needPreActivate = forbidActivate.Count > 0 || forbidNoTargetField.Count > 0 || forbidNoTargetCard.Count > 0 || forbidNoTargetEnemy.Count > 0 || chainLimits.Count > 0 || forbidChainToMine.Count > 0 || avoidColumnCard > 0;
            if (needPreActivate)
            {
                sb.AppendLine();
                sb.AppendLine("        public override bool OnPreActivate(ClientCard card)");
                sb.AppendLine("        {");
                sb.AppendLine("            if (card == null) return base.OnPreActivate(card);");
                if (avoidColumnCard > 0)
                {
                    sb.AppendLine("            // " + avoidColumnCard + " 를 세트에서 발동하면 그 열의 다른 마함이 이 턴 무효가 된다 → 열을 기억");
                    sb.AppendLine("            if (" + IsCodeOr("card", avoidColumnCard) + " && card.Location == CardLocation.SpellZone && (card.Position & (int)CardPosition.FaceDown) != 0)");
                    sb.AppendLine("            {");
                    sb.AppendLine("                int avoidCol = AvoidColumnOf(card);");
                    sb.AppendLine("                if (avoidCol >= 0) { _avoidColumn = avoidCol; _avoidTurn = Duel.Turn; }");
                    sb.AppendLine("            }");
                    sb.AppendLine("            // 이번 턴 무효 열에 있는 내 마함은 발동하지 않는다 (효과가 무효될 발동을 미리 막는다)");
                    sb.AppendLine("            if (_avoidTurn == Duel.Turn && _avoidColumn >= 0 && _avoidColumn < 5");
                    sb.AppendLine("                && card.Location == CardLocation.SpellZone && card.Sequence == _avoidColumn");
                    sb.AppendLine("                && !" + IsCodeOr("card", avoidColumnCard) + ")");
                    if (p.DebugEffectLog)
                    {
                        sb.AppendLine("            {");
                        sb.AppendLine("                LogLine(\"[AI-EDITOR] avoid-column block col=\" + _avoidColumn.ToString() + \" card=\" + card.Id.ToString());");   // B-47
                        sb.AppendLine("                return false;");
                        sb.AppendLine("            }");
                    }
                    else
                        sb.AppendLine("                return false;");
                }
                if (forbidActivate.Count > 0)
                    sb.AppendLine("            if (IsForbiddenActivate(card)) return false;");
                // B-13: 내 카드에 체인해서 발동하는 것만 금지 (체인 없음/상대 카드에 체인은 허용)
                foreach (var f in forbidChainToMine)
                {
                    var chainCond = IsCodeOr("card", f.Card) + " && ChainRespondsToMine() && (" + TurnExpr(f) + ")" + Exc(f);
                    if (p.DebugEffectLog)
                    {
                        sb.AppendLine("            if (" + chainCond + ")");
                        sb.AppendLine("            {");
                        sb.AppendLine("                LogForbid(\"ActivateChainToMine\", card);");
                        sb.AppendLine("                return false;");
                        sb.AppendLine("            }");
                    }
                    else
                        sb.AppendLine("            if (" + chainCond + ") return false;");
                }
                foreach (var f in forbidNoTargetField)
                    sb.AppendLine($"            if ({IsCodeOr("card", f.Card)} && ({TurnExpr(f)}){Exc(f)} && ChainTargetsAreMine(true)) {{ LogForbid(\"NoTargetMyField\", card); return false; }}");
                foreach (var f in forbidNoTargetCard)
                    sb.AppendLine($"            if ({IsCodeOr("card", f.Card)} && ({TurnExpr(f)}){Exc(f)} && ChainTargetsAreMine(false)) {{ LogForbid(\"NoTargetMyCard\", card); return false; }}");
                foreach (var f in forbidNoTargetEnemy)
                    sb.AppendLine($"            if ({IsCodeOr("card", f.Card)} && ({TurnExpr(f)}){Exc(f)} && ChainLinksBelongTo(false)) {{ LogForbid(\"NoTargetEnemyCard\", card); return false; }}");
                var specificCards = chainLimits.Where(x => x.Card > 0).Select(x => x.Card).Distinct().ToList();
                foreach (var f in chainLimits)
                {
                    var who = f.Card > 0 ? IsCodeOr("card", f.Card) : "true";
                    if (f.Card <= 0 && specificCards.Count > 0)
                    {
                        // 전체 규칙은 카드별 규칙이 있는 카드는 건너뛴다 (카드별 설정이 우선)
                        var ex = new StringBuilder();
                        foreach (var id in specificCards) ex.Append("!" + IsCodeOr("card", id) + " && ");
                        who = ex.ToString() + "true";
                    }
                    var lim = Math.Max(1, f.ChainLimit);
                    sb.AppendLine($"            if ({who} && ({TurnExpr(f)}){Exc(f)} && ChainCount(card) >= {lim}) {{ LogForbid(\"ChainLimit\", card); return false; }}");
                }
                sb.AppendLine("            return base.OnPreActivate(card);");
                sb.AppendLine("        }");
            }

            // ---- 소환/세트 제어 ----
            if (forbidSummon.Count > 0 || forbidNoUse.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("        public override bool OnSelectMonsterSummonOrSet(ClientCard card)");
                sb.AppendLine("        {");
                if (forbidSummon.Count > 0)
                    sb.AppendLine("            if (IsForbiddenSummon(card)) return true;  // 소환 대신 세트");
                if (forbidNoUse.Count > 0)
                    sb.AppendLine("            if (IsForbiddenNoUse(card)) return true;   // 소환/세트 회피");
                sb.AppendLine("            return base.OnSelectMonsterSummonOrSet(card);");
                sb.AppendLine("        }");
            }

            // ---- 공격 금지 ----
            if (forbidAttack.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("        public override BattlePhaseAction OnBattle(IList<ClientCard> attackers, IList<ClientCard> defenders)");
                sb.AppendLine("        {");
                sb.AppendLine("            List<ClientCard> allowed = new List<ClientCard>();");
                sb.AppendLine("            for (int i = 0; i < attackers.Count; i++)");
                sb.AppendLine("                if (!IsForbiddenAttack(attackers[i])) allowed.Add(attackers[i]);");
                sb.AppendLine("            if (allowed.Count == 0) return null;");
                sb.AppendLine("            return base.OnBattle(allowed, defenders);");
                sb.AppendLine("        }");
            }

            // ---- 선택 콜백 ----
            var listChoices = choices
                .Where(c => c.On != "SelectYesNo" && c.On != "SelectOption" && c.On != "AnnounceCard"
                            && c.On != "SelectPlace" && c.On != "SelectPosition")
                .Where(c => CallbackSig.ContainsKey(c.On))
                .OrderBy(c => c.Priority)
                .ToList();

            var callbacks = new HashSet<string>(listChoices.Select(c => c.On));
            if (forbidTarget.Count > 0 || forbidNoTargetField.Count > 0 || forbidNoTargetCard.Count > 0 || forbidNoTargetEnemy.Count > 0)
                callbacks.Add("SelectCard");
            bool needReleaseFilter = forbidRelease.Count > 0;
            if (needReleaseFilter)
                foreach (var cb in new[] { "SelectSum", "RitualTribute", "FusionMaterial", "SynchroMaterial", "XyzMaterial", "LinkMaterial" })
                    callbacks.Add(cb);
            // B-16: 릴리스/제물 후보에서 엑스트라 덱 몬스터를 빼는 콜백 3종
            //   (OnSelectSum mode=true / OnSelectCard hint=Release / OnSelectRitualTribute)
            var protectCbs = new[] { "SelectSum", "SelectCard", "RitualTribute" };
            if (protectExtra)
                foreach (var cb in protectCbs) callbacks.Add(cb);

            foreach (var cb in callbacks)
            {
                if (!CallbackSig.TryGetValue(cb, out var sig)) continue;
                var rulesForCb = listChoices.Where(c => c.On == cb).ToList();

                bool isSelectCard = cb == "SelectCard";
                bool needProtectFilter = protectExtra && Array.IndexOf(protectCbs, cb) >= 0;
                bool needFilter = needProtectFilter
                                  || (isSelectCard && (forbidTarget.Count > 0 || forbidNoTargetField.Count > 0 || forbidNoTargetCard.Count > 0 || forbidNoTargetEnemy.Count > 0))
                                  || (!isSelectCard && needReleaseFilter);
                if (rulesForCb.Count == 0 && !needFilter) continue;

                sb.AppendLine();
                if (isSelectCard && p.DebugEffectLog)
                {
                    // B-27: 결과를 파일에도 남기기 위해 얇은 래퍼를 쓴다 (본문은 Core 로 이동)
                    sb.AppendLine("        public override " + sig.ReturnType + " " + sig.Name + "(" + sig.Params + ")");
                    sb.AppendLine("        {");
                    sb.AppendLine("            IList<ClientCard> __picked = SelectCardCore(" + sig.BaseArgs + ");");
                    sb.AppendLine("            LogSelectCard(min, max, hint, __picked);");
                    sb.AppendLine("            return __picked;");
                    sb.AppendLine("        }");
                    sb.AppendLine();
                    sb.AppendLine("        private " + sig.ReturnType + " SelectCardCore(" + sig.Params + ")");
                    sb.AppendLine("        {");
                }
                else
                {
                    sb.AppendLine("        public override " + sig.ReturnType + " " + sig.Name + "(" + sig.Params + ")");
                    sb.AppendLine("        {");
                }

                var srcList = "cards";
                if (needFilter)
                {
                    sb.AppendLine("            IList<ClientCard> allowedCards = cards;");
                    // B-16: 기존 선택 규칙/금지 필터 본문 **앞에** 한 줄만 끼워 넣는다 (중복 정의 방지)
                    if (needProtectFilter)
                    {
                        if (cb == "SelectSum")
                            sb.AppendLine("            if (mode) allowedCards = FilterExtraRelease(allowedCards, min);   // B-16: 제물(릴리스) 후보에서 엑스트라 덱 몬스터 제외");
                        else if (cb == "SelectCard")
                            sb.AppendLine("            if (hint == HintMsg.Release) allowedCards = FilterExtraRelease(allowedCards, min);   // B-16: 릴리스 후보에서 엑스트라 덱 몬스터 제외");
                        else
                            sb.AppendLine("            allowedCards = FilterExtraRelease(allowedCards, min);   // B-16: 의식 릴리스 후보에서 엑스트라 덱 몬스터 제외");
                    }
                    if (isSelectCard)
                    {
                        if (forbidTarget.Count > 0 || forbidNoTargetField.Count > 0 || forbidNoTargetCard.Count > 0 || forbidNoTargetEnemy.Count > 0)
                        {
                            sb.AppendLine("            {");
                            sb.AppendLine("                List<ClientCard> f1 = new List<ClientCard>();");
                            sb.AppendLine("                for (int i = 0; i < allowedCards.Count; i++)");
                            sb.AppendLine("                {");
                            sb.AppendLine("                    ClientCard cc = allowedCards[i];");
                            if (forbidTarget.Count > 0)
                                sb.AppendLine("                    if (IsForbiddenTarget(cc)) continue;");
                            if (forbidNoTargetField.Count > 0)
                                sb.AppendLine("                    if (IsForbiddenNoTargetMyField(Card, cc) || IsForbiddenNoTargetMyField(ActivatingCard(), cc)) continue;");
                            if (forbidNoTargetCard.Count > 0)
                                sb.AppendLine("                    if (IsForbiddenNoTargetMyCard(Card, cc) || IsForbiddenNoTargetMyCard(ActivatingCard(), cc)) continue;");
                            if (forbidNoTargetEnemy.Count > 0)
                                sb.AppendLine("                    if (IsForbiddenNoTargetEnemy(Card, cc) || IsForbiddenNoTargetEnemy(ActivatingCard(), cc)) continue;");
                            sb.AppendLine("                    f1.Add(cc);");
                            sb.AppendLine("                }");
                            sb.AppendLine("                if (f1.Count < min)");
                            sb.AppendLine("                {");
                            sb.AppendLine("                    LogLine(\"[AI-EDITOR] forbid fallback: 내 카드 제외 후 후보 부족, 원래 목록 사용\");");
                            sb.AppendLine("                    f1 = new List<ClientCard>(allowedCards);  // 최소 선택 수 보장");
                            sb.AppendLine("                }");
                            sb.AppendLine("                allowedCards = f1;");
                            sb.AppendLine("            }");
                        }
                    }
                    else
                    {
                        // 금지 릴리스 필터가 있을 때만 (B-16 보호 필터만 있는 경우엔 이미 위에서 처리됨)
                        if (needReleaseFilter)
                        {
                            sb.AppendLine("            {");
                            sb.AppendLine("                List<ClientCard> f2 = new List<ClientCard>();");
                            sb.AppendLine("                for (int i = 0; i < allowedCards.Count; i++)");
                            sb.AppendLine("                    if (!IsForbiddenRelease(allowedCards[i])) f2.Add(allowedCards[i]);");
                            if (!string.IsNullOrEmpty(sig.MinExpr))
                                sb.AppendLine("                if (f2.Count < " + sig.MinExpr + ") f2 = new List<ClientCard>(allowedCards);  // 최소 선택 수 보장");
                            sb.AppendLine("                allowedCards = f2;");
                            sb.AppendLine("            }");
                        }
                    }
                    srcList = "allowedCards";
                }

                // whenMax: 이 콜백이 max 인자를 가질 때만 "max == N" 조건을 붙인다
                Func<ChoiceRule, string> maxCond = (ChoiceRule r) =>
                    (r.WhenMax > 0 && sig.MaxExpr == "max") ? $"(max == {r.WhenMax})" : "";

                // B-25: whenHint — 같은 콜백이 여러 번 뜰 때 힌트로 구분 (0 = 무관)
                //   MSG_SELECT_UNSELECT_CARD(공개/다중 선택)는 항상 max = 1 로 오므로 힌트가 유일한 구분자다.
                Func<ChoiceRule, string> hintCond = (ChoiceRule r) =>
                    (r.WhenHint > 0 && sig.HintExpr.Length > 0) ? $"(hint == {r.WhenHint})" : "";

                // B-6: 규칙을 우선순위 순서대로 검사한다.
                //   when카드가 있는 규칙 → 그 카드가 주체일 때만, 조건(conditions)도 모두 만족해야 적용.
                //   when카드가 없는 규칙 → 조건만 검사. 조건이 없으면 무조건 적용(예전과 동일).
                int emitted = 0;
                bool decidedElsewhere = false;   // 무조건 적용되는 규칙을 이미 냈다 → 뒤 규칙은 죽은 코드
                foreach (var c in rulesForCb)
                {
                    // B-22: 제외 카드가 있는 SelectCard 규칙은 **정책과 무관하게** 후보를 먼저 거른다.
                    //   pool 이 min 미만이면 그 규칙을 건너뛰고 다음 규칙/기본 폴백 (억지 선택 금지).
                    int xi = isSelectCard ? excludePickRules.IndexOf(c) + 1 : 0;
                    string ruleSrc = srcList;
                    if (xi > 0)
                    {
                        ruleSrc = "pool" + xi;
                        sb.AppendLine("            IList<ClientCard> pool" + xi + " = ExcludeCodes_" + xi + "(" + srcList + ");   // B-22: 제외 카드 먼저 제거");
                    }

                    // B-29: 내성 몬스터 제외 (WindBot 내장 목록) — 후보가 min 미만이면 이 규칙을 건너뛴다
                    bool resistFilter = isSelectCard && c.SkipResistant;
                    if (resistFilter)
                    {
                        ruleSrc = "rs" + (emitted + 1);
                        sb.AppendLine("            IList<ClientCard> " + ruleSrc + " = FilterResistant(" + (xi > 0 ? "pool" + xi : srcList) + ");   // B-29: 내성 몹 제외");
                    }

                    var parts = new List<string>();
                    if (xi > 0) parts.Add("pool" + xi + ".Count >= min");                          // 후보 부족 → 다음 규칙
                    if (resistFilter) parts.Add(ruleSrc + ".Count >= min");                        // 내성 제외 후 부족 → 다음 규칙
                    if (c.WhenCard > 0)
                        parts.Add(WhenCardExpr(c.WhenCard));   // B-33: Card/ActivatingCard/체인 링크 모두 확인
                    var mc = maxCond(c);
                    if (mc.Length > 0) parts.Add(mc);
                    var hc = hintCond(c);
                    if (hc.Length > 0) parts.Add(hc);
                    var cc = ChoiceGate(c);
                    if (cc != null) parts.Add("(" + cc + ")");                                    // 조건 불충족이면 다음 규칙으로
                    if (PolicyId(c.Pick) == 5 && c.Card > 0)
                        parts.Add(CallOr(x => "HasCode(" + ruleSrc + ", " + x + ")", c.Card));     // 후보에 없으면 다음 규칙으로

                    // B-14: 테마 카드 우선 — 고를 카드가 없으면 다음 규칙으로
                    var themeCall = "";
                    if (c.Pick == "themeMatch")
                    {
                        int ti = themePickRules.IndexOf(c) + 1;
                        themeCall = $"PickByTheme_{ti}({ruleSrc}, {sig.MaxExpr})";
                        parts.Add($"PickByTheme_{ti}({ruleSrc}, 1).Count > 0");
                    }

                    // B-17: 발동 중인 카드(체인) 우선 — 체인에 상대 카드가 후보에 없으면 다음 규칙으로
                    var chainCall = "";
                    if (c.Pick == "chainCard")
                    {
                        int ci = chainPickRules.IndexOf(c) + 1;
                        chainCall = $"PickChainCard_{ci}({ruleSrc}, {sig.MaxExpr})";
                        parts.Add($"PickChainCard_{ci}({ruleSrc}, 1).Count > 0");
                    }

                    var call = c.Pick == "themeMatch"
                        ? themeCall
                        : (c.Pick == "chainCard" ? chainCall : $"Pick({ruleSrc}, {sig.MaxExpr}, {PolicyId(c.Pick)}, {c.Card})");
                    if (parts.Count == 0)
                    {
                        sb.AppendLine($"            return {call};");
                        decidedElsewhere = true;
                        break;
                    }
                    var guard = parts.Count == 1 ? parts[0] : "(" + string.Join(" && ", parts) + ")";
                    sb.AppendLine($"            if ({guard}) return {call};");
                    emitted++;
                }

                if (!decidedElsewhere)
                {
                    if (emitted > 0 || rulesForCb.Count > 0)
                    {
                        // B-35: 공격 대상 선택(hint=549)은 규칙이 없으면 "이길 수 있는 가장 약한 상대"를 고른다
                        if (isSelectCard)
                            sb.AppendLine("            if (hint == HintMsg.AttackTarget) return PickAttackTarget(" + srcList + ");   // B-35: 공격 대상 기본 정책");
                        sb.AppendLine($"            return Pick({srcList}, {sig.MaxExpr}, 0, 0);   // 남은 규칙 없음 → 목록 순서");
                    }
                    else if (needFilter)
                        sb.AppendLine($"            return base.{sig.Name}({sig.BaseArgs.Replace("cards", srcList)});  // 필터만 적용, 선택은 기본 AI");
                    else
                        sb.AppendLine($"            return {srcList};");
                }
                sb.AppendLine("        }");
            }

            // ---- YesNo / Option / Announce / Place / Position ----
            // B-6: 조건이 있으면 조건을 먼저 검사한다. 불충족 → 다음 우선순위 규칙 → 모두 불충족이면 base 로 위임.
            if (yesNoRules.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("        public override bool OnSelectYesNo(long desc)");
                sb.AppendLine("        {");
                bool done = false;
                foreach (var r in yesNoRules)
                {
                    var gate = ChoiceGate(r);
                    var body = "return " + (r.YesNo ? "true" : "false") + ";";
                    if (gate == null) { sb.AppendLine("            " + body); done = true; break; }
                    sb.AppendLine("            if (" + gate + ") " + body);
                }
                if (!done) sb.AppendLine("            return base.OnSelectYesNo(desc);   // 조건 불충족 → 기본 동작");
                sb.AppendLine("        }");
            }

            // B-21: debugEffectLog ON 이면 옵션 콜백 선택 결과를 콘솔에 남긴다 (규칙이 없어 base 로 위임하는 경우도 1회)
            bool dbgOption = p.DebugEffectLog;
            if (optionRules.Count > 0 || dbgOption)
            {
                sb.AppendLine();
                sb.AppendLine("        public override int OnSelectOption(IList<long> options)");
                sb.AppendLine("        {");
                if (dbgOption) sb.AppendLine("            int idx = -1;");
                bool done = false;
                foreach (var r in optionRules)
                {
                    // B-14: 같은 카드에서 여러 번 뜨는 SelectOption 을 옵션 desc(문자열 ID)로 구분한다
                    var parts = new List<string>();
                    if (r.WhenCard > 0)
                        parts.Add(WhenCardExpr(r.WhenCard));   // B-33: Card/ActivatingCard/체인 링크 모두 확인
                    var gate = ChoiceGate(r);
                    if (gate != null) parts.Add(gate);
                    if (r.OptionDesc > 0) parts.Add("options.Contains(" + LongLiteral(r.OptionDesc) + ")");
                    var idx = Math.Max(0, r.OptionIndex);
                    // B-21: 로그 모드에서는 return 대신 idx 에 담아 마지막에 1회만 기록한다
                    var pick = "options.Count == 0 ? 0 : Math.Min(" + idx + ", options.Count - 1)";
                    if (parts.Count == 0)
                    {
                        if (dbgOption) sb.AppendLine("            idx = " + pick + ";");
                        else
                        {
                            sb.AppendLine("            if (options.Count == 0) return 0;");
                            sb.AppendLine("            return Math.Min(" + idx + ", options.Count - 1);");
                        }
                        done = true;
                        break;
                    }
                    sb.AppendLine("            if (" + (dbgOption ? "idx < 0 && (" + string.Join(" && ", parts) + ")" : string.Join(" && ", parts)) + ")");
                    sb.AppendLine("            {");
                    if (dbgOption) sb.AppendLine("                idx = " + pick + ";");
                    else
                    {
                        sb.AppendLine("                if (options.Count == 0) return 0;");
                        sb.AppendLine("                return Math.Min(" + idx + ", options.Count - 1);");
                    }
                    sb.AppendLine("            }");
                }
                if (dbgOption)
                {
                    if (!done) sb.AppendLine("            if (idx < 0) idx = base.OnSelectOption(options);   // 조건 불충족 → 기본 동작 (로그는 1회)");
                    // 옵션 **전체 목록**을 남긴다 (무제물/제물 같은 소환 방법 선택 인덱스를 알아내기 위해)
                    sb.AppendLine("            string optStr = \"\";");
                    sb.AppendLine("            if (options != null)");
                    sb.AppendLine("                for (int oi = 0; oi < options.Count; oi++)");
                    sb.AppendLine("                {");
                    sb.AppendLine("                    if (oi > 0) optStr += \",\";");
                    sb.AppendLine("                    optStr += options[oi].ToString();");
                    sb.AppendLine("                }");
                    sb.AppendLine("            LogLine(\"[AI-EDITOR] option card=\" + (Card != null ? Card.Id.ToString() : \"-\") + \" n=\" + (options != null ? options.Count : 0) + \" opts=[\" + optStr + \"] -> \" + idx.ToString());   // B-27: 콘솔 + 파일");
                    sb.AppendLine("            return idx;");
                }
                else if (!done) sb.AppendLine("            return base.OnSelectOption(options);   // 조건 불충족 → 기본 동작");
                sb.AppendLine("        }");
            }

            if (announceRules.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("        public override int OnAnnounceCard(IList<int> avail)");
                sb.AppendLine("        {");
                bool done = false;
                foreach (var r in announceRules)
                {
                    var gate = ChoiceGate(r);
                    if (gate == null)
                    {
                        if (r.Card > 0)
                        {
                            sb.AppendLine("            for (int i = 0; i < avail.Count; i++)");
                            sb.AppendLine($"                if (avail[i] == {r.Card}) return avail[i];");
                        }
                        sb.AppendLine("            return avail.Count > 0 ? avail[0] : 0;");
                        done = true;
                        break;
                    }
                    sb.AppendLine("            if (" + gate + ")");
                    sb.AppendLine("            {");
                    if (r.Card > 0)
                    {
                        sb.AppendLine("                for (int i = 0; i < avail.Count; i++)");
                        sb.AppendLine($"                    if (avail[i] == {r.Card}) return avail[i];");
                    }
                    sb.AppendLine("                return avail.Count > 0 ? avail[0] : 0;");
                    sb.AppendLine("            }");
                }
                if (!done) sb.AppendLine("            return base.OnAnnounceCard(avail);   // 조건 불충족 → 기본 동작");
                sb.AppendLine("        }");
            }

            // 존 선택: 소환 존/열 회피가 같은 메서드를 쓰므로, 그때는 헬퍼만 만들고 위에서 호출한다 (중복 정의 방지)
            if (placeRules.Count > 0)
            {
                bool mergedPlace = zoneActions.Count > 0 || avoidColumnCard > 0;
                if (!mergedPlace)
                {
                    sb.AppendLine();
                    sb.AppendLine("        // ---- 선택 규칙: 존 선택 ----");
                    sb.AppendLine("        public override int OnSelectPlace(long cardId, int player, CardLocation location, int available)");
                    sb.AppendLine("        {");
                    sb.AppendLine("            int ruleZone = SelectZoneByRule(cardId, player, location, available);");
                    sb.AppendLine("            if (ruleZone >= 0) return ruleZone;   // ② 선택 규칙(조건 포함)이 먼저");
                    sb.AppendLine("            return base.OnSelectPlace(cardId, player, location, available);");
                    sb.AppendLine("        }");
                }
                sb.AppendLine();
                sb.AppendLine("        // ② 존 선택 규칙 — 조건을 순서대로 검사, 모두 불충족이면 -1 (기본 동작)");
                sb.AppendLine("        private int SelectZoneByRule(long cardId, int player, CardLocation location, int available)");
                sb.AppendLine("        {");
                bool done = false;
                foreach (var r in placeRules)
                {
                    var gate = ChoiceGate(r);
                    if (gate != null)
                    {
                        sb.AppendLine("            if (" + gate + ")");
                        sb.AppendLine("            {");
                    }
                    if (r.PlaceIndex <= 0 || string.Equals(r.Position, "first", StringComparison.OrdinalIgnoreCase))
                    {
                        sb.AppendLine("            for (int i = 0; i < 7; i++)");
                        sb.AppendLine("                if ((available & (1 << i)) != 0) return i;");
                        sb.AppendLine("            return 0;");
                    }
                    else
                    {
                        sb.AppendLine($"            if ((available & (1 << {r.PlaceIndex})) != 0) return {r.PlaceIndex};");
                        sb.AppendLine("            for (int i = 0; i < 7; i++)");
                        sb.AppendLine("                if ((available & (1 << i)) != 0) return i;");
                        sb.AppendLine("            return 0;");
                    }
                    if (gate != null) sb.AppendLine("            }");
                    else { done = true; break; }
                }
                if (!done) sb.AppendLine("            return -1;   // 조건 불충족 → 기본 존 선택");
                sb.AppendLine("        }");
            }

            // 표시형식: 소환 표시형식(공격/수비)이 같은 메서드를 쓰므로, 그때는 헬퍼만 만들고 위에서 호출한다
            if (positionRules.Count > 0)
            {
                // B-40: OnSelectPosition 오버라이드는 위에서 항상 생성되므로(토큰 규칙), 여기서는 헬퍼만 만든다
                sb.AppendLine();
                sb.AppendLine("        // ② 표시형식 규칙 — 조건을 순서대로 검사, 모두 불충족이면 -1 (기본 동작)");
                sb.AppendLine("        private int SelectPositionByRule(int cardId, IList<CardPosition> positions)");
                sb.AppendLine("        {");
                bool done = false;
                foreach (var r in positionRules)
                {
                    var gate = ChoiceGate(r);
                    if (gate != null)
                    {
                        sb.AppendLine("            if (" + gate + ")");
                        sb.AppendLine("            {");
                    }
                    if (string.Equals(r.Position, "defence", StringComparison.OrdinalIgnoreCase))
                    {
                        sb.AppendLine("            for (int i = 0; i < positions.Count; i++)");
                        sb.AppendLine("                if (positions[i] == CardPosition.FaceUpDefence || positions[i] == CardPosition.FaceDownDefence) return (int)positions[i];");
                        sb.AppendLine("            return positions.Count > 0 ? (int)positions[0] : (int)CardPosition.FaceUpDefence;");
                    }
                    else
                    {
                        sb.AppendLine("            for (int i = 0; i < positions.Count; i++)");
                        sb.AppendLine("                if (positions[i] == CardPosition.FaceUpAttack || positions[i] == CardPosition.FaceDownAttack) return (int)positions[i];");
                        sb.AppendLine("            return positions.Count > 0 ? (int)positions[0] : (int)CardPosition.FaceUpAttack;");
                    }
                    if (gate != null) sb.AppendLine("            }");
                    else { done = true; break; }
                }
                if (!done) sb.AppendLine("            return -1;   // 조건 불충족 → 기본 표시형식");
                sb.AppendLine("        }");
            }

            // ---- 도우미 ----
            var allConds = new List<Condition>();
            foreach (var a in actions) allConds.AddRange(a.Conditions ?? new List<Condition>());
            foreach (var f in forbids) allConds.AddRange(f.Conditions ?? new List<Condition>());
            // ② 선택 규칙 조건(B-6)도 도우미가 필요할 수 있다
            foreach (var c in choices) allConds.AddRange(c.Conditions ?? new List<Condition>());
            // 전개 루트(벌 진입 조건 + 순위 조건)도 도우미가 필요할 수 있다
            foreach (var set in firstSets.Concat(secondSets))
            {
                allConds.AddRange(set.Conditions ?? new List<Condition>());
                foreach (var st in RouteSteps(set))
                    foreach (var o in OptionsOf(st))
                        allConds.AddRange(o.Conditions ?? new List<Condition>());
            }

            bool needBest = allConds.Any(c => c.Kind == "enemyBestAttack" || c.Kind == "myBestAttack");
            bool needOnField = allConds.Any(c => c.Kind == "hasOnField") || triggers.Count > 0;
            bool needAttacking = allConds.Any(c => c.Kind == "enemyAttackingCount");
            bool needFaceUp = allConds.Any(c => c.Kind == "enemyFaceUpCount");
            bool needFaceDown = allConds.Any(c => c.Kind == "enemyFaceDownCount");
            bool needLevel = allConds.Any(c => c.Kind == "fieldHasLevel");
            bool needLevelCount = allConds.Any(c => c.Kind == "fieldLevelCount");   // B-20
            bool needEmz = allConds.Any(c => c.Kind == "fieldCardInEmz");           // B-20
            bool needPendulum = allConds.Any(c => c.Kind == "fieldHasPendulum" || c.Kind == "pendulumScale");   // N-6
            bool needRank = allConds.Any(c => c.Kind == "fieldHasRank");
            bool needLink = allConds.Any(c => c.Kind == "fieldHasLink");
            bool needRace = allConds.Any(c => c.Kind == "fieldHasRace");
            bool needThemeRaceXyz = allConds.Any(c => c.Kind == "fieldHasThemeRaceXyz");   // B-21
            bool needTuner = allConds.Any(c => c.Kind == "fieldHasTunerLevel");
            bool needEvents = allConds.Any(c => EventId(c.Kind) >= 0);
            bool needTheme = allConds.Any(c => c.Kind == "fieldHasTheme" || c.Kind == "handHasTheme" || c.Kind == "gyHasTheme");
            bool needThemeMonster = allConds.Any(c => c.MonsterOnly &&
                (c.Kind == "fieldHasTheme" || c.Kind == "handHasTheme" || c.Kind == "gyHasTheme"));
            bool needSetcode = needTheme || themePickRules.Count > 0 || needThemeRaceXyz || forbidThemeSummon.Count > 0;   // B-26 포함
            // B-32: 테마 조건에 제외 카드가 붙었는가
            bool needExtraHas = allConds.Any(c => c.Kind == "extraHas");   // 2026-10-29
            bool needThemeExcept = allConds.Any(c => (c.Kind == "handHasTheme" || c.Kind == "gyHasTheme" || c.Kind == "fieldHasTheme")
                                                     && c.ExcludeCards != null && c.ExcludeCards.Count > 0);
            bool needOppAct = allConds.Any(c => c.Kind == "oppMonsterActivated" || c.Kind == "oppSpellActivated" || c.Kind == "oppTrapActivated");
            bool needMyAct = allConds.Any(c => c.Kind == "myCardActivated" || c.Kind == "myHandActivated");
            bool needPick = listChoices.Count > 0;

            // B-29: 내성 몹 제외 필터가 필요한가 (SelectCard 규칙 중 skipResistant)
            bool needResistFilter = choices.Any(c => c.On == "SelectCard" && c.SkipResistant);

            sb.AppendLine();
            if (needBest) sb.Append(StaticHelper("BestAttack", "int", "ClientField f",
                "            int best = 0;",
                "            for (int i = 0; i < f.MonsterZone.Length; i++)",
                "            {",
                "                ClientCard c = f.MonsterZone[i];",
                "                if (c != null && c.Attack > best) best = c.Attack;",
                "            }",
                "            return best;"));

            if (needAttacking) sb.Append(StaticHelper("CountAttacking", "int", "ClientField f",
                "            int n = 0;",
                "            for (int i = 0; i < f.MonsterZone.Length; i++)",
                "            {",
                "                ClientCard c = f.MonsterZone[i];",
                "                if (c != null && c.IsAttack()) n++;",
                "            }",
                "            return n;"));

            if (needFaceUp) sb.Append(StaticHelper("CountFaceUp", "int", "ClientField f",
                "            int n = 0;",
                "            for (int i = 0; i < f.MonsterZone.Length; i++)",
                "            {",
                "                ClientCard c = f.MonsterZone[i];",
                "                if (c != null && ((int)c.Position & 0x5) != 0) n++;   // 앞면: 공격(0x1)/수비(0x4)",
                "            }",
                "            for (int i = 0; i < f.SpellZone.Length; i++)",
                "            {",
                "                ClientCard c = f.SpellZone[i];",
                "                if (c != null && ((int)c.Position & 0x5) != 0) n++;",
                "            }",
                "            return n;"));

            if (needFaceDown) sb.Append(StaticHelper("CountFaceDown", "int", "ClientField f",
                "            int n = 0;",
                "            for (int i = 0; i < f.MonsterZone.Length; i++)",
                "            {",
                "                ClientCard c = f.MonsterZone[i];",
                "                if (c != null && ((int)c.Position & 0xA) != 0) n++;   // 뒷면: 세트(0x2/0x8)",
                "            }",
                "            for (int i = 0; i < f.SpellZone.Length; i++)",
                "            {",
                "                ClientCard c = f.SpellZone[i];",
                "                if (c != null && ((int)c.Position & 0xA) != 0) n++;",
                "            }",
                "            return n;"));

            if (needOnField) sb.Append(StaticHelper("OnField", "bool", "ClientField f, int code",
                "            for (int i = 0; i < f.MonsterZone.Length; i++)",
                "            {",
                "                ClientCard c = f.MonsterZone[i];",
                "                if (c != null && c.IsCode(code)) return true;",
                "            }",
                "            for (int i = 0; i < f.SpellZone.Length; i++)",
                "            {",
                "                ClientCard c = f.SpellZone[i];",
                "                if (c != null && c.IsCode(code)) return true;",
                "            }",
                "            return false;"));

            if (needLevel) sb.Append(StaticHelper("FieldHasLevel", "bool", "ClientField f, int level",
                "            for (int i = 0; i < f.MonsterZone.Length; i++)",
                "            {",
                "                ClientCard c = f.MonsterZone[i];",
                "                if (c == null) continue;",
                "                if (level == 0 ? c.Level > 0 : c.Level == level) return true;",
                "            }",
                "            return false;"));

            if (needRank) sb.Append(StaticHelper("FieldHasRank", "bool", "ClientField f, int rank",
                "            for (int i = 0; i < f.MonsterZone.Length; i++)",
                "            {",
                "                ClientCard c = f.MonsterZone[i];",
                "                if (c == null) continue;",
                "                if (rank == 0 ? c.Rank > 0 : c.Rank == rank) return true;",
                "            }",
                "            return false;"));

            if (needLink) sb.Append(StaticHelper("FieldHasLink", "bool", "ClientField f, int link",
                "            for (int i = 0; i < f.MonsterZone.Length; i++)",
                "            {",
                "                ClientCard c = f.MonsterZone[i];",
                "                if (c == null) continue;",
                "                if (link == 0 ? c.LinkCount > 0 : c.LinkCount == link) return true;",
                "            }",
                "            return false;"));

            if (needRace) sb.Append(StaticHelper("FieldHasRace", "bool", "ClientField f, int race",
                "            for (int i = 0; i < f.MonsterZone.Length; i++)",
                "            {",
                "                ClientCard c = f.MonsterZone[i];",
                "                if (c == null) continue;",
                "                if (race == 0 || c.HasRace((CardRace)race)) return true;",
                "            }",
                "            return false;"));

            if (needTuner) sb.Append(StaticHelper("FieldHasTuner", "bool", "ClientField f, int level",
                "            if (f == null || f.MonsterZone == null) return false;",
                "            for (int i = 0; i < f.MonsterZone.Length; i++)",
                "            {",
                "                ClientCard c = f.MonsterZone[i];",
                "                if (c == null || !c.HasType(CardType.Tuner)) continue;",
                "                if (level == 0 || c.Level == level) return true;",
                "            }",
                "            return false;"));
            // B-20: 엑스트라 몬스터 존(MonsterZone 마지막 2칸)에 특정 카드가 있는지
            if (needEmz) sb.Append(StaticHelper("FieldHasCardInEmz", "bool", "ClientField f, int code",
                "            if (f == null || f.MonsterZone == null) return false;",
                "            int start = f.MonsterZone.Length - 2;   // EMZ = 마지막 2칸 (EDOPro 는 7칸 = 메인 5 + 엑몹 2)",
                "            if (start < 0) start = 0;",
                "            for (int i = start; i < f.MonsterZone.Length; i++)",
                "            {",
                "                ClientCard c = f.MonsterZone[i];",
                "                if (c != null && c.IsCode(code)) return true;",
                "            }",
                "            return false;"));
            // B-20: 필드에서 특정 레벨 몬스터가 몇 장인지 (level 0 = 레벨 무관 = 모든 몬스터)
            if (needLevelCount) sb.Append(StaticHelper("CountLevels", "int", "ClientField f, int level, bool onlyMonster",
                "            if (f == null || f.MonsterZone == null) return 0;",
                "            int n = 0;",
                "            for (int i = 0; i < f.MonsterZone.Length; i++)",
                "            {",
                "                ClientCard c = f.MonsterZone[i];",
                "                if (c == null) continue;",
                "                if ((c.Type & 0x800000) != 0) continue;    // 엑시즈는 레벨 없음(랭크)",
                "                if ((c.Type & 0x4000000) != 0) continue;   // 링크는 레벨 없음",
                "                if (onlyMonster && (c.Data == null || (c.Data.Type & 0x1) == 0)) continue;",
                "                if (level == 0 ? c.Level > 0 : c.Level == level) n++;",
                "            }",
                "            return n;"));
            // N-6: 펜듈럼 — P존(마함존 0·4) 카드 수와 스케일 범위 (ClientCard.LScale/RScale)
            if (needPendulum) sb.Append(StaticHelper("PendulumCount", "int", "ClientField f",
                "            if (f == null || f.SpellZone == null) return 0;",
                "            int n = 0;",
                "            for (int i = 0; i < f.SpellZone.Length && i < 5; i++)",
                "            {",
                "                ClientCard c = f.SpellZone[i];",
                "                if (c != null && (c.Type & 0x1000000) != 0) n++;   // 펜듈럼",
                "            }",
                "            return n;"));
            if (needPendulum) sb.Append(StaticHelper("PendulumCovers", "bool", "ClientField f, int level",
                "            if (f == null || f.SpellZone == null) return false;",
                "            int low = 99, high = 0;",
                "            for (int i = 0; i < f.SpellZone.Length && i < 5; i++)",
                "            {",
                "                ClientCard c = f.SpellZone[i];",
                "                if (c == null || (c.Type & 0x1000000) == 0) continue;",
                "                if (c.LScale > 0 && c.LScale < low) low = c.LScale;",
                "                if (c.RScale > 0 && c.RScale > high) high = c.RScale;",
                "            }",
                "            return high > low && level > low && level < high;   // 스케일 사이(양 끝 제외)"));
            // B-21: 같은 카드가 테마 + 종족 + 엑시즈를 모두 만족하는지 (0 = 해당 검사 생략)
            if (needThemeRaceXyz) sb.Append(StaticHelper("FieldHasThemeRaceXyz", "bool", "ClientField f, int theme, ulong race",
                "            if (f == null || f.MonsterZone == null) return false;",
                "            for (int i = 0; i < f.MonsterZone.Length; i++)",
                "            {",
                "                ClientCard c = f.MonsterZone[i];",
                "                if (c == null || c.Data == null) continue;",
                "                if ((c.Type & 0x800000) == 0) continue;              // 엑시즈",
                "                if (race != 0 && (c.Race & race) == 0) continue;     // 종족 (0 = 무관)",
                "                if (theme != 0 && !HasSetcode(c, theme)) continue;   // 테마 (0 = 무관)",
                "                return true;",
                "            }",
                "            return false;"));
            if (needSetcode) EmitHasSetcodeHelper(sb);
            if (needResistFilter) EmitResistFilterHelper(sb);
            if (needPick) EmitAttackTargetHelper(sb);   // B-35: 공격 대상 기본 정책
            if (needTheme) EmitThemeHelpers(sb, needThemeMonster);
            if (needThemeExcept) EmitThemeExceptHelpers(sb);   // B-32
            if (needExtraHas) EmitExtraHasHelper(sb);
            if (needMyAct) EmitMyActivatedHelper(sb);
            if (needOppAct) EmitOppActivatedHelper(sb);
            if (needEvents) EmitEventCounters(sb);

            if (needPick)
            {
                var codeMatchGroups = choices.Where(ch => ch != null && ch.Pick == "codeMatch" && ch.Card > 0)
                    .Select(ch => ch.Card).Distinct()
                    .Select(AliasIds)
                    .Where(g => g.Count > 1)
                    .ToList();
                if (codeMatchGroups.Count > 0) EmitAliasCodeMatchHelper(sb, codeMatchGroups);

                sb.AppendLine("        // policy: 0=목록순 1=공격력↓ 2=공격력↑ 3=수비력↓ 4=레벨↑ 5=특정카드우선");
                sb.AppendLine("        private static IList<ClientCard> Pick(IList<ClientCard> cards, int max, int policy, int code)");
                sb.AppendLine("        {");
                sb.AppendLine("            List<ClientCard> pool = new List<ClientCard>(cards);");
                sb.AppendLine("            if (policy == 1) pool.Sort(delegate(ClientCard a, ClientCard b) { return b.Attack.CompareTo(a.Attack); });");
                sb.AppendLine("            else if (policy == 2) pool.Sort(delegate(ClientCard a, ClientCard b) { return a.Attack.CompareTo(b.Attack); });");
                sb.AppendLine("            else if (policy == 3) pool.Sort(delegate(ClientCard a, ClientCard b) { return b.Defense.CompareTo(a.Defense); });");
                sb.AppendLine("            else if (policy == 4) pool.Sort(delegate(ClientCard a, ClientCard b) { return a.Level.CompareTo(b.Level); });");
                sb.AppendLine("            List<ClientCard> picked = new List<ClientCard>();");
                sb.AppendLine("            if (code > 0)");
                sb.AppendLine("            {");
                sb.AppendLine("                for (int i = 0; i < pool.Count && picked.Count < max; i++)");
                sb.AppendLine("                    if (" + (codeMatchGroups.Count > 0 ? "IsCodeAlias(pool[i], code)" : "pool[i].IsCode(code)") + ") picked.Add(pool[i]);");
                sb.AppendLine("            }");
                sb.AppendLine("            for (int i = 0; i < pool.Count && picked.Count < max; i++)");
                sb.AppendLine("                if (!picked.Contains(pool[i])) picked.Add(pool[i]);");
                sb.AppendLine("            return picked;");
                sb.AppendLine("        }");
                sb.AppendLine();
            }

            // B-14: 테마 카드 우선 정책 헬퍼
            if (themePickRules.Count > 0) EmitThemePickHelpers(sb, themePickRules);

            // B-17: 발동 중인 카드(체인) 우선 정책 헬퍼
            if (chainPickRules.Count > 0) EmitChainPickHelpers(sb, chainPickRules);

            // B-22: 제외 카드 후보 필터 헬퍼 (SelectCard 전용)
            if (excludePickRules.Count > 0) EmitExcludeCodeHelpers(sb, excludePickRules);

            if (triggers.Count > 0)
            {
                var groupExprs = triggers.GroupBy(t => t.Group).OrderBy(g => g.Key)
                    .Select(g => "(" + GroupExpression(g.OrderBy(t => t.Priority).ToList()) + ")").ToList();
                sb.AppendLine("        private bool BuildComplete()");
                sb.AppendLine("        {");
                sb.AppendLine("            return " + string.Join(" || ", groupExprs) + ";");
                sb.AppendLine("        }");
                sb.AppendLine();
            }

            if (p.DebugEffectLog)
            {
                sb.AppendLine("        private void LogForbid(string kind, ClientCard target)");
                sb.AppendLine("        {");
                sb.AppendLine("            LogLine(\"[AI-EDITOR] forbid \" + kind + \" card=\" + (target != null ? target.Id.ToString() : \"-\") + \" ctrl=\" + (target != null ? target.Controller.ToString() : \"-\") + \" loc=\" + (target != null ? ((int)target.Location).ToString() : \"-\"));");
                sb.AppendLine("        }");
                sb.AppendLine();
            }
            if (p.DebugEffectLog)
            {
                sb.AppendLine("        // 효과번호 확인용 로그 (WindBot 콘솔에 표시)");
                sb.AppendLine("        private bool CheckActivate()");
                sb.AppendLine("        {");
                sb.AppendLine("            LogLine(\"[AI-EDITOR] activate \" + (Card != null ? Card.Id.ToString() : \"?\") + \" (\" + (Card != null ? Card.Name : \"\") + \") desc=\" + ActivateDescription);");
                sb.AppendLine("            return true;");
                sb.AppendLine("        }");
                sb.AppendLine();
            }

            // 금지 판정 헬퍼들 (턴 조건 포함)
            if (forbidActivate.Count > 0) sb.Append(ForbidHelper("IsForbiddenActivate", forbidActivate, f => IsCodeOr("card", f.Card), true));
            if (forbidSummon.Count > 0) sb.Append(ForbidHelper("IsForbiddenSummon", forbidSummon, f => IsCodeOr("card", f.Card), true));
            if (forbidNoUse.Count > 0) sb.Append(ForbidHelper("IsForbiddenNoUse", forbidNoUse, f => IsCodeOr("card", f.Card), true));
            // B-26: 테마 일반소환·세트 금지 — 자유 플레이의 Summon/MonsterSet 에만 붙는다(특수소환 제외)
            if (forbidThemeSummon.Count > 0) sb.Append(ThemeSummonForbidHelper(forbidThemeSummon));
            if (forbidAttack.Count > 0) sb.Append(ForbidHelper("IsForbiddenAttack", forbidAttack, f => IsCodeOr("card", f.Card), true));
            if (forbidTarget.Count > 0) sb.Append(ForbidHelper("IsForbiddenTarget", forbidTarget, f => IsCodeOr("card", f.Card), true));
            if (forbidRelease.Count > 0) sb.Append(ForbidHelper("IsForbiddenRelease", forbidRelease, f => IsCodeOr("card", f.Card), true));

            // B-16: 엑스트라 덱 몬스터(융합·싱크로·엑시즈·링크) 판정 + 릴리스 후보 필터
            if (protectExtra)
            {
                sb.AppendLine("        // B-16: 엑스트라 덱 몬스터인가 (융합 0x40 / 싱크로 0x2000 / 엑시즈 0x800000 / 링크 0x4000000)");
                sb.AppendLine("        private static bool IsExtraDeckMonster(ClientCard c)");
                sb.AppendLine("        {");
                sb.AppendLine("            if (c == null || c.Data == null) return false;");
                sb.AppendLine("            int t = c.Data.Type;");
                sb.AppendLine("            return (t & 0x40) != 0");
                sb.AppendLine("                || (t & 0x2000) != 0");
                sb.AppendLine("                || (t & 0x800000) != 0");
                sb.AppendLine("                || (t & 0x4000000) != 0;");
                sb.AppendLine("        }");
                sb.AppendLine();
                sb.AppendLine("        // B-16: 릴리스/제물 후보에서 엑스트라 덱 몬스터를 뺀다.");
                sb.AppendLine("        //   남은 후보가 min 미만(또는 0장)이면 원래 목록을 그대로 쓴다 — 엔진이 요구한 최소 선택 수를 깨지 않기 위해.");
                sb.AppendLine("        private IList<ClientCard> FilterExtraRelease(IList<ClientCard> cards, int min)");
                sb.AppendLine("        {");
                sb.AppendLine("            if (cards == null) return cards;");
                sb.AppendLine("            List<ClientCard> keep = new List<ClientCard>();");
                sb.AppendLine("            for (int i = 0; i < cards.Count; i++)");
                sb.AppendLine("                if (!IsExtraDeckMonster(cards[i])) keep.Add(cards[i]);");
                sb.AppendLine("            if (keep.Count == 0 || keep.Count < min)");
                sb.AppendLine("            {");
                sb.AppendLine("                Console.WriteLine(\"[AI-EDITOR] protect-extra: 엑스트라 덱 몬스터 제외 후 후보 부족, 원래 목록 사용\");");
                sb.AppendLine("                return cards;");
                sb.AppendLine("            }");
                sb.AppendLine("            return keep;");
                sb.AppendLine("        }");
                sb.AppendLine();
                // B-18: 제물 여력 검사 (IsExtraDeckMonster 재사용 — 중복 정의 금지)
                if (needTributeGuard)
                {
                    sb.AppendLine("        // B-18: 제물 여력 — 레벨 5~6 = 1장, 레벨 7+ = 2장이 필요한데");
                    sb.AppendLine("        //   내 필드의 '메인 덱 몬스터'가 그보다 적으면 자유 플레이 일반소환을 아예 시도하지 않는다");
                    sb.AppendLine("        //   (B-16 필터는 후보가 부족하면 원본으로 폴백하므로, 시도 자체를 막아야 엑시즈가 릴리스되지 않는다)");
                    sb.AppendLine("        private static bool NeedsTributeFodder(ClientField f, ClientCard c)");
                    sb.AppendLine("        {");
                    sb.AppendLine("            if (c == null || c.Data == null) return false;");
                    sb.AppendLine("            int need = c.Level >= 7 ? 2 : (c.Level >= 5 ? 1 : 0);");
                    sb.AppendLine("            if (need <= 0) return false;");
                    sb.AppendLine("            int have = 0;");
                    sb.AppendLine("            for (int i = 0; i < f.MonsterZone.Length; i++)");
                    sb.AppendLine("            {");
                    sb.AppendLine("                ClientCard m = f.MonsterZone[i];");
                    sb.AppendLine("                if (m == null || IsExtraDeckMonster(m)) continue;");
                    sb.AppendLine("                have++;");
                    sb.AppendLine("            }");
                    sb.AppendLine("            return have < need;");
                    sb.AppendLine("        }");
                    sb.AppendLine();
                    // B-46: 가드 예외 카드 (무제물 일반소환 카드 — 제물이 없어도 소환 시도)
                    if (tributeGuardExcept.Count > 0)
                    {
                        sb.AppendLine("        // B-46: 제물 여력 가드 예외 — EFFECT_SUMMON_PROC 로 무제물 일반소환하는 카드");
                        sb.AppendLine("        private static bool IsTributeGuardExempt(ClientCard c)");
                        sb.AppendLine("        {");
                        sb.AppendLine("            if (c == null) return false;");
                        sb.AppendLine("            return " + string.Join(" || ", tributeGuardExcept.Select(x => "c.IsCode(" + x + ")")) + ";");
                        sb.AppendLine("        }");
                        sb.AppendLine();
                    }
                }
            }

            if (forbidChainToMine.Count > 0)
            {
                sb.AppendLine("        // B-13: 지금 발동하려는 것이 '내 카드에 체인'인가 (체인 마지막 링크의 컨트롤러가 0)");
                sb.AppendLine("        private bool ChainRespondsToMine()");
                sb.AppendLine("        {");
                sb.AppendLine("            if (Duel.CurrentChain == null || Duel.CurrentChain.Count == 0) return false;");
                sb.AppendLine("            ClientCard last = Duel.CurrentChain[Duel.CurrentChain.Count - 1];");
                sb.AppendLine("            return last != null && last.Controller == 0;");
                sb.AppendLine("        }");
                sb.AppendLine();
            }

            if (needHasCode)
            {
                sb.AppendLine("        // 후보 목록에 특정 카드가 있는지 (없으면 다음 우선순위 규칙으로 넘어간다)");
                sb.AppendLine("        private static bool HasCode(IList<ClientCard> cards, int code)");
                sb.AppendLine("        {");
                sb.AppendLine("            if (cards == null) return false;");
                sb.AppendLine("            for (int i = 0; i < cards.Count; i++)");
                sb.AppendLine("                if (cards[i] != null && cards[i].IsCode(code)) return true;");
                sb.AppendLine("            return false;");
                sb.AppendLine("        }");
                sb.AppendLine();
            }

            if (needActivatingCard)
            {
                sb.AppendLine("        // 지금 처리 중인 발동 카드 추정: Executor.Card 또는 현재 체인의 마지막 링크");
                sb.AppendLine("        private ClientCard ActivatingCard()");
                sb.AppendLine("        {");
                sb.AppendLine("            if (Duel.CurrentChain != null && Duel.CurrentChain.Count > 0)");
                sb.AppendLine("            {");
                sb.AppendLine("                ClientCard last = Duel.CurrentChain[Duel.CurrentChain.Count - 1];");
                sb.AppendLine("                if (last != null) return last;");
                sb.AppendLine("            }");
                sb.AppendLine("            return Card;");
                sb.AppendLine("        }");
                sb.AppendLine();
                sb.AppendLine();
                sb.AppendLine("        // B-33: 현재 체인 안에 그 카드가 있는지 (해결 중 링크 판별 보조)");
                sb.AppendLine("        private bool InChain(int code)");
                sb.AppendLine("        {");
                sb.AppendLine("            if (Duel.CurrentChain == null) return false;");
                sb.AppendLine("            for (int i = 0; i < Duel.CurrentChain.Count; i++)");
                sb.AppendLine("            {");
                sb.AppendLine("                ClientCard c = Duel.CurrentChain[i];");
                sb.AppendLine("                if (c != null && c.IsCode(code)) return true;");
                sb.AppendLine("            }");
                sb.AppendLine("            return false;");
                sb.AppendLine("        }");
            }
            if (forbidNoTargetField.Count > 0 || forbidNoTargetCard.Count > 0 || forbidNoTargetEnemy.Count > 0)
            {
                sb.AppendLine("        // 현재 체인 링크가 모두 내 것 / 모두 상대 것인지");
                sb.AppendLine("        private bool ChainLinksBelongTo(bool mine)");
                sb.AppendLine("        {");
                sb.AppendLine("            IList<ClientCard> links = (Duel.CurrentChain != null && Duel.CurrentChain.Count > 0) ? Duel.CurrentChain : Duel.ChainTargets;");
                sb.AppendLine("            if (links == null || links.Count == 0) return false;");
                sb.AppendLine("            bool anyMatch = false;");
                sb.AppendLine("            bool anyOther = false;");
                sb.AppendLine("            for (int i = 0; i < links.Count; i++)");
                sb.AppendLine("            {");
                sb.AppendLine("                ClientCard t = links[i];");
                sb.AppendLine("                if (t == null) continue;");
                sb.AppendLine("                if ((t.Controller == 0) == mine) anyMatch = true; else anyOther = true;");
                sb.AppendLine("            }");
                sb.AppendLine("            return anyMatch && !anyOther;");
                sb.AppendLine("        }");
                sb.AppendLine();
            }

            if (forbidNoTargetEnemy.Count > 0)
            {
                sb.AppendLine("        // 발동 카드(owner)가 금지 대상이면 상대 카드(target)를 후보에서 제외");
                sb.AppendLine("        private bool IsForbiddenNoTargetEnemy(ClientCard owner, ClientCard target)");
                sb.AppendLine("        {");
                sb.AppendLine("            if (owner == null || target == null) return false;");
                sb.AppendLine("            if (target.Controller == 0) return false;");
                foreach (var f in forbidNoTargetEnemy)
                    sb.AppendLine($"            if ({IsCodeOr("owner", f.Card)} && ({TurnExpr(f)}){Exc(f)}) return true;");
                sb.AppendLine("            return false;");
                sb.AppendLine("        }");
                sb.AppendLine();
            }

            if (chainLimits.Count > 0)
            {
                int basisMode = p.ChainBasis == "name" ? 1 : (p.ChainBasis == "theme" ? 2 : 0);
                sb.AppendLine("        // ---- 체인 중복 제한 (같은 카드/이름/테마가 이미 체인에 있는지) ----");
                sb.AppendLine("        private static readonly int ChainBasisMode = " + basisMode + ";   // 0=카드ID 1=카드명 2=테마 (readonly: CS0162 방지)");
                sb.AppendLine();
                sb.AppendLine("        private int ChainCount(ClientCard card)");
                sb.AppendLine("        {");
                sb.AppendLine("            if (card == null || Duel.CurrentChain == null) return 0;");
                sb.AppendLine("            int n = 0;");
                sb.AppendLine("            for (int i = 0; i < Duel.CurrentChain.Count; i++)");
                sb.AppendLine("                if (ChainSameKind(Duel.CurrentChain[i], card)) n++;");
                sb.AppendLine("            return n;");
                sb.AppendLine("        }");
                sb.AppendLine();
                sb.AppendLine("        private static bool ChainSameKind(ClientCard a, ClientCard b)");
                sb.AppendLine("        {");
                sb.AppendLine("            if (a == null || b == null) return false;");
                sb.AppendLine("            if (ChainBasisMode == 0) return a.Id == b.Id && a.Id > 0;");
                sb.AppendLine("            if (ChainBasisMode == 1)");
                sb.AppendLine("                return a.Name != null && b.Name != null && a.Name.Length > 0 && string.Equals(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);");
                sb.AppendLine("            return SameTheme(a, b);");
                sb.AppendLine("        }");
                sb.AppendLine();
                sb.AppendLine("        private static bool SameTheme(ClientCard a, ClientCard b)");
                sb.AppendLine("        {");
                sb.AppendLine("            if (a == null || b == null || a.Data == null || b.Data == null) return false;");
                sb.AppendLine("            long sa = a.Data.Setcode;");
                sb.AppendLine("            long sb = b.Data.Setcode;");
                sb.AppendLine("            for (int i = 0; i < 4; i++)");
                sb.AppendLine("            {");
                sb.AppendLine("                long code = (sa >> (i * 16)) & 0xFFFF;");
                sb.AppendLine("                if (code == 0) continue;");
                sb.AppendLine("                for (int j = 0; j < 4; j++)");
                sb.AppendLine("                    if (((sb >> (j * 16)) & 0xFFFF) == code) return true;");
                sb.AppendLine("            }");
                sb.AppendLine("            return false;");
                sb.AppendLine("        }");
                sb.AppendLine();
            }
            if (forbidNoTargetField.Count > 0 || forbidNoTargetCard.Count > 0)
            {
                sb.AppendLine("        // 발동하려는 카드가 '내 카드'를 향하는지 판정 (체인 대상 → 없으면 마지막 체인 링크)");
                sb.AppendLine("        private bool ChainTargetsAreMine(bool fieldOnly)");
                sb.AppendLine("        {");
                sb.AppendLine("            bool anyMine = false;");
                sb.AppendLine("            bool anyOther = false;");
                sb.AppendLine("            if (Duel.ChainTargets != null)");
                sb.AppendLine("            {");
                sb.AppendLine("                for (int i = 0; i < Duel.ChainTargets.Count; i++)");
                sb.AppendLine("                {");
                sb.AppendLine("                    ClientCard t = Duel.ChainTargets[i];");
                sb.AppendLine("                    if (t == null) continue;");
                sb.AppendLine("                    bool isMine = t.Controller == 0 && (!fieldOnly || (t.Location & CardLocation.Onfield) != 0);");
                sb.AppendLine("                    if (isMine) anyMine = true; else anyOther = true;");
                sb.AppendLine("                }");
                sb.AppendLine("            }");
                sb.AppendLine("            if (anyMine && !anyOther) return true;");
                sb.AppendLine();
                sb.AppendLine("            // 대상 없는 효과(예: 발동에 반응)는 지금 응답 대상인 마지막 체인 링크로 판정");
                sb.AppendLine("            if (!anyMine && !anyOther && Duel.CurrentChain != null && Duel.CurrentChain.Count > 0)");
                sb.AppendLine("            {");
                sb.AppendLine("                ClientCard last = Duel.CurrentChain[Duel.CurrentChain.Count - 1];");
                sb.AppendLine("                if (last != null && last.Controller == 0 && (!fieldOnly || (last.Location & CardLocation.Onfield) != 0))");
                sb.AppendLine("                    return true;");
                sb.AppendLine("            }");
                sb.AppendLine("            return false;");
                sb.AppendLine("        }");
                sb.AppendLine();
            }
            if (forbidNoTargetField.Count > 0)
            {
                sb.AppendLine("        // 발동 카드(owner)가 금지 대상이면, 내 필드 카드(target)를 후보에서 제외");
                sb.AppendLine("        private bool IsForbiddenNoTargetMyField(ClientCard owner, ClientCard target)");
                sb.AppendLine("        {");
                sb.AppendLine("            if (owner == null || target == null) return false;");
                sb.AppendLine("            if (target.Controller != 0) return false;");
                sb.AppendLine("            if ((target.Location & CardLocation.Onfield) == 0) return false;");
                foreach (var f in forbidNoTargetField)
                    sb.AppendLine($"            if ({IsCodeOr("owner", f.Card)} && ({TurnExpr(f)}){Exc(f)}) return true;");
                sb.AppendLine("            return false;");
                sb.AppendLine("        }");
                sb.AppendLine();
            }

            if (forbidNoTargetCard.Count > 0)
            {
                sb.AppendLine("        // 발동 카드(owner)가 금지 대상이면, 내 모든 카드(target)를 후보에서 제외 (덱 제외)");
                sb.AppendLine("        private bool IsForbiddenNoTargetMyCard(ClientCard owner, ClientCard target)");
                sb.AppendLine("        {");
                sb.AppendLine("            if (owner == null || target == null) return false;");
                sb.AppendLine("            if (target.Controller != 0) return false;");
                sb.AppendLine("            if ((target.Location & CardLocation.Deck) != 0) return false;");
                foreach (var f in forbidNoTargetCard)
                    sb.AppendLine($"            if ({IsCodeOr("owner", f.Card)} && ({TurnExpr(f)}){Exc(f)}) return true;");
                sb.AppendLine("            return false;");
                sb.AppendLine("        }");
                sb.AppendLine();
            }

            if (effectBlocks.Count > 0)
            {
                sb.AppendLine("        // 특정 효과 발동 금지 (효과번호/턴)");
                sb.AppendLine("        private bool IsForbiddenEffect(ClientCard card, long desc)");
                sb.AppendLine("        {");
                sb.AppendLine("            if (card == null) return false;");
                foreach (var f in effectBlocks)
                {
                    var parts = new List<string> { IsCodeOr("card", f.Card) };
                    if (f.EffectDesc >= 0) parts.Add($"desc == {f.EffectDesc}");
                    parts.Add(TurnExpr(f));
                    sb.AppendLine("            if (" + string.Join(" && ", parts) + ") return true;");
                }
                sb.AppendLine("            return false;");
                sb.AppendLine("        }");
                sb.AppendLine();
            }

            sb.AppendLine("    }");
            sb.AppendLine("}");
            return sb.ToString();
        }

        // ================= helpers =================

        // ---- B-12: 같은 이름 카드(대체 일러 / 재록) 별칭 ----

        /// <summary>별칭을 최대 몇 개까지 코드에 쓸지 (초과분은 경고 후 버린다 — 코드 폭증 방지)</summary>
        private const int AliasMax = 8;

        /// <summary>이번 Generate 에서 별칭 확장을 쓰는지 (AiProfile.SameNameAliases)</summary>
        private static bool _aliasesOn;

        /// <summary>카드 ID → 별칭 목록 캐시 (cdb 별칭 인덱스가 비싸므로 생성 중 재사용)</summary>
        private static readonly Dictionary<int, IReadOnlyList<int>> _aliasMemo = new Dictionary<int, IReadOnlyList<int>>();

        /// <summary>이번 Generate 에서 수집한 경고 (별칭 8개 초과·cdb 미로드 등). UI/CLI 가 읽어간다.</summary>
        public static List<string> Warnings { get; } = new List<string>();

        /// <summary>카드 ID 의 같은 이름 별칭 목록 (첫 값은 항상 자기 자신). 설정 OFF/이름 없음 → 자기 자신만.</summary>
        private static IReadOnlyList<int> AliasIds(int id)
        {
            if (id <= 0) return Array.Empty<int>();
            if (!_aliasesOn) return new[] { id };
            if (_aliasMemo.TryGetValue(id, out var cached)) return cached;

            IReadOnlyList<int> list;
            try { list = CardDatabase.Instance.AliasesOf(id); }
            catch { list = new[] { id }; }
            if (list == null || list.Count == 0) list = new[] { id };

            if (list.Count > AliasMax)
            {
                Warnings.Add("카드 " + id + " 의 같은 이름 카드가 " + list.Count + "개 → 앞 " + AliasMax + "개만 사용");
                var cut = new List<int>(AliasMax);
                for (int i = 0; i < AliasMax && i < list.Count; i++) cut.Add(list[i]);
                list = cut;
            }
            _aliasMemo[id] = list;
            return list;
        }

        /// <summary>"card.IsCode(1)" 또는 "(card.IsCode(1) || card.IsCode(2))"</summary>
        private static string IsCodeOr(string varExpr, int id)
        {
            var ids = AliasIds(id);
            if (ids.Count <= 1) return varExpr + ".IsCode(" + id + ")";
            var sb = new StringBuilder();
            for (int i = 0; i < ids.Count; i++)
            {
                if (i > 0) sb.Append(" || ");
                sb.Append(varExpr).Append(".IsCode(").Append(ids[i]).Append(")");
            }
            return "(" + sb + ")";
        }

        /// <summary>"Bot.HasInHand(1)" 또는 "(Bot.HasInHand(1) || Bot.HasInHand(2))" — 호출형 조건/트리거 공용</summary>
        private static string CallOr(Func<int, string> make, int id)
        {
            var ids = AliasIds(id);
            if (ids.Count <= 1) return make(id);
            var parts = new List<string>(ids.Count);
            for (int i = 0; i < ids.Count; i++) parts.Add(make(ids[i]));
            return "(" + string.Join(" || ", parts) + ")";
        }

        /// <summary>"cardId == 1" 또는 "(cardId == 1 || cardId == 2)" — long cardId 인자 비교용</summary>
        private static string IdEqOr(string varExpr, int id)
        {
            var ids = AliasIds(id);
            if (ids.Count <= 1) return varExpr + " == " + id;
            var parts = new List<string>(ids.Count);
            for (int i = 0; i < ids.Count; i++) parts.Add(varExpr + " == " + ids[i]);
            return "(" + string.Join(" || ", parts) + ")";
        }

        /// <summary>
        /// 이 프로필의 규칙에 등장하는 카드 ID 중 별칭이 2개 이상인 그룹 (생성 소스 머리말/검증용).
        /// </summary>
        private static List<KeyValuePair<int, IReadOnlyList<int>>> AliasGroupsOf(AiProfile p)
        {
            var result = new List<KeyValuePair<int, IReadOnlyList<int>>>();
            if (!_aliasesOn) return result;

            var ids = new SortedSet<int>();
            void Add(int id) { if (id > 0) ids.Add(id); }
            void AddConds(IEnumerable<Condition> conds)
            {
                if (conds == null) return;
                foreach (var c in conds) if (c != null) Add(c.Card);
            }

            foreach (var a in p.Actions ?? new List<ActionRule>()) { if (a == null) continue; Add(a.Card); AddConds(a.Conditions); }
            foreach (var f in p.Forbids ?? new List<ForbidRule>()) { if (f == null) continue; Add(f.Card); AddConds(f.Conditions); }
            foreach (var c in p.Choices ?? new List<ChoiceRule>()) { if (c == null) continue; Add(c.Card); Add(c.WhenCard); AddConds(c.Conditions); }
            foreach (var t in p.Triggers ?? new List<FinishTrigger>()) if (t != null) Add(t.Card);
            foreach (var o in p.OppTriggers ?? new List<OppTrigger>()) { if (o == null) continue; Add(o.Card); Add(o.ResponseCard); }
            Add(p.AvoidColumnCard);
            if (p.FreePlaySetSpellExclude != null)   // B-15: 자유 플레이 세트 제외 카드
                foreach (var id in p.FreePlaySetSpellExclude) Add(id);
            foreach (var set in (p.Routes ?? new ObservableCollection<RouteSet>()).Concat(p.RoutesSecond ?? new ObservableCollection<RouteSet>()))
            {
                if (set == null) continue;
                AddConds(set.Conditions);
                foreach (var st in RouteSteps(set))
                    foreach (var opt in OptionsOf(st))
                    {
                        if (opt == null) continue;
                        Add(opt.Card);
                        AddConds(opt.Conditions);
                    }
            }

            foreach (var id in ids)
            {
                var group = AliasIds(id);
                if (group.Count > 1) result.Add(new KeyValuePair<int, IReadOnlyList<int>>(id, group));
            }
            return result;
        }

        /// <summary>
        /// ② 선택 규칙 codeMatch 용 — 코드 하나를 그룹 전체와 비교하는 헬퍼 (별칭이 있을 때만 배출).
        /// Pick(...) 은 int code 하나만 받으므로, 그 코드가 속한 그룹을 static 으로 펼쳐 둔다.
        /// </summary>
        private static void EmitAliasCodeMatchHelper(StringBuilder sb, List<IReadOnlyList<int>> groups)
        {
            sb.AppendLine("        // B-12: 같은 이름 카드(대체 일러/재록)까지 '특정 카드 우선' 매칭");
            sb.AppendLine("        private static bool IsCodeAlias(ClientCard c, int code)");
            sb.AppendLine("        {");
            sb.AppendLine("            if (c == null) return false;");
            foreach (var g in groups)
            {
                var any = IsCodeOr("c", g[0]);
                for (int i = 0; i < g.Count; i++)
                    sb.AppendLine("            if (code == " + g[i] + ") return " + any + ";");
            }
            sb.AppendLine("            return c.IsCode(code);");
            sb.AppendLine("        }");
            sb.AppendLine();
        }

        private static void EmitFreePlay(StringBuilder sb, List<ActionRule> actions, List<FinishTrigger> triggers,
            bool stopOthers, bool debugLog, bool hasEffectBlock, bool hasSummonForbid, bool hasNoUseForbid,
            bool setSpell, string setSpellKind, List<int> setSpellExclude, bool tributeGuard, bool themeNormalForbid,
            List<int> tributeGuardExcept)
        {
            // B-46: 제물 여력 가드에 예외 카드가 있으면 판정을 OR 로 완화한다
            string tribGuardExpr = "";
            if (tributeGuard)
                tribGuardExpr = (tributeGuardExcept != null && tributeGuardExcept.Count > 0)
                    ? " && (!NeedsTributeFodder(Bot, Card) || IsTributeGuardExempt(Card))"
                    : " && !NeedsTributeFodder(Bot, Card)";
            var used = new HashSet<string>(actions.Select(x => x.Type), StringComparer.OrdinalIgnoreCase);
            string condGuard = stopOthers ? "!BuildComplete() && " : "";
            var guardParts = new List<string>();
            if (hasSummonForbid) guardParts.Add("!IsForbiddenSummon(Card)");
            if (hasNoUseForbid) guardParts.Add("!IsForbiddenNoUse(Card)");
            string cardGuard = guardParts.Count == 0 ? "true" : "(Card == null || (" + string.Join(" && ", guardParts) + "))";
            string activateGuard = debugLog
                ? ", () => CheckActivate()" + (hasEffectBlock ? " && !IsForbiddenEffect(Card, ActivateDescription)" : "")
                : (hasEffectBlock ? ", () => !IsForbiddenEffect(Card, ActivateDescription)" : "");
            if (!used.Contains("Summon") && !used.Contains("SummonOrSet"))
                sb.AppendLine("            AddExecutor(ExecutorType.Summon, () => " + condGuard + cardGuard
                              + (themeNormalForbid ? " && !IsForbiddenThemeNormalSummon(Card)" : "")
                              + tribGuardExpr
                              + (tributeGuard
                                  ? "); // 자유 플레이: 제물 여력 없으면 소환 시도 안 함"
                                  : "); // 자유 플레이"));
            if (!used.Contains("SpSummon"))
                sb.AppendLine("            AddExecutor(ExecutorType.SpSummon, () => " + condGuard + cardGuard + "); // 자유 플레이");   // B-26: 특수소환은 테마 금지 대상 아님
            if (!used.Contains("Activate"))
                sb.AppendLine("            AddExecutor(ExecutorType.Activate" + activateGuard + "); // 자유 플레이");
            if (!used.Contains("MonsterSet") && !used.Contains("SummonOrSet"))
                sb.AppendLine("            AddExecutor(ExecutorType.MonsterSet, () => " + condGuard + cardGuard
                              + (themeNormalForbid ? " && !IsForbiddenThemeNormalSummon(Card)" : "")
                              + tribGuardExpr
                              + (tributeGuard
                                  ? "); // 자유 플레이: 제물 여력 없으면 세트 시도 안 함"
                                  : "); // 자유 플레이"));
            // B-15: 함정/속공은 패에서 발동할 수 없어 세트만 가능 → 세트 실행기를 추가 (기본 OFF)
            //   소환/발동 뒤에 두어야 "먼저 소환·발동" 순서가 유지된다 (GameAI 는 등록 순서대로 판정).
            if (setSpell && !used.Contains("SpellSet"))
                sb.AppendLine("            AddExecutor(ExecutorType.SpellSet, () => " + condGuard + "Card != null && Card.Data != null && "
                              + SpellSetKindExpression(setSpellKind) + SpellSetExcludeExpression(setSpellExclude)
                              + "); // 자유 플레이: 마함 세트");
            if (!used.Contains("GoToBattlePhase") && triggers.Count == 0)
                sb.AppendLine("            AddExecutor(ExecutorType.GoToBattlePhase, () => Enemy.GetMonsterCount() == 0); // 자유 플레이");
        }

        /// <summary>
        /// B-15: 세트 가능한 카드 종류 판정식 (TYPE_SPELL 0x2 / TYPE_TRAP 0x4 / TYPE_QUICKPLAY 0x10000).
        /// </summary>
        private static string SpellSetKindExpression(string kind)
        {
            switch ((kind ?? "all").ToLowerInvariant())
            {
                case "trap": return "(Card.Data.Type & 0x4) != 0";
                case "quick": return "(Card.Data.Type & 0x2) != 0 && (Card.Data.Type & 0x10000) != 0";
                default: return "(Card.Data.Type & 0x6) != 0";
            }
        }

        /// <summary>
        /// B-15: 제외 카드 판정식 " && !(Card.IsCode(a) || …)". 목록이 비면 빈 문자열(부분 생략).
        /// sameNameAliases 가 ON 이면 <see cref="IsCodeOr"/> 로 별칭까지 펼쳐진다.
        /// </summary>
        private static string SpellSetExcludeExpression(List<int> exclude)
        {
            if (exclude == null || exclude.Count == 0) return "";
            var seen = new HashSet<int>();
            var parts = new List<string>();
            foreach (var id in exclude)
            {
                if (id <= 0 || !seen.Add(id)) continue;
                parts.Add(IsCodeOr("Card", id));
            }
            if (parts.Count == 0) return "";
            return " && !(" + string.Join(" || ", parts) + ")";
        }

        private static string ForbidHelper(string name, List<ForbidRule> rules, Func<ForbidRule, string> cond, bool withTurn)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"        private bool {name}(ClientCard card)");
            sb.AppendLine("        {");
            sb.AppendLine("            if (card == null) return false;");
            foreach (var f in rules)
            {
                var expr = cond(f);
                if (withTurn) expr += " && (" + TurnExpr(f) + ")";
                var exc = ExceptionExpr(f);
                if (exc != null) expr += IsDeny(f) ? " && (" + exc + ")" : " && !(" + exc + ")";
                sb.AppendLine($"            if ({expr}) return true;");
            }
            sb.AppendLine("            return false;");
            sb.AppendLine("        }");
            sb.AppendLine();
            return sb.ToString();
        }
        /// <summary>
        /// B-26: 테마 일반소환·세트 금지 헬퍼.
        /// 자유 플레이가 만드는 <c>Summon</c>/<c>MonsterSet</c> 실행기에만 붙는다 — <b>특수소환은 막지 않는다</b>.
        /// 예외 카드는 별칭(sameNameAliases)까지 OR 확장한다.
        /// </summary>
        private static string ThemeSummonForbidHelper(List<ForbidRule> rules)
        {
            var sb = new StringBuilder();
            sb.AppendLine("        // B-26: 테마 일반소환·세트 금지 (특수소환은 허용)");
            sb.AppendLine("        private bool IsForbiddenThemeNormalSummon(ClientCard card)");
            sb.AppendLine("        {");
            sb.AppendLine("            if (card == null) return false;");
            sb.AppendLine("            if (card.Data == null || (card.Data.Type & 0x1) == 0) return false;   // 몬스터만 일반소환 대상");
            foreach (var f in rules)
            {
                string expr = "HasSetcode(card, " + f.ThemeCode + ")";
                if (f.ThemeExceptionCards != null && f.ThemeExceptionCards.Count > 0)
                    expr += " && !(" + string.Join(" || ", f.ThemeExceptionCards.Select(id => IsCodeOr("card", id))) + ")";
                expr += " && (" + TurnExpr(f) + ")";
                var exc = ExceptionExpr(f);
                if (exc != null) expr += IsDeny(f) ? " && (" + exc + ")" : " && !(" + exc + ")";
                sb.AppendLine("            if (" + expr + ") return true;");
            }
            sb.AppendLine("            return false;");
            sb.AppendLine("        }");
            sb.AppendLine();
            return sb.ToString();
        }

        /// <summary>
        /// B-29: 내성 몬스터 제외 필터. WindBot 내장 목록(`CardExtension`)을 그대로 쓴다.
        /// 몬스터: IsMonsterInvincible / IsShouldNotBeTarget / IsShouldNotBeMonsterTarget
        /// 마함: IsShouldNotBeSpellTrapTarget
        /// </summary>
        private static void EmitResistFilterHelper(StringBuilder sb)
        {
            sb.AppendLine("        // B-29: 내성 몹(대상/효과 보호) 제외 — WindBot 내장 목록 사용");
            sb.AppendLine("        private static IList<ClientCard> FilterResistant(IList<ClientCard> cards)");
            sb.AppendLine("        {");
            sb.AppendLine("            IList<ClientCard> result = new List<ClientCard>();");
            sb.AppendLine("            if (cards == null) return result;");
            sb.AppendLine("            for (int i = 0; i < cards.Count; i++)");
            sb.AppendLine("            {");
            sb.AppendLine("                ClientCard c = cards[i];");
            sb.AppendLine("                if (c == null) continue;");
            sb.AppendLine("                if (c.Data != null && (c.Data.Type & 0x1) != 0)   // 몬스터");
            sb.AppendLine("                {");
            sb.AppendLine("                    if (c.IsMonsterInvincible() || c.IsShouldNotBeTarget() || c.IsShouldNotBeMonsterTarget()) continue;");
            sb.AppendLine("                }");
            sb.AppendLine("                else if (c.Data != null && (c.Data.Type & 0x6) != 0)   // 마법/함정");
            sb.AppendLine("                {");
            sb.AppendLine("                    if (c.IsShouldNotBeSpellTrapTarget()) continue;");
            sb.AppendLine("                }");
            sb.AppendLine("                result.Add(c);");
            sb.AppendLine("            }");
            sb.AppendLine("            return result;");
            sb.AppendLine("        }");
            sb.AppendLine();
        }

        /// <summary>
        /// B-32: 테마 조건식. `excludeCards` 가 있으면 "그 카드를 뺀 테마 [몬스터]" 판정 헬퍼로 바꾼다.
        /// 예: 패에 테마 몬스터가 있지만 그게 스퀴즈뿐이면 콤보 대상이 아니다.
        /// </summary>
        private static string ThemeCond(Condition c, string helper)
        {
            if (c.ExcludeCards == null || c.ExcludeCards.Count == 0)
                return helper + "(" + SideExpr(c) + ", " + c.ThemeCode + ")";
            string arr = "new int[] { " + string.Join(", ", c.ExcludeCards.Select(id => id.ToString()).ToArray()) + " }";
            return helper + "Except(" + SideExpr(c) + ", " + c.ThemeCode + ", " + arr + ")";
        }

        /// <summary>
        /// B-33: `whenCard` 게이트 식. 기존에는 `Card`/`ActivatingCard()` 만 봤는데,
        /// **해결 중인 링크보다 나중 링크가 카드**일 때(예: 반짝임 해결 중 마지막 링크가 증식의 G)
        /// `Card` 가 엉뚱한 카드가 되어 규칙이 통째로 빗나갔다.
        /// → 현재 체인(`Duel.CurrentChain`)에 그 카드가 있으면 매칭으로 본다.
        /// </summary>
        private static string WhenCardExpr(int whenCard)
        {
            var sb = new StringBuilder();
            sb.Append("((Card != null && ").Append(IsCodeOr("Card", whenCard))
              .Append(") || (ActivatingCard() != null && ").Append(IsCodeOr("ActivatingCard()", whenCard)).Append(")");
            foreach (var id in AliasIds(whenCard))
                sb.Append(" || InChain(").Append(id).Append(")");
            sb.Append(")");
            return sb.ToString();
        }

        /// <summary>
        /// B-35: 공격 대상 선택(hint = <c>HintMsg.AttackTarget</c>) 기본 정책.
        /// 규칙이 없으면 지금은 **목록 첫 카드**를 골라 공격력이 낮은 몬스터가 높은 몬스터를 공격하는 사고가 났다.
        /// → "내 공격력으로 이길 수 있는(상대 공격력이 더 낮은) 카드 중 가장 약한 것"을 고른다.
        /// </summary>
        private static void EmitAttackTargetHelper(StringBuilder sb)
        {
            sb.AppendLine("        // B-35: 공격 대상 기본 정책 — 이길 수 있는 가장 약한 상대");
            sb.AppendLine("        private IList<ClientCard> PickAttackTarget(IList<ClientCard> cards)");
            sb.AppendLine("        {");
            sb.AppendLine("            List<ClientCard> picked = new List<ClientCard>();");
            sb.AppendLine("            if (cards == null || cards.Count == 0) return picked;");
            sb.AppendLine("            int myAtk = (Card != null ? Card.Attack : 0);");
            sb.AppendLine("            ClientCard best = null;");
            sb.AppendLine("            for (int i = 0; i < cards.Count; i++)");
            sb.AppendLine("            {");
            sb.AppendLine("                ClientCard c = cards[i];");
            sb.AppendLine("                if (c == null) continue;");
            sb.AppendLine("                if (best == null) { best = c; continue; }");
            sb.AppendLine("                bool cWin = c.Attack < myAtk;   // 이길 수 있음");
            sb.AppendLine("                bool bWin = best.Attack < myAtk;");
            sb.AppendLine("                if (cWin != bWin) { if (cWin) best = c; continue; }");
            sb.AppendLine("                if (c.Attack < best.Attack) best = c;   // 더 약한 쪽");
            sb.AppendLine("            }");
            sb.AppendLine("            if (best != null) picked.Add(best);");
            sb.AppendLine("            return picked;");
            sb.AppendLine("        }");
            sb.AppendLine();
        }

        /// <summary>
        /// 2026-10-29: 내 엑스트라 덱에 특정 카드가 있는지 (예: 플뢰르 드 바로네스 84815190).
        /// `extraHas` 조건이 있을 때만 생성된다.
        /// </summary>
        private static void EmitExtraHasHelper(StringBuilder sb)
        {
            sb.AppendLine("        // 내 엑스트라 덱에 특정 카드 (extraHas)");
            sb.AppendLine("        private static bool ExtraHas(ClientField f, int code)");
            sb.AppendLine("        {");
            sb.AppendLine("            if (f == null || f.ExtraDeck == null) return false;");
            sb.AppendLine("            for (int i = 0; i < f.ExtraDeck.Count; i++)");
            sb.AppendLine("            {");
            sb.AppendLine("                ClientCard c = f.ExtraDeck[i];");
            sb.AppendLine("                if (c != null && c.IsCode(code)) return true;");
            sb.AppendLine("            }");
            sb.AppendLine("            return false;");
            sb.AppendLine("        }");
            sb.AppendLine();
        }

        private static string StaticHelper(string name, string ret, string args, params string[] body)        {
            var sb = new StringBuilder();
            sb.AppendLine($"        private static {ret} {name}({args})");
            sb.AppendLine("        {");
            foreach (var line in body) sb.AppendLine(line);
            sb.AppendLine("        }");
            sb.AppendLine();
            return sb.ToString();
        }

        /// <summary>이 엔진 전용 대사: 런타임에 WindBot Dialogs 인스턴스의 문자열을 교체한다.</summary>
        private static void EmitDialogs(StringBuilder sb, string file, string cls)
        {
            var name = System.IO.Path.GetFileNameWithoutExtension(file ?? "");
            if (string.IsNullOrWhiteSpace(name)) return;
            name = new string(name.Where(ch => ch != '"' && ch != '\\' && ch != '/' && ch != ':').ToArray());
            if (name.Length == 0) return;

            var fields = new (string Field, string Type)[]
            {
                ("_welcome", "string[]"), ("_deckerror", "string[]"), ("_duelstart", "string[]"),
                ("_newturn", "string[]"), ("_endturn", "string[]"), ("_directattack", "string[]"),
                ("_attack", "string[]"), ("_ondirectattack", "string[]"), ("_facedownmonstername", "string"),
                ("_activate", "string[]"), ("_summon", "string[]"), ("_setmonster", "string[]"),
                ("_chaining", "string[]"), ("_surrender", "string[]"), ("_custom", "string[]"),
            };

            sb.AppendLine();
            sb.AppendLine("        // ---- 이 엔진 전용 대사 (Dialogs/" + name + ".json) ----");
            sb.AppendLine("        private static bool _dlgApplied;");
            sb.AppendLine();
            sb.AppendLine("        private static void ApplyDialogs(GameAI ai)");
            sb.AppendLine("        {");
            sb.AppendLine("            if (_dlgApplied || ai == null) return;");
            sb.AppendLine("            _dlgApplied = true;");
            sb.AppendLine("            try");
            sb.AppendLine("            {");
            sb.AppendLine("                // B-43: 대사 파일 경로 후보 — 실행기 DLL 위치(Executors) 기준 → AppDomain → 상대 경로");
            sb.AppendLine("                //   (안드로이드에서는 AppDomain.BaseDirectory 가 WindBot 폴더가 아니라서 못 찾는다)");
            sb.AppendLine("                string path = null;");
            sb.AppendLine("                string asmDir = null;");
            sb.AppendLine("                try { asmDir = Path.GetDirectoryName(typeof(" + cls + ").Assembly.Location); } catch { }");
            sb.AppendLine("                string root = null;");
            sb.AppendLine("                if (asmDir != null && asmDir.Length > 0) root = Path.GetDirectoryName(asmDir);");
            sb.AppendLine("                string[] cand = new string[3];");
            sb.AppendLine("                cand[0] = (root != null && root.Length > 0) ? Path.Combine(Path.Combine(root, \"Dialogs\"), \"" + name + ".json\") : null;");
            sb.AppendLine("                cand[1] = Path.Combine(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, \"Dialogs\"), \"" + name + ".json\");");
            sb.AppendLine("                cand[2] = Path.Combine(\"Dialogs\", \"" + name + ".json\");");
            sb.AppendLine("                for (int i = 0; i < cand.Length; i++)");
            sb.AppendLine("                    if (cand[i] != null && cand[i].Length > 0 && File.Exists(cand[i])) { path = cand[i]; break; }");
            sb.AppendLine("                if (path == null)");
            sb.AppendLine("                {");
            sb.AppendLine("                    Console.WriteLine(\"[AI-EDITOR] dialog file not found. tried: \" + (cand[0] == null ? \"-\" : cand[0]) + \" | \" + cand[1]);");
            sb.AppendLine("                    return;");
            sb.AppendLine("                }");
            sb.AppendLine("                DataContractJsonSerializer ser = new DataContractJsonSerializer(typeof(DlgData));");
            sb.AppendLine("                DlgData d;");
            sb.AppendLine("                using (FileStream fs = File.OpenRead(path)) d = (DlgData)ser.ReadObject(fs);");
            sb.AppendLine("                FieldInfo fi = typeof(GameAI).GetField(\"_dialogs\", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);");
            sb.AppendLine("                object dlg = fi == null ? null : fi.GetValue(ai);");
            sb.AppendLine("                if (dlg == null) return;");
            foreach (var (field, _) in fields)
                sb.AppendLine("                SetDlg(dlg, \"" + field + "\", d." + field.TrimStart('_') + ");");
            sb.AppendLine("                Console.WriteLine(\"[AI-EDITOR] dialog applied: " + name + "\");");
            sb.AppendLine("            }");
            sb.AppendLine("            catch (Exception ex) { Console.WriteLine(\"[AI-EDITOR] dialog apply failed: \" + ex.Message); }");
            sb.AppendLine("        }");
            sb.AppendLine();
            sb.AppendLine("        private static void SetDlg(object dlg, string name, object value)");
            sb.AppendLine("        {");
            sb.AppendLine("            if (value == null) return;");
            sb.AppendLine("            FieldInfo f = dlg.GetType().GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);");
            sb.AppendLine("            if (f != null) f.SetValue(dlg, value);");
            sb.AppendLine("        }");
            sb.AppendLine();
            sb.AppendLine("        [DataContract]");
            sb.AppendLine("        private class DlgData");
            sb.AppendLine("        {");
            foreach (var (field, type) in fields)
                sb.AppendLine("            [DataMember] public " + type + " " + field.TrimStart('_') + " { get; set; }");
            sb.AppendLine("        }");
        }

        /// <summary>내 발동 조건 헬퍼 (체인에 내 카드가 올라가 있는지).</summary>
        private static void EmitMyActivatedHelper(StringBuilder sb)
        {
            sb.AppendLine("        // ---- 지금 체인에 내가 발동한 카드가 있는지 (handOnly = 패에서 발동) ----");
            sb.AppendLine("        private bool MyCardActivated(bool handOnly)");
            sb.AppendLine("        {");
            sb.AppendLine("            if (Duel.CurrentChain == null) return false;");
            sb.AppendLine("            for (int i = 0; i < Duel.CurrentChain.Count; i++)");
            sb.AppendLine("            {");
            sb.AppendLine("                ClientCard c = Duel.CurrentChain[i];");
            sb.AppendLine("                if (c == null || c.Controller != 0) continue;");
            sb.AppendLine("                if (!handOnly || c.Location == CardLocation.Hand) return true;");
            sb.AppendLine("            }");
            sb.AppendLine("            return false;");
            sb.AppendLine("        }");
            sb.AppendLine();
        }

        /// <summary>상대 발동 조건 헬퍼 (0=몬스터 1=마법 2=함정).</summary>
        private static void EmitOppActivatedHelper(StringBuilder sb)
        {
            sb.AppendLine("        // ---- 지금 체인에서 '상대가' 마지막으로 발동한 카드의 종류 ----");
            sb.AppendLine("        private bool OppActivated(int kind)");
            sb.AppendLine("        {");
            sb.AppendLine("            if (Duel.CurrentChain == null) return false;");
            sb.AppendLine("            for (int i = Duel.CurrentChain.Count - 1; i >= 0; i--)");
            sb.AppendLine("            {");
            sb.AppendLine("                ClientCard c = Duel.CurrentChain[i];");
            sb.AppendLine("                if (c == null || c.Controller != 1) continue;   // 상대 카드만");
            sb.AppendLine("                if (kind == 0) return c.IsMonster();");
            sb.AppendLine("                if (kind == 1) return c.IsSpell();");
            sb.AppendLine("                return c.IsTrap();");
            sb.AppendLine("            }");
            sb.AppendLine("            return false;");
            sb.AppendLine("        }");
            sb.AppendLine();
        }
        /// <summary>테마(아키타입) setcode 판정 헬퍼 생성 (B-14 에서 분리).</summary>
        private static void EmitHasSetcodeHelper(StringBuilder sb)
        {
            sb.AppendLine("        // ---- 테마(아키타입) 판정: ClientCard.Data.Setcode 4슬롯 비교 ----");
            sb.AppendLine("        private static bool HasSetcode(ClientCard c, int code)");
            sb.AppendLine("        {");
            sb.AppendLine("            if (c == null) return false;");
            sb.AppendLine("            if (code == 0) return true;   // 전체(테마 무관)");
            sb.AppendLine("            if (c.Data == null) return false;");
            sb.AppendLine("            long sc = c.Data.Setcode;");
            sb.AppendLine("            if ((sc & 0xFFFF) == code) return true;");
            sb.AppendLine("            if (((sc >> 16) & 0xFFFF) == code) return true;");
            sb.AppendLine("            if (((sc >> 32) & 0xFFFF) == code) return true;");
            sb.AppendLine("            if (((sc >> 48) & 0xFFFF) == code) return true;");
            sb.AppendLine("            return false;");
            sb.AppendLine("        }");
            sb.AppendLine();
        }

        /// <summary>테마(아키타입) 조건 헬퍼 생성.</summary>
        /// <summary>
        /// B-32: 테마 조건 + **제외 카드** 헬퍼 (Hand/Field/Gy × 전체/몬스터만).
        /// `handHasTheme` 등에 `excludeCards` 가 있을 때만 생성된다.
        /// </summary>
        private static void EmitThemeExceptHelpers(StringBuilder sb)
        {
            sb.AppendLine("        // B-32: 테마 조건에서 특정 카드 제외 (예: 스퀴즈는 콤보 대상이 아님)");
            sb.AppendLine("        private static bool HasThemeExcept(ClientCard c, int code, int[] except)");
            sb.AppendLine("        {");
            sb.AppendLine("            if (c == null || c.Data == null) return false;");
            sb.AppendLine("            if (!HasSetcode(c, code)) return false;");
            sb.AppendLine("            for (int i = 0; i < except.Length; i++) if (c.IsCode(except[i])) return false;");
            sb.AppendLine("            return true;");
            sb.AppendLine("        }");
            sb.AppendLine();
            sb.AppendLine("        private static bool IsThemeMonsterExcept(ClientCard c, int code, int[] except)");
            sb.AppendLine("        {");
            sb.AppendLine("            if (c == null || c.Data == null) return false;");
            sb.AppendLine("            if ((c.Data.Type & 0x1) == 0) return false;   // TYPE_MONSTER");
            sb.AppendLine("            return HasThemeExcept(c, code, except);");
            sb.AppendLine("        }");
            sb.AppendLine();
            // Hand
            sb.AppendLine("        private static bool HandHasThemeExcept(ClientField f, int code, int[] except)");
            sb.AppendLine("        {");
            sb.AppendLine("            if (f == null || f.Hand == null) return false;");
            sb.AppendLine("            for (int i = 0; i < f.Hand.Count; i++) if (HasThemeExcept(f.Hand[i], code, except)) return true;");
            sb.AppendLine("            return false;");
            sb.AppendLine("        }");
            sb.AppendLine();
            sb.AppendLine("        private static bool HandHasThemeMonsterExcept(ClientField f, int code, int[] except)");
            sb.AppendLine("        {");
            sb.AppendLine("            if (f == null || f.Hand == null) return false;");
            sb.AppendLine("            for (int i = 0; i < f.Hand.Count; i++) if (IsThemeMonsterExcept(f.Hand[i], code, except)) return true;");
            sb.AppendLine("            return false;");
            sb.AppendLine("        }");
            sb.AppendLine();
            // Graveyard
            sb.AppendLine("        private static bool GyHasThemeExcept(ClientField f, int code, int[] except)");
            sb.AppendLine("        {");
            sb.AppendLine("            if (f == null || f.Graveyard == null) return false;");
            sb.AppendLine("            for (int i = 0; i < f.Graveyard.Count; i++) if (HasThemeExcept(f.Graveyard[i], code, except)) return true;");
            sb.AppendLine("            return false;");
            sb.AppendLine("        }");
            sb.AppendLine();
            sb.AppendLine("        private static bool GyHasThemeMonsterExcept(ClientField f, int code, int[] except)");
            sb.AppendLine("        {");
            sb.AppendLine("            if (f == null || f.Graveyard == null) return false;");
            sb.AppendLine("            for (int i = 0; i < f.Graveyard.Count; i++) if (IsThemeMonsterExcept(f.Graveyard[i], code, except)) return true;");
            sb.AppendLine("            return false;");
            sb.AppendLine("        }");
            sb.AppendLine();
            // Field
            sb.AppendLine("        private static bool FieldHasThemeExcept(ClientField f, int code, int[] except)");
            sb.AppendLine("        {");
            sb.AppendLine("            if (f == null || f.MonsterZone == null) return false;");
            sb.AppendLine("            for (int i = 0; i < f.MonsterZone.Length; i++) if (HasThemeExcept(f.MonsterZone[i], code, except)) return true;");
            sb.AppendLine("            return false;");
            sb.AppendLine("        }");
            sb.AppendLine();
            sb.AppendLine("        private static bool FieldHasThemeMonsterExcept(ClientField f, int code, int[] except)");
            sb.AppendLine("        {");
            sb.AppendLine("            if (f == null || f.MonsterZone == null) return false;");
            sb.AppendLine("            for (int i = 0; i < f.MonsterZone.Length; i++) if (IsThemeMonsterExcept(f.MonsterZone[i], code, except)) return true;");
            sb.AppendLine("            return false;");
            sb.AppendLine("        }");
            sb.AppendLine();
        }

        private static void EmitThemeHelpers(StringBuilder sb, bool monsterOnly)
        {
            sb.AppendLine("        private static bool FieldHasTheme(ClientField f, int code)");
            sb.AppendLine("        {");
            sb.AppendLine("            if (f == null || f.MonsterZone == null) return false;");
            sb.AppendLine("            for (int i = 0; i < f.MonsterZone.Length; i++)");
            sb.AppendLine("                if (HasSetcode(f.MonsterZone[i], code)) return true;");
            sb.AppendLine("            return false;");
            sb.AppendLine("        }");
            sb.AppendLine();
            sb.AppendLine("        private static bool HandHasTheme(ClientField f, int code)");
            sb.AppendLine("        {");
            sb.AppendLine("            if (f == null || f.Hand == null) return false;");
            sb.AppendLine("            for (int i = 0; i < f.Hand.Count; i++)");
            sb.AppendLine("                if (HasSetcode(f.Hand[i], code)) return true;");
            sb.AppendLine("            return false;");
            sb.AppendLine("        }");
            sb.AppendLine();
            sb.AppendLine("        private static bool GyHasTheme(ClientField f, int code)");
            sb.AppendLine("        {");
            sb.AppendLine("            if (f == null || f.Graveyard == null) return false;");
            sb.AppendLine("            for (int i = 0; i < f.Graveyard.Count; i++)");
            sb.AppendLine("                if (HasSetcode(f.Graveyard[i], code)) return true;");
            sb.AppendLine("            return false;");
            sb.AppendLine("        }");
            sb.AppendLine();
            if (!monsterOnly) return;
            sb.AppendLine("        // B-14: 테마 + 몬스터만 (Data.Type & 0x1)");
            sb.AppendLine("        private static bool IsThemeMonster(ClientCard c, int code)");
            sb.AppendLine("        {");
            sb.AppendLine("            if (c == null || c.Data == null) return false;");
            sb.AppendLine("            if ((c.Data.Type & 0x1) == 0) return false;   // TYPE_MONSTER");
            sb.AppendLine("            return HasSetcode(c, code);");
            sb.AppendLine("        }");
            sb.AppendLine();
            sb.AppendLine("        private static bool FieldHasThemeMonster(ClientField f, int code)");
            sb.AppendLine("        {");
            sb.AppendLine("            if (f == null || f.MonsterZone == null) return false;");
            sb.AppendLine("            for (int i = 0; i < f.MonsterZone.Length; i++)");
            sb.AppendLine("                if (IsThemeMonster(f.MonsterZone[i], code)) return true;");
            sb.AppendLine("            return false;");
            sb.AppendLine("        }");
            sb.AppendLine();
            sb.AppendLine("        private static bool HandHasThemeMonster(ClientField f, int code)");
            sb.AppendLine("        {");
            sb.AppendLine("            if (f == null || f.Hand == null) return false;");
            sb.AppendLine("            for (int i = 0; i < f.Hand.Count; i++)");
            sb.AppendLine("                if (IsThemeMonster(f.Hand[i], code)) return true;");
            sb.AppendLine("            return false;");
            sb.AppendLine("        }");
            sb.AppendLine();
            sb.AppendLine("        private static bool GyHasThemeMonster(ClientField f, int code)");
            sb.AppendLine("        {");
            sb.AppendLine("            if (f == null || f.Graveyard == null) return false;");
            sb.AppendLine("            for (int i = 0; i < f.Graveyard.Count; i++)");
            sb.AppendLine("                if (IsThemeMonster(f.Graveyard[i], code)) return true;");
            sb.AppendLine("            return false;");
            sb.AppendLine("        }");
            sb.AppendLine();
        }

        /// <summary>B-14: 규칙별 '테마 카드 우선' 선택 헬퍼 생성.</summary>
        private static void EmitThemePickHelpers(StringBuilder sb, List<ChoiceRule> themeRules)
        {
            for (int n = 1; n <= themeRules.Count; n++)
            {
                var r = themeRules[n - 1];
                sb.AppendLine("        // B-14: 테마 카드 우선 — setcode " + r.ThemeCode +
                              (r.ThemeMonsterOnly ? " · 몬스터만" : "") +
                              " · 제외 " + (r.ExcludeCards == null ? 0 : r.ExcludeCards.Count));
                sb.AppendLine("        private static IList<ClientCard> PickByTheme_" + n + "(IList<ClientCard> cards, int max)");
                sb.AppendLine("        {");
                sb.AppendLine("            List<ClientCard> picked = new List<ClientCard>();");
                sb.AppendLine("            for (int i = 0; i < cards.Count && picked.Count < max; i++)");
                sb.AppendLine("            {");
                sb.AppendLine("                ClientCard c = cards[i];");
                sb.AppendLine("                if (c == null) continue;");
                sb.AppendLine("                if (!HasSetcode(c, " + r.ThemeCode + ")) continue;");
                if (r.ThemeMonsterOnly)
                {
                    sb.AppendLine("                if (c.Data == null || (c.Data.Type & 0x1) == 0) continue;   // 몬스터만");
                }
                if (r.ExcludeCards != null)
                {
                    foreach (var id in r.ExcludeCards)
                    {
                        if (id <= 0) continue;
                        sb.AppendLine("                if (" + IsCodeOr("c", id) + ") continue;   // 제외 카드");
                    }
                }
                sb.AppendLine("                picked.Add(c);");
                sb.AppendLine("            }");
                sb.AppendLine("            return picked;");
                sb.AppendLine("        }");
                sb.AppendLine();
            }
        }

        /// <summary>B-22: 규칙별 '제외 카드' 후보 필터 헬퍼 생성 (SelectCard 전용).</summary>
        private static void EmitExcludeCodeHelpers(StringBuilder sb, List<ChoiceRule> excludeRules)
        {
            for (int n = 1; n <= excludeRules.Count; n++)
            {
                var r = excludeRules[n - 1];
                var ids = (r.ExcludeCards ?? new List<int>()).Where(id => id > 0).ToList();
                sb.AppendLine("        // B-22: 제외 카드 — 정책과 무관하게 후보에서 먼저 제거 (" + string.Join(", ", ids) + ")");
                sb.AppendLine("        private static IList<ClientCard> ExcludeCodes_" + n + "(IList<ClientCard> cards)");
                sb.AppendLine("        {");
                sb.AppendLine("            List<ClientCard> kept = new List<ClientCard>();");
                sb.AppendLine("            if (cards == null) return kept;");
                sb.AppendLine("            for (int i = 0; i < cards.Count; i++)");
                sb.AppendLine("            {");
                sb.AppendLine("                ClientCard c = cards[i];");
                sb.AppendLine("                if (c == null) continue;");
                foreach (var id in ids)
                    sb.AppendLine("                if (" + IsCodeOr("c", id) + ") continue;   // 제외 카드");
                sb.AppendLine("                kept.Add(c);");
                sb.AppendLine("            }");
                sb.AppendLine("            return kept;");
                sb.AppendLine("        }");
                sb.AppendLine();
            }
        }

        /// <summary>B-17: 규칙별 '발동 중인 카드(체인)' 선택 헬퍼 생성.</summary>
        private static void EmitChainPickHelpers(StringBuilder sb, List<ChoiceRule> chainRules)
        {
            for (int n = 1; n <= chainRules.Count; n++)
            {
                sb.AppendLine("        // B-17: 발동 중인 카드(체인) 우선 — 체인을 뒤에서부터 훑어 상대 카드 중 후보에 있는 것을 고른다");
                sb.AppendLine("        private IList<ClientCard> PickChainCard_" + n + "(IList<ClientCard> cards, int max)");
                sb.AppendLine("        {");
                sb.AppendLine("            List<ClientCard> picked = new List<ClientCard>();");
                sb.AppendLine("            if (cards == null || Duel.CurrentChain == null) return picked;");
                sb.AppendLine("            for (int i = Duel.CurrentChain.Count - 1; i >= 0 && picked.Count < max; i--)");
                sb.AppendLine("            {");
                sb.AppendLine("                ClientCard lc = Duel.CurrentChain[i];");
                sb.AppendLine("                if (lc == null) continue;");
                sb.AppendLine("                if (lc.Controller == 0) continue;   // 상대(플레이어)가 발동한 카드만");
                sb.AppendLine("                int hit = -1;");
                sb.AppendLine("                for (int j = 0; j < cards.Count; j++)   // 1순위: 같은 존/시퀀스 (같은 카드 2장 구분)");
                sb.AppendLine("                {");
                sb.AppendLine("                    ClientCard c = cards[j];");
                sb.AppendLine("                    if (c == null) continue;");
                sb.AppendLine("                    if (c.Id != lc.Id || c.Controller != lc.Controller) continue;");
                sb.AppendLine("                    if (c.Location != lc.Location || c.Sequence != lc.Sequence) continue;");
                sb.AppendLine("                    hit = j; break;");
                sb.AppendLine("                }");
                sb.AppendLine("                if (hit < 0) for (int j = 0; j < cards.Count; j++)   // 2순위(폴백): 카드ID+컨트롤러");
                sb.AppendLine("                {");
                sb.AppendLine("                    ClientCard c = cards[j];");
                sb.AppendLine("                    if (c == null) continue;");
                sb.AppendLine("                    if (c.Id != lc.Id || c.Controller != lc.Controller) continue;");
                sb.AppendLine("                    hit = j; break;");
                sb.AppendLine("                }");
                sb.AppendLine("                if (hit < 0) continue;");
                sb.AppendLine("                if (!picked.Contains(cards[hit])) picked.Add(cards[hit]);");
                sb.AppendLine("                }");
                sb.AppendLine("            return picked;");
                sb.AppendLine("        }");
                sb.AppendLine();
            }
        }

        /// <summary>이번 턴 이벤트 카운터(서치/소환/묘지 이동/드로우) 생성.</summary>
        private static void EmitEventCounters(StringBuilder sb)
        {
            sb.AppendLine("        // ---- 이번 턴 이벤트 카운터 (존 이동량 기반 추정) ----");
            sb.AppendLine("        // 0=서치(덱→패) 1=일반소환 2=특수소환 3=묘지→제외 4=묘지→패 5=묘지→특소 6=덱→묘지 7=덱→특소 8=드로우");
            sb.AppendLine("        private int _evtTurn = -1;");
            sb.AppendLine("        private bool _prevReady;");
            sb.AppendLine("        private int[] _evt = new int[20];");
            sb.AppendLine("        private int[] _prev = new int[12];");
            sb.AppendLine("        private int[] _draws = new int[2];");
            sb.AppendLine("        private List<ClientCard> _prevBotMon = new List<ClientCard>();");
            sb.AppendLine("        private List<ClientCard> _prevEnemyMon = new List<ClientCard>();");
            sb.AppendLine();
            sb.AppendLine("        public override void OnDraw(int player)");
            sb.AppendLine("        {");
            sb.AppendLine("            if (player >= 0 && player <= 1) _draws[player]++;");
            sb.AppendLine("            base.OnDraw(player);");
            sb.AppendLine("        }");
            sb.AppendLine();
            sb.AppendLine("        private static int ZoneCount(ClientField f, int idx)");
            sb.AppendLine("        {");
            sb.AppendLine("            if (f == null) return 0;");
            sb.AppendLine("            if (idx == 0) return f.Deck == null ? 0 : f.Deck.Count;");
            sb.AppendLine("            if (idx == 1) return f.Hand == null ? 0 : f.Hand.Count;");
            sb.AppendLine("            if (idx == 2) return f.Graveyard == null ? 0 : f.Graveyard.Count;");
            sb.AppendLine("            if (idx == 3) return f.Banished == null ? 0 : f.Banished.Count;");
            sb.AppendLine("            if (idx == 4) return f.GetMonsterCount();");
            sb.AppendLine("            return f.ExtraDeck == null ? 0 : f.ExtraDeck.Count;");
            sb.AppendLine("        }");
            sb.AppendLine();
            sb.AppendLine("        private int EventCount(ClientField f, int id)");
            sb.AppendLine("        {");
            sb.AppendLine("            UpdateEvents();");
            sb.AppendLine("            return _evt[((f == Bot) ? 0 : 1) * 10 + id];");
            sb.AppendLine("        }");
            sb.AppendLine();
            sb.AppendLine("        private void CaptureEvents()");
            sb.AppendLine("        {");
            sb.AppendLine("            for (int s = 0; s < 2; s++)");
            sb.AppendLine("            {");
            sb.AppendLine("                ClientField f = (s == 0) ? Bot : Enemy;");
            sb.AppendLine("                for (int i = 0; i < 6; i++) _prev[s * 6 + i] = ZoneCount(f, i);");
            sb.AppendLine("            }");
            sb.AppendLine("            if (Bot.MonsterZone != null) _prevBotMon = Bot.GetMonsters();");
            sb.AppendLine("            if (Enemy.MonsterZone != null) _prevEnemyMon = Enemy.GetMonsters();");
            sb.AppendLine("        }");
            sb.AppendLine();
            sb.AppendLine("        private void UpdateEvents()");
            sb.AppendLine("        {");
            sb.AppendLine("            if (Bot == null || Enemy == null) return;");
            sb.AppendLine("            if (!_prevReady)");
            sb.AppendLine("            {");
            sb.AppendLine("                _prevReady = true;");
            sb.AppendLine("                _evtTurn = Duel.Turn;");
            sb.AppendLine("                CaptureEvents();");
            sb.AppendLine("                return;");
            sb.AppendLine("            }");
            sb.AppendLine("            if (_evtTurn != Duel.Turn)");
            sb.AppendLine("            {");
            sb.AppendLine("                _evtTurn = Duel.Turn;");
            sb.AppendLine("                for (int i = 0; i < _evt.Length; i++) _evt[i] = 0;");
            sb.AppendLine("            }");
            sb.AppendLine();
            sb.AppendLine("            for (int s = 0; s < 2; s++)");
            sb.AppendLine("            {");
            sb.AppendLine("                ClientField f = (s == 0) ? Bot : Enemy;");
            sb.AppendLine("                int o = s * 6;");
            sb.AppendLine("                int e = s * 10;");
            sb.AppendLine("                int deck = ZoneCount(f, 0) - _prev[o + 0];");
            sb.AppendLine("                int hand = ZoneCount(f, 1) - _prev[o + 1];");
            sb.AppendLine("                int gy = ZoneCount(f, 2) - _prev[o + 2];");
            sb.AppendLine("                int ban = ZoneCount(f, 3) - _prev[o + 3];");
            sb.AppendLine("                int draw = _draws[s];");
            sb.AppendLine("                _draws[s] = 0;");
            sb.AppendLine();
            sb.AppendLine("                if (draw > 0) _evt[e + 8] += draw;                             // 드로우");
            sb.AppendLine("                if (deck < 0 && hand > 0)");
            sb.AppendLine("                {");
            sb.AppendLine("                    int n = Math.Min(-deck, hand) - draw;");
            sb.AppendLine("                    if (n > 0) _evt[e + 0] += n;            // 서치(드로우 제외)");
            sb.AppendLine("                }");
            sb.AppendLine("                if (gy < 0 && ban > 0) _evt[e + 3] += Math.Min(-gy, ban);      // 묘지→제외");
            sb.AppendLine("                if (gy < 0 && hand > 0) _evt[e + 4] += Math.Min(-gy, hand);    // 묘지→패");
            sb.AppendLine("                if (deck < 0 && gy > 0) _evt[e + 6] += Math.Min(-deck, gy);    // 덱→묘지");
            sb.AppendLine();
            sb.AppendLine("                List<ClientCard> prevMon = (s == 0) ? _prevBotMon : _prevEnemyMon;");
            sb.AppendLine("                List<ClientCard> curMon = (f.MonsterZone == null) ? new List<ClientCard>() : f.GetMonsters();");
            sb.AppendLine("                int norm = 0;");
            sb.AppendLine("                int spec = 0;");
            sb.AppendLine("                for (int i = 0; i < curMon.Count; i++)");
            sb.AppendLine("                {");
            sb.AppendLine("                    ClientCard c = curMon[i];");
            sb.AppendLine("                    if (c == null || prevMon.Contains(c)) continue;");
            sb.AppendLine("                    if (c.IsSpecialSummoned) spec++; else norm++;");
            sb.AppendLine("                }");
            sb.AppendLine("                if (norm > 0) _evt[e + 1] += norm;             // 일반 소환");
            sb.AppendLine("                if (spec > 0)");
            sb.AppendLine("                {");
            sb.AppendLine("                    _evt[e + 2] += spec;                        // 특수 소환");
            sb.AppendLine("                    int fromGy = Math.Min(spec, Math.Max(0, -gy));");
            sb.AppendLine("                    if (fromGy > 0) _evt[e + 5] += fromGy;      // 묘지→특수소환");
            sb.AppendLine("                    int fromDeck = Math.Min(spec - fromGy, Math.Max(0, -deck));");
            sb.AppendLine("                    if (fromDeck > 0) _evt[e + 7] += fromDeck;  // 덱→특수소환");
            sb.AppendLine("                }");
            sb.AppendLine("                if (s == 0) _prevBotMon = curMon; else _prevEnemyMon = curMon;");
            sb.AppendLine("                for (int i = 0; i < 6; i++) _prev[o + i] = ZoneCount(f, i);");
            sb.AppendLine("            }");
            sb.AppendLine("        }");
            sb.AppendLine();
        }
        private static string GroupExpression(List<FinishTrigger> group)
        {
            string acc = null;
            foreach (var t in group)
            {
                var text = TriggerCondition(t);
                if (acc == null) { acc = text; continue; }
                var op = string.Equals(t.Join, "or", StringComparison.OrdinalIgnoreCase) ? "||" : "&&";
                acc = "(" + acc + " " + op + " " + text + ")";
            }
            return acc ?? "true";
        }

        private static string TriggerCondition(FinishTrigger t)
        {
            switch (t.Zone)
            {
                case "hand": return CallOr(x => "Bot.HasInHand(" + x + ")", t.Card);
                case "graveyard": return CallOr(x => "Bot.HasInGraveyard(" + x + ")", t.Card);
                case "enemyField": return CallOr(x => "OnField(Enemy, " + x + ")", t.Card);
                default: return CallOr(x => "OnField(Bot, " + x + ")", t.Card);
            }
        }

        private sealed class Sig
        {
            public string ReturnType = "IList<ClientCard>";
            public string Name = "";
            public string Params = "";
            public string MaxExpr = "max";
            public string MinExpr = "min";

            /// <summary>B-25: hint 인자 이름 (없으면 빈 문자열 → whenHint 규칙은 항상 불일치)</summary>
            public string HintExpr = "";

            public string BaseArgs = "cards";
        }

        private static readonly Dictionary<string, Sig> CallbackSig = new Dictionary<string, Sig>
        {
            ["SelectCard"] = new Sig { Name = "OnSelectCard", Params = "IList<ClientCard> cards, int min, int max, long hint, bool cancelable", BaseArgs = "cards, min, max, hint, cancelable", HintExpr = "hint" },
            ["SelectSum"] = new Sig { Name = "OnSelectSum", Params = "IList<ClientCard> cards, int sum, int min, int max, long hint, bool mode", BaseArgs = "cards, sum, min, max, hint, mode", HintExpr = "hint" },
            ["RitualTribute"] = new Sig { Name = "OnSelectRitualTribute", Params = "IList<ClientCard> cards, int sum, int min, int max", BaseArgs = "cards, sum, min, max" },
            ["FusionMaterial"] = new Sig { Name = "OnSelectFusionMaterial", Params = "IList<ClientCard> cards, int min, int max", BaseArgs = "cards, min, max" },
            ["SynchroMaterial"] = new Sig { Name = "OnSelectSynchroMaterial", Params = "IList<ClientCard> cards, int sum, int min, int max", BaseArgs = "cards, sum, min, max" },
            ["XyzMaterial"] = new Sig { Name = "OnSelectXyzMaterial", Params = "IList<ClientCard> cards, int min, int max", BaseArgs = "cards, min, max" },
            ["LinkMaterial"] = new Sig { Name = "OnSelectLinkMaterial", Params = "IList<ClientCard> cards, int min, int max", BaseArgs = "cards, min, max" },
            ["PendulumSummon"] = new Sig { Name = "OnSelectPendulumSummon", Params = "IList<ClientCard> cards, int max", MinExpr = "", BaseArgs = "cards, max" },
            ["CardSorting"] = new Sig { Name = "OnCardSorting", Params = "IList<ClientCard> cards", MaxExpr = "cards.Count", MinExpr = "", BaseArgs = "cards" },
        };

        /// <summary>한 루트 벌의 단계를 Order 순서로 정렬하고 null 을 제거한다.</summary>
        private static List<RouteStep> RouteSteps(RouteSet set) =>
            set?.Steps == null
                ? new List<RouteStep>()
                : set.Steps.Where(s => s != null).OrderBy(s => s.Order).ToList();

        /// <summary>
        /// 트랙의 루트 목록. 구형 단일 루트(<c>route</c>/<c>routeSecond</c>)만 있으면 1벌로 승격해 읽는다.
        /// (RuleMigration 을 거치지 않은 프로필도 안전하게 처리)
        /// </summary>
        private static List<RouteSet> RouteSetsOf(IEnumerable<RouteSet>? sets, List<RouteStep>? legacy)
        {
            var list = (sets ?? Enumerable.Empty<RouteSet>()).Where(s => s != null).ToList();
            if (list.Count > 0) return list;
            if (legacy == null || legacy.All(s => s == null)) return list;

            var set = new RouteSet { Name = "기본", IsDefault = true };
            foreach (var s in legacy) if (s != null) set.Steps.Add(s);
            list.Add(set);
            return list;
        }

        /// <summary>메서드 이름 — 트랙/벌/단계/순위가 모두 들어가 충돌이 없다.</summary>
        private static string RouteFuncName(bool second, int setIndex, int step, int rank) =>
            (second ? "RouteSecond" : "RouteFirst") + setIndex + "Step" + step + "_R" + rank;

        private static string RouteSetStateName(bool second) => second ? "_routeSetSecond" : "_routeSetFirst";
        private static string RouteStateName(bool second) => second ? "_routeStepSecond" : "_routeStep";
        private static string RouteSelectName(bool second) => second ? "SelectRouteSecond" : "SelectRouteFirst";
        private static string RouteGateName(bool second) => second ? "OnRouteSecond()" : "OnRouteFirst()";

        /// <summary>
        /// 단계의 대안(순위)을 낮은 순위부터 반환.
        /// Options 가 비어 있으면 예전 형식(Card/Action)으로 1순위 대안을 만든다.
        /// </summary>
        private static List<RouteOption> OptionsOf(RouteStep st)
        {
            var list = (st.Options ?? new ObservableCollection<RouteOption>()).Where(o => o != null).ToList();
            if (list.Count == 0)
                list.Add(new RouteOption
                {
                    Rank = 1,
                    Card = st.Card,
                    Action = string.IsNullOrWhiteSpace(st.Action) ? "Activate" : st.Action
                });
            return list.OrderBy(o => o.Rank).ToList();
        }

        /// <summary>전개 루트 실행기 등록 — 벌/단계마다 각 순위의 AddExecutor 한 줄.</summary>
        private static void EmitRouteRegistrations(StringBuilder sb, List<RouteSet> sets, bool second)
        {
            for (int si = 0; si < sets.Count; si++)
            {
                var set = sets[si];
                var steps = RouteSteps(set);
                if (steps.Count == 0) continue;

                sb.AppendLine("            // [" + (second ? "후공 " : "선공 ") + (si + 1) + "벌] " + OneLine(set.Name)
                              + (set.IsDefault ? " (기본)" : "") + " · 진입 조건 "
                              + ((set.Conditions != null && set.Conditions.Count > 0) ? set.Conditions.Count + "개" : "없음(항상 선택)"));

                for (int i = 0; i < steps.Count; i++)
                {
                    var st = steps[i];
                    if (!string.IsNullOrWhiteSpace(st.Note))
                        sb.AppendLine("            //   " + (i + 1) + "단계: " + OneLine(st.Note));

                    var opts = OptionsOf(st);
                    for (int k = 0; k < opts.Count; k++)
                    {
                        var o = opts[k];
                        var fn = RouteFuncName(second, si, i, k + 1);
                        var cond = (o.Conditions != null && o.Conditions.Count > 0) ? " / 조건 " + o.Conditions.Count : " / 조건 없음";
                        sb.AppendLine("            //   " + (k + 1) + "순위: " + o.Action + (o.Card > 0 ? " " + o.Card : "") + cond);
                        if (o.Card > 0)
                        {
                            foreach (var cid in AliasIds(o.Card))   // B-12: 같은 이름 카드도 같은 단계로
                                sb.AppendLine("            AddExecutor(ExecutorType." + o.Action + ", " + cid + ", () => " + fn + "());");
                        }
                        else
                        {
                            sb.AppendLine("            AddExecutor(ExecutorType." + o.Action + ", () => " + fn + "());");
                        }
                    }
                }
            }
        }

        /// <summary>
        /// 루트 선택 메서드 생성 (B-5).
        /// 기본(<c>IsDefault</c>)이 아닌 루트를 위에서부터 조건 검사해 처음 만족한 1벌을 고르고,
        /// 아무것도 만족하지 못하면 기본 루트를 고른다. 기본 루트도 없으면 <c>-2</c>(전개 안 함).
        /// </summary>
        private static void EmitRouteSelect(StringBuilder sb, List<RouteSet> sets, bool second)
        {
            var setState = RouteSetStateName(second);
            var stepState = RouteStateName(second);

            var defaultIndex = -1;
            for (int i = 0; i < sets.Count; i++)
                if (sets[i].IsDefault && RouteSteps(sets[i]).Count > 0) { defaultIndex = i; break; }

            sb.AppendLine();
            sb.AppendLine("        // ---- " + (second ? "후공" : "선공") + " 루트 선택 ----");
            sb.AppendLine("        // 기본이 아닌 루트를 목록 순서대로 조건 검사 → 처음 만족한 1벌");
            sb.AppendLine("        // 아무것도 만족 못하면 기본 루트 / 기본 루트도 없으면 -2 (이번 턴 전개 안 함)");
            sb.AppendLine("        private void " + RouteSelectName(second) + "()");
            sb.AppendLine("        {");
            sb.AppendLine("            " + stepState + " = 0;   // 고른 루트의 1단계부터");

            for (int i = 0; i < sets.Count; i++)
            {
                var set = sets[i];
                if (set.IsDefault) continue;
                if (RouteSteps(set).Count == 0) continue;

                bool hasCond = set.Conditions != null && set.Conditions.Count > 0;
                sb.AppendLine("            // " + (i + 1) + "벌 " + OneLine(set.Name) + (hasCond ? " (조건 " + set.Conditions.Count + "개)" : " (조건 없음 → 항상 선택)"));
                if (hasCond)
                    sb.AppendLine("            if (" + ConditionExpression(set.Conditions) + ") { " + setState + " = " + i + "; return; }");
                else
                {
                    sb.AppendLine("            " + setState + " = " + i + ";   // 조건 없음 → 항상 이 루트");
                    sb.AppendLine("            return;");
                    sb.AppendLine("        }");
                    return;   // 뒤쪽 루트는 평가되지 않는다 (조건 없는 루트가 첫 번째로 확정)
                }
            }

            if (defaultIndex >= 0)
                sb.AppendLine("            " + setState + " = " + defaultIndex + ";   // 기본 루트: " + OneLine(sets[defaultIndex].Name));
            else
                sb.AppendLine("            " + setState + " = -2;   // 기본 루트 없음 → 이번 턴 전개 안 함");

            sb.AppendLine("        }");
        }

        /// <summary>
        /// 전개 루트 단계 메서드.
        /// ① 선공/후공 게이트 ② 루트 선택(첫 평가 때) ③ 선택된 루트인지 ④ 이 단계 차례인지 ⑤ 순위 조건.
        /// 순위 조건이 맞으면 그 단계를 확정(<c>_routeStep = N+1</c>)하고 true.
        /// 마지막 순위까지 불충족이면 <c>_routeStep = -1</c> → 그 턴의 이후 단계는 실행되지 않는다.
        /// </summary>
        private static void EmitRouteMethods(StringBuilder sb, List<RouteSet> sets, bool second)
        {
            var setState = RouteSetStateName(second);
            var state = RouteStateName(second);

            for (int si = 0; si < sets.Count; si++)
            {
                var set = sets[si];
                var steps = RouteSteps(set);
                if (steps.Count == 0) continue;

                for (int i = 0; i < steps.Count; i++)
                {
                    var opts = OptionsOf(steps[i]);
                    for (int k = 0; k < opts.Count; k++)
                    {
                        var o = opts[k];
                        bool last = k == opts.Count - 1;
                        bool hasCond = o.Conditions != null && o.Conditions.Count > 0;

                        sb.AppendLine();
                        sb.AppendLine("        // " + (second ? "[후공] " : "[선공] ") + (si + 1) + "벌 " + OneLine(set.Name)
                                      + " · " + (i + 1) + "단계 · " + (k + 1) + "순위"
                                      + (string.IsNullOrWhiteSpace(o.Note) ? "" : " — " + OneLine(o.Note)));
                        sb.AppendLine("        private bool " + RouteFuncName(second, si, i, k + 1) + "()");
                        sb.AppendLine("        {");
                        sb.AppendLine("            if (!" + RouteGateName(second) + ") return false;   // 선공/후공 게이트");
                        sb.AppendLine("            if (" + setState + " == -1) " + RouteSelectName(second) + "();   // 이번 턴 루트 선택");
                        sb.AppendLine("            if (" + setState + " != " + si + ") return false;   // 다른 루트가 선택됨");
                        sb.AppendLine("            if (" + state + " != " + i + ") return false;   // 이 단계 차례인가");
                        if (hasCond)
                        {
                            var expr = ConditionExpression(o.Conditions);
                            if (last)
                            {
                                sb.AppendLine("            if (!(" + expr + "))");
                                sb.AppendLine("            {");
                                sb.AppendLine("                " + state + " = -1;   // 어떤 순위도 불충족 → 이번 턴 전개 종료");
                                sb.AppendLine("                return false;");
                                sb.AppendLine("            }");
                            }
                            else
                            {
                                sb.AppendLine("            if (!(" + expr + ")) return false;   // 불충족 → 다음 순위로");
                            }
                        }
                        sb.AppendLine("            " + state + " = " + (i + 1) + ";   // 이 단계 확정");
                        sb.AppendLine("            return true;");
                        sb.AppendLine("        }");
                    }
                }
            }
        }

        private static string OneLine(string s) => s.Replace("\r", " ").Replace("\n", " ").Trim();

        /// <summary>long 리터럴 (int 범위면 접미사 없이 → options.Contains(1607328404) 모양 유지)</summary>
        private static string LongLiteral(long v) =>
            (v >= int.MinValue && v <= int.MaxValue) ? v.ToString() : v + "L";

        private static string ActionCall(ActionRule a, int card, bool stopWhenComplete, bool guardEffect)
        {
            bool hasCond = a.Conditions.Count > 0;
            bool needGuard = guardEffect && string.Equals(a.Type, "Activate", StringComparison.OrdinalIgnoreCase);
            bool hasDesc = string.Equals(a.Type, "Activate", StringComparison.OrdinalIgnoreCase) && a.EffectDesc >= 0;   // B-45

            if (!hasCond && !stopWhenComplete && !needGuard && !hasDesc)
            {
                return card > 0
                    ? $"AddExecutor(ExecutorType.{a.Type}, {card})"
                    : $"AddExecutor(ExecutorType.{a.Type})";
            }

            var expr = hasCond ? ConditionExpression(a.Conditions) : "true";
            if (stopWhenComplete) expr = "!BuildComplete() && (" + expr + ")";
            // B-45: 효과번호(desc) 필터 — Activate 행에서만
            if (string.Equals(a.Type, "Activate", StringComparison.OrdinalIgnoreCase) && a.EffectDesc >= 0)
                expr = "(ActivateDescription == " + a.EffectDesc + ") && (" + expr + ")";
            if (needGuard) expr = "!IsForbiddenEffect(Card, ActivateDescription) && (" + expr + ")";
            var func = "() => " + expr;

            return card > 0
                ? $"AddExecutor(ExecutorType.{a.Type}, {card}, {func})"
                : $"AddExecutor(ExecutorType.{a.Type}, {func})";
        }

        /// <summary>B-14: 조건 부정(Not)을 식 전체에 적용</summary>
        private static string ConditionExpression(List<Condition> conds)
        {
            string acc = null;
            foreach (var c in conds)
            {
                var text = ConditionText(c);
                if (string.IsNullOrWhiteSpace(text)) continue;
                if (c.Not) text = "!(" + text + ")";   // B-14: 부정
                if (acc == null) { acc = text; continue; }
                var op = string.Equals(c.Join, "or", StringComparison.OrdinalIgnoreCase) ? "||" : "&&";
                acc = "(" + acc + " " + op + " " + text + ")";
            }
            return acc == null ? "true" : acc;
        }

        /// <summary>조건의 대상 필드 식 (me → Bot, 그 외 → Enemy)</summary>
        private static string SideExpr(Condition c) => c.Side == "me" ? "Bot" : "Enemy";

        /// <summary>이벤트 조건 kind → 내부 이벤트 번호 (-1 = 아님). "Enemy" 접미사는 상대 기준.</summary>
        private static int EventId(string kind)
        {
            if (string.IsNullOrEmpty(kind)) return -1;
            var k = kind.EndsWith("Enemy", StringComparison.Ordinal) ? kind.Substring(0, kind.Length - 5) : kind;
            switch (k)
            {
                case "evtSearch": return 0;
                case "evtNormalSummon": return 1;
                case "evtSpecialSummon": return 2;
                case "evtBanishFromGy": return 3;
                case "evtGyToHand": return 4;
                case "evtSpecialFromGy": return 5;
                case "evtDeckToGy": return 6;
                case "evtSpecialFromDeck": return 7;
                case "evtDraw": return 8;
                default: return -1;
            }
        }

        private static bool IsEnemyEvent(string kind) =>
            !string.IsNullOrEmpty(kind) && kind.EndsWith("Enemy", StringComparison.Ordinal);

        /// <summary>B-20: 장수 조건의 비교 연산자 (빈 값/이상한 값은 ">=" 로 보정)</summary>
        private static string CountOp(Condition c)
        {
            switch (c.Op)
            {
                case ">=": case "<=": case "==": case "!=": case ">": case "<": return c.Op;
                default: return ">=";
            }
        }

        private static string ConditionText(Condition c)
        {
            switch (c.Kind)
            {
                case "enemyMonsterCount": return $"Enemy.GetMonsterCount() {c.Op} {c.Value}";
                case "enemySpellCount": return $"Enemy.GetSpellCount() {c.Op} {c.Value}";
                case "enemyHandCount": return $"Enemy.GetHandCount() {c.Op} {c.Value}";
                case "enemyAttackingCount": return $"CountAttacking(Enemy) {c.Op} {c.Value}";
                case "enemyFaceUpCount": return $"CountFaceUp(Enemy) {c.Op} {c.Value}";
                case "enemyFaceDownCount": return $"CountFaceDown(Enemy) {c.Op} {c.Value}";
                case "myMonsterCount": return $"Bot.GetMonsterCount() {c.Op} {c.Value}";
                case "myHandCount": return $"Bot.GetHandCount() {c.Op} {c.Value}";
                case "myLp": return $"Bot.LifePoints {c.Op} {c.Value}";
                case "enemyLp": return $"Enemy.LifePoints {c.Op} {c.Value}";
                case "turn": return $"Duel.Turn {c.Op} {c.Value}";
                case "enemyBestAttack": return $"BestAttack(Enemy) {c.Op} {c.Value}";
                case "myBestAttack": return $"BestAttack(Bot) {c.Op} {c.Value}";
                case "cardAttack": return $"Card.Attack {c.Op} {c.Value}";
                case "cardLevel": return $"Card.Level {c.Op} {c.Value}";
                case "extraHas": return CallOr(x => "ExtraHas(Bot, " + x + ")", c.Card);   // 2026-10-29: 내 엑스트라 덱
                case "hasInHand": return c.Side == "me"
                    ? CallOr(x => "Bot.HasInHand(" + x + ")", c.Card)
                    : CallOr(x => "Enemy.HasInHand(" + x + ")", c.Card);
                case "hasInGraveyard": return c.Side == "me"
                    ? CallOr(x => "Bot.HasInGraveyard(" + x + ")", c.Card)
                    : CallOr(x => "Enemy.HasInGraveyard(" + x + ")", c.Card);
                case "hasOnField": return c.Side == "me"
                    ? CallOr(x => "OnField(Bot, " + x + ")", c.Card)
                    : CallOr(x => "OnField(Enemy, " + x + ")", c.Card);
                case "fieldHasLevel": return $"FieldHasLevel({SideExpr(c)}, {c.Value})";
                case "fieldHasRank": return $"FieldHasRank({SideExpr(c)}, {c.Value})";
                case "fieldHasLink": return $"FieldHasLink({SideExpr(c)}, {c.Value})";
                case "fieldHasRace": return $"FieldHasRace({SideExpr(c)}, {c.RaceId})";
                case "fieldHasTunerLevel": return $"FieldHasTuner({SideExpr(c)}, {c.Value})";
                case "fieldCardInEmz": return CallOr(x => "FieldHasCardInEmz(" + SideExpr(c) + ", " + x + ")", c.Card);
                case "fieldLevelCount": return $"CountLevels({SideExpr(c)}, {c.Value}, true) {CountOp(c)} {Math.Max(1, c.Count)}";
                // N-6: 펜듈럼
                case "fieldHasPendulum": return $"PendulumCount({SideExpr(c)}) > 0";
                case "pendulumScale": return $"PendulumCovers({SideExpr(c)}, {c.Value})";
                // B-21: 테마 + 종족 + 엑시즈를 "같은 카드"가 모두 만족 (0 = 해당 검사 생략)
                case "fieldHasThemeRaceXyz": return $"FieldHasThemeRaceXyz({SideExpr(c)}, {c.ThemeCode}, {c.RaceId})";
                case "amFirst": return "Duel.IsFirst";
                case "amSecond": return "!Duel.IsFirst";
                case "fieldHasTheme": return ThemeCond(c, c.MonsterOnly
                    ? "FieldHasThemeMonster" : "FieldHasTheme");
                case "handHasTheme": return ThemeCond(c, c.MonsterOnly
                    ? "HandHasThemeMonster" : "HandHasTheme");
                case "gyHasTheme": return ThemeCond(c, c.MonsterOnly
                    ? "GyHasThemeMonster" : "GyHasTheme");
                case "myCardActivated": return "MyCardActivated(false)";
                case "myHandActivated": return "MyCardActivated(true)";
                case "oppMonsterActivated": return "OppActivated(0)";
                case "oppSpellActivated": return "OppActivated(1)";
                case "oppTrapActivated": return "OppActivated(2)";
                case "enemyFieldEmpty": return "Enemy.IsFieldEmpty()";
                case "enemyHasNoMonster": return "Enemy.GetMonsterCount() == 0";
                case "cardIsMonster": return "Card.IsMonster()";
                case "cardIsSpell": return "Card.IsSpell()";
                case "cardIsTrap": return "Card.IsTrap()";
                default:
                    {
                        int eid = EventId(c.Kind);
                        if (eid >= 0) return $"EventCount({(IsEnemyEvent(c.Kind) ? "Enemy" : "Bot")}, {eid}) {c.Op} {c.Value}";
                        return null;
                    }
            }
        }
    }
}































