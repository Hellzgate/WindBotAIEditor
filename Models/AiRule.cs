using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace WindBotAIEditor.Models
{
    /// <summary>코드 + 이름 목록 항목 (종족/테마 검색 상자 공용)</summary>
    public interface ICodeOption
    {
        int Code { get; }
        string Name { get; }
    }

    public abstract class ObservableModel : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        protected void Raise([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            Raise(name);
            return true;
        }
    }

    /// <summary>AI 행동 규칙 한 줄 = AddExecutor(...) 한 번.</summary>
    public class ActionRule : ObservableModel
    {
        private int _card;

        /// <summary>우선순위 (낮을수록 먼저 평가)</summary>
        public int Priority { get; set; } = 100;

        /// <summary>ExecutorType 이름 (Summon, SpSummon, Activate ...)</summary>
        public string Type { get; set; } = "Activate";

        /// <summary>0 이면 카드 무관 (전체 상황 규칙)</summary>
        public int Card
        {
            get => _card;
            set
            {
                if (Set(ref _card, value)) Raise(nameof(CardName));
            }
        }

        public string Note { get; set; } = "";

        public List<Condition> Conditions { get; set; } = new List<Condition>();

        /// <summary>소환 존: any(자동) | main(메인 몬스터 존) | extra(엑스트라 몬스터 존)</summary>
        public string SummonZone { get; set; } = "any";

        /// <summary>소환 시 표시형식: any(자동) | attack(공격) | defense(수비)</summary>
        public string SummonPosition { get; set; } = "any";

        [JsonIgnore]
        public PositionOption SummonPositionName
        {
            get => PositionOption.Find(SummonPosition);
            set { if (value != null) SummonPosition = value.Id; }
        }

        [JsonIgnore]
        public SummonZoneOption SummonZoneName
        {
            get => SummonZoneOption.Find(SummonZone);
            set { if (value != null) SummonZone = value.Id; }
        }

        [JsonIgnore]
        public string Display => Card > 0 ? $"{Type} / {Card}" : Type;

        /// <summary>카드명 표시용 (DB 연동)</summary>
        [JsonIgnore]
        public string CardName => Card > 0 ? Services.CardDatabase.Instance.NameOf(Card) : "(전체)";
    }

    /// <summary>조건 한 줄.</summary>
    public class Condition : ObservableModel
    {
        private int _card;
        private string _kind = "enemyMonsterCount";
        private string _side = "enemy";

        /// <summary>ConditionKind 의 Id</summary>
        public string Kind
        {
            get => _kind;
            set
            {
                if (Set(ref _kind, value))
                {
                    Raise(nameof(SideEditable));
                    Raise(nameof(ThemeEditable));
                    Raise(nameof(KindName));
                }
            }
        }

        /// <summary>대상(누가)을 직접 고를 수 있는지 (이벤트 조건은 kind 에 포함)</summary>
        [JsonIgnore]
        public bool SideEditable => string.IsNullOrEmpty(ConditionKind.Find(_kind).FixedSide);

        /// <summary>테마 선택이 의미 있는 조건인지</summary>
        [JsonIgnore]
        public bool ThemeEditable => ConditionKind.Find(_kind).NeedsTheme;

        public string Op { get; set; } = ">=";
        public int Value { get; set; }

        public int Card
        {
            get => _card;
            set
            {
                if (Set(ref _card, value)) Raise(nameof(CardName));
            }
        }

        /// <summary>"me" 또는 "enemy"</summary>
        public string Side
        {
            get => _side;
            set => Set(ref _side, value);
        }

        /// <summary>앞 조건과의 연결: "and" | "or" (첫 조건은 무시)</summary>
        public string Join { get; set; } = "and";

        /// <summary>B-14: 조건 부정 — 식 전체를 !(...) 로 감싼다</summary>
        public bool Not { get; set; }

        /// <summary>B-14: 테마 조건에서 몬스터만 (Data.Type &amp; 0x1)</summary>
        public bool MonsterOnly { get; set; }

        /// <summary>종족 조건(fieldHasRace)에서 사용할 종족 비트값 (0 = 전체/종족 무관)</summary>
        public int RaceId { get; set; } = 0;

        /// <summary>테마 조건에서 사용할 setcode (0 = 전체/테마 무관)</summary>
        public int ThemeCode { get; set; }

        /// <summary>DataGrid ComboBox 바인딩용 (ThemeCode 와 동기화)</summary>
        [JsonIgnore]
        public ThemeOption ThemeName
        {
            get => Services.ThemeCatalog.Instance.Find(ThemeCode);
            set { if (value != null) ThemeCode = value.Code; }
        }

        /// <summary>DataGrid ComboBox 바인딩용 (RaceId 와 동기화)</summary>
        [JsonIgnore]
        public RaceOption RaceName
        {
            get => RaceOption.Find(RaceId);
            set { if (value != null) RaceId = value.Id; }
        }

        /// <summary>DataGrid ComboBox 바인딩용 (Kind 와 동기화)</summary>
        [JsonIgnore]
        public ConditionKind KindName
        {
            get => ConditionKind.Find(Kind);
            set
            {
                if (value == null) return;
                Kind = value.Id;
                if (!string.IsNullOrEmpty(value.FixedSide)) Side = value.FixedSide;
            }
        }

        [JsonIgnore]
        public string CardName => Card > 0 ? Services.CardDatabase.Instance.NameOf(Card) : "";
    }

    /// <summary>게임이 선택을 요구할 때의 정책.</summary>
    public class ChoiceRule : ObservableModel
    {
        private int _whenCard;
        private string _pick = "enemyHighestAttack";
        private string _on = "SelectCard";

        /// <summary>우선순위 (낮을수록 먼저 매칭)</summary>
        public int Priority { get; set; } = 100;

        /// <summary>콜백 이름 (SelectCard, SelectSum, RitualTribute, FusionMaterial ...)</summary>
        public string On
        {
            get => _on;
            set
            {
                if (Set(ref _on, value)) Raise(nameof(IsSelectOption));
            }
        }

        /// <summary>PickPolicy Id</summary>
        public string Pick
        {
            get => _pick;
            set
            {
                if (Set(ref _pick, value))
                {
                    Raise(nameof(IsThemeMatch));
                    Raise(nameof(ThemePickText));
                }
            }
        }

        /// <summary>B-14: 정책이 "테마 카드 우선" 인지 (테마 칸 표시 여부)</summary>
        [JsonIgnore]
        public bool IsThemeMatch => _pick == "themeMatch";

        /// <summary>B-14: 선택 콜백이 SelectOption 인지 (옵션 desc 칸 표시 여부)</summary>
        [JsonIgnore]
        public bool IsSelectOption => On == "SelectOption";

        /// <summary>B-14: 테마 카드 우선 정책의 테마 setcode (0 = 전체)</summary>
        public int ThemeCode { get; set; }

        /// <summary>B-14: 테마 카드 우선 정책에서 몬스터만 고르기</summary>
        public bool ThemeMonsterOnly { get; set; }

        /// <summary>B-14: 테마 카드 우선 정책에서 제외할 카드 ID 목록</summary>
        public List<int> ExcludeCards { get; set; } = new List<int>();

        private int _excludeAdd;

        /// <summary>B-14: 제외 카드 입력 상자 (카드를 고르면 ExcludeCards 에 추가)</summary>
        [JsonIgnore]
        public int ExcludeCardAdd
        {
            get => _excludeAdd;
            set
            {
                if (!Set(ref _excludeAdd, value)) return;
                if (value <= 0) return;
                ExcludeCards ??= new List<int>();
                if (!ExcludeCards.Contains(value)) ExcludeCards.Add(value);
                Raise(nameof(ExcludeCardText));
            }
        }

        /// <summary>B-14: 제외 카드 목록 표시</summary>
        [JsonIgnore]
        public string ExcludeCardText
        {
            get
            {
                if (ExcludeCards == null || ExcludeCards.Count == 0) return "(없음)";
                var sb = new System.Text.StringBuilder();
                foreach (var id in ExcludeCards)
                {
                    if (sb.Length > 0) sb.Append(", ");
                    var name = Services.CardDatabase.Instance.NameOf(id);
                    sb.Append(name.Length > 0 ? name : id.ToString());
                }
                return sb.ToString();
            }
        }

        /// <summary>B-14: 제외 카드 목록 비우기 (UI 버튼)</summary>
        public void ClearExcludeCards()
        {
            ExcludeCards?.Clear();
            _excludeAdd = 0;
            Raise(nameof(ExcludeCardAdd));
            Raise(nameof(ExcludeCardText));
        }

        /// <summary>B-14: SelectOption 옵션 desc(문자열 ID). 0 = 무관</summary>
        public long OptionDesc { get; set; }

        [JsonIgnore]
        public string ThemePickText => IsThemeMatch
            ? "테마 " + ThemeCode + (ThemeMonsterOnly ? " · 몬스터만" : "") + " · 제외 " + (ExcludeCards?.Count ?? 0)
            : "테마 무관";

        /// <summary>codeMatch 정책에서 사용</summary>
        public int Card { get; set; }

        /// <summary>이 선택 규칙을 적용할 카드(발동한 카드). 0 = 기본 규칙</summary>
        public int WhenCard
        {
            get => _whenCard;
            set
            {
                if (Set(ref _whenCard, value)) Raise(nameof(WhenCardName));
            }
        }

        /// <summary>최대 선택 수(max)가 이 값일 때만 적용 (0 = 무관)</summary>
        public int WhenMax { get; set; }

        /// <summary>
        /// 적용 조건 (B-6). 비어 있으면 무조건 적용(기존 동작과 동일).
        /// 기존 적용 조건(On/WhenCard/WhenMax)과 **AND** 로 결합하며,
        /// 불충족이면 이 규칙만 건너뛰고 다음 우선순위 규칙을 평가한다.
        /// </summary>
        public List<Condition> Conditions { get; set; } = new List<Condition>();

        [JsonIgnore]
        public string ConditionBadge => Conditions == null || Conditions.Count == 0
            ? "조건 없음"
            : "조건 " + Conditions.Count;

        /// <summary>조건 목록이 바뀌면 배지를 갱신</summary>
        public void NotifyConditions() => Raise(nameof(ConditionBadge));

        /// <summary>SelectYesNo: true/false</summary>
        public bool YesNo { get; set; } = true;

        public int OptionIndex { get; set; }

        /// <summary>DataGrid ComboBox 바인딩용 (Pick 과 동기화)</summary>
        [JsonIgnore]
        public PickPolicy PickName
        {
            get => PickPolicy.All.Find(p => p.Id == Pick) ?? PickPolicy.All[0];
            set { if (value != null) Pick = value.Id; }
        }

        /// <summary>표시형식/존 선택용: attack | defence | first</summary>
        public string Position { get; set; } = "attack";

        /// <summary>존 선택 인덱스 (SelectPlace 에서 first 가 아닐 때)</summary>
        public int PlaceIndex { get; set; } = 0;

        [JsonIgnore]
        public string WhenCardName => WhenCard > 0 ? Services.CardDatabase.Instance.NameOf(WhenCard) : "(기본)";

        /// <summary>DataGrid ComboBox 바인딩용 (On 과 동기화)</summary>
        [JsonIgnore]
        public ChoiceKind OnName
        {
            get => ChoiceKind.All.Find(k => k.Id == On) ?? ChoiceKind.All[0];
            set { if (value != null) On = value.Id; }
        }
    }

    /// <summary>특정 카드가 있으면 "빌드 완성"으로 보고 다음 단계(배틀/턴 종료)로 넘어가는 트리거.</summary>
    public class FinishTrigger : ObservableModel
    {
        private int _card;

        public int Card
        {
            get => _card;
            set { if (Set(ref _card, value)) Raise(nameof(CardName)); }
        }

        /// <summary>myField | hand | graveyard | enemyField</summary>
        public string Zone { get; set; } = "myField";

        /// <summary>완료 시 동작: battle | end | none</summary>
        public string WhenDone { get; set; } = "battle";

        /// <summary>완료 후 다른 행동 규칙을 중단할지</summary>
        public bool StopOthers { get; set; } = true;

        public int Priority { get; set; } = 10;

        /// <summary>트리거 묶음 번호 (같은 번호끼리 AND/OR 결합, 다른 번호는 OR)</summary>
        public int Group { get; set; } = 1;

        /// <summary>같은 그룹 안에서 앞 트리거와의 연결: and | or</summary>
        public string Join { get; set; } = "and";

        public string Note { get; set; } = "";

        [JsonIgnore]
        public string CardName => Card > 0 ? Services.CardDatabase.Instance.NameOf(Card) : "";

        [JsonIgnore]
        public FinishZone ZoneName
        {
            get => FinishZone.Find(Zone);
            set { if (value != null) Zone = value.Id; }
        }

        [JsonIgnore]
        public FinishAction DoneName
        {
            get => FinishAction.Find(WhenDone);
            set { if (value != null) WhenDone = value.Id; }
        }
    }

    /// <summary>상대가 특정 카드를 발동/사용했을 때 내 카드로 대응하는 트리거.</summary>
    public class OppTrigger : ObservableModel
    {
        private int _card;
        private int _responseCard;

        /// <summary>상대가 사용한 카드 ID</summary>
        public int Card
        {
            get => _card;
            set { if (Set(ref _card, value)) Raise(nameof(CardName)); }
        }

        /// <summary>내가 대응해서 발동할 카드 ID (0이면 플래그만 설정)</summary>
        public int ResponseCard
        {
            get => _responseCard;
            set { if (Set(ref _responseCard, value)) Raise(nameof(ResponseCardName)); }
        }

        public int Priority { get; set; } = 100;

        public string Note { get; set; } = "";

        [JsonIgnore]
        public string CardName => Card > 0 ? Services.CardDatabase.Instance.NameOf(Card) : "";

        [JsonIgnore]
        public string ResponseCardName => ResponseCard > 0 ? Services.CardDatabase.Instance.NameOf(ResponseCard) : "(플래그만)";
    }

    public class FinishZone
    {
        public string Id { get; set; } = "";
        public string Label { get; set; } = "";
        public static readonly List<FinishZone> All = new List<FinishZone>
        {
            new FinishZone { Id = "myField",    Label = "내 필드" },
            new FinishZone { Id = "hand",       Label = "내 패" },
            new FinishZone { Id = "graveyard",  Label = "내 묘지" },
            new FinishZone { Id = "enemyField", Label = "상대 필드" },
        };
        public static FinishZone Find(string id) => All.Find(z => z.Id == id) ?? All[0];
        public override string ToString() => Label;
    }

    public class FinishAction
    {
        public string Id { get; set; } = "";
        public string Label { get; set; } = "";
        public static readonly List<FinishAction> All = new List<FinishAction>
        {
            new FinishAction { Id = "battle", Label = "배틀 페이즈로" },
            new FinishAction { Id = "end",    Label = "턴 종료" },
            new FinishAction { Id = "none",   Label = "아무것도 안 함" },
        };
        public static FinishAction Find(string id) => All.Find(a => a.Id == id) ?? All[0];
        public override string ToString() => Label;
    }

    /// <summary>
    /// 전개 루트의 한 단계. 대안(순위) 여러 개를 가질 수 있고,
    /// 낮은 순위부터 조건을 검사해 처음 만족하는 대안 하나만 실행한다.
    /// </summary>
    public class RouteStep : ObservableModel
    {
        private int _order;
        private int _card;

        /// <summary>실행 순서 (0부터)</summary>
        public int Order
        {
            get => _order;
            set { if (Set(ref _order, value)) Raise(nameof(Title)); }
        }

        public string Note { get; set; } = "";

        /// <summary>이 단계의 대안(순위). 앞에서부터(순위 낮은 것부터) 조건을 검사한다.</summary>
        public ObservableCollection<RouteOption> Options { get; set; } = new ObservableCollection<RouteOption>();

        /// <summary>하위 호환용: 예전 JSON 의 단일 카드. 읽을 때 Options[0] 으로 옮긴다.</summary>
        public int Card
        {
            get => _card;
            set { if (Set(ref _card, value)) Raise(nameof(CardName)); }
        }

        /// <summary>하위 호환용: 예전 JSON 의 단일 행동. 읽을 때 Options[0] 으로 옮긴다.</summary>
        public string Action { get; set; } = "Activate";

        [JsonIgnore]
        public string Title => "단계 " + (_order + 1);

        [JsonIgnore]
        public string CardName => Card > 0 ? Services.CardDatabase.Instance.NameOf(Card) : "";

        [JsonIgnore]
        public string OptionSummary => Options == null || Options.Count == 0 ? "순위 없음" : Options.Count + "순위";
    }

    /// <summary>전개 루트 한 단계의 대안(순위). 조건이 비어 있으면 "무조건 만족".</summary>
    public class RouteOption : ObservableModel
    {
        private int _rank = 1;
        private int _card;
        private bool _editing;

        /// <summary>1순위, 2순위 … (낮을수록 먼저 검사)</summary>
        public int Rank
        {
            get => _rank;
            set { if (Set(ref _rank, value)) Raise(nameof(RankLabel)); }
        }

        public int Card
        {
            get => _card;
            set { if (Set(ref _card, value)) Raise(nameof(CardName)); }
        }

        /// <summary>ExecutorType 이름</summary>
        public string Action { get; set; } = "Summon";

        /// <summary>이 순위를 실행할 조건. 비어 있으면 무조건 만족.</summary>
        public List<Condition> Conditions { get; set; } = new List<Condition>();

        public string Note { get; set; } = "";

        [JsonIgnore]
        public string CardName => Card > 0 ? Services.CardDatabase.Instance.NameOf(Card) : "";

        [JsonIgnore]
        public string RankLabel => Rank >= 1 && Rank <= 20 ? ((char)(0x2460 + Rank - 1)).ToString() : Rank + "순위";

        /// <summary>지금 조건을 편집 중인 순위인지 (UI 표시용)</summary>
        [JsonIgnore]
        public bool IsEditing
        {
            get => _editing;
            set { if (Set(ref _editing, value)) Raise(nameof(ConditionBadge)); }
        }

        [JsonIgnore]
        public string ConditionBadge
        {
            get
            {
                var n = Conditions?.Count ?? 0;
                var text = n == 0 ? "조건 없음" : "조건 " + n;
                return _editing ? "● " + text : text;
            }
        }

        /// <summary>조건 목록이 바뀌면 배지/개수 표시를 갱신</summary>
        public void NotifyConditions()
        {
            Raise(nameof(ConditionBadge));
        }
    }

    /// <summary>
    /// 전개 루트 한 벌 (B-5). 트랙(선공/후공)마다 여러 벌을 둘 수 있다.
    /// 실행 순서: <see cref="IsDefault"/> 가 아닌 루트를 목록 순서대로 조건 검사 → 처음 만족한 1벌만 실행.
    /// 아무것도 만족하지 못하면 기본 루트, 기본 루트도 없으면 그 턴에는 전개하지 않는다.
    /// </summary>
    public class RouteSet : ObservableModel
    {
        private string _name = "";
        private bool _isDefault;

        /// <summary>루트 이름 (예: "A콤보")</summary>
        public string Name
        {
            get => _name;
            set { if (Set(ref _name, value)) Raise(nameof(DisplayName)); }
        }

        /// <summary>이 트랙의 기본 루트 (트랙당 1개만 true — 라디오)</summary>
        public bool IsDefault
        {
            get => _isDefault;
            set { if (Set(ref _isDefault, value)) Raise(nameof(DefaultBadge)); }
        }

        /// <summary>진입 조건 — 비어 있으면 "항상 만족"</summary>
        public List<Condition> Conditions { get; set; } = new List<Condition>();

        /// <summary>단계 목록 (B-1/B-2 카드형 스텝)</summary>
        public ObservableCollection<RouteStep> Steps { get; set; } = new ObservableCollection<RouteStep>();

        [JsonIgnore]
        public string DefaultBadge => _isDefault ? "★ 기본" : "";

        [JsonIgnore]
        public string DisplayName => string.IsNullOrWhiteSpace(_name) ? "(이름 없음)" : _name;

        [JsonIgnore]
        public string ConditionBadge => Conditions == null || Conditions.Count == 0
            ? "조건 없음"
            : "조건 " + Conditions.Count;

        [JsonIgnore]
        public string StepBadge => (Steps == null ? 0 : Steps.Count) + "단계";

        /// <summary>조건 목록이 바뀌면 배지를 갱신</summary>
        public void NotifyConditions() => Raise(nameof(ConditionBadge));

        /// <summary>단계 목록이 바뀌면 배지를 갱신</summary>
        public void NotifySteps() => Raise(nameof(StepBadge));
    }

    /// <summary>한 엔진(덱)의 전체 AI 프로필 — 파일로 저장/복원.</summary>
    public class AiProfile
    {
        public string Engine { get; set; } = "MyDeck";
        public string Description { get; set; } = "";
        public int SchemaVersion { get; set; } = 11;
        public List<ActionRule> Actions { get; set; } = new List<ActionRule>();
        public List<ChoiceRule> Choices { get; set; } = new List<ChoiceRule>();
        public List<ForbidRule> Forbids { get; set; } = new List<ForbidRule>();
        public List<FinishTrigger> Triggers { get; set; } = new List<FinishTrigger>();
        public List<OppTrigger> OppTriggers { get; set; } = new List<OppTrigger>();

        /// <summary>전개 루트 — 선공 N벌 (B-5)</summary>
        public ObservableCollection<RouteSet> Routes { get; set; } = new ObservableCollection<RouteSet>();

        /// <summary>전개 루트 — 후공 N벌. 비어 있으면 선공 트랙을 그대로 사용한다.</summary>
        public ObservableCollection<RouteSet> RoutesSecond { get; set; } = new ObservableCollection<RouteSet>();

        /// <summary>
        /// 하위 호환 전용 — 예전 단일 루트(선공). 읽을 때 <see cref="Routes"/> 1벌로 이관하고 비운다.
        /// 새 스키마로 저장할 때는 쓰지 않는다(빈 값이면 JSON 에도 나오지 않는다).
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<RouteStep>? Route { get; set; }

        /// <summary>하위 호환 전용 — 예전 단일 루트(후공). <see cref="RoutesSecond"/> 로 이관 후 비운다.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<RouteStep>? RouteSecond { get; set; }

        /// <summary>규칙이 없는 행동 유형은 기본(자유) 플레이 규칙을 자동 추가</summary>
        public bool FreePlay { get; set; } = true;

        /// <summary>발동 시 카드/효과번호(desc)를 로그로 기록 (효과번호 확인용)</summary>
        public bool DebugEffectLog { get; set; }

        /// <summary>가위바위보 선택: random | 1 | 2 | 3</summary>
        public string RpsHand { get; set; } = "random";

        /// <summary>가위바위보 승리 시 진영 선택: random | first | second</summary>
        public string TurnChoice { get; set; } = "random";

        /// <summary>이 엔진이 사용할 대사 파일 (Dialogs/&lt;이름&gt;.json). 빈 값이면 기본 대사.</summary>
        public string DialogFile { get; set; } = "";

        /// <summary>체인 중복 판정 기준: code | name | theme</summary>
        public string ChainBasis { get; set; } = "code";

        /// <summary>이 카드가 놓인 세로열을 피해 마법/함정을 배치한다 (0 = 사용 안 함)</summary>
        public int AvoidColumnCard { get; set; }

        /// <summary>
        /// B-12: cdb 카드 이름이 같으면 같은 카드로 취급한다 (대체 일러 / 재록 = 다른 ID).
        /// 예) 금지 59438930 → 생성 코드가 59438931 도 함께 판정. 구형 JSON 에 없으면 true 로 읽힌다.
        /// </summary>
        public bool SameNameAliases { get; set; } = true;

        /// <summary>
        /// B-15: 자유 플레이에 마법/함정 세트(<c>ExecutorType.SpellSet</c>)도 추가한다. 기본 OFF.
        /// 함정·속공은 패에서 발동할 수 없어 세트만 가능하므로, 이 옵션이 없으면 패에서 썩는다.
        /// </summary>
        public bool FreePlaySetSpell { get; set; } = false;

        /// <summary>B-15: 세트할 카드 종류 — all(마법+함정) | trap(함정만) | quick(속공 마법만)</summary>
        public string FreePlaySetSpellKind { get; set; } = "all";

        /// <summary>B-15: 세트하지 않을 카드 ID 목록 (패트랩 등). sameNameAliases 규칙으로 OR 확장된다.</summary>
        public List<int> FreePlaySetSpellExclude { get; set; } = new List<int>();

        /// <summary>
        /// B-16: 릴리스/제물 후보에서 엑스트라 덱 몬스터(융합·싱크로·엑시즈·링크)를 뺀다. 기본 <b>ON</b>.
        /// 키가 없는 구형 JSON 은 이니셜라이저 기본값(ON)으로 읽히고, <see cref="RuleMigration.NormalizeFreePlayProtectExtra"/>
        /// 가 스키마 버전을 11 로 올려 한 번만 보정한다. (소재 사용은 그대로 허용 — 릴리스만 막는다)
        /// </summary>
        public bool FreePlayProtectExtra { get; set; } = true;
    }

    /// <summary>특정 카드의 특정 행동을 금지하는 규칙.</summary>
    public class ForbidRule : ObservableModel
    {
        private int _card;

        public int Card
        {
            get => _card;
            set
            {
                if (Set(ref _card, value)) Raise(nameof(CardName));
            }
        }

        /// <summary>금지할 행동 Id (ForbidAction)</summary>
        public string Action { get; set; } = "Activate";

        public string Note { get; set; } = "";

        /// <summary>우선순위 (낮을수록 먼저 적용)</summary>
        public int Priority { get; set; } = 100;

        /// <summary>특정 효과 번호 (ActivateDescription). -1 = 카드의 모든 효과</summary>
        public long EffectDesc { get; set; } = -1;

        /// <summary>체인당 허용 횟수 (ChainLimit 규칙에서 사용)</summary>
        public int ChainLimit { get; set; } = 1;

        /// <summary>예외 조건: 이 조건을 만족하면 금지를 해제한다</summary>
        public List<Condition> Conditions { get; set; } = new List<Condition>();

        /// <summary>조건 해석: allow(조건 충족 시 허용) | deny(조건 충족 시에만 금지)</summary>
        public string ConditionMode { get; set; } = "allow";

        [JsonIgnore]
        public ConditionModeOption ConditionModeName
        {
            get => ConditionModeOption.Find(ConditionMode);
            set { if (value != null) ConditionMode = value.Id; }
        }

        [JsonIgnore]
        public string ExceptionText => Conditions.Count == 0 ? "" : Conditions.Count + "개 조건";

        /// <summary>턴 제한: any | my | opponent</summary>
        public string TurnScope { get; set; } = "any";

        [JsonIgnore]
        public string CardName => Card > 0 ? Services.CardDatabase.Instance.NameOf(Card) : "";

        [JsonIgnore]
        public ForbidAction ActionName
        {
            get => ForbidAction.Find(Action);
            set { if (value != null) Action = value.Id; }
        }

        [JsonIgnore]
        public TurnScopeOption TurnScopeName
        {
            get => TurnScopeOption.Find(TurnScope);
            set { if (value != null) TurnScope = value.Id; }
        }
    }

    public class TurnScopeOption
    {
        public string Id { get; set; } = "";
        public string Label { get; set; } = "";
        public static readonly List<TurnScopeOption> All = new List<TurnScopeOption>
        {
            new TurnScopeOption { Id = "any",      Label = "턴 무관" },
            new TurnScopeOption { Id = "my",       Label = "내 턴에만" },
            new TurnScopeOption { Id = "opponent", Label = "상대 턴에만" },
        };
        public static TurnScopeOption Find(string id) => All.Find(t => t.Id == id) ?? All[0];
        public override string ToString() => Label;
    }

    /// <summary>금지 조건 해석 방식</summary>
    public class ConditionModeOption
    {
        public string Id { get; set; } = "";
        public string Label { get; set; } = "";
        public static readonly List<ConditionModeOption> All = new List<ConditionModeOption>
        {
            new ConditionModeOption { Id = "allow", Label = "조건 충족 시 허용(예외)" },
            new ConditionModeOption { Id = "deny",  Label = "조건 충족 시에만 금지" },
        };
        public static ConditionModeOption Find(string id) => All.Find(t => t.Id == id) ?? All[0];
        public override string ToString() => Label;
    }

    /// <summary>체인 중복 판정 기준</summary>
    public class ChainBasisOption
    {
        public string Id { get; set; } = "";
        public string Label { get; set; } = "";
        public static readonly List<ChainBasisOption> All = new List<ChainBasisOption>
        {
            new ChainBasisOption { Id = "code",  Label = "같은 카드ID" },
            new ChainBasisOption { Id = "name",  Label = "같은 카드명" },
            new ChainBasisOption { Id = "theme", Label = "같은 테마" },
        };
        public static ChainBasisOption Find(string id) => All.Find(t => t.Id == id) ?? All[0];
        public override string ToString() => Label;
    }

    /// <summary>B-15: 자유 플레이에서 세트(SpellSet)할 마법/함정 종류</summary>
    public class FreePlaySetKindOption
    {
        public string Id { get; set; } = "";
        public string Label { get; set; } = "";

        public static readonly List<FreePlaySetKindOption> All = new List<FreePlaySetKindOption>
        {
            new FreePlaySetKindOption { Id = "all",   Label = "전체 (마법 + 함정)" },
            new FreePlaySetKindOption { Id = "trap",  Label = "함정만" },
            new FreePlaySetKindOption { Id = "quick", Label = "속공 마법만" },
        };

        public static FreePlaySetKindOption Find(string id) => All.Find(t => t.Id == id) ?? All[0];
        public override string ToString() => Label;
    }

    /// <summary>소환 시 표시형식</summary>
    public class PositionOption
    {
        public string Id { get; set; } = "";
        public string Label { get; set; } = "";
        public static readonly List<PositionOption> All = new List<PositionOption>
        {
            new PositionOption { Id = "any",     Label = "자동 (기본)" },
            new PositionOption { Id = "attack",  Label = "공격 표시" },
            new PositionOption { Id = "defense", Label = "수비 표시" },
        };
        public static PositionOption Find(string id) => All.Find(t => t.Id == id) ?? All[0];
        public override string ToString() => Label;
    }

    /// <summary>소환 존 선택</summary>
    public class SummonZoneOption
    {
        public string Id { get; set; } = "";
        public string Label { get; set; } = "";
        public static readonly List<SummonZoneOption> All = new List<SummonZoneOption>
        {
            new SummonZoneOption { Id = "any",   Label = "자동 (기본)" },
            new SummonZoneOption { Id = "main",  Label = "메인 몬스터 존" },
            new SummonZoneOption { Id = "extra", Label = "엑스트라 몬스터 존" },
        };
        public static SummonZoneOption Find(string id) => All.Find(t => t.Id == id) ?? All[0];
        public override string ToString() => Label;
    }

    /// <summary>몬스터 종족 (CardRace 비트값)</summary>
    public class RaceOption : ICodeOption
    {
        public int Id { get; set; }
        public string Label { get; set; } = "";

        /// <summary>ICodeOption (검색 상자용)</summary>
        public int Code { get => Id; set => Id = value; }
        public string Name { get => Label; set => Label = value; }
        public static readonly List<RaceOption> All = new List<RaceOption>
        {
            new RaceOption { Id = 0,        Label = "전체 (종족 무관)" },
            new RaceOption { Id = 0x1,       Label = "전사" },
            new RaceOption { Id = 0x2,       Label = "마법사" },
            new RaceOption { Id = 0x4,       Label = "천사" },
            new RaceOption { Id = 0x8,       Label = "악마" },
            new RaceOption { Id = 0x10,      Label = "좀비" },
            new RaceOption { Id = 0x20,      Label = "기계" },
            new RaceOption { Id = 0x40,      Label = "물" },
            new RaceOption { Id = 0x80,      Label = "화염" },
            new RaceOption { Id = 0x100,     Label = "암석" },
            new RaceOption { Id = 0x200,     Label = "비행야수" },
            new RaceOption { Id = 0x400,     Label = "식물" },
            new RaceOption { Id = 0x800,     Label = "곤충" },
            new RaceOption { Id = 0x1000,    Label = "번개" },
            new RaceOption { Id = 0x2000,    Label = "드래곤" },
            new RaceOption { Id = 0x4000,    Label = "야수" },
            new RaceOption { Id = 0x8000,    Label = "야수전사" },
            new RaceOption { Id = 0x10000,   Label = "공룡" },
            new RaceOption { Id = 0x20000,   Label = "어류" },
            new RaceOption { Id = 0x40000,   Label = "해룡" },
            new RaceOption { Id = 0x80000,   Label = "파충류" },
            new RaceOption { Id = 0x100000,  Label = "사이킥" },
            new RaceOption { Id = 0x200000,  Label = "창생신" },
            new RaceOption { Id = 0x400000,  Label = "불사" },
            new RaceOption { Id = 0x800000,  Label = "환룡" },
            new RaceOption { Id = 0x1000000, Label = "사이버스" },
        };
        public static RaceOption Find(int id) => All.Find(t => t.Id == id) ?? All[0];
        public override string ToString() => Label;
    }

    /// <summary>가위바위보 / 선후공 선택 옵션</summary>
    public class ChoiceOption
    {
        public string Id { get; set; } = "";
        public string Label { get; set; } = "";
        public override string ToString() => Label;
    }

    public class ForbidAction
    {
        public string Id { get; set; } = "";
        public string Label { get; set; } = "";

        /// <summary>③ 탭 콤보 툴팁 (항목 설명)</summary>
        public string Hint { get; set; } = "";

        public static readonly List<ForbidAction> All = new List<ForbidAction>
        {
            new ForbidAction { Id = "Activate",       Label = "발동 금지",                 Hint = "그 카드를 발동하지 않는다" },
            new ForbidAction { Id = "ActivateOnField",Label = "내 필드에서 발동 금지",      Hint = "필드에 있는 그 카드를 발동하지 않는다" },
            new ForbidAction { Id = "ActivateHand",  Label = "내 패에서 발동 금지",         Hint = "패에 있는 그 카드를 발동하지 않는다" },
            new ForbidAction { Id = "ActivateGrave", Label = "내 묘지에서 발동 금지",       Hint = "묘지에 있는 그 카드를 발동하지 않는다" },
            // B-13: 체인 방향 제한 — 상대 카드에 체인하는 것은 허용
            new ForbidAction { Id = "ActivateChainToMine", Label = "내 카드에 체인하여 발동 금지", Hint = "내 카드에 체인해서 발동하지 않는다 (상대 카드에는 허용)" },
            new ForbidAction { Id = "NoTargetMyField",   Label = "내(AI) 필드 카드에 대하여 발동 불가", Hint = "내 필드 카드를 대상으로 하는 발동을 하지 않는다" },
            new ForbidAction { Id = "NoTargetMyCard",    Label = "내(AI) 카드에 대하여 발동 금지",      Hint = "내 카드를 대상으로 하는 발동을 하지 않는다" },
            new ForbidAction { Id = "NoTargetEnemyCard", Label = "상대(플레이어) 카드에 대하여 발동 금지", Hint = "상대 카드를 대상으로 하는 발동을 하지 않는다" },
            new ForbidAction { Id = "BlockEffect",     Label = "특정 효과 발동 금지 (효과번호/턴)", Hint = "지정 효과번호의 발동을 막는다 (-1 = 모든 효과)" },
            new ForbidAction { Id = "Summon",         Label = "일반 소환 금지 (세트 유도)",   Hint = "일반 소환 대신 세트한다" },
            new ForbidAction { Id = "SummonSet",      Label = "일반 소환 금지 (세트도 금지)", Hint = "일반 소환과 세트를 모두 하지 않는다" },
            new ForbidAction { Id = "Attack",         Label = "공격 금지",                 Hint = "공격 선언을 하지 않는다" },
            new ForbidAction { Id = "Target",         Label = "효과 대상 선택 금지",         Hint = "그 카드의 효과 대상을 선택하지 않는다" },
            new ForbidAction { Id = "Release",        Label = "릴리스/소재 사용 금지",       Hint = "그 카드를 릴리스/소재로 쓰지 않는다" },
            new ForbidAction { Id = "ChainLimit",     Label = "체인당 N회 제한",            Hint = "한 체인에 N회까지만 발동한다" },
        };
        public static ForbidAction Find(string id) => All.Find(a => a.Id == id) ?? All[0];
        public override string ToString() => Label;
    }

    /// <summary>조건 종류 정의 (UI 라벨 + 코드 생성 정보)</summary>
    public class ConditionKind
    {
        public string Id { get; set; } = "";
        public string Label { get; set; } = "";
        public bool NeedsValue { get; set; } = true;
        public bool NeedsCard { get; set; }
        public bool NeedsSide { get; set; }

        /// <summary>이 조건이 대상(누가)을 고정하면 "me"/"enemy" (아니면 "")</summary>
        public string FixedSide { get; set; } = "";

        /// <summary>테마(아키타입) 선택이 필요한 조건</summary>
        public bool NeedsTheme { get; set; }

        public static readonly List<ConditionKind> All = new List<ConditionKind>
        {
            new ConditionKind { Id = "enemyMonsterCount", Label = "상대 필드 몬스터 수", NeedsValue = true },
            new ConditionKind { Id = "enemySpellCount",   Label = "상대 마법/함정 존 수", NeedsValue = true },
            new ConditionKind { Id = "enemyHandCount",    Label = "상대 패 수", NeedsValue = true },
            new ConditionKind { Id = "enemyAttackingCount",Label = "상대 공격 표시 몬스터 수", NeedsValue = true },
            new ConditionKind { Id = "enemyFaceUpCount",  Label = "상대 필드 앞면 카드 수", NeedsValue = true, NeedsSide = false },
            new ConditionKind { Id = "enemyFaceDownCount",Label = "상대 필드 뒷면 카드 수", NeedsValue = true, NeedsSide = false },
            new ConditionKind { Id = "myMonsterCount",    Label = "내 필드 몬스터 수", NeedsValue = true },
            new ConditionKind { Id = "myHandCount",       Label = "내 패 수", NeedsValue = true },
            new ConditionKind { Id = "myLp",              Label = "내 LP", NeedsValue = true },
            new ConditionKind { Id = "enemyLp",           Label = "상대 LP", NeedsValue = true },
            new ConditionKind { Id = "turn",              Label = "현재 턴 수", NeedsValue = true },
            new ConditionKind { Id = "enemyBestAttack",   Label = "상대 최고 공격력", NeedsValue = true },
            new ConditionKind { Id = "myBestAttack",      Label = "내 최고 공격력", NeedsValue = true },
            new ConditionKind { Id = "cardAttack",        Label = "이 카드의 공격력", NeedsValue = true },
            new ConditionKind { Id = "cardLevel",         Label = "이 카드의 레벨", NeedsValue = true },
            new ConditionKind { Id = "hasInHand",         Label = "패에 특정 카드", NeedsValue = false, NeedsCard = true, NeedsSide = true },
            new ConditionKind { Id = "hasInGraveyard",    Label = "묘지에 특정 카드", NeedsValue = false, NeedsCard = true, NeedsSide = true },
            new ConditionKind { Id = "hasOnField",        Label = "필드에 특정 카드", NeedsValue = false, NeedsCard = true, NeedsSide = true },
            new ConditionKind { Id = "fieldHasLevel", Label = "필드에 레벨 N 몬스터 있음", NeedsValue = true, NeedsSide = true },
            new ConditionKind { Id = "fieldHasRank",  Label = "필드에 랭크 N 몬스터 있음", NeedsValue = true, NeedsSide = true },
            new ConditionKind { Id = "fieldHasLink",  Label = "필드에 링크 N 몬스터 있음", NeedsValue = true, NeedsSide = true },
            new ConditionKind { Id = "fieldHasRace",  Label = "필드에 특정 종족 몬스터 있음", NeedsValue = false, NeedsSide = true },
            new ConditionKind { Id = "fieldHasTunerLevel", Label = "필드에 N레벨 튜너 몬스터 있음", NeedsValue = true, NeedsSide = true },
            new ConditionKind { Id = "amFirst",  Label = "내가 선공일 때", NeedsValue = false },
            new ConditionKind { Id = "amSecond", Label = "내가 후공일 때", NeedsValue = false },
            new ConditionKind { Id = "fieldHasTheme", Label = "필드에 테마 몬스터 있음", NeedsValue = false, NeedsSide = true, NeedsTheme = true },
            new ConditionKind { Id = "handHasTheme",  Label = "패에 테마 카드 있음", NeedsValue = false, NeedsSide = true, NeedsTheme = true },
            new ConditionKind { Id = "gyHasTheme",    Label = "묘지에 테마 카드 있음", NeedsValue = false, NeedsSide = true, NeedsTheme = true },
            new ConditionKind { Id = "evtDraw",             Label = "내(AI) 이번 턴 드로우 횟수", NeedsValue = true, NeedsSide = true, FixedSide = "me" },
            new ConditionKind { Id = "evtDrawEnemy",        Label = "상대 이번 턴 드로우 횟수", NeedsValue = true, NeedsSide = true, FixedSide = "enemy" },
            new ConditionKind { Id = "evtSearch",           Label = "내(AI) 이번 턴 서치(덱→패) 횟수", NeedsValue = true, NeedsSide = true, FixedSide = "me" },
            new ConditionKind { Id = "evtSearchEnemy",      Label = "상대 이번 턴 서치(덱→패) 횟수", NeedsValue = true, NeedsSide = true, FixedSide = "enemy" },
            new ConditionKind { Id = "evtNormalSummon",     Label = "내(AI) 이번 턴 일반 소환 횟수", NeedsValue = true, NeedsSide = true, FixedSide = "me" },
            new ConditionKind { Id = "evtNormalSummonEnemy",Label = "상대 이번 턴 일반 소환 횟수", NeedsValue = true, NeedsSide = true, FixedSide = "enemy" },
            new ConditionKind { Id = "evtSpecialSummon",    Label = "내(AI) 이번 턴 특수 소환 횟수", NeedsValue = true, NeedsSide = true, FixedSide = "me" },
            new ConditionKind { Id = "evtSpecialSummonEnemy",Label = "상대 이번 턴 특수 소환 횟수", NeedsValue = true, NeedsSide = true, FixedSide = "enemy" },
            new ConditionKind { Id = "evtBanishFromGy",     Label = "내(AI) 이번 턴 묘지→제외 횟수", NeedsValue = true, NeedsSide = true, FixedSide = "me" },
            new ConditionKind { Id = "evtBanishFromGyEnemy",Label = "상대 이번 턴 묘지→제외 횟수", NeedsValue = true, NeedsSide = true, FixedSide = "enemy" },
            new ConditionKind { Id = "evtGyToHand",         Label = "내(AI) 이번 턴 묘지→패 횟수", NeedsValue = true, NeedsSide = true, FixedSide = "me" },
            new ConditionKind { Id = "evtGyToHandEnemy",    Label = "상대 이번 턴 묘지→패 횟수", NeedsValue = true, NeedsSide = true, FixedSide = "enemy" },
            new ConditionKind { Id = "evtSpecialFromGy",    Label = "내(AI) 이번 턴 묘지→특수소환 횟수", NeedsValue = true, NeedsSide = true, FixedSide = "me" },
            new ConditionKind { Id = "evtSpecialFromGyEnemy",Label = "상대 이번 턴 묘지→특수소환 횟수", NeedsValue = true, NeedsSide = true, FixedSide = "enemy" },
            new ConditionKind { Id = "evtDeckToGy",         Label = "내(AI) 이번 턴 덱→묘지 횟수", NeedsValue = true, NeedsSide = true, FixedSide = "me" },
            new ConditionKind { Id = "evtDeckToGyEnemy",    Label = "상대 이번 턴 덱→묘지 횟수", NeedsValue = true, NeedsSide = true, FixedSide = "enemy" },
            new ConditionKind { Id = "evtSpecialFromDeck",  Label = "내(AI) 이번 턴 덱→특수소환 횟수", NeedsValue = true, NeedsSide = true, FixedSide = "me" },
            new ConditionKind { Id = "evtSpecialFromDeckEnemy",Label = "상대 이번 턴 덱→특수소환 횟수", NeedsValue = true, NeedsSide = true, FixedSide = "enemy" },
            new ConditionKind { Id = "myCardActivated",  Label = "내 카드가 발동 중일 때", NeedsValue = false, NeedsSide = true, FixedSide = "me" },
            new ConditionKind { Id = "myHandActivated",  Label = "내 패에서 카드가 발동 중일 때", NeedsValue = false, NeedsSide = true, FixedSide = "me" },
            new ConditionKind { Id = "oppMonsterActivated", Label = "상대 몬스터 효과가 발동했을 때", NeedsValue = false, NeedsSide = true, FixedSide = "enemy" },
            new ConditionKind { Id = "oppSpellActivated",   Label = "상대 마법 효과가 발동했을 때", NeedsValue = false, NeedsSide = true, FixedSide = "enemy" },
            new ConditionKind { Id = "oppTrapActivated",    Label = "상대 함정 효과가 발동했을 때", NeedsValue = false, NeedsSide = true, FixedSide = "enemy" },
            new ConditionKind { Id = "enemyFieldEmpty",   Label = "상대 필드가 비어 있음", NeedsValue = false },
            new ConditionKind { Id = "enemyHasNoMonster", Label = "상대 몬스터 없음(직공 가능)", NeedsValue = false },
            new ConditionKind { Id = "cardIsMonster",     Label = "이 카드는 몬스터", NeedsValue = false },
            new ConditionKind { Id = "cardIsSpell",       Label = "이 카드는 마법", NeedsValue = false },
            new ConditionKind { Id = "cardIsTrap",        Label = "이 카드는 함정", NeedsValue = false },
        };

        public static ConditionKind Find(string id) =>
            All.Find(k => k.Id == id) ?? All[0];

        public override string ToString() => Label;
    }

    /// <summary>선택 정책 정의</summary>
    public class PickPolicy
    {
        public string Id { get; set; } = "";
        public string Label { get; set; } = "";
        public static readonly List<PickPolicy> All = new List<PickPolicy>
        {
            new PickPolicy { Id = "enemyHighestAttack", Label = "공격력 높은 순" },
            new PickPolicy { Id = "enemyLowestAttack",  Label = "공격력 낮은 순" },
            new PickPolicy { Id = "enemyHighestDefense",Label = "수비력 높은 순" },
            new PickPolicy { Id = "lowestLevel",        Label = "레벨 낮은 순" },
            new PickPolicy { Id = "codeMatch",          Label = "특정 카드 우선" },
            new PickPolicy { Id = "themeMatch",         Label = "테마 카드 우선" },
            new PickPolicy { Id = "chainCard",          Label = "발동 중인 카드" },
            new PickPolicy { Id = "firstAvailable",     Label = "목록 순서대로" },
        };
        public override string ToString() => Label;
    }

    /// <summary>선택 콜백 종류</summary>
    public class ChoiceKind
    {
        public string Id { get; set; } = "";
        public string Label { get; set; } = "";
        public static readonly List<ChoiceKind> All = new List<ChoiceKind>
        {
            new ChoiceKind { Id = "SelectCard",        Label = "카드 선택 (일반)" },
            new ChoiceKind { Id = "SelectSum",         Label = "합계 선택 (릴리스/제물)" },
            new ChoiceKind { Id = "RitualTribute",     Label = "의식 릴리스" },
            new ChoiceKind { Id = "FusionMaterial",    Label = "융합 소재" },
            new ChoiceKind { Id = "SynchroMaterial",   Label = "싱크로 소재" },
            new ChoiceKind { Id = "XyzMaterial",       Label = "엑시즈 소재" },
            new ChoiceKind { Id = "LinkMaterial",      Label = "링크 소재" },
            new ChoiceKind { Id = "PendulumSummon",    Label = "펜듈럼 소환" },
            new ChoiceKind { Id = "CardSorting",       Label = "덱/패 정렬" },
            new ChoiceKind { Id = "SelectYesNo",       Label = "예/아니오" },
            new ChoiceKind { Id = "SelectOption",      Label = "옵션 선택" },
            new ChoiceKind { Id = "AnnounceCard",      Label = "카드 선언(종족/이름)" },
            new ChoiceKind { Id = "SelectPlace",       Label = "존(위치) 선택" },
            new ChoiceKind { Id = "SelectPosition",    Label = "표시형식 선택" },
        };
        public override string ToString() => Label;
    }

    /// <summary>예전 프로필(이벤트 조건 + 대상 열)을 새 형식(kind 에 나/상대 포함)으로 변환.</summary>
    public static class RuleMigration
    {
        private static readonly string[] EventKinds =
        {
            "evtSearch", "evtDraw", "evtNormalSummon", "evtSpecialSummon", "evtBanishFromGy",
            "evtGyToHand", "evtSpecialFromGy", "evtDeckToGy", "evtSpecialFromDeck"
        };

        public static void Normalize(AiProfile? p)
        {
            if (p == null) return;
            foreach (var a in p.Actions ?? new List<ActionRule>()) Fix(a.Conditions);
            foreach (var f in p.Forbids ?? new List<ForbidRule>()) Fix(f.Conditions);
            foreach (var c in p.Choices ?? new List<ChoiceRule>()) { if (c == null) continue; c.Conditions ??= new List<Condition>(); c.ExcludeCards ??= new List<int>(); Fix(c.Conditions); }
            NormalizeFreePlaySetSpell(p);
            NormalizeFreePlayProtectExtra(p);
            NormalizeRoutes(p);
        }

        /// <summary>
        /// B-15: 자유 플레이 마함 세트 옵션 정규화.
        /// 구형 JSON 은 키가 없으므로 <c>false / "all" / 빈 목록</c> 으로 읽히고, 여기서 null·미지원 값만 바로잡는다.
        /// </summary>
        public static void NormalizeFreePlaySetSpell(AiProfile? p)
        {
            if (p == null) return;
            p.FreePlaySetSpellExclude ??= new List<int>();
            var cleaned = new List<int>();
            foreach (var id in p.FreePlaySetSpellExclude)
                if (id > 0 && !cleaned.Contains(id)) cleaned.Add(id);
            p.FreePlaySetSpellExclude = cleaned;
            p.FreePlaySetSpellKind = FreePlaySetKindOption.Find(p.FreePlaySetSpellKind).Id;   // null/미지원 → all
        }

        /// <summary>
        /// B-16: 자유 플레이 엑스트라 덱 릴리스 보호 옵션 마이그레이션.
        /// 스키마 10 이하(키가 없던 파일)는 기본값 <b>ON</b> 으로 맞추고 버전을 올린다.
        /// 한 번 올라간 뒤에는 사용자가 끈 값을 그대로 존중한다(스키마 11 파일은 손대지 않는다).
        /// </summary>
        public static void NormalizeFreePlayProtectExtra(AiProfile? p)
        {
            if (p == null) return;
            if (p.SchemaVersion < 11)
            {
                p.FreePlayProtectExtra = true;
                p.SchemaVersion = 11;
            }
        }

        /// <summary>
        /// 전개 루트 정규화 (B-5).
        /// ① 구형 단일 루트(<c>route</c>/<c>routeSecond</c>) → <c>routes</c>/<c>routesSecond</c> 1벌로 이관
        ///    (Name="기본", IsDefault=true, Conditions=[]).
        /// ② 각 벌의 이름/기본 표시(트랙당 1개)/단계·순위를 정리한다.
        /// </summary>
        public static void NormalizeRoutes(AiProfile? p)
        {
            if (p == null) return;

            p.Routes ??= new ObservableCollection<RouteSet>();
            p.RoutesSecond ??= new ObservableCollection<RouteSet>();

            if (p.Routes.Count == 0 && p.Route != null && p.Route.Count > 0)
                p.Routes.Add(MigratedSet(p.Route));
            if (p.RoutesSecond.Count == 0 && p.RouteSecond != null && p.RouteSecond.Count > 0)
                p.RoutesSecond.Add(MigratedSet(p.RouteSecond));

            // 이제 저장은 새 스키마만 쓴다 (구형 키는 비운다)
            p.Route = null;
            p.RouteSecond = null;

            NormalizeRouteSets(p.Routes);
            NormalizeRouteSets(p.RoutesSecond);
        }

        private static RouteSet MigratedSet(List<RouteStep> steps)
        {
            var set = new RouteSet { Name = "기본", IsDefault = true };
            foreach (var s in steps) if (s != null) set.Steps.Add(s);
            return set;
        }

        /// <summary>루트 목록 정리 — null 제거, 기본 루트는 트랙당 1개, 각 루트의 단계 정규화.</summary>
        public static void NormalizeRouteSets(IList<RouteSet>? sets)
        {
            if (sets == null) return;
            for (var i = sets.Count - 1; i >= 0; i--)
                if (sets[i] == null) sets.RemoveAt(i);

            var hasDefault = false;
            foreach (var s in sets)
            {
                s.Name ??= "";
                s.Conditions ??= new List<Condition>();
                s.Steps ??= new ObservableCollection<RouteStep>();
                NormalizeRoute(s.Steps);

                if (!s.IsDefault) continue;
                if (hasDefault) s.IsDefault = false;   // 라디오: 트랙당 1개
                else hasDefault = true;
            }
        }

        /// <summary>
        /// 전개 루트 정규화.
        /// 예전 형식(Card/Action 한 개)은 <c>Options = [ {Rank=1, Card, Action} ]</c> 로 옮긴다.
        /// 단계 번호(Order)와 순위 번호(Rank)는 1..N 으로 다시 매긴다.
        /// </summary>
        public static void NormalizeRoute(IList<RouteStep>? steps)
        {
            if (steps == null) return;
            int order = 0;
            foreach (var st in steps)
            {
                if (st == null) continue;

                st.Note ??= "";
                st.Options ??= new ObservableCollection<RouteOption>();
                foreach (var o in st.Options)
                {
                    if (o == null) continue;
                    o.Note ??= "";
                    o.Conditions ??= new List<Condition>();
                    if (string.IsNullOrWhiteSpace(o.Action)) o.Action = "Summon";
                }

                if (st.Options.Count == 0)
                {
                    // 예전 JSON: 단계에 카드/행동이 하나만 있었다 → 순위 1 대안으로 이관
                    st.Options.Add(new RouteOption
                    {
                        Rank = 1,
                        Card = st.Card,
                        Action = string.IsNullOrWhiteSpace(st.Action) ? "Summon" : st.Action
                    });
                }

                RenumberRanks(st.Options);
                st.Order = order++;

                // 저장 호환: 예전 리더도 읽을 수 있게 1순위 값을 Card/Action 에도 남긴다
                st.Card = st.Options[0].Card;
                st.Action = st.Options[0].Action;
            }
        }

        /// <summary>순위 번호를 목록 순서대로 1..N 으로 다시 매긴다 (개수 제한 없음).</summary>
        public static void RenumberRanks(IList<RouteOption>? options)
        {
            if (options == null) return;
            for (int i = 0; i < options.Count; i++)
                if (options[i] != null) options[i].Rank = i + 1;
        }

        private static void Fix(List<Condition>? conds)
        {
            if (conds == null) return;
            foreach (var c in conds)
            {
                if (c == null) continue;
                var k = c.Kind ?? "";
                bool isEvent = false;
                foreach (var e in EventKinds)
                    if (k == e || k == e + "Enemy") { isEvent = true; break; }
                if (!isEvent) continue;

                bool enemy = k.EndsWith("Enemy", StringComparison.Ordinal);
                if (!enemy && string.Equals(c.Side, "enemy", StringComparison.OrdinalIgnoreCase))
                {
                    c.Kind = k + "Enemy";
                    enemy = true;
                }
                c.Side = enemy ? "enemy" : "me";
            }
        }
    }
}






















