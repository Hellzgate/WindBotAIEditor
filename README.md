# WindBot AI 에디터

**덱을 지정하고, 그 AI가 할 행동을 유희왕식 규칙으로 편집해 저장한 뒤, 그 AI로 듀얼**하는 도구.

- UI: WPF (.NET 8)
- 플러그인 컴파일: .NET Framework 4.0 `csc.exe` (WindBot이 4.0 기반)
- 생성 코드 문법: **C# 5** (표현식 본문·문자열 보간·`?.` 사용 불가)
- 카드 DB: `Microsoft.Data.Sqlite` — **한국어 cards.cdb 자동 로드**

---

## 1. 동작 원리 (바이너리 분석으로 확인)

| 단계 | 확인된 사실 |
|---|---|
| AI 로딩 | `[Deck]` 어트리뷰트가 붙은 클래스만 등록된다 (`DecksManager.Init`) |
| 플러그인 경로 | `WindBot/Executors/*.dll` — `Assembly.LoadFrom` 후 `[Deck]` 스캔 |
| 덱 선택 | `DecksManager.Instantiate(ai, duel, deck)` — `deck` 키가 없으면 **랜덤 덱** + `"Deck not found, loading random"` |
| 덱 파일 | `DecksManager` 는 **`[Deck]` 의 file 값**(확장자 없이)을 `Deck.Load` 에 넘긴다 → `Decks/<file>.ydk` |
| 설정 전달 | 실행 인자 `key=value` (`Config.Load(args)`) 예: `Deck=raisemoon Host=127.0.0.1 Port=7911` |
| 로비 노출 | `WindBot/bots.json` (`name`, `deck`, `difficulty`, `masterRules`) — EDOPro가 읽음 |
| 카드 스크립트 | `EDOPro/script/{official,local,}/c<id>.lua` |

### "AI가 랜덤 덱을 든다" 원인 (해결됨)

1. 생성 클래스에 `[Deck(...)]` 가 없으면 등록되지 않아 **랜덤 덱**으로 대체된다
2. 어트리뷰트의 file 값에 `.ydk` 를 붙이면 `Decks/xxx.ydk.ydk` 를 찾다가 실패한다
3. WindBot 실행 시 `Deck=` 인자를 주지 않으면 랜덤이다

에디터는 위 3가지를 모두 자동으로 처리한다.

### 리플렉션으로 확정한 API (전부 컴파일 검증됨)

```csharp
// 커스텀 덱 등록 — 없으면 WindBot 이 랜덤 덱을 고른다
[Deck("raisemoon", "AI_raisemoon", "Normal")]   // 이름, ydk파일명(확장자 없이), 레벨
public class raisemoonExecutor : DefaultExecutor

AddExecutor(ExecutorType t, int cardId, Func<bool> cond)
AddExecutor(ExecutorType t, Func<bool> cond)
AddExecutor(ExecutorType t, int cardId)
AddExecutor(ExecutorType t)

IList<ClientCard> OnSelectCard(IList<ClientCard> cards, int min, int max, long hint, bool cancelable)
IList<ClientCard> OnSelectSum(IList<ClientCard> cards, int sum, int min, int max, long hint, bool mode)
IList<ClientCard> OnSelectRitualTribute(IList<ClientCard> cards, int sum, int min, int max)
IList<ClientCard> OnSelectFusionMaterial(IList<ClientCard> cards, int min, int max)
IList<ClientCard> OnSelectSynchroMaterial(IList<ClientCard> cards, int sum, int min, int max)
IList<ClientCard> OnSelectXyzMaterial(IList<ClientCard> cards, int min, int max)
IList<ClientCard> OnSelectLinkMaterial(IList<ClientCard> cards, int min, int max)
IList<ClientCard> OnSelectPendulumSummon(IList<ClientCard> cards, int max)
IList<ClientCard> OnCardSorting(IList<ClientCard> cards)
bool OnSelectYesNo(long desc)
int  OnSelectOption(IList<long> options)
```

조건식 컨텍스트: `Bot`, `Enemy` (ClientField), `Card` (ClientCard), `Duel`, `Util`

---

## 2. 규칙 DSL (schema v2)

### 2.1 행동 규칙 (`actions`)

| 유희왕 표현 | ExecutorType |
|---|---|
| 일반 소환 / 특수 소환 | `Summon` / `SpSummon` |
| 발동 | `Activate` |
| 마법·함정 세트 / 몬스터 세트 | `SpellSet` / `MonsterSet` |
| 표시형식 변경 | `Repos` |
| 소환 또는 세트 | `SummonOrSet` |
| 배틀 페이즈로 / 메인2로 / 턴 종료 | `GoToBattlePhase` / `GoToMainPhase2` / `GoToEndPhase` |

#### 소환 존 지정 (`summonZone`) — 메인 / 엑스트라 몬스터 존

행동 표의 **`소환 존`** 열로 정한다. (링크·펜듈럼·융합/싱크로/엑시즈처럼 엑스트라 덱에서 나오는 소환은 엑스트라 존이 필요)

| 값 | 의미 | 생성 |
|---|---|---|
| `any` (기본) | 자동 (게임 기본 순서) | 오버라이드 없음 |
| `main` | 메인 몬스터 존(0~4) | `want = Zones.MainMonsterZones` |
| `extra` | 엑스트라 몬스터 존(5·6) | `want = Zones.ExtraMonsterZones` |

```csharp
public override int OnSelectPlace(long cardId, int player, CardLocation location, int available)
{
    if (player == 0 && location == CardLocation.MonsterZone)
    {
        int want = 0;
        if (cardId == 19333157) want = Zones.ExtraMonsterZones;   // 이 카드는 엑스트라 존
        if (cardId == 97268402) want = Zones.MainMonsterZones;    // 이 카드는 메인 존
        if (want == 0) want = Zones.ExtraMonsterZones;            // 기본값
        int z = PreferZone(available & want);
        if (z != 0) return z;
    }
    return base.OnSelectPlace(cardId, player, location, available);
}
```

- 존 배치는 `cardId`(= `HINT_SELECTMSG` 값)로 카드를 식별해 규칙을 적용한다. 카드가 특정되지 않으면 **가장 우선순위가 높은 규칙의 존**을 기본값으로 쓴다.
- 요청한 존이 불가능하면(예: 일반 소환인데 엑스트라 존) 아무 것도 강제하지 않고 기본 동작으로 돌아간다.
- 메인 존은 **가운데(z2) → z1 → z3 → z0 → z4**, 엑스트라 존은 **z6 → z5** 순으로 고른다.

### 2.2 조건 (`conditions`) — AND/OR 혼합

각 조건의 `join` 이 **앞 조건과의 연결**을 결정한다 (`and` / `or`). 왼쪽 결합으로 괄호가 생성된다.

```
A and B or C   →   ((A && B) || C)
```

| kind | 의미 | 값 | 카드 | 대상 |
|---|---|---|---|---|
| `enemyMonsterCount` | 상대 몬스터 수 | ✓ | | |
| `enemySpellCount` | 상대 마법/함정 존 수 | ✓ | | |
| `enemyHandCount` | 상대 패 수 | ✓ | | |
| `enemyAttackingCount` | 상대 공격 표시 몬스터 수 | ✓ | | |
| `myMonsterCount` / `myHandCount` | 내 몬스터/패 수 | ✓ | | |
| `myLp` / `enemyLp` | LP | ✓ | | |
| `turn` | 턴 수 | ✓ | | |
| `enemyBestAttack` / `myBestAttack` | 최고 공격력 | ✓ | | |
| `cardAttack` / `cardLevel` | 이 카드의 공격력/레벨 | ✓ | | |
| `hasInHand` / `hasInGraveyard` / `hasOnField` | 특정 카드 보유 | | ✓ | me/enemy |
| `fieldHasLevel` / `fieldHasRank` / `fieldHasLink` | 필드에 해당 레벨/랭크/링크 수치의 몬스터 | ✓ (수치) | | me/enemy |
| `fieldHasRace` | 필드에 특정 종족 몬스터 | | | me/enemy + 종족 |
| `fieldHasTheme` / `handHasTheme` / `gyHasTheme` | 해당 **테마(아키타입)** 카드가 필드/패/묘지에 있음 | | | me/enemy + 테마 |
| `oppMonsterActivated` / `oppSpellActivated` / `oppTrapActivated` | 지금 체인에서 **상대가 마지막으로 발동한 카드**가 몬스터/마법/함정 | | | 상대 고정 |
| `enemyFieldEmpty` / `enemyHasNoMonster` | 상대 필드 상태 | | | |
| `cardIsMonster` / `cardIsSpell` / `cardIsTrap` | 이 카드 종류 | | | |

> `fieldHasRace` 는 `raceId`(CardRace 비트값)를 사용한다. UI 에서는 `종족` 열에서 고른다.
> `fieldHas*` 는 `대상` 열이 `me` 면 AI 자신의 필드, `enemy` 면 상대 필드를 검사한다.
>
> **전체(ALL)**: `종족` = `전체 (종족 무관)` 또는 `raceId: 0` → 아무 종족(몬스터가 있으면 참).
> `레벨/랭크/링크` 는 **값 0** 이 전체를 뜻한다 — 레벨 0 = 아무 일반 몬스터, 랭크 0 = 아무 엑시즈, 링크 0 = 아무 링크 몬스터.

비교: `>= <= == != > <`

#### 이벤트 조건 — 이번 턴에 무슨 일이 있었는지

UI 에서는 **나/상대가 조건 이름에 들어가 있다** (총 18개). `대상(누가)` 칸은 이 조건들에서 자동으로 잠긴다.

| 내(AI) 기준 | 상대 기준 | 의미 |
|---|---|---|
| `evtDraw` | `evtDrawEnemy` | 드로우 |
| `evtSearch` | `evtSearchEnemy` | 서치 (**덱→패**, 드로우 제외) |
| `evtNormalSummon` | `evtNormalSummonEnemy` | 일반 소환 |
| `evtSpecialSummon` | `evtSpecialSummonEnemy` | 특수 소환 |
| `evtBanishFromGy` | `evtBanishFromGyEnemy` | 묘지 → 제외 |
| `evtGyToHand` | `evtGyToHandEnemy` | 묘지 → 패 |
| `evtSpecialFromGy` | `evtSpecialFromGyEnemy` | 묘지 → 특수소환 |
| `evtDeckToGy` | `evtDeckToGyEnemy` | 덱 → 묘지 |
| `evtSpecialFromDeck` | `evtSpecialFromDeckEnemy` | 덱 → 특수소환 |

`비교`/`값` 은 "이번 턴 누적 횟수"에 대한 비교다. 예: `evtDrawEnemy` + `>= 2` → "이번 턴 상대가 2번 이상 드로우했으면".

```json
{ "kind": "evtDrawEnemy", "op": ">=", "value": 2, "side": "enemy" }
```

> 예전 형식(`evtDraw` + `side: "enemy"`)으로 저장된 규칙은 불러올 때 자동으로 `…Enemy` 로 변환된다.

```csharp
AddExecutor(ExecutorType.Activate, 14558128, () => (EventCount(Enemy, 8) >= 2));
```

집계 방식(추정):

- 콜백 시점마다 **존 수 변화**(덱/패/묘지/제외/필드)를 이전 스냅샷과 비교해 누적한다.
- **드로우는 `OnDraw` 로 따로 세고, 서치 집계에서 뺀다** (드로우와 서치를 구분).
- **일반/특수 소환**은 새로 등장한 필드 몬스터의 `ClientCard.IsSpecialSummoned` 플래그로 판별한다.
- 여러 장이 한 번에 이동하면 수량만큼 집계하고, 턴이 바뀌면 0으로 초기화한다.
- 상대의 비공개 영역 이동은 수량 기반이라 "서치"와 "덱→패 효과"를 완전히 구분하지는 못한다.

### 2.3 선택 규칙 (`choices`) — 소재·릴리스·대상 지정

| `on` | 대상 상황 |
|---|---|
| `SelectCard` | 일반 카드 선택 (효과 대상 등) |
| `SelectSum` | 합계 선택 (릴리스/제물) |
| `RitualTribute` | 의식 릴리스 |
| `FusionMaterial` / `SynchroMaterial` / `XyzMaterial` / `LinkMaterial` | 각 소재 선택 |
| `PendulumSummon` | 펜듈럼 소환 |
| `CardSorting` | 덱/패 정렬 |
| `SelectYesNo` / `SelectOption` | 예/아니오, 옵션 |
| `AnnounceCard` | 카드/종족/이름 선언 (특정 ID 우선 가능) |
| `SelectPlace` | 존 선택 (첫 빈 존 또는 지정 인덱스) |
| `SelectPosition` | 표시형식 (공격 우선 / 수비 우선) |

정책(`pick`): `enemyHighestAttack`(공격력↓), `enemyLowestAttack`(공격력↑), `enemyHighestDefense`, `lowestLevel`, `codeMatch`(+`card`), `firstAvailable`

**`whenCard`** — 그 카드를 발동/처리 중일 때만 적용되는 대상 지정 규칙.
`whenCard` 가 0인 규칙은 기본값으로 사용된다.

```json
{ "on": "SelectCard", "pick": "enemyHighestAttack", "whenCard": 14558127 }
```

→ 생성 코드

```csharp
if (Card != null)
{
    if (Card.IsCode(14558127)) return Pick(cards, max, 1, 0);
}
return Pick(cards, max, 0, 0);
```

### 2.4 금지 규칙 (`forbids`) — 특정 카드의 특정 행동 차단

| action | 의미 | 구현 훅 |
|---|---|---|
| `Activate` | 발동 금지 | `OnPreActivate(card) → false` (**하드 veto**, GameAI가 직접 검사) |
| `NoTargetMy*` | 내 카드/필드에 대하여 발동 금지 | `OnPreActivate` veto: 체인 **대상**이 내 카드이거나, 대상이 없는 효과면 **마지막 체인 링크**가 내 필드 카드일 때 |
| `Summon` | 일반 소환 금지(세트 유도) | `OnSelectMonsterSummonOrSet → true` + `AddExecutor(Summon, id, () => false)` |
| `Attack` | 공격 금지 | `OnBattle` 에서 공격자 목록 필터 |
| `Target` | 효과 대상 선택 금지 | `OnSelectCard` 후보에서 제외 |
| `Release` | 릴리스/소재 사용 금지 | `OnSelectSum`·`OnSelectRitualTribute`·각 소재 콜백에서 제외 |
| `ChainLimit` | **체인당 N회 제한** | `OnPreActivate` veto: 이번 체인의 카드와 같은 카드/이름/테마가 N번 이상이면 발동 금지 |

```json
{ "card": 27204313, "action": "Activate", "priority": 10, "note": "니비루 발동 금지" }
```

#### 예외 조건 (`conditions`) — 조건을 만족하면 금지 해제

금지 규칙에도 `conditions` 를 붙일 수 있다. **평소에는 금지**, 조건을 만족하는 순간에만 허용된다.
에디터의 `③ 금지 규칙` 탭에서 금지 행을 선택하면 아래 `예외 조건` 표에 조건을 추가할 수 있다.

```json
{
  "card": 14558128,
  "action": "Activate",
  "turnScope": "any",
  "note": "평소 발동 금지, 상대 몬스터가 2장 이상일 때만 허용",
  "conditions": [
    { "kind": "enemyMonsterCount", "op": ">=", "value": 2, "join": "and" }
  ]
}
```

생성되는 코드는 금지 조건에 `&& !(예외)` 를 추가한다.

```csharp
// 평소: 금지 / 예외 조건 만족: 허용
if (card.IsCode(14558128) && (true) && !(Enemy.GetMonsterCount() >= 2)) return true;
```

- 소환/세트 금지(`SummonSet`)는 `AddExecutor` 가드에 반영된다: `() => (!(턴조건) || (예외))`
- `Activate` 하드 veto, `NoTargetMy*` 필터, `Release`/`Attack` 필터 모두 동일하게 예외를 존중한다.
- 예외 조건이 비어 있으면 기존과 같이 **항상 금지**된다.

#### 체인당 N회 제한 (`ChainLimit`)

같은 카드가 **한 번의 체인에 여러 번** 들어가는 것을 막는다. (예: 하루 우라라 2장 연속 사용 금지)

| 항목 | 의미 |
|---|---|
| `카드ID` | `0` = **전체 카드** (특정 카드만 제한하려면 ID 입력) |
| `금지 행동` | `체인당 N회 제한` |
| `횟수` | 허용 횟수 (기본 1) |
| `턴` / `예외 조건` | 기존 금지 규칙과 동일하게 적용 |
| 판정 기준 | **`⚙ AI 설정` 탭 · 동작 세부**의 **`체인 중복 기준`**: `같은 카드ID` / `같은 카드명` / `같은 테마` |

```csharp
// 전체: 같은 카드는 체인당 1회
if (!card.IsCode(14558128) && true && ChainCount(card) >= 1) { LogForbid("ChainLimit", card); return false; }
// 하루 우라라만 2회 허용 (카드별 규칙이 있으면 전체 규칙은 그 카드를 건너뛴다)
if (card.IsCode(14558128) && true && ChainCount(card) >= 2) { LogForbid("ChainLimit", card); return false; }
```

> `ChainCount` 는 `Duel.CurrentChain` 을 훑어 같은 종류가 몇 장 올라가 있는지 센다.
>
> **상대 발동 종류**: `OppActivated(kind)` 가 체인을 뒤에서부터 훑어 **가장 최근 상대 링크**의 카드 종류를 본다
> (내가 그 위에 체인을 걸었어도 상대 링크를 찾아서 판정). 체인이 없으면 항상 false.
> `같은 테마` 는 setcode 4슬롯 교집합으로 판정.

#### 조건 해석 (`conditionMode`) — 반대로 뒤집기

| 값 | 의미 | 생성 코드 |
|---|---|---|
| `allow` (기본) | 조건을 만족하면 **허용** (평소 금지) | `... && !(조건)` |
| `deny` | 조건을 만족할 때만 **금지** (평소 허용) | `... && (조건)` |

```json
{ "card": 97268402, "action": "SummonSet", "conditionMode": "deny",
  "conditions": [ { "kind": "fieldHasRank", "value": 4, "side": "me" } ] }
```

```csharp
// 내 필드에 랭크 4 몬스터가 있을 때만 소환 금지
AddExecutor(ExecutorType.Summon, 97268402, () => (!(true) || !(FieldHasRank(Bot, 4))));
```

### 2.5 가위바위보 / 선·후공 (`rpsHand`, `turnChoice`)

엔진 헤더의 두 콤보로 지정한다 (적용하려면 `생성 + 컴파일`).

| 필드 | 값 | 생성 |
|---|---|---|
| `rpsHand` | `random`(기본), `1`, `2`, `3` | `public override int OnRockPaperScissors() => 2;` |
| `turnChoice` | `random`(기본), `first`, `second` | `public override bool OnSelectHand() => true;` |

```json
{ "rpsHand": "2", "turnChoice": "first" }
```

> `OnSelectHand()` 의 반환값이 `선공 여부` 다 (`true` → `CTOS_TP_RESULT 1` = 선공).
> `random` 이면 오버라이드 코드를 생성하지 않아 WindBot 기본(무작위) 동작을 쓴다.

### 2.6 우선순위 (`priority`)

행동·선택·금지 규칙 모두 **`priority` 오름차순**으로 처리된다 (낮을수록 먼저).

- 행동 규칙: `AddExecutor` 등록 순서 = 평가 순서
- 선택 규칙: 같은 콜백 안에서 `whenCard` 매칭이 이 순서로 검사됨
- 금지 규칙: 낮은 우선순위의 금지가 먼저 나열됨

---

## 3. 생성 예시 (`samples/generated-example-v2.cs`)


```csharp
[Deck("TestRules2", "AI_TestRules2", "Normal")]
public class TestRules2Executor : DefaultExecutor
{
    public TestRules2Executor(GameAI ai, Duel duel) : base(ai, duel)
    {
        AddExecutor(ExecutorType.Summon, () => (Enemy.GetMonsterCount() >= 2 || Enemy.LifePoints <= 3000));
        AddExecutor(ExecutorType.Activate, 14558127, () => (Enemy.HasInHand(23434538) || Bot.HasInGraveyard(14558127)));
        AddExecutor(ExecutorType.GoToBattlePhase, () => Enemy.GetMonsterCount() == 0);
    }

    public override IList<ClientCard> OnSelectSum(IList<ClientCard> cards, int sum, int min, int max, long hint, bool mode)
    {
        return Pick(cards, max, 4, 0);          // 레벨 낮은 순
    }

    public override IList<ClientCard> OnSelectFusionMaterial(IList<ClientCard> cards, int min, int max)
    {
        return Pick(cards, max, 1, 89631139);   // 공격력↓, 특정 카드 우선
    }
}
```


---

## 4. 봇 대사 (`WindBot/Dialogs/*.json`)

WindBot 은 `Dialogs/<Dialog>.json` 을 읽어 채팅 대사를 고른다.
`Dialog` 는 실행 인자/설정값이고, **EDOPro 가 봇을 실행할 때는 인자를 주지 않으므로 `default.json`** 이 쓰인다.
(직접 실행 시에는 `WindBot.exe Deck=raisemoon Dialog=korean …` 처럼 지정 가능)

`⑥ 봇 대사` 탭에서 지원하는 것:

- `Dialogs/*.json` 목록에서 파일 선택 → 15개 항목(환영/턴 시작/공격/발동/체인/항복 …) 편집
- **한 줄 = 대사 후보 1개** (그중 하나가 랜덤 출력). `{0}`, `{1}` 은 카드 이름으로 치환
- `새 파일` 로 한국어 기본값이 채워진 파일 생성 (예: `korean.json`)
- `default.json 에 적용` — EDOPro 가 실행하는 모든 봇이 쓰는 기본 대사로 복사 (기존 파일은 `.bak` 백업)
- 저장 시 **누락 키를 기본값으로 채우고**, `facedownmonstername` 은 배열이 아닌 **문자열**로 기록
  (WindBot 의 `DataContractJsonSerializer` 계약과 동일 — 검증 완료)

```json
{
  "welcome": ["안녕하세요! AI와 듀얼을 시작합니다."],
  "duelstart": ["듀얼 시작!", "잘 부탁드립니다."],
  "summon": ["{0} 소환!", "나와라, {0}!"],
  "facedownmonstername": "몬스터"
}
```

> 대사는 `WindBot\Dialogs` 폴더에 저장된다. 봇을 재시작하면 반영된다.

#### 엔진(덱)마다 다른 대사 쓰기

엔진 헤더의 **`대사` 콤보**에서 파일을 고르고 `생성 + 컴파일` 하면, 그 엔진 DLL 에 파일 이름이 박힌다.
봇이 시작될 때 `WindBot/Dialogs/<파일>.json` 을 읽어 **WindBot 내장 대사 배열을 교체**하므로,
EDOPro 가 직접 띄운 봇(인자 없음)에서도 엔진별 대사가 적용된다.

| 항목 | 동작 |
|---|---|
| 대사 **내용** 수정 | 봇만 재시작하면 반영 (재컴파일 불필요) |
| 대사 **파일 이름** 변경 | `생성 + 컴파일` 필요 |
| 파일이 없을 때 | 내장(영어) 대사로 자동 폴백 + `[AI-EDITOR] dialog file not found` 로그 |
| `(사용 안 함)` | 대사 교체 코드를 생성하지 않음 (기존과 동일) |

```json
{ "engine": "raisemoon", "dialogFile": "test_kr" }
```

구현: 생성된 실행기 생성자에서 `Dialogs` 인스턴스(`GameAI._dialogs`)의
`_welcome` / `_summon` / `_attack` / `_facedownmonstername` … 필드를 리플렉션으로 교체한다.
필드가 없거나 실패하면 예외를 삼키고 기본 동작으로 돌아간다.

> 검증(.NET Framework x86 하네스):
> `BEFORE welcome='Hi, I'm WindBot Ignite...'` → `AFTER welcome='안녕하세요! AI와 듀얼을 시작합니다.'`

---

## 5. 카드 DB / Lua 탭

- 시작 시 **`EDOPro/repositories/languages` 전체 cdb** 를 스캔해 최대 8개를 동시에 연다
  (한국어 폴더 `한국어*` 우선 → `languages/Korean` → `expansions`)
- 카드명으로 검색(부분 일치), 카드 ID / 이름 / 공격·수비 / 레벨 / 속성 / 종족 / **효과 텍스트**
- 해당 카드의 **Lua 스크립트 원문** + 효과 유형 요약
  (`EFFECT_TYPE_ACTIVATE`, `EFFECT_SUMMON_PROC`, `EFFECT_NEGATE` …)
- 규칙 편집 중 **[카드 선택]** 버튼 → 이름으로 검색해 ID 자동 입력 (카드명 열에 즉시 표시)
- **이름→ID** 버튼: 덱 파일의 카드명 줄을 ID 로 일괄 변환 (원본 카드 우선, 대체 일러스트 제외)

---

## 6. 사용법

```
① "덱" 탭 — 덱만 편집
   - 왼쪽: 덱 목록 + **덱 이름 검색창** (즉시 필터) / 새 덱 / 가져오기(.ydk) / 새로고침 / 삭제
   - 오른쪽: 덱 내용(.ydk) — 카드 추가(이름 검색), 이름→ID 변환, 저장
     · 아래쪽 **덱 내 카드 검색**: `카드명 ×장수 [구역]` 표시, 더블클릭/버튼으로 1장 제거
   - 덱을 선택하면 같은 이름의 엔진 규칙 JSON 이 자동으로 함께 로드된다
①-2 "⚙ AI 설정" 탭 — 엔진 설정 + 저장/컴파일
   - 위: 설정 카드 2열 (엔진명/설명 · 자유 플레이 · 효과번호 기록 · 가위바위보 · 선공/후공 · 대사
     / 체인 중복 기준 · 열 회피 카드) — 창이 낮으면 **[설정 접기]** 로 공간을 넓힌다
   - 아래: [규칙 저장][규칙 불러오기][소스 미리보기][생성 + 컴파일] + **현재 규칙 요약**
     (행동 N · 선택 N · 금지 N · 트리거 N · 선공 루트 N벌 / 후공 N벌) + 빌드 로그
①-3 규칙 탭 ①~⑤ (최상위 탭 — 2026-10-27 B-8 로 중첩 제거)
   - ① 행동·조건 / ② 선택 규칙 / ③ 금지 규칙 / ④ 트리거 / ⑤ 전개 루트
   - ⑤ 는 왼쪽 **루트 목록** · 오른쪽 **단계 카드** · 아래 **진입/순위 조건** 으로 나뉜다
     (창 1100x700 에서도 목록·단계가 보이도록 최소 높이 확보)
② 봇 목록 탭 → 항목 추가(또는 "선택 덱으로 자동 추가") → 저장
③ 봇 대사 탭 → 대사 편집/저장 (EDOPro 가 실행하는 봇은 default.json 사용)
④ 검증/실행 → EDOPro 실행 → 로비에서 AI 선택
```

> **덱 변경이 게임에 반영되지 않을 때**
> WindBot 은 시작할 때 `Decks initialized, N found` 로 덱을 **한 번만** 읽는다.
> 덱/봇 목록을 수정한 뒤에는 **WindBot 과 EDOPro 를 재시작**해야 한다.
> (에디터의 저장/재읽기 자체는 `--decktest` 로 검증됨: write/read/update/list 모두 PASS)

헤드리스:

```powershell
WindBotAIEditor.exe --build samples\sample-profile-v2.json D:\edopro\WindBot
WindBotAIEditor.exe --dbscan D:\edopro\WindBot      # CDB 후보/연결 확인
WindBotAIEditor.exe --decktest D:\edopro\WindBot    # 덱 저장/갱신 왕복 검사
```

---

## 7. 안전장치

- `bots.json` 저장 시 `_backup/bots_<타임스탬프>.json`
- DLL 덮어쓰기 전 `.bak_<타임스탬프>`
- 생성 소스/규칙은 `Executors/_src/`

---

## 8. 남은 확인

| 항목 | 상태 |
|---|---|
| 로비 "덱 엔진" 드롭다운에 커스텀 엔진이 뜨는지 | **실측 필요** (EDOPro 실행 후 확인) |
| EDOPro → WindBot 실행 방식 | 미확인 (프로세스 관찰 필요) |
| `OnSelectChain` / `OnAnnounceCard` / `OnSelectPlace` | 미구현 (필요 시 추가) |
---

## 9. 봇 배포 (다른 사람과 듀얼)

`봇 목록` 탭에서 항목을 선택하고 **`배포용 ZIP 내보내기`** 를 누르면 필요한 것만 ZIP 으로 묶인다.

```
<봇이름>-bot.zip
  WindBot/Executors/<엔진>Executor.dll   ← AI 본체
  WindBot/Decks/AI_<엔진>.ydk            ← 덱 레시피
  WindBot/Dialogs/<대사>.json            ← 엔진에 지정한 대사(있는 경우)
  bots-entry.json                        ← 로비 AI 목록에 넣을 항목
  install.ps1                            ← 자동 설치(파일 복사 + bots.json 병합/백업)
  설치방법.txt
```

받는 사람은 ZIP 을 풀고 `install.ps1` 을 실행한 뒤 EDOPro/WindBot 을 재시작하면
로비 AI 목록에서 그 봇과 듀얼할 수 있다. (EDOPro + WindBot 이 설치되어 있어야 함)

> 실행기 DLL 안에 `[Deck("<엔진>", "AI_<엔진>", ...)]` 이 박혀 있으므로 **덱/엔진 이름은 바꾸지 않는다.**



