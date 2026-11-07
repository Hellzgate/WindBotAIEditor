using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using WindBotAIEditor.Models;
using WindBotAIEditor.Services;

namespace WindBotAIEditor
{
    /// <summary>
    /// 2026-11-01 (B-51): 효과번호(`효과`) 칸 자기검사 — 편집형 콤보를 쓰면 **칸을 눌러도 목록이 안 열린다**는
    /// 사용자 보고 때문에 일반 콤보(SelectedValue)로 바꿨다. 이 검사는 그 구성이 유지되는지 확인한다.
    /// </summary>
    public partial class MainWindow
    {
        public List<string> VerifyEffectPickerUi()
        {
            var problems = new List<string>();

            // 1) 목록에 "무관(−1)" 항목이 항상 있어야 지울 수 있다
            var hasNone = false;
            foreach (var it in EffectDescCatalog.Instance.Items)
                if (it.Value == -1) hasNone = true;
            if (!hasNone) problems.Add("b51: 효과 목록에 '(효과 없음)' 항목이 없습니다");

            // 2) 모델 왕복(EfectDescText ↔ EffectDescValue)
            var a = new ActionRule();
            a.EffectDescText = "106328798265345";
            if (a.EffectDescValue != 106328798265345L) problems.Add("b51: ActionRule.EffectDescValue 읽기 실패");
            a.EffectDescValue = -1;
            if (a.EffectDescText != "") problems.Add("b51: ActionRule.EffectDescValue 쓰기 실패");
            var f = new ForbidRule();
            f.EffectDescText = "12345";
            if (f.EffectDescValue != 12345L) problems.Add("b51: ForbidRule.EffectDescValue 읽기 실패");
            f.EffectDescValue = 7;
            if (f.EffectDesc != 7) problems.Add("b51: ForbidRule.EffectDescValue → EffectDesc 실패");

            // 3) 두 그리드의 효과 칸 = 편집형이 아닌 콤보 + SelectedValuePath=Value
            var found = 0;
            foreach (var grid in new[] { GridActions, GridForbids })
            {
                foreach (var col in grid.Columns)
                {
                    var tc = col as DataGridTemplateColumn;
                    if (tc?.CellTemplate == null) continue;
                    var content = tc.CellTemplate.LoadContent() as DependencyObject;
                    var cb = content as ComboBox ?? FindChild<ComboBox>(content);
                    if (cb == null) continue;
                    if (!ReferenceEquals(cb.ItemsSource, EffectDescCatalog.Instance.Items)) continue;
                    found++;
                    if (cb.IsEditable) problems.Add("b51: 효과 콤보가 편집형입니다 (눌러도 목록이 안 열림)");
                    if (cb.SelectedValuePath != "Value") problems.Add("b51: 효과 콤보 SelectedValuePath != Value");
                }
            }
            if (found < 2) problems.Add("b51: 효과 콤보를 2개 찾지 못했습니다 (found=" + found + ")");

            if (problems.Count == 0)
                System.Console.Error.WriteLine("b51 effectPicker ok items=" + EffectDescCatalog.Instance.Items.Count +
                                               " first=" + (EffectDescCatalog.Instance.Items.Count > 0 ? EffectDescCatalog.Instance.Items[0].Label : "-") +
                                               " status=" + EffectDescCatalog.Instance.Status +
                                               " observed=" + EffectDescCatalog.Instance.ObservedCount);
            return problems;
        }

        /// <summary>
        /// 2026-11-01 (B-52): 선택 규칙의 `옵션`/`옵션desc` 가 **읽히는 콤보**인지 확인한다.
        /// 옵션 = 0부터의 목록 순서, 옵션desc = 발동카드의 선택지 문자열(cdb strN).
        /// </summary>
        public List<string> VerifyOptionPickerUi()
        {
            var problems = new List<string>();

            if (OptionIndexOption.All.Count < 3) problems.Add("b52: 옵션 인덱스 후보가 3개 미만입니다");
            if (OptionIndexOption.Find(1).Label.IndexOf("둘째", System.StringComparison.Ordinal) < 0)
                problems.Add("b52: 옵션 라벨이 한글이 아닙니다");

            var none = EffectDescCatalog.BuildChoices(0, 0);
            if (none.Count != 1 || none[0].Value != 0)
                problems.Add("b52: 카드 미지정이면 옵션desc 후보는 (무관) 1개여야 합니다 (n=" + none.Count + ")");

            var cdb = CardDatabase.Instance.IsOpen ? "on" : "skip";
            var probe = 0;
            if (CardDatabase.Instance.IsOpen)
            {
                var list = EffectDescCatalog.BuildChoices(100458025, 0);
                probe = list.Count;
                var hit = false;
                foreach (var it in list)
                    if (it.Value == 105337874022402L && it.Label.IndexOf("#2", System.StringComparison.Ordinal) >= 0) hit = true;
                if (!hit) problems.Add("b52: 100458025 의 옵션desc 후보에 #2(105337874022402) 가 없습니다 (n=" + list.Count + ")");

                // 목록에 없는 값도 후보에 남아야 선택이 빈칸으로 보이지 않는다
                var added = false;
                foreach (var it in EffectDescCatalog.BuildChoices(100458025, 123456789L))
                    if (it.Value == 123456789L && it.Label.IndexOf("[규칙 값]", System.StringComparison.Ordinal) >= 0) added = true;
                if (!added) problems.Add("b52: 목록에 없는 옵션desc 값이 후보에 추가되지 않습니다");
            }

            // 그리드 컬럼이 콤보(SelectedValue=OptionDesc / OptionIndex)인지
            var foundDesc = false;
            var foundIdx = false;
            foreach (var col in GridChoices.Columns)
            {
                var tc = col as DataGridTemplateColumn;
                if (tc?.CellTemplate == null) continue;
                var content = tc.CellTemplate.LoadContent() as DependencyObject;
                var cb = content as ComboBox ?? FindChild<ComboBox>(content);
                if (cb == null || cb.SelectedValuePath != "Value") continue;
                var b = System.Windows.Data.BindingOperations.GetBinding(cb, ComboBox.SelectedValueProperty);
                var path = b?.Path?.Path ?? "";
                if (path == "OptionDesc") foundDesc = true;
                if (path == "OptionIndex") foundIdx = true;
            }
            if (!foundDesc) problems.Add("b52: 옵션desc 콤보(SelectedValue=OptionDesc)를 찾지 못했습니다");
            if (!foundIdx) problems.Add("b52: 옵션 콤보(SelectedValue=OptionIndex)를 찾지 못했습니다");

            if (problems.Count == 0)
                System.Console.Error.WriteLine("b52 optionPicker ok idx=" + OptionIndexOption.All.Count +
                                               " cdb=" + cdb + " descs=" + probe + " colDesc=" + foundDesc + " colIdx=" + foundIdx);
            return problems;
        }

        /// <summary>
        /// 2026-11-01 (B-53): "이번 턴 효과를 사용한 몬스터" 추적 — ② 필터(`skipEffectUsed`)와
        /// ③ 조건(`enemyUnusedEffectCount`)이 생성 코드에 나오는지 확인한다.
        /// </summary>
        public List<string> VerifyEffectUsedUi()
        {
            var problems = new List<string>();

            var p = new AiProfile();
            p.Engine = "probe_uicheck_effused";
            p.FreePlay = true;
            p.DebugEffectLog = false;
            var c = new ChoiceRule
            {
                On = "SelectCard",
                WhenCard = 97268402,
                WhenHint = 514,
                Pick = "enemyHighestAttack",
                SkipEffectUsed = true,
                Priority = 2
            };
            p.Choices.Add(c);
            var f = new ForbidRule { Card = 97268402, Action = "BlockEffect", Priority = 0, ConditionMode = "deny" };
            f.Conditions.Add(new Models.Condition { Kind = "enemyUnusedEffectCount", Op = "<=", Value = 0 });
            p.Forbids.Add(f);

            var src = RuleCodeGen.Generate(p);
            if (src.IndexOf("_usedEffectKeys", System.StringComparison.Ordinal) < 0) problems.Add("b53: _usedEffectKeys 필드가 없습니다");
            if (src.IndexOf("MarkEffectUsed(card)", System.StringComparison.Ordinal) < 0) problems.Add("b53: OnChaining 기록이 없습니다");
            if (src.IndexOf("FilterNotEffectUsed(", System.StringComparison.Ordinal) < 0) problems.Add("b53: skipEffectUsed 필터가 없습니다");
            if (src.IndexOf("CountUnusedEffect(Enemy) <= 0", System.StringComparison.Ordinal) < 0) problems.Add("b53: enemyUnusedEffectCount 조건식이 없습니다");
            if (src.IndexOf("_usedEffectKeys.Clear()", System.StringComparison.Ordinal) < 0) problems.Add("b53: 새 턴 초기화가 없습니다");

            // ② 그리드에는 **필터 요약 열**이 있고, 실제 체크박스는 팝업(ChoiceFiltersWindow)에 있다 (2026-11-07)
            var hasFilterCol = false;
            foreach (var col in GridChoices.Columns)
                if (Convert.ToString(col.Header) == "필터") hasFilterCol = true;
            if (!hasFilterCol) problems.Add("b53: ② 그리드에 '필터' 요약 컬럼이 없습니다");

            var probeRule = new ChoiceRule { On = "SelectCard", SkipEffectUsed = true, OnlyEnemy = true };
            if (probeRule.FilterSummary.IndexOf("사용됨", System.StringComparison.Ordinal) < 0 ||
                probeRule.FilterSummary.IndexOf("상대", System.StringComparison.Ordinal) < 0)
                problems.Add("b53: FilterSummary 요약이 필터 값을 반영하지 않습니다: " + probeRule.FilterSummary);

            var filterWin = new ChoiceFiltersWindow(new ChoiceRule { On = "SelectCard" });
            try
            {
                if (!filterWin.HasCheckBoxBinding("SkipEffectUsed"))
                    problems.Add("b53: 필터 팝업에 SkipEffectUsed 체크박스 바인딩이 없습니다");
                if (!filterWin.HasCheckBoxBinding("OnlyEnemy"))
                    problems.Add("b53: 필터 팝업에 OnlyEnemy 체크박스 바인딩이 없습니다");
            }
            finally { filterWin.Close(); }

            // 2026-11-01: 체인 마지막 = 상대 필드 몬스터 조건 (뵐러/포영 발동 게이트)
            var p2 = new AiProfile();
            p2.Engine = "probe_uicheck_chainlast";
            p2.FreePlay = true;
            var f2 = new ForbidRule { Card = 97268402, Action = "BlockEffect", Priority = 0, ConditionMode = "deny" };
            f2.Conditions.Add(new Models.Condition { Kind = "chainLastEnemyFieldMonster", Not = true });
            p2.Forbids.Add(f2);
            var src2 = RuleCodeGen.Generate(p2);
            if (src2.IndexOf("ChainLastEnemyFieldMonster()", System.StringComparison.Ordinal) < 0)
                problems.Add("b53: 체인 마지막 몬스터 조건이 생성되지 않습니다");
            if (src2.IndexOf("!(ChainLastEnemyFieldMonster())", System.StringComparison.Ordinal) < 0)
                problems.Add("b53: 체인 마지막 몬스터 조건의 부정식이 없습니다");

            if (problems.Count == 0)
                System.Console.Error.WriteLine("b53 effectUsed ok gen=OK col=OK lines=" + src.Split('\n').Length);
            return problems;
        }

        /// <summary>
        /// 2026-11-01 (B-54): 파괴 선택(hint=Destroy)에서 파괴 내성 카드 제외 — **전역 옵션** 자기검사.
        /// 옵션 ON 이면 생성 코드에 `FilterDestructImmune` + `hint == HintMsg.Destroy` 가 있어야 하고,
        /// OFF 면 하나도 없어야 한다(회귀 0줄).
        /// </summary>
        public List<string> VerifyDestructImmuneUi()
        {
            var problems = new List<string>();

            if (ChkSkipDestructImmune == null) { problems.Add("b54: 전역 체크박스(ChkSkipDestructImmune) 없음"); return problems; }
            if (ChkSkipDestructImmune.IsChecked != true) problems.Add("b54: 전역 기본값이 ON 이 아님");
            if (!new AiProfile().SkipDestructImmuneOnDestroy) problems.Add("b54: 모델 기본값이 ON 이 아님");

            var on = new AiProfile { Engine = "probe_uicheck_destruct_on", FreePlay = true, SkipDestructImmuneOnDestroy = true };
            on.Choices.Add(new ChoiceRule { On = "SelectCard", WhenCard = 70088809, WhenHint = 502, Pick = "enemyHighestAttack", Priority = 1 });
            var srcOn = RuleCodeGen.Generate(on);
            if (srcOn.IndexOf("FilterDestructImmune(", System.StringComparison.Ordinal) < 0) problems.Add("b54: ON 인데 FilterDestructImmune 호출이 없습니다");
            if (srcOn.IndexOf("hint == HintMsg.Destroy", System.StringComparison.Ordinal) < 0) problems.Add("b54: ON 인데 hint == HintMsg.Destroy 게이트가 없습니다");

            var off = new AiProfile { Engine = "probe_uicheck_destruct_off", FreePlay = true, SkipDestructImmuneOnDestroy = false };
            off.Choices.Add(new ChoiceRule { On = "SelectCard", WhenCard = 70088809, WhenHint = 502, Pick = "enemyHighestAttack", Priority = 1 });
            var srcOff = RuleCodeGen.Generate(off);
            if (srcOff.IndexOf("FilterDestructImmune", System.StringComparison.Ordinal) >= 0) problems.Add("b54: OFF 인데 FilterDestructImmune 이 생성됨");

            if (problems.Count == 0)
                System.Console.Error.WriteLine("b54 destructImmune ok chk=ON gen=OK off=0 lines=" + srcOn.Split('\n').Length);
            return problems;
        }

        /// <summary>
        /// 2026-11-01 (B-55): 파괴 실패 후보 수집 — 로그 파싱 + UI 컨트롤 자기검사.
        /// </summary>
        public List<string> VerifyDestructCandUi()
        {
            var problems = new List<string>();

            if (TxtDestructCand == null) problems.Add("b55: TxtDestructCand(후보 문구) 없음");
            if (BtnDestructCandAdd == null) problems.Add("b55: BtnDestructCandAdd([후보 추가]) 없음");
            if (BtnDestructCandIgnore == null) problems.Add("b55: BtnDestructCandIgnore([무시]) 없음");

            var ids = DestructFailCatalog.ParseFailIds(
                "23:12:07 [AI-EDITOR] destruct-fail card=101403999 seq=0 name=Test Card  → 파괴 내성 후보");
            if (ids.Count != 1 || ids[0] != 101403999) problems.Add("b55: destruct-fail 파싱 실패 (n=" + ids.Count + ")");

            var none = DestructFailCatalog.ParseFailIds("23:12:07 [AI-EDITOR] select card=1 min=1 max=1 -> 2");
            if (none.Count != 0) problems.Add("b55: 무관한 줄을 후보로 파싱함");

            if (DestructFailCatalog.Instance.Items == null) problems.Add("b55: 후보 컬렉션 없음");

            if (problems.Count == 0)
                System.Console.Error.WriteLine("b55 destructCand ok parse=OK ui=OK items=" + DestructFailCatalog.Instance.Items.Count +
                                               " (" + DestructFailCatalog.Instance.Status + ")");
            return problems;
        }

        /// <summary>
        /// 2026-11-01 (B-56): duel-log 를 **2곳**(WindBot 폴더 / %LOCALAPPDATA%)에서 읽는지 자기검사.
        /// </summary>
        public List<string> VerifyDuelLogPathsUi()
        {
            var problems = new List<string>();
            var paths = DuelLogLocator.Paths();
            if (paths.Count == 0) problems.Add("b56: 로그 경로 후보가 없습니다");
            foreach (var p in paths)
                if (!p.EndsWith("duel-log.txt", StringComparison.OrdinalIgnoreCase))
                    problems.Add("b56: 로그 경로가 duel-log.txt 가 아닙니다: " + p);
            var local = DuelLogLocator.LocalPath;
            if (!paths.Contains(local)) problems.Add("b56: %LOCALAPPDATA% 경로가 후보에 없습니다");

            if (problems.Count == 0)
                System.Console.Error.WriteLine("b56 duelLogPaths ok n=" + paths.Count + " local=OK");
            return problems;
        }

        /// <summary>
        /// 2026-11-02 (B-57): "효과 면역 카드 대상 제외(릴리스 제외)" 전역 옵션 자기검사.
        /// 목록이 있고 옵션이 ON 이면 생성 코드에 `FilterEffectImmune` + `hint != HintMsg.Release` 가 있어야 하고,
        /// 목록이 비었거나 옵션이 OFF 면 하나도 없어야 한다.
        /// </summary>
        public List<string> VerifyEffectImmuneUi()
        {
            var problems = new List<string>();

            if (ChkSkipEffectImmune == null) problems.Add("b57: 전역 체크박스(ChkSkipEffectImmune) 없음");
            if (ChkSkipEffectImmune != null && ChkSkipEffectImmune.IsChecked != true) problems.Add("b57: 전역 기본값이 ON 이 아님");
            if (CardEffectImmune == null) problems.Add("b57: 카드 검색(CardEffectImmune) 없음");
            if (TxtEffectImmune == null) problems.Add("b57: 목록 표시(TxtEffectImmune) 없음");
            if (!new AiProfile().SkipEffectImmuneTargets) problems.Add("b57: 모델 기본값이 ON 이 아님");

            var on = new AiProfile { Engine = "probe_uicheck_eimmune_on", FreePlay = true, SkipEffectImmuneTargets = true };
            on.EffectImmuneCards.Add(17242023);
            var srcOn = RuleCodeGen.Generate(on);
            if (srcOn.IndexOf("FilterEffectImmune(", StringComparison.Ordinal) < 0) problems.Add("b57: ON+목록인데 FilterEffectImmune 이 없습니다");
            if (srcOn.IndexOf("hint != HintMsg.Release", StringComparison.Ordinal) < 0) problems.Add("b57: 릴리스 제외 게이트가 없습니다");
            if (srcOn.IndexOf("IsCode(17242023)", StringComparison.Ordinal) < 0) problems.Add("b57: 목록 카드가 생성 코드에 없습니다");

            var empty = new AiProfile { Engine = "probe_uicheck_eimmune_empty", FreePlay = true, SkipEffectImmuneTargets = true };
            if (RuleCodeGen.Generate(empty).IndexOf("FilterEffectImmune", StringComparison.Ordinal) >= 0)
                problems.Add("b57: 목록이 비었는데 FilterEffectImmune 이 생성됨");

            var off = new AiProfile { Engine = "probe_uicheck_eimmune_off", FreePlay = true, SkipEffectImmuneTargets = false };
            off.EffectImmuneCards.Add(17242023);
            if (RuleCodeGen.Generate(off).IndexOf("FilterEffectImmune", StringComparison.Ordinal) >= 0)
                problems.Add("b57: 옵션 OFF 인데 FilterEffectImmune 이 생성됨");

            if (problems.Count == 0)
                System.Console.Error.WriteLine("b57 effectImmune ok chk=ON gen=OK empty=0 off=0 lines=" + srcOn.Split('\n').Length);
            return problems;
        }

        /// <summary>
        /// 2026-11-02 (B-58): '무효화 카드' 목록 + 전역 옵션 — 효과 면역 카드의 효과에 체인 금지(발동 차단).
        /// 목록이 비었거나 옵션이 OFF 면 생성 코드에 `NegateVsEffectImmune` 이 없어야 한다.
        /// </summary>
        public List<string> VerifyNegateImmuneUi()
        {
            var problems = new List<string>();

            if (ChkSkipEffectImmuneNegate == null) problems.Add("b58: 전역 체크박스(ChkSkipEffectImmuneNegate) 없음");
            if (ChkSkipEffectImmuneNegate != null && ChkSkipEffectImmuneNegate.IsChecked != true) problems.Add("b58: 전역 기본값이 ON 이 아님");
            if (CardNegate == null) problems.Add("b58: 카드 검색(CardNegate) 없음");
            if (TxtNegate == null) problems.Add("b58: 목록 표시(TxtNegate) 없음");
            if (BtnNegateDefault == null) problems.Add("b58: 기본값 버튼(BtnNegateDefault) 없음");

            var on = new AiProfile { Engine = "probe_uicheck_neg_on", FreePlay = true, DebugEffectLog = true };
            on.EffectImmuneCards.Add(17242023);
            on.NegateCardIds.Add(97268402);
            var srcOn = RuleCodeGen.Generate(on);
            if (srcOn.IndexOf("NegateVsEffectImmune(", StringComparison.Ordinal) < 0) problems.Add("b58: ON 인데 NegateVsEffectImmune 이 없습니다");
            if (srcOn.IndexOf("negate-immune", StringComparison.Ordinal) < 0) problems.Add("b58: 차단 로그가 없습니다");

            var noNeg = new AiProfile { Engine = "probe_uicheck_neg_empty", FreePlay = true };
            noNeg.EffectImmuneCards.Add(17242023);
            if (RuleCodeGen.Generate(noNeg).IndexOf("NegateVsEffectImmune", StringComparison.Ordinal) >= 0)
                problems.Add("b58: 무효화 목록이 비었는데 생성됨");

            var off = new AiProfile { Engine = "probe_uicheck_neg_off", FreePlay = true, SkipEffectImmuneNegate = false };
            off.EffectImmuneCards.Add(17242023);
            off.NegateCardIds.Add(97268402);
            if (RuleCodeGen.Generate(off).IndexOf("NegateVsEffectImmune", StringComparison.Ordinal) >= 0)
                problems.Add("b58: 옵션 OFF 인데 생성됨");

            if (problems.Count == 0)
                System.Console.Error.WriteLine("b58 negateImmune ok chk=ON gen=OK empty=0 off=0 lines=" + srcOn.Split('\n').Length);
            return problems;
        }

        /// <summary>
        /// 2026-11-02 (B-59): '파괴 카드' 목록(유령토끼) — 파괴 내성 카드의 효과에 체인 금지.
        /// </summary>
        public List<string> VerifyDestroyCardUi()
        {
            var problems = new List<string>();

            if (ChkSkipDestructImmuneDestroy == null) problems.Add("b59: 전역 체크박스(ChkSkipDestructImmuneDestroy) 없음");
            if (ChkSkipDestructImmuneDestroy != null && ChkSkipDestructImmuneDestroy.IsChecked != true) problems.Add("b59: 전역 기본값이 ON 이 아님");
            if (CardDestroy == null) problems.Add("b59: 카드 검색(CardDestroy) 없음");
            if (TxtDestroy == null) problems.Add("b59: 목록 표시(TxtDestroy) 없음");
            if (BtnDestroyDefault == null) problems.Add("b59: 기본값 버튼(BtnDestroyDefault) 없음");

            var on = new AiProfile { Engine = "probe_uicheck_dst_on", FreePlay = true, DebugEffectLog = true };
            on.DestructibleImmuneCards.Add(101403017);
            on.DestroyCardIds.Add(59438931);
            var srcOn = RuleCodeGen.Generate(on);
            if (srcOn.IndexOf("DestroyVsImmune(", StringComparison.Ordinal) < 0) problems.Add("b59: ON 인데 DestroyVsImmune 이 없습니다");
            if (srcOn.IndexOf("destroy-immune", StringComparison.Ordinal) < 0) problems.Add("b59: 차단 로그가 없습니다");

            var empty = new AiProfile { Engine = "probe_uicheck_dst_empty", FreePlay = true };
            empty.DestructibleImmuneCards.Add(101403017);
            if (RuleCodeGen.Generate(empty).IndexOf("DestroyVsImmune", StringComparison.Ordinal) >= 0)
                problems.Add("b59: 파괴 카드 목록이 비었는데 생성됨");

            var off = new AiProfile { Engine = "probe_uicheck_dst_off", FreePlay = true, SkipDestructImmuneDestroy = false };
            off.DestructibleImmuneCards.Add(101403017);
            off.DestroyCardIds.Add(59438931);
            if (RuleCodeGen.Generate(off).IndexOf("DestroyVsImmune", StringComparison.Ordinal) >= 0)
                problems.Add("b59: 옵션 OFF 인데 생성됨");

            if (problems.Count == 0)
                System.Console.Error.WriteLine("b59 destroyCard ok chk=ON gen=OK empty=0 off=0 lines=" + srcOn.Split('\n').Length);
            return problems;
        }

        /// <summary>
        /// 2026-11-02 (B-61): **다른 규칙 JSON 불러오기** — 컨트롤 존재 + 템플릿 로드(다른 엔진) 검증.
        /// </summary>
        public List<string> VerifyImportJsonUi()
        {
            var problems = new List<string>();
            if (BtnImportJson == null) problems.Add("b61: 버튼(BtnImportJson) 없음");
            if (ChkImportApplyEngine == null) problems.Add("b61: 체크박스(ChkImportApplyEngine) 없음");
            if (ChkImportApplyEngine != null && ChkImportApplyEngine.IsChecked == true)
                problems.Add("b61: 기본값이 '엔진명 적용' — 기본은 유지여야 합니다");

            string tmp = null;
            var saved = _profile;   // 2026-11-02: 검사 후 복원용
            try
            {
                var p = new AiProfile { Engine = "probeImportSrc", Description = "template" };
                p.Actions.Add(new ActionRule { Type = "Activate", Card = 59438930, Priority = 0 });
                p.Forbids.Add(new ForbidRule { Card = 97268402, Action = "Activate", Priority = 5 });
                p.SkipResistantGlobal = false;
                tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "wbimport-probe.json");
                File.WriteAllText(tmp, JsonSerializer.Serialize(p, WindBotProject.JsonOpts), new System.Text.UTF8Encoding(false));

                if (!LoadProfileFile(tmp)) { problems.Add("b61: 템플릿 JSON 로드 실패"); return problems; }
                if (_actions.Count != 1 || _forbids.Count != 1) problems.Add("b61: 로드 후 규칙 수가 다릅니다 (행동 " + _actions.Count + "/금지 " + _forbids.Count + ")");
                if (_profile == null || _profile.SkipResistantGlobal) problems.Add("b61: 전역 설정이 반영되지 않았습니다");
                // 2026-11-02: 검사 후 **원래 프로필 복원** — --uicheck 의 최종 요약이 오염되지 않게
                if (saved != null) { try { ApplyProfile(saved); } catch { } }
            }
            catch (Exception ex)
            {
                problems.Add("b61: 예외 " + ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                try { if (tmp != null && File.Exists(tmp)) File.Delete(tmp); } catch { }
            }

            if (problems.Count == 0)
                System.Console.Error.WriteLine("b61 importJson ok btn=OK chkDefault=OK load=OK");
            return problems;
        }

        /// <summary>
        /// 2026-11-02 (B-62): 다른 JSON **추가(병합)** — 중복 건너뜀 · 우선순위 이어붙임 · 전역 설정 유지.
        /// </summary>
        public List<string> VerifyMergeProfileUi()
        {
            var problems = new List<string>();
            try
            {
                var target = new AiProfile { Engine = "mergeTarget", SkipResistantGlobal = false, SkipDestructImmuneOnDestroy = false };
                target.Actions.Add(new ActionRule { Type = "Activate", Card = 59438930, Priority = 10 });
                target.Forbids.Add(new ForbidRule { Card = 97268402, Action = "Activate", Priority = 10 });

                var src = new AiProfile { Engine = "mergeSrc", SkipResistantGlobal = true, SkipDestructImmuneOnDestroy = true };
                src.Actions.Add(new ActionRule { Type = "Activate", Card = 59438930, Priority = 10 });   // 중복
                src.Actions.Add(new ActionRule { Type = "SpSummon", Card = 100458018, Priority = 20 });  // 신규
                src.Choices.Add(new ChoiceRule { On = "SelectCard", Pick = "firstAvailable", WhenCard = 0, Priority = 5 });
                src.Forbids.Add(new ForbidRule { Card = 97268402, Action = "Activate", Priority = 20 }); // 중복

                var c = ProfileMerger.MergeInto(target, src);
                if (c.Actions != 1) problems.Add("b62: 추가된 행동 수 " + c.Actions + " (1 기대)");
                if (c.Choices != 1) problems.Add("b62: 추가된 선택 규칙 수 " + c.Choices + " (1 기대)");
                if (c.Forbids != 0) problems.Add("b62: 중복 금지가 추가됨 (" + c.Forbids + ")");
                if (c.Skipped != 2) problems.Add("b62: 건너뛴 중복 " + c.Skipped + " (2 기대)");
                if (target.Actions.Count != 2) problems.Add("b62: 병합 후 행동 " + target.Actions.Count + " (2 기대)");

                var added = target.Actions.FirstOrDefault(x => x.Card == 100458018);
                if (added == null) problems.Add("b62: 신규 행동이 없습니다");
                else if (added.Priority <= 10) problems.Add("b62: 병합 행동 우선순위 " + added.Priority + " (기존 10 보다 커야 함)");

                if (target.SkipResistantGlobal) problems.Add("b62: 병합이 전역 설정을 덮어씀(내성)");
                if (target.SkipDestructImmuneOnDestroy) problems.Add("b62: 병합이 전역 설정을 덮어씀(파괴내성)");

                // 선택 창이 정상 생성되는지(표시하지 않음) — 버튼/기본값
                var dlg = new ImportModeWindow("probe");
                if (dlg.Mode != ImportModeWindow.ImportMode.Cancel) problems.Add("b62: 선택 창 기본 모드가 취소가 아님");
                if (dlg.BtnReplace == null || dlg.BtnMerge == null) problems.Add("b62: 선택 창 버튼 누락");
            }
            catch (System.Exception ex)
            {
                problems.Add("b62: 예외 " + ex.GetType().Name + ": " + ex.Message);
            }

            if (problems.Count == 0)
                System.Console.Error.WriteLine("b62 mergeProfile ok dup=skip order=OK global=keep");
            return problems;
        }

        /// <summary>
        /// 2026-11-02 (B-63): C#5 검증기 — 정상 코드는 OK, C#6+ 문법은 오류로 잡고, 안드로이드 금지 API 도 스캔.
        /// </summary>
        public List<string> VerifyCSharp5VerifierUi()
        {
            var problems = new List<string>();
            try
            {
                // ① 금지 API 스캔 (프로젝트 없이 가능)
                var hits = CSharp5Verifier.ScanForbiddenApis("File.AppendAllText(\"x\", \"y\");\nvar t = Path.GetTempPath();");
                if (hits.Count < 2) problems.Add("b63: 금지 API 스캔이 동작하지 않습니다 (" + hits.Count + ")");
                if (hits.Count > 0 && hits[0].Line <= 0) problems.Add("b63: 금지 API 줄 번호가 없습니다");
                if (CSharp5Verifier.ScanForbiddenApis("Console.WriteLine(1);").Count != 0)
                    problems.Add("b63: 정상 코드를 금지 API 로 오탐");
                // LINQ 누락(Any) 도 잡아야 한다 — 안드로이드 트리밍 BCL
                if (CSharp5Verifier.ScanForbiddenApis("if (Bot.Hand.Any(c => c.IsCode(1))) return true;").Count == 0)
                    problems.Add("b63: LINQ .Any() 를 잡지 못함");
                // 주석은 오탐하지 않아야 한다
                if (CSharp5Verifier.ScanForbiddenApis("// File.AppendAllText 는 안드로이드에 없음").Count != 0)
                    problems.Add("b63: 주석의 API 이름을 오탐");

                // Android 안전 검사 토글
                if (ChkAndroidSafe == null) problems.Add("b63: 체크박스(ChkAndroidSafe) 없음");
                else if (ChkAndroidSafe.IsChecked != true) problems.Add("b63: Android 안전 검사 기본값이 OFF");

                // ② 실제 컴파일 검증 (WindBot 프로젝트가 있을 때만)
                var proj = _project;
                if (proj == null)
                {
                    try
                    {
                        var r0 = App.EdoproRoot ?? "";
                        if (!string.IsNullOrWhiteSpace(r0))
                        {
                            var wb = System.IO.Path.Combine(r0, "WindBot");
                            if (System.IO.Directory.Exists(wb)) proj = new WindBotProject(wb);
                        }
                    }
                    catch { }
                }
                if (proj == null || !System.IO.File.Exists(proj.ExecutorBaseDll))
                {
                    System.Console.Error.WriteLine("b63 csharp5 ok scan=OK compile=skip (WindBot 폴더 없음)");
                    return problems;
                }

                var p = new AiProfile { Engine = "probeVerify", FreePlay = true };
                var good = RuleCodeGen.Generate(p);
                var okRes = CSharp5Verifier.Verify(proj, good, "probeVerifyExecutor");
                if (!okRes.Ok)
                    problems.Add("b63: 정상 생성 코드인데 검증 실패 — " + okRes.Summary + " " + string.Join(" | ", okRes.Items.ConvertAll(d => d.ToString()).ToArray()));

                var bad = good.Replace("public Nekroz", "public X") // no-op guard
                               .Replace("    public class probeVerifyExecutor : DefaultExecutor", "    public class probeVerifyExecutor : DefaultExecutor");
                bad = bad.Replace("        public probeVerifyExecutor(", "        public int BadProp => 1;\n        public probeVerifyExecutor(");
                var badRes = CSharp5Verifier.Verify(proj, bad, "probeVerifyExecuto");
                if (badRes.Ok) problems.Add("b63: C#6 식 본문(=>)을 검증기가 통과시킴");

                if (problems.Count == 0)
                    System.Console.Error.WriteLine("b63 csharp5 ok scan=OK compile=OK bad=detected");
            }
            catch (System.Exception ex)
            {
                problems.Add("b63: 예외 " + ex.GetType().Name + ": " + ex.Message);
            }
            return problems;
        }

        /// <summary>
        /// 2026-11-02 (B-65): 로그 진단 강화 — `hit rule=N`(C-1) + 후보 목록 `cards=[…]`(C-2).
        /// debugEffectLog ON 일 때만 생성되고, OFF 면 0줄이어야 한다(회귀).
        /// </summary>
        public List<string> VerifyHitLogUi()
        {
            var problems = new List<string>();
            try
            {
                var on = new AiProfile { Engine = "probeHit", FreePlay = true, DebugEffectLog = true };
                on.Choices.Add(new ChoiceRule { On = "SelectCard", Pick = "firstAvailable", Priority = 10 });
                var srcOn = RuleCodeGen.Generate(on);
                if (srcOn.IndexOf("LogHit(", StringComparison.Ordinal) < 0) problems.Add("b65: hit rule 로그(LogHit)가 없습니다");
                if (srcOn.IndexOf("cards=[", StringComparison.Ordinal) < 0) problems.Add("b65: 후보 목록(cards=[) 로그가 없습니다");
                if (srcOn.IndexOf("hit rule=", StringComparison.Ordinal) < 0) problems.Add("b65: 'hit rule=' 문구가 없습니다");

                var off = new AiProfile { Engine = "probeHitOff", FreePlay = true, DebugEffectLog = false };
                off.Choices.Add(new ChoiceRule { On = "SelectCard", Pick = "firstAvailable", Priority = 10 });
                var srcOff = RuleCodeGen.Generate(off);
                if (srcOff.IndexOf("LogHit(", StringComparison.Ordinal) >= 0) problems.Add("b65: 로그 OFF 인데 LogHit 생성됨");

                if (problems.Count == 0)
                    System.Console.Error.WriteLine("b65 hitLog ok on=OK off=0");
            }
            catch (System.Exception ex)
            {
                problems.Add("b65: 예외 " + ex.GetType().Name + ": " + ex.Message);
            }
            return problems;
        }

        /// <summary>
        /// 2026-11-02 (B-66): ⚙ AI 설정 **검색 상자** — 입력 시 자동 펼침 + 매칭 행만 표시 + 지우면 복원.
        /// 검사 후 반드시 원상 복구한다(접힘 상태·검색어 초기화).
        /// </summary>
        public List<string> VerifySettingsSearchUi()
        {
            var problems = new List<string>();
            var oldCollapsed = PnlSettings.Visibility != Visibility.Visible;
            try
            {
                if (TxtSettingsSearch == null) { problems.Add("b66: 검색 상자 없음"); return problems; }
                if (TxtSettingsSearchInfo == null) problems.Add("b66: 결과 표시 없음");

                var rows = SettingsRows();
                if (rows.Count < 10) problems.Add("b66: 설정 행을 찾지 못했습니다 (" + rows.Count + ")");

                TxtSettingsSearch.Text = "면역";
                if (PnlSettings.Visibility != Visibility.Visible) problems.Add("b66: 검색해도 설정 카드가 펼쳐지지 않음");
                var visible = 0;
                foreach (var r in rows) if (r.Visibility == Visibility.Visible) visible++;
                if (visible == 0) problems.Add("b66: '면역' 검색 결과가 0건");
                if (rows.Count > 0 && visible >= rows.Count) problems.Add("b66: 검색했는데 모든 행이 보임(필터 미동작)");
                if (TxtSettingsSearchInfo.Text.IndexOf("/", StringComparison.Ordinal) < 0)
                    problems.Add("b66: 결과 수 표시가 없습니다 (" + TxtSettingsSearchInfo.Text + ")");

                TxtSettingsSearch.Text = "";
                var restored = 0;
                foreach (var r in rows) if (r.Visibility == Visibility.Visible) restored++;
                if (rows.Count > 0 && restored != rows.Count)
                    problems.Add("b66: 검색어를 지웠는데 복원되지 않음 (" + restored + "/" + rows.Count + ")");

                if (problems.Count == 0)
                    System.Console.Error.WriteLine("b66 settingsSearch ok rows=" + rows.Count + " filter=OK restore=OK");
            }
            catch (System.Exception ex)
            {
                problems.Add("b66: 예외 " + ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                try
                {
                    if (TxtSettingsSearch != null) TxtSettingsSearch.Text = "";
                    SetSettingsCollapsed(oldCollapsed);
                }
                catch { }
            }
            return problems;
        }

        /// <summary>
        /// 2026-11-02 (B-67): 설정 **기본값/변경됨 배지** — 값 변경 시 감지 + `@변경` 필터 + 되돌리기.
        /// 검사 후 원래 값으로 복구한다.
        /// </summary>
        public List<string> VerifySettingBadgesUi()
        {
            var problems = new List<string>();
            var oldCollapsed = PnlSettings.Visibility != Visibility.Visible;
            var cb = FindNamed("ChkAndroidSafe") as CheckBox;
            var oldValue = cb?.IsChecked;
            try
            {
                if (cb == null) { problems.Add("b67: ChkAndroidSafe 를 찾지 못했습니다"); return problems; }

                // 라이브 프로필은 이미 기본값과 다른 항목이 있을 수 있으므로 **기준선**을 잡고 증감만 검사한다
                cb.IsChecked = true;
                UpdateSettingBadges();
                var baseline = _changedNames.Count;
                if (_changedNames.Contains("ChkAndroidSafe"))
                    problems.Add("b67: 기본(ON)인데 변경으로 표시됨");
                var baselineRows = _changedRows.Count;

                // 2) 하나 바꾸면 그 항목만 추가로 '변경'
                cb.IsChecked = false;
                UpdateSettingBadges();
                if (!_changedNames.Contains("ChkAndroidSafe"))
                    problems.Add("b67: Android 안전 검사 OFF 인데 변경 감지 안 됨");
                if (_changedNames.Count != baseline + 1)
                    problems.Add("b67: 변경 항목이 1개 늘지 않음 (" + baseline + " → " + _changedNames.Count + ")");
                if (_changedRows.Count != baselineRows + 1)
                    problems.Add("b67: 변경 행이 1개 늘지 않음 (" + baselineRows + " → " + _changedRows.Count + ")");
                if (cb.FontWeight != System.Windows.FontWeights.Bold)
                    problems.Add("b67: 변경된 행이 굵게 표시되지 않음");

                // 3) @변경 필터 — 그 행이 포함되어야 한다
                TxtSettingsSearch.Text = "@변경";
                var shown = 0;
                foreach (var r in SettingsRows()) if (r.Visibility == Visibility.Visible) shown++;
                if (shown != _changedRows.Count)
                    problems.Add("b67: '@변경' 필터 결과(" + shown + ")가 변경 행 수(" + _changedRows.Count + ")와 다름");

                // 4) 되돌리기 → 기본값 복귀 + 변경 목록에서 제거
                RevertSetting(SettingsMap().First(d => d.Name == "ChkAndroidSafe"));
                if (cb.IsChecked != true) problems.Add("b67: 되돌리기 실패 (값이 기본으로 안 돌아감)");
                if (_changedNames.Contains("ChkAndroidSafe")) problems.Add("b67: 되돌린 뒤에도 변경 항목이 남음");

                if (problems.Count == 0)
                    System.Console.Error.WriteLine("b67 settingBadges ok baseline=" + baseline + " change=+1 filter=" + shown + " revert=OK");
            }
            catch (System.Exception ex)
            {
                problems.Add("b67: 예외 " + ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                try
                {
                    if (cb != null) cb.IsChecked = oldValue;
                    if (TxtSettingsSearch != null) TxtSettingsSearch.Text = "";
                    UpdateSettingBadges();
                    SetSettingsCollapsed(oldCollapsed);
                }
                catch { }
            }
            return problems;
        }

        /// <summary>
        /// 2026-11-02 (B-68): 규칙 JSON 형식 판별 — **분석 덤프**를 규칙으로 오인해 조용히 0규칙으로 여는 사고 방지.
        /// </summary>
        public List<string> VerifyProfileJsonGuardUi()
        {
            var problems = new List<string>();
            try
            {
                string why;
                // 정상 규칙 JSON
                if (!ProfileJsonGuard.IsEditorProfile("{\"engine\":\"X\",\"actions\":[]}", out why))
                    problems.Add("b68: 정상 규칙 JSON 을 거부했습니다 (" + why + ")");
                // 분석 덤프
                if (ProfileJsonGuard.IsEditorProfile("{\"class\":\"XExecutor\",\"deckName\":\"X\",\"behaviors\":[{\"type\":\"Activate\"}],\"signalCounts\":{}}", out why))
                    problems.Add("b68: 분석 덤프를 규칙으로 통과시킴");
                else if (why.IndexOf("분석 덤프", StringComparison.Ordinal) < 0)
                    problems.Add("b68: 덤프 거부 사유가 불명확: " + why);
                // 규칙 키 없음
                if (ProfileJsonGuard.IsEditorProfile("{\"foo\":1}", out why))
                    problems.Add("b68: 규칙 키가 없는데 통과시킴");
                // 깨진 JSON
                if (ProfileJsonGuard.IsEditorProfile("{ not json", out why))
                    problems.Add("b68: 깨진 JSON 을 통과시킴");

                if (problems.Count == 0)
                    System.Console.Error.WriteLine("b68 jsonGuard ok profile=OK dump=blocked nokey=blocked broken=blocked");
            }
            catch (System.Exception ex)
            {
                problems.Add("b68: 예외 " + ex.GetType().Name + ": " + ex.Message);
            }
            return problems;
        }

        /// <summary>
        /// 2026-11-02 (B-72): ⑤ 전개 루트 **목록에서 루트 삭제** — `− 루트` 버튼 / Delete 키 동작 검증.
        /// <para>추가 → 삭제 → 개수 복원, 기본 루트 삭제 시 다른 루트가 기본으로 승격되는지 확인.</para>
        /// </summary>
        public List<string> VerifyRouteDeleteUi()
        {
            var problems = new List<string>();
            try
            {
                CommitGrids();
                var before = RouteSetCount(false);
                var origDefaults = ActiveRouteSets.Where(r => r.IsDefault).ToList();
                var added = new List<RouteSet>();

                // 1) 2벌 추가
                added.Add(AddRouteSet());
                added.Add(AddRouteSet());
                if (RouteSetCount(false) != before + 2)
                    problems.Add("b72: 루트 추가 실패 (" + before + " → " + RouteSetCount(false) + ")");

                // 2) 일반 루트 삭제
                RemoveRouteSet(added[0]);
                if (RouteSetCount(false) != before + 1)
                    problems.Add("b72: 루트 삭제 실패 (" + RouteSetCount(false) + ")");
                if (ActiveRouteSets.Contains(added[0]))
                    problems.Add("b72: 삭제한 루트가 목록에 남아 있음");

                // 3) **기본 루트**를 삭제해도 트랙에 기본이 1개 남는지 (추가분을 기본으로 만들어 안전하게 검사)
                added[1].IsDefault = true;
                RemoveRouteSet(added[1]);
                if (RouteSetCount(false) != before)
                    problems.Add("b72: 기본 루트 삭제 후 개수가 원복되지 않음 (" + RouteSetCount(false) + ")");
                if (ActiveRouteSets.Count > 0 && !ActiveRouteSets.Any(r => r.IsDefault))
                    problems.Add("b72: 기본 루트 삭제 후 다른 루트가 기본으로 승격되지 않음");

                // 4) 원래 기본 표시 복원
                foreach (var r in origDefaults) if (ActiveRouteSets.Contains(r)) r.IsDefault = true;
                UpdateRouteTrackUi();

                if (problems.Count == 0)
                    System.Console.Error.WriteLine("b72 routeDelete ok add=+2 del=OK default=OK restore=OK");
            }
            catch (System.Exception ex)
            {
                problems.Add("b72: 예외 " + ex.GetType().Name + ": " + ex.Message);
            }
            return problems;
        }

        /// <summary>
        /// B-73 (2026-11-02): ⑤ 루트 순서 강제 — 체크박스 기본값 + 생성 코드 on/off 검증.
        /// 켜면 ① 액션에 <c>RoutePending</c> 게이트가 붙고, 끄면 <b>한 줄도</b> 붙지 않아야 한다.
        /// </summary>
        public List<string> VerifyRouteAuthoritativeUi()
        {
            var problems = new List<string>();
            try
            {
                if (ChkRouteAuthoritative == null) { problems.Add("b73: ChkRouteAuthoritative 없음"); return problems; }
                if (ChkRouteAuthoritative.IsChecked != true) problems.Add("b73: 기본값이 ON 이 아님");

                var p = new AiProfile { Engine = "RouteGateProbe", RouteAuthoritative = true };
                p.Actions.Add(new ActionRule { Card = 40235813, Type = "Activate", Priority = 10 });
                var set = new RouteSet { Name = "probe", IsDefault = true };
                var st = new RouteStep { Order = 0 };
                st.Options.Add(new RouteOption { Rank = 1, Card = 40235813, Action = "Activate" });
                set.Steps.Add(st);
                p.Routes.Add(set);

                var onSrc = RuleCodeGen.Generate(p);
                p.RouteAuthoritative = false;
                var offSrc = RuleCodeGen.Generate(p);

                if (onSrc.IndexOf("private bool RoutePending(", System.StringComparison.Ordinal) < 0)
                    problems.Add("b73: RoutePending 헬퍼가 없습니다");
                if (onSrc.IndexOf("!RoutePending(40235813, 5)", System.StringComparison.Ordinal) < 0)
                    problems.Add("b73: ① 액션에 게이트가 없습니다");
                if (onSrc.IndexOf("private int RouteStepFirst(", System.StringComparison.Ordinal) < 0)
                    problems.Add("b73: RouteStepFirst 표가 없습니다");
                // 2026-11-03: B-85(routeGuard) 이후에는 OFF 여도 RoutePending **헬퍼 자체**는 배출된다
                //   (RouteHoldNonRoute 가 그 안에서만 호출됨). 실제 차단은 ① 액션의 `!RoutePending(...)` 참조가 하므로 그것만 본다.
                if (offSrc.IndexOf("!RoutePending(", System.StringComparison.Ordinal) >= 0)
                    problems.Add("b73: OFF 인데 ① 액션 게이트가 생성됨");

                if (problems.Count == 0)
                    System.Console.Error.WriteLine("b73 routeGate ok helper=OK gate=OK off=0");
            }
            catch (System.Exception ex)
            {
                problems.Add("b73: 예외 " + ex.GetType().Name + ": " + ex.Message);
            }
            return problems;
        }
        /// <summary>
        /// 2026-11-02 (B-74): 생성 코드 읽기 전용 창 — ⑤ 탭 버튼 존재 + 창 생성 + 루트 선택 블록 검색.
        /// </summary>
        public List<string> VerifyGeneratedCodeUi()
        {
            var problems = new List<string>();
            try
            {
                if (BtnShowRoutedCode == null) { problems.Add("b74: BtnShowRoutedCode 없음"); return problems; }
                CommitGrids();
                // 불러온 프로필에 루트가 없으면(예: 레이즈문 = 0벌) 검사가 무의미하게 실패한다 →
                // 루트 1벌을 가진 **합성 프로필**로 코드젠을 검사한다.
                var probe = new AiProfile();
                var probeSet = new RouteSet { Name = "uicheck", IsDefault = true };
                var probeStep = new RouteStep { Order = 0 };
                probeStep.Options.Add(new RouteOption { Rank = 1, Card = 100458022, Action = "Summon" });
                probeSet.Steps.Add(probeStep);
                probe.Routes.Add(probeSet);
                var src = RuleCodeGen.Generate(probe);
                if (src.IndexOf("private void SelectRouteFirst", System.StringComparison.Ordinal) < 0)
                    problems.Add("b74: 생성 코드에 SelectRouteFirst 가 없습니다");
                var w = new GeneratedCodeWindow("uicheck", src, "private void SelectRouteFirst");
                if (w.TxtCode == null || w.TxtCode.Text.Length < 100) problems.Add("b74: 뷰어 본문이 비었습니다");
                else if (w.TxtCode.Text.IndexOf("SelectRouteFirst", System.StringComparison.Ordinal) < 0)
                    problems.Add("b74: 뷰어 본문에 루트 선택 블록이 없습니다");
                w.Close();
                if (problems.Count == 0)
                    System.Console.Error.WriteLine("b74 codeViewer ok btn=OK routeBlock=OK body=OK");
            }
            catch (System.Exception ex)
            {
                problems.Add("b74: 예외 " + ex.GetType().Name + ": " + ex.Message);
            }
            return problems;
        }
        /// <summary>
        /// B-76/B-77 (2026-11-02): 선택 정책 `routeNext`(루트 다음 단계 카드) + 필터 `OnlyEnemy` + 조건 `enemyFaceUpMonsterCount`.
        /// 코드젠이 실제로 헬퍼/게이트를 배출하는지 확인한다(창 없이 생성 코드 문자열만 본다).
        /// </summary>
        public List<string> VerifyRouteSearchUi()
        {
            var problems = new List<string>();
            try
            {
                // 1) 루트 + routeNext 규칙
                var pr = new AiProfile { Engine = "UiCheckRouteNext" };
                pr.Actions = new List<ActionRule>();
                pr.Forbids = new List<ForbidRule>();
                pr.Triggers = new List<FinishTrigger>();
                pr.OppTriggers = new List<OppTrigger>();
                pr.Routes = new System.Collections.ObjectModel.ObservableCollection<RouteSet>();
                pr.RoutesSecond = new System.Collections.ObjectModel.ObservableCollection<RouteSet>();
                var rs = new RouteSet { Name = "uicheck", IsDefault = true };
                rs.Steps = new System.Collections.ObjectModel.ObservableCollection<RouteStep>();
                var st1 = new RouteStep();
                st1.Options = new System.Collections.ObjectModel.ObservableCollection<RouteOption>();
                st1.Options.Add(new RouteOption { Rank = 1, Card = 4881365, Action = "Activate" });
                var st2 = new RouteStep();
                st2.Options = new System.Collections.ObjectModel.ObservableCollection<RouteOption>();
                st2.Options.Add(new RouteOption { Rank = 1, Card = 101403017, Action = "Activate" });
                rs.Steps.Add(st1); rs.Steps.Add(st2);
                pr.Routes.Add(rs);
                var ch = new ChoiceRule { Priority = 0, On = "SelectCard", Pick = "routeNext", WhenCard = 40235813 };
                var onlyEnemy = new ChoiceRule { Priority = 1, On = "SelectCard", Pick = "enemyHighestAttack", WhenCard = 34876719, WhenHint = 513, OnlyEnemy = true };
                pr.Choices = new List<ChoiceRule>();
                pr.Choices.Add(ch); pr.Choices.Add(onlyEnemy);
                var src = RuleCodeGen.Generate(pr);
                if (src.IndexOf("PickRouteNext_1(", System.StringComparison.Ordinal) < 0) problems.Add("b76: PickRouteNext_1 없음");
                if (src.IndexOf("private List<int> RouteNeedCards()", System.StringComparison.Ordinal) < 0) problems.Add("b76: RouteNeedCards 없음");
                if (src.IndexOf("if (RouteCardAvailable(Bot, code)) continue;", System.StringComparison.Ordinal) < 0) problems.Add("b76: 보유 카드 skip 없음");
                if (src.IndexOf("FilterEnemyOnly(", System.StringComparison.Ordinal) < 0) problems.Add("b77: FilterEnemyOnly 없음");
                if (src.IndexOf("private static IList<ClientCard> FilterEnemyOnly", System.StringComparison.Ordinal) < 0) problems.Add("b77: FilterEnemyOnly 정의 없음");
                if (src.IndexOf("c.Controller == 0) continue;", System.StringComparison.Ordinal) < 0) problems.Add("b77: 내 카드 제외 없음");

                // 2) 조건 enemyFaceUpMonsterCount
                var pr2 = new AiProfile { Engine = "UiCheckFaceUpMonster" };
                pr2.Actions = new List<ActionRule>();
                pr2.Forbids = new List<ForbidRule>();
                pr2.Triggers = new List<FinishTrigger>();
                pr2.OppTriggers = new List<OppTrigger>();
                pr2.Routes = new System.Collections.ObjectModel.ObservableCollection<RouteSet>();
                pr2.RoutesSecond = new System.Collections.ObjectModel.ObservableCollection<RouteSet>();
                var a2 = new ActionRule { Priority = 1, Type = "Activate", Card = 101403052 };
                a2.Conditions = new List<Models.Condition>();
                a2.Conditions.Add(new Models.Condition { Kind = "enemyFaceUpMonsterCount", Op = ">=", Value = 1, Side = "enemy" });
                pr2.Actions.Add(a2);
                pr2.Choices = new List<ChoiceRule>();
                var src2 = RuleCodeGen.Generate(pr2);
                if (src2.IndexOf("CountFaceUpMonster(Enemy) >= 1", System.StringComparison.Ordinal) < 0) problems.Add("b77: CountFaceUpMonster 조건 없음");
                if (src2.IndexOf("if ((c.Type & 0x4000) != 0) continue;", System.StringComparison.Ordinal) < 0) problems.Add("b77: 토큰 제외 없음");

                // 3) 루트가 없으면 routeNext 는 빈 결과여야 한다(고정 사슬 폴백)
                var pr3 = new AiProfile { Engine = "UiCheckRouteNextNoRoute" };
                pr3.Actions = new List<ActionRule>();
                pr3.Forbids = new List<ForbidRule>();
                pr3.Triggers = new List<FinishTrigger>();
                pr3.OppTriggers = new List<OppTrigger>();
                pr3.Routes = new System.Collections.ObjectModel.ObservableCollection<RouteSet>();
                pr3.RoutesSecond = new System.Collections.ObjectModel.ObservableCollection<RouteSet>();
                pr3.Choices = new List<ChoiceRule>();
                pr3.Choices.Add(new ChoiceRule { Priority = 0, On = "SelectCard", Pick = "routeNext", WhenCard = 40235813 });
                var src3 = RuleCodeGen.Generate(pr3);
                if (src3.IndexOf("PickRouteNext_1(", System.StringComparison.Ordinal) < 0) problems.Add("b76: 루트 없음 스텁 없음");
                if (src3.IndexOf("private List<int> RouteNeedCards()", System.StringComparison.Ordinal) >= 0) problems.Add("b76: 루트 없는데 RouteNeedCards 생성됨");

                if (problems.Count == 0)
                    System.Console.Error.WriteLine("b76 routeNext ok need=OK skipAvailable=OK noRoute=stub | b77 enemyOnly ok filter=OK cond=OK token=OK");
            }
            catch (System.Exception ex)
            {
                problems.Add("b76/b77: 예외 " + ex.GetType().Name + ": " + ex.Message);
            }
            return problems;
        }
        /// <summary>
        /// B-78 (2026-11-02): ⑤ 단계 조건 팝업 — BuildBody 가 순위별 카드·행동·조건(요약+생성식)을 만드는지,
        /// 창이 그 본문을 표시하는지 확인한다(창은 표시하지 않고 생성/닫기만).
        /// </summary>
        public List<string> VerifyRouteStepPopupUi()
        {
            var problems = new List<string>();
            try
            {
                var set = new RouteSet { Name = "uicheck 루트", IsDefault = false };
                set.Conditions = new List<Models.Condition>();
                set.Conditions.Add(new Models.Condition { Kind = "hasInHandOrSet", Card = 101403052, Op = ">=", Value = 1 });
                var step = new RouteStep { Note = "uicheck 메모" };
                step.Options = new System.Collections.ObjectModel.ObservableCollection<RouteOption>();
                var o1 = new RouteOption { Rank = 1, Card = 101403017, Action = "Activate" };
                o1.Conditions = new List<Models.Condition>();
                o1.Conditions.Add(new Models.Condition { Kind = "chainHasCard", Card = 34876719, Op = ">=", Value = 1 });
                o1.Conditions.Add(new Models.Condition { Kind = "enemyFaceUpMonsterCount", Op = ">=", Value = 1, Side = "enemy" });
                var o2 = new RouteOption { Rank = 2, Card = 76630812, Action = "SpellSet" };
                o2.Conditions = new List<Models.Condition>();
                step.Options.Add(o1); step.Options.Add(o2);
                set.Steps = new System.Collections.ObjectModel.ObservableCollection<RouteStep>();
                set.Steps.Add(step);

                string body = RouteStepWindow.BuildBody(set, step, 1);
                if (body.IndexOf("진입 조건:", System.StringComparison.Ordinal) < 0) problems.Add("b78: 진입 조건 없음");
                if (body.IndexOf("단계 1", System.StringComparison.Ordinal) < 0) problems.Add("b78: 단계 번호 없음");
                if (body.IndexOf("101403017", System.StringComparison.Ordinal) < 0) problems.Add("b78: 1순위 카드 없음");
                if (body.IndexOf("76630812", System.StringComparison.Ordinal) < 0) problems.Add("b78: 2순위 카드 없음");
                if (body.IndexOf("1순위", System.StringComparison.Ordinal) < 0) problems.Add("b78: 순위 표기 없음");
                if (body.IndexOf("생성식:", System.StringComparison.Ordinal) < 0) problems.Add("b78: 생성식 없음");
                if (body.IndexOf("조건 없음 · 항상 실행", System.StringComparison.Ordinal) < 0) problems.Add("b78: 조건 없음 표기 실패");
                if (body.IndexOf("상대 필드 앞면 몬스터 수", System.StringComparison.Ordinal) < 0) problems.Add("b78: 조건 라벨 없음");

                // 편집기 창: 편집 대상 콤보(루트 진입 + 2순위) + 조건 그리드 연결 + 생성식 미리보기
                var w = new RouteStepWindow(set, step, 1, "uicheck");
                if (w.CmbCondTarget == null || w.CmbCondTarget.Items.Count != 3) problems.Add("b79: 편집 대상 콤보가 3개가 아닙니다");
                if (w.GridConds == null) problems.Add("b79: 조건 그리드 없음");
                else if (!object.ReferenceEquals(w.GridConds.ItemsSource, o1.Conditions)) problems.Add("b79: 1순위 조건에 연결되지 않았습니다");
                if (w.TxtExpr == null || w.TxtExpr.Text.IndexOf("CountFaceUpMonster(Enemy)", System.StringComparison.Ordinal) < 0)
                    problems.Add("b79: 생성식 미리보기가 1순위 조건을 반영하지 않습니다");
                // 편집 대상 전환 → 루트 진입 조건 + 그 식
                w.CmbCondTarget.SelectedIndex = 0;
                if (!object.ReferenceEquals(w.GridConds.ItemsSource, set.Conditions)) problems.Add("b79: 진입 조건으로 전환되지 않았습니다");
                if (w.TxtExpr.Text.IndexOf("HasInHandOrSet", System.StringComparison.Ordinal) < 0) problems.Add("b79: 진입 조건 미리보기 실패");
                // 실제 편집이 원본 객체에 반영되는지 (그리드에 새 행 추가 = 같은 List 에 추가)
                int before = set.Conditions.Count;
                var added = new Models.Condition { Kind = "enemyMonsterCount", Op = ">=", Value = 2, Side = "enemy" };
                set.Conditions.Add(added);
                w.GridConds.Items.Refresh();
                if (set.Conditions.Count != before + 1) problems.Add("b79: 조건 추가 반영 실패");
                set.Conditions.Remove(added);
                w.Close();

                if (problems.Count == 0)
                    System.Console.Error.WriteLine("b78 stepPopup ok entry=OK ranks=OK conds=OK expr=OK window=OK");
            }
            catch (System.Exception ex)
            {
                problems.Add("b78: 예외 " + ex.GetType().Name + ": " + ex.Message);
            }
            return problems;
        }
        /// <summary>
        /// B-80 (2026-11-02): **조건 생성식 미리보기 바** — ①②③ 조건 그리드를 선택/편집하면 식이 갱신되는지,
        /// 빈 조건이면 안내 문구가 나오는지, 복사 버튼이 있는지 확인한다(원상 복구까지).
        /// </summary>
        public List<string> VerifyCondPreviewUi()
        {
            var problems = new List<string>();
            try
            {
                if (PnlCondPreview == null) problems.Add("b80: 미리보기 패널 없음");
                if (TxtCondPreview == null) { problems.Add("b80: TxtCondPreview 없음"); return problems; }
                if (BtnCopyCondExpr == null) problems.Add("b80: 복사 버튼 없음");

                var g = GridConds;
                var backup = g.ItemsSource;
                var tmp = new List<Models.Condition>();
                tmp.Add(new Models.Condition { Kind = "hasInHand", Card = 101403052, Op = ">=", Value = 1 });
                tmp.Add(new Models.Condition { Kind = "enemyFaceUpMonsterCount", Op = ">=", Value = 1, Side = "enemy", Join = "or", Group = 1 });
                g.ItemsSource = tmp;
                OnCondGridPreview(g, System.EventArgs.Empty);
                if (TxtCondPreview.Text.IndexOf("① 행동 조건", System.StringComparison.Ordinal) < 0) problems.Add("b80: ① 접두 없음");
                if (TxtCondPreview.Text.IndexOf("HasInHand", System.StringComparison.Ordinal) < 0) problems.Add("b80: 조건식이 없습니다");
                if (TxtCondPreview.Text.IndexOf("||", System.StringComparison.Ordinal) < 0) problems.Add("b80: OR 그룹이 반영되지 않았습니다");

                g.ItemsSource = new List<Models.Condition>();
                OnCondGridPreview(g, System.EventArgs.Empty);
                if (TxtCondPreview.Text.IndexOf("조건이 없습니다", System.StringComparison.Ordinal) < 0) problems.Add("b80: 빈 조건 안내 없음");

                g.ItemsSource = backup;
                OnCondGridPreview(g, System.EventArgs.Empty);   // 원상 복구 + 실제 상태로 되돌림

                if (problems.Count == 0)
                    System.Console.Error.WriteLine("b80 condPreview ok panel=OK areas=OK expr=OK or=OK empty=OK copy=OK");
            }
            catch (System.Exception ex)
            {
                problems.Add("b80: 예외 " + ex.GetType().Name + ": " + ex.Message);
            }
            return problems;
        }
        /// <summary>
        /// B-81 (2026-11-02): 루트 게이트 **창(2단계)** — 커서에서 먼 단계는 막지 않아 턴을 버리지 않는지 확인.
        /// 20:41 리플레이(와이반 코스트 불가로 커서 정지 → 턴 전체 차단) 재발 방지 검사.
        /// </summary>
        public List<string> VerifyRouteWindowUi()
        {
            var problems = new List<string>();
            try
            {
                var pr = new AiProfile { Engine = "UiCheckRouteWindow", DebugEffectLog = true };   // 로그도 검사 대상
                pr.Actions = new List<ActionRule>();
                pr.Choices = new List<ChoiceRule>();
                pr.Forbids = new List<ForbidRule>();
                pr.Triggers = new List<FinishTrigger>();
                pr.OppTriggers = new List<OppTrigger>();
                pr.Routes = new System.Collections.ObjectModel.ObservableCollection<RouteSet>();
                pr.RoutesSecond = new System.Collections.ObjectModel.ObservableCollection<RouteSet>();
                var rs = new RouteSet { Name = "uicheck" };
                rs.Steps = new System.Collections.ObjectModel.ObservableCollection<RouteStep>();
                for (int k = 0; k < 6; k++)
                {
                    var st = new RouteStep();
                    st.Options = new System.Collections.ObjectModel.ObservableCollection<RouteOption>();
                    st.Options.Add(new RouteOption { Rank = 1, Card = 101403000 + k, Action = "Activate" });
                    rs.Steps.Add(st);
                }
                pr.Routes.Add(rs);
                var src = RuleCodeGen.Generate(pr);
                if (src.IndexOf("if (needF > _routeStep + 2)", System.StringComparison.Ordinal) < 0)
                    problems.Add("b81: 게이트 창(needF > _routeStep + 2)이 없습니다");
                if (src.IndexOf("if (needF < _routeStep)", System.StringComparison.Ordinal) < 0)
                    problems.Add("b81: 지난 단계 허용(needF < _routeStep)이 없습니다");
                if (src.IndexOf("route hold set=", System.StringComparison.Ordinal) < 0)
                    problems.Add("b81: route hold 로그가 없습니다");
                if (src.IndexOf("route skip set=", System.StringComparison.Ordinal) < 0)
                    problems.Add("b81: route skip 로그가 없습니다");
                if (src.IndexOf("route first sel=", System.StringComparison.Ordinal) < 0)
                    problems.Add("b81: route sel 로그가 없습니다");

                // 게이트 OFF(routeAuthoritative=false)면 ① 액션 차단이 0줄이어야 한다(회귀 방지)
                //   2026-11-03: 헬퍼(RoutePending 안의 창/홀드 코드)는 routeGuard 때문에 남으므로,
                //   실제 차단 지점인 `!RoutePending(` 참조가 없는지로 판정한다.
                pr.RouteAuthoritative = false;
                var src2 = RuleCodeGen.Generate(pr);
                if (src2.IndexOf("!RoutePending(", System.StringComparison.Ordinal) >= 0)
                    problems.Add("b81: 게이트 OFF 인데 차단 코드가 생성되었습니다");

                if (problems.Count == 0)
                    System.Console.Error.WriteLine("b81 routeWindow ok window=OK past=OK logs=OK off=0");
            }
            catch (System.Exception ex)
            {
                problems.Add("b81: 예외 " + ex.GetType().Name + ": " + ex.Message);
            }
            return problems;
        }
        /// <summary>
        /// B-82 (2026-11-02): 루트 대기 **진단 로그**(`Main` = idle 목록 접근 여부) 생성 여부.
        /// `debugEffectLog` ON 일 때만 `RouteProbe` 가 들어가고, OFF 면 0줄이어야 한다.
        /// </summary>
        public List<string> VerifyRouteProbeUi()
        {
            var problems = new List<string>();
            try
            {
                var pr = new AiProfile { Engine = "UiCheckRouteProbe", DebugEffectLog = true };
                pr.Actions = new List<ActionRule>();
                pr.Choices = new List<ChoiceRule>();
                pr.Forbids = new List<ForbidRule>();
                pr.Triggers = new List<FinishTrigger>();
                pr.OppTriggers = new List<OppTrigger>();
                pr.Routes = new System.Collections.ObjectModel.ObservableCollection<RouteSet>();
                pr.RoutesSecond = new System.Collections.ObjectModel.ObservableCollection<RouteSet>();
                var rs = new RouteSet { Name = "uicheck" };
                rs.Steps = new System.Collections.ObjectModel.ObservableCollection<RouteStep>();
                for (int k = 0; k < 4; k++)
                {
                    var st = new RouteStep();
                    st.Options = new System.Collections.ObjectModel.ObservableCollection<RouteOption>();
                    st.Options.Add(new RouteOption { Rank = 1, Card = 101403000 + k, Action = "Activate" });
                    rs.Steps.Add(st);
                }
                pr.Routes.Add(rs);
                var src = RuleCodeGen.Generate(pr);
                if (src.IndexOf("private string RouteProbe(int code)", System.StringComparison.Ordinal) < 0)
                    problems.Add("b82: RouteProbe 헬퍼가 없습니다");
                if (src.IndexOf("MainPhase m = Main;", System.StringComparison.Ordinal) < 0)
                    problems.Add("b82: Main 접근 코드가 없습니다");
                if (src.IndexOf("RouteProbe(card)", System.StringComparison.Ordinal) < 0)
                    problems.Add("b82: hold 로그에 probe 가 없습니다");

                pr.DebugEffectLog = false;
                var src2 = RuleCodeGen.Generate(pr);
                if (src2.IndexOf("private string RouteProbe(int code)", System.StringComparison.Ordinal) >= 0 || src2.IndexOf("RouteProbe(card)", System.StringComparison.Ordinal) >= 0)
                    problems.Add("b82: 로그 OFF 인데 probe 코드가 생성되었습니다");

                if (problems.Count == 0)
                    System.Console.Error.WriteLine("b82 routeProbe ok helper=OK main=OK hold=OK off=0");
            }
            catch (System.Exception ex)
            {
                problems.Add("b82: 예외 " + ex.GetType().Name + ": " + ex.Message);
            }
            return problems;
        }
        /// <summary>
        /// B-83 (2026-11-02): 루트 정확화 3종 —
        ///  (i) `OnNewTurn` 에서 루트 확정  (ii) 타입별 사용가능(`RouteCardUsable`)  (iii) `Main` 기반 스킵(`RouteAdvance`).
        /// </summary>
        public List<string> VerifyRouteExactUi()
        {
            var problems = new List<string>();
            try
            {
                var pr = new AiProfile { Engine = "UiCheckRouteExact", DebugEffectLog = true };
                pr.Actions = new List<ActionRule>();
                pr.Choices = new List<ChoiceRule>();
                pr.Forbids = new List<ForbidRule>();
                pr.Triggers = new List<FinishTrigger>();
                pr.OppTriggers = new List<OppTrigger>();
                pr.Routes = new System.Collections.ObjectModel.ObservableCollection<RouteSet>();
                pr.RoutesSecond = new System.Collections.ObjectModel.ObservableCollection<RouteSet>();
                var rs = new RouteSet { Name = "uicheck" };
                rs.Steps = new System.Collections.ObjectModel.ObservableCollection<RouteStep>();
                var acts = new string[] { "Activate", "Summon", "SpSummon", "SpellSet", "MonsterSet" };
                for (int k = 0; k < 5; k++)
                {
                    var st = new RouteStep();
                    st.Options = new System.Collections.ObjectModel.ObservableCollection<RouteOption>();
                    st.Options.Add(new RouteOption { Rank = 1, Card = 101403000 + k, Action = acts[k] });
                    rs.Steps.Add(st);
                }
                pr.Routes.Add(rs);
                var src = RuleCodeGen.Generate(pr);

                if (src.IndexOf("private int RouteStepTypeFirst(int step)", System.StringComparison.Ordinal) < 0)
                    problems.Add("b83: RouteStepType 표가 없습니다");
                if (src.IndexOf("private bool RouteCardUsable(ClientField me, int code, int type)", System.StringComparison.Ordinal) < 0)
                    problems.Add("b83: RouteCardUsable 이 없습니다");
                if (src.IndexOf("if (type == 0 || type == 3 || type == 4 || type == 6)", System.StringComparison.Ordinal) < 0)
                    problems.Add("b83: 패 전용 판정(소환/세트)이 없습니다");
                if (src.IndexOf("if (type == 5 && !IsActivatedThisTurn(code)) return true;", System.StringComparison.Ordinal) < 0)
                    problems.Add("b83: 묘지 발동 예외 처리가 없습니다");
                if (src.IndexOf("private int RouteAdvance(int set, int cur", System.StringComparison.Ordinal) < 0)
                    problems.Add("b83: RouteAdvance 가 없습니다");
                if (src.IndexOf("private int RouteOffered(int code, int type)", System.StringComparison.Ordinal) < 0)
                    problems.Add("b83: RouteOffered 가 없습니다");
                if (src.IndexOf("if (card != null && player == 0) MarkMyActivated(card);", System.StringComparison.Ordinal) < 0)
                    problems.Add("b83: 내 발동 기록이 없습니다");
                // (i) 턴 시작 루트 확정
                int onNew = src.IndexOf("public override void OnNewTurn()", System.StringComparison.Ordinal);
                if (onNew < 0) problems.Add("b83: OnNewTurn 이 없습니다");
                else
                {
                    int seg = src.IndexOf("base.OnNewTurn();", onNew, System.StringComparison.Ordinal);
                    string body = seg > onNew ? src.Substring(onNew, seg - onNew) : "";
                    if (body.IndexOf("SelectRouteFirst();", System.StringComparison.Ordinal) < 0)
                        problems.Add("b83: OnNewTurn 에서 루트를 확정하지 않습니다");
                    if (body.IndexOf("_usedActivated.Clear();", System.StringComparison.Ordinal) < 0)
                        problems.Add("b83: OnNewTurn 에서 발동 기록을 초기화하지 않습니다");
                }

                if (problems.Count == 0)
                    System.Console.Error.WriteLine("b83 routeExact ok typeTable=OK usable=OK gy=OK advance=OK onNewTurn=OK");
            }
            catch (System.Exception ex)
            {
                problems.Add("b83: 예외 " + ex.GetType().Name + ": " + ex.Message);
            }
            return problems;
        }    }
}
