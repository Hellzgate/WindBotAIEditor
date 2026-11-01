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

            // ② 그리드에 체크박스(SkipEffectUsed) 컬럼이 있는지
            var colFound = false;
            foreach (var col in GridChoices.Columns)
            {
                var tc = col as DataGridTemplateColumn;
                if (tc?.CellTemplate == null) continue;
                var content = tc.CellTemplate.LoadContent() as DependencyObject;
                var cb = content as System.Windows.Controls.CheckBox ?? FindChild<System.Windows.Controls.CheckBox>(content);
                if (cb == null) continue;
                var b = System.Windows.Data.BindingOperations.GetBinding(cb, System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty);
                if (b?.Path?.Path == "SkipEffectUsed") colFound = true;
            }
            if (!colFound) problems.Add("b53: ② 그리드에 '효과 사용' 체크박스 컬럼이 없습니다");

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
    }
}

