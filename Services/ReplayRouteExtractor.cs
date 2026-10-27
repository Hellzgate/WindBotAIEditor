using System;
using System.Collections.Generic;
using System.Linq;
using WindBotAIEditor.Models;

namespace WindBotAIEditor.Services
{
    /// <summary>
    /// 리플레이에서 뽑아낸 "내 행동 1개" — ⑤ 전개 루트의 단계 카드 1장에 대응한다.
    /// Action 값은 WindBot 의 <c>ExecutorType</c> 이름만 쓴다 (그대로 코드 생성에 들어간다).
    /// </summary>
    public sealed class ReplayAction
    {
        /// <summary>듀얼 턴 번호 (MSG_NEW_TURN 기준 1..N)</summary>
        public int Turn { get; set; }

        /// <summary>"first" = 내 첫 턴(선공 루트) / "second" = 내 두 번째 턴(후공 루트)</summary>
        public string Track { get; set; } = "first";

        /// <summary>카드 ID</summary>
        public int Card { get; set; }

        /// <summary>ExecutorType 이름 (Summon / SpSummon / MonsterSet / SpellSet / Activate)</summary>
        public string Action { get; set; } = "Summon";

        /// <summary>부가 설명 (어디서 했는지 등)</summary>
        public string Note { get; set; } = "";

        public string TrackLabel => Track == "second" ? "후공" : "선공";

        public string CardName => ReplayRouteExtractor.CardNameOf(Card);

        /// <summary>카드명이 없으면 ID 로 표시 (UI/CLI 공용)</summary>
        public string CardLabel => string.IsNullOrEmpty(CardName) ? Card.ToString() : CardName;

        public string ActionLabel => ReplayRouteExtractor.ActionLabel(Action);

        public override string ToString() =>
            Turn + "턴/" + TrackLabel + " · " + CardLabel + " → " + ActionLabel + (Note.Length > 0 ? " (" + Note + ")" : "");
    }

    /// <summary>리플레이 1개에서 뽑은 결과 묶음 (두 트랙 + 통계).</summary>
    public sealed class ReplayExtraction
    {
        public ReplayFile Replay { get; set; } = new ReplayFile();

        /// <summary>"나" 로 고른 쪽 (0/1)</summary>
        public int MySide { get; set; }

        /// <summary>내 턴 총 개수 (3번째 이후 턴은 루트에 넣지 않는다)</summary>
        public int MyTurnCount { get; set; }

        /// <summary>내 첫 턴의 듀얼 턴 번호 (1 = 내가 선공)</summary>
        public int MyFirstTurnNumber { get; set; }

        public List<ReplayAction> Actions { get; set; } = new List<ReplayAction>();

        /// <summary>2번째 이후(=3턴째부터)라서 기록하지 않은 턴 수</summary>
        public int IgnoredTurnCount => Math.Max(0, MyTurnCount - 2);

        /// <summary>내가 이 듀얼의 선공이었는지 (내 첫 턴 = 1턴)</summary>
        public bool IWasFirstPlayer => MyFirstTurnNumber == 1;

        public List<ReplayAction> OfTrack(string track) =>
            Actions.Where(a => string.Equals(a.Track, track, StringComparison.Ordinal)).ToList();

        public int FirstCount => Actions.Count(a => a.Track == "first");
        public int SecondCount => Actions.Count(a => a.Track == "second");

        /// <summary>"선공 3단계 / 후공 2단계" 요약</summary>
        public string Summary => "선공 " + FirstCount + "단계 / 후공 " + SecondCount + "단계";
    }

    /// <summary>
    /// 리플레이 패킷(MSG_*) → 전개 루트 단계.
    ///
    /// 규칙 (B-3 확정):
    /// · 내 쪽(컨트롤러 == mySide)만 기록한다.
    /// · 내 턴(MSG_NEW_TURN 의 턴 플레이어 == mySide)에서 한 행동만 기록한다.
    ///   상대 턴에 내 카드가 발동한 것(패트랩 등)은 전개 루트가 아니므로 버린다.
    /// · 내 1번째 턴 → "first"(선공 루트), 내 2번째 턴 → "second"(후공 루트).
    ///   그 뒤 턴은 루트 하나로 표현할 수 없어 기록하지 않는다(개수만 보고).
    /// · 같은 턴에 같은 카드·같은 행동이 연속으로 오면 1회로 합친다.
    ///
    /// 메시지 본문 레이아웃 (출처: gframe/duelclient.cpp, gframe/core_utils.cpp — 비압축/비호환 모드):
    /// <code>
    /// loc_info        = u8 controller, u8 location, u32 sequence, u32 position   (10B)
    /// MSG_SUMMONING   = u32 code, loc_info
    /// MSG_SPSUMMONING = u32 code, loc_info
    /// MSG_SET         = u32 code, loc_info
    /// MSG_CHAINING    = u32 code, loc_info, u8 cc, u8 cl, u32 cs, u64 desc, u32 ct
    /// MSG_NEW_TURN    = u8 player
    /// MSG_MOVE        = u32 code, loc_info prev, loc_info cur, u32 reason   (참고용, 미사용)
    /// </code>
    /// </summary>
    public static class ReplayRouteExtractor
    {
        // 위치 비트 (ocgapi_constants.h)
        public const int LOCATION_DECK = 0x01;
        public const int LOCATION_HAND = 0x02;
        public const int LOCATION_MZONE = 0x04;
        public const int LOCATION_SZONE = 0x08;
        public const int LOCATION_GRAVE = 0x10;
        public const int LOCATION_REMOVED = 0x20;
        public const int LOCATION_EXTRA = 0x40;
        public const int LOCATION_OVERLAY = 0x80;

        /// <summary>
        /// "내 쪽" 기본값 — 이름에 <c>[AI]</c> 접두사가 없는 쪽. 둘 다 아니면 player 0.
        /// </summary>
        public static int GuessMySide(ReplayFile replay)
        {
            for (var i = 0; i < replay.PlayerNames.Length; i++)
            {
                var n = (replay.PlayerNames[i] ?? "").TrimStart();
                if (!n.StartsWith("[AI]", StringComparison.OrdinalIgnoreCase))
                    return i;
            }
            return 0;
        }

        /// <summary>패킷 스트림에서 내 행동만 뽑아 선공/후공 트랙으로 나눈다.</summary>
        public static ReplayExtraction Extract(ReplayFile replay, int mySide)
        {
            var result = new ReplayExtraction { Replay = replay, MySide = mySide };
            if (replay == null) return result;

            var turn = 0;                 // 듀얼 턴 번호
            var turnPlayer = -1;          // 이번 턴 플레이어
            var myTurn = 0;               // 내 턴 순번 (1 = 내 첫 턴)

            foreach (var p in replay.Packets)
            {
                if (p.Message == ReplayMsg.NEW_TURN)
                {
                    turn++;
                    turnPlayer = p.U8(0);
                    if (turnPlayer == mySide)
                    {
                        myTurn++;
                        if (myTurn == 1) result.MyFirstTurnNumber = turn;
                    }
                    continue;
                }

                if (turn == 0) continue;
                if (turnPlayer != mySide) continue;            // 상대 턴은 전개 루트가 아니다
                if (myTurn <= 0 || myTurn > 2) continue;       // 3번째 내 턴부터는 기록하지 않는다

                var location = p.U8(5);        // loc_info: controller(u8) location(u8) seq(u32) pos(u32)
                string action;
                switch (p.Message)
                {
                    case ReplayMsg.SUMMONING:
                        action = "Summon"; break;
                    case ReplayMsg.SPSUMMONING:
                        action = "SpSummon"; break;
                    case ReplayMsg.CHAINING:
                        action = "Activate"; break;
                    case ReplayMsg.SET:
                        // 세트는 몬스터 존이면 몬스터 세트, 마함 존이면 마법/함정 세트
                        action = (location & LOCATION_MZONE) != 0 ? "MonsterSet" : "SpellSet";
                        break;
                    default:
                        continue;
                }

                var code = (int)p.U32(0);
                var controller = p.U8(4);
                if (code <= 0 || controller != mySide) continue;

                var item = new ReplayAction
                {
                    Turn = turn,
                    Track = myTurn == 1 ? "first" : "second",
                    Card = code,
                    Action = action,
                    Note = TurnNote(turn, location)
                };

                // 연속 중복(같은 턴·같은 카드·같은 행동)은 1회로 합친다
                if (result.Actions.Count > 0)
                {
                    var last = result.Actions[result.Actions.Count - 1];
                    if (last.Turn == item.Turn && last.Card == item.Card && last.Action == item.Action)
                        continue;
                }
                result.Actions.Add(item);
            }

            result.MyTurnCount = myTurn;
            return result;
        }

        /// <summary>내 행동 목록 → ⑤ 전개 루트 단계 (순위 1개, 조건 없음).</summary>
        public static List<RouteStep> ToRouteSteps(IEnumerable<ReplayAction> actions)
        {
            var steps = new List<RouteStep>();
            if (actions == null) return steps;

            foreach (var a in actions)
            {
                var step = new RouteStep { Note = "리플레이 " + a.Turn + "턴" };
                step.Options.Add(new RouteOption
                {
                    Rank = 1,
                    Card = a.Card,
                    Action = a.Action,
                    Note = a.Note
                });
                steps.Add(step);
            }

            for (var i = 0; i < steps.Count; i++) steps[i].Order = i;
            return steps;
        }

        /// <summary>행동 한국어 라벨 (콤보박스 값은 ExecutorType 이름 그대로)</summary>
        public static string ActionLabel(string action)
        {
            switch (action)
            {
                case "Summon": return "일반소환";
                case "SpSummon": return "특수소환";
                case "MonsterSet": return "몬스터 세트";
                case "SpellSet": return "마함 세트";
                case "Activate": return "발동";
                case "Repos": return "표시형식 변경";
                case "SummonOrSet": return "소환/세트";
                case "GoToBattlePhase": return "배틀 페이즈";
                case "GoToMainPhase2": return "메인2";
                case "GoToEndPhase": return "엔드 페이즈";
                default: return action;
            }
        }

        /// <summary>위치 비트 → 한국어 (패/필드/묘지 …)</summary>
        public static string LocationLabel(int location)
        {
            if ((location & LOCATION_HAND) != 0) return "패";
            if ((location & LOCATION_MZONE) != 0) return "필드(몬스터)";
            if ((location & LOCATION_SZONE) != 0) return "필드(마함)";
            if ((location & LOCATION_GRAVE) != 0) return "묘지";
            if ((location & LOCATION_REMOVED) != 0) return "제외";
            if ((location & LOCATION_EXTRA) != 0) return "엑스트라";
            if ((location & LOCATION_DECK) != 0) return "덱";
            if ((location & LOCATION_OVERLAY) != 0) return "오버레이";
            return "";
        }

        private static string TurnNote(int turn, int location)
        {
            var loc = LocationLabel(location);
            return loc.Length == 0 ? turn + "턴" : turn + "턴 · " + loc;
        }

        /// <summary>카드 ID → 카드명 (cdb 가 열려 있지 않으면 빈 문자열)</summary>
        public static string CardNameOf(int id)
        {
            if (id <= 0) return "";
            try
            {
                return CardDatabase.Instance.IsOpen ? (CardDatabase.Instance.NameOf(id) ?? "") : "";
            }
            catch { return ""; }
        }
    }
}
