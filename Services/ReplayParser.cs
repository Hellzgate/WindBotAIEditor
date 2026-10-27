using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using SharpCompress.Compressors.LZMA;

namespace WindBotAIEditor.Services
{
    /// <summary>
    /// 리플레이 파일을 읽을 수 없을 때 던지는 예외.
    /// Message 는 사용자에게 그대로 보여줄 수 있는 한국어 문장이다.
    /// </summary>
    public class ReplayFormatException : Exception
    {
        public ReplayFormatException(string message) : base(message) { }
    }

    /// <summary>리플레이 스트림의 패킷 하나 = 코어 메시지 1개 (MSG_*).</summary>
    public sealed class ReplayPacket
    {
        /// <summary>MSG_* 값 (ReplayMsg 참고)</summary>
        public byte Message { get; set; }

        /// <summary>메시지 본문 (길이는 스트림의 u32 len)</summary>
        public byte[] Data { get; set; } = Array.Empty<byte>();

        public int Length => Data.Length;

        /// <summary>u32 하나 (본문이 짧으면 0)</summary>
        public uint U32(int offset) =>
            offset + 4 <= Data.Length
                ? (uint)(Data[offset] | (Data[offset + 1] << 8) | (Data[offset + 2] << 16) | (Data[offset + 3] << 24))
                : 0u;

        /// <summary>u8 하나 (본문이 짧으면 -1)</summary>
        public int U8(int offset) => offset < Data.Length ? Data[offset] : -1;
    }

    /// <summary>
    /// .yrpX (스트리밍 리플레이) 파싱 결과.
    /// 구조: 헤더 → 이름 → 파라미터 → 패킷 스트림.
    /// </summary>
    public sealed class ReplayFile
    {
        /// <summary>파일 경로 (오류 메시지/표시용)</summary>
        public string FilePath { get; set; } = "";

        /// <summary>리플레이 식별자 (0x58707279 = 'yrpX')</summary>
        public uint Id { get; set; }

        public uint Version { get; set; }

        /// <summary>REPLAY_* 플래그 조합</summary>
        public uint Flags { get; set; }

        /// <summary>플레이어 이름 (side 0 …, side 1 …)</summary>
        public string[] PlayerNames { get; set; } = Array.Empty<string>();

        /// <summary>duel_flags (u64 또는 u32 — 플래그에 따라)</summary>
        public ulong DuelFlags { get; set; }

        /// <summary>확장 헤더(72B)인지 여부</summary>
        public bool ExtendedHeader { get; set; }

        /// <summary>헤더 + 이름 + 파라미터 크기 (스트림 시작 오프셋). 압축 리플레이는 해제 버퍼 기준(0부터).</summary>
        public int StreamOffset { get; set; }

        /// <summary>스트림 뒤에 남은 바이트 (정상 = 0)</summary>
        public int TrailingBytes { get; set; }

        /// <summary>LZMA 압축 본문 크기 (비압축 = 0)</summary>
        public int CompressedBytes { get; set; }

        /// <summary>LZMA 해제 후 본문 크기 (비압축 = 0)</summary>
        public int DecompressedBytes { get; set; }

        /// <summary>MSG_NEW_TURN 개수 = 듀얼 턴 수</summary>
        public int TurnCount { get; set; }

        public List<ReplayPacket> Packets { get; set; } = new List<ReplayPacket>();

        public int PacketCount => Packets.Count;

        /// <summary>LZMA 압축 리플레이인지</summary>
        public bool Compressed => (Flags & ReplayParser.REPLAY_COMPRESSED) != 0;

        public bool IsSingleMode => (Flags & ReplayParser.REPLAY_SINGLE_MODE) != 0;

        /// <summary>"이름 / 이름" 형태의 표시 문자열</summary>
        public string PlayerSummary =>
            PlayerNames.Length == 0 ? "(이름 없음)" : string.Join(" / ", PlayerNames);

        public string NameOf(int side) =>
            side >= 0 && side < PlayerNames.Length ? PlayerNames[side] : "(플레이어 " + side + ")";
    }

    /// <summary>
    /// EDOPro 코어 메시지 ID.
    /// 출처: <c>edo9300/edopro</c> → <c>gframe/ocgapi_constants.h</c> ("Ocgapi Messages" 절) 값 그대로.
    /// 클래식 ygopro 표와 다르다 — 예) 0x06 이 MSG_UPDATE_DATA(카드 쿼리), 0x28 이 MSG_NEW_TURN.
    /// </summary>
    public static class ReplayMsg
    {
        public const byte RETRY = 1;
        public const byte HINT = 2;
        public const byte WAITING = 3;
        public const byte START = 4;
        public const byte WIN = 5;
        public const byte UPDATE_DATA = 6;
        public const byte UPDATE_CARD = 7;
        public const byte REQUEST_DECK = 8;
        public const byte SELECT_BATTLECMD = 10;
        public const byte SELECT_IDLECMD = 11;
        public const byte SELECT_EFFECTYN = 12;
        public const byte SELECT_YESNO = 13;
        public const byte SELECT_OPTION = 14;
        public const byte SELECT_CARD = 15;
        public const byte SELECT_CHAIN = 16;
        public const byte SELECT_PLACE = 18;
        public const byte SELECT_POSITION = 19;
        public const byte SELECT_TRIBUTE = 20;
        public const byte SORT_CHAIN = 21;
        public const byte SELECT_COUNTER = 22;
        public const byte SELECT_SUM = 23;
        public const byte SELECT_DISFIELD = 24;
        public const byte SORT_CARD = 25;
        public const byte SELECT_UNSELECT_CARD = 26;
        public const byte CONFIRM_DECKTOP = 30;
        public const byte CONFIRM_CARDS = 31;
        public const byte SHUFFLE_DECK = 32;
        public const byte SHUFFLE_HAND = 33;
        public const byte REFRESH_DECK = 34;
        public const byte SWAP_GRAVE_DECK = 35;
        public const byte SHUFFLE_SET_CARD = 36;
        public const byte REVERSE_DECK = 37;
        public const byte DECK_TOP = 38;
        public const byte SHUFFLE_EXTRA = 39;
        public const byte NEW_TURN = 40;
        public const byte NEW_PHASE = 41;
        public const byte CONFIRM_EXTRATOP = 42;
        public const byte MOVE = 50;
        public const byte POS_CHANGE = 53;
        public const byte SET = 54;
        public const byte SWAP = 55;
        public const byte FIELD_DISABLED = 56;
        public const byte SUMMONING = 60;
        public const byte SUMMONED = 61;
        public const byte SPSUMMONING = 62;
        public const byte SPSUMMONED = 63;
        public const byte FLIPSUMMONING = 64;
        public const byte FLIPSUMMONED = 65;
        public const byte CHAINING = 70;
        public const byte CHAINED = 71;
        public const byte CHAIN_SOLVING = 72;
        public const byte CHAIN_SOLVED = 73;
        public const byte CHAIN_END = 74;
        public const byte CHAIN_NEGATED = 75;
        public const byte CHAIN_DISABLED = 76;
        public const byte CARD_SELECTED = 80;
        public const byte RANDOM_SELECTED = 81;
        public const byte BECOME_TARGET = 83;
        public const byte DRAW = 90;
        public const byte DAMAGE = 91;
        public const byte RECOVER = 92;
        public const byte EQUIP = 93;
        public const byte LPUPDATE = 94;
        public const byte UNEQUIP = 95;
        public const byte CARD_TARGET = 96;
        public const byte CANCEL_TARGET = 97;
        public const byte PAY_LPCOST = 100;
        public const byte ADD_COUNTER = 101;
        public const byte REMOVE_COUNTER = 102;
        public const byte ATTACK = 110;
        public const byte BATTLE = 111;
        public const byte ATTACK_DISABLED = 112;
        public const byte DAMAGE_STEP_START = 113;
        public const byte DAMAGE_STEP_END = 114;
        public const byte MISSED_EFFECT = 120;
        public const byte BE_CHAIN_TARGET = 121;
        public const byte CREATE_RELATION = 122;
        public const byte RELEASE_RELATION = 123;
        public const byte TOSS_COIN = 130;
        public const byte TOSS_DICE = 131;
        public const byte ROCK_PAPER_SCISSORS = 132;
        public const byte HAND_RES = 133;
        public const byte ANNOUNCE_RACE = 140;
        public const byte ANNOUNCE_ATTRIB = 141;
        public const byte ANNOUNCE_CARD = 142;
        public const byte ANNOUNCE_NUMBER = 143;
        public const byte CARD_HINT = 160;
        public const byte TAG_SWAP = 161;
        public const byte RELOAD_FIELD = 162;
        public const byte AI_NAME = 163;
        public const byte SHOW_HINT = 164;
        public const byte PLAYER_HINT = 165;
        public const byte MATCH_KILL = 170;
        public const byte CUSTOM_MSG = 180;
        public const byte REMOVE_CARDS = 190;

        public static string Name(byte id)
        {
            switch (id)
            {
                case RETRY: return "MSG_RETRY";
                case HINT: return "MSG_HINT";
                case WAITING: return "MSG_WAITING";
                case START: return "MSG_START";
                case WIN: return "MSG_WIN";
                case UPDATE_DATA: return "MSG_UPDATE_DATA";
                case UPDATE_CARD: return "MSG_UPDATE_CARD";
                case REQUEST_DECK: return "MSG_REQUEST_DECK";
                case SELECT_BATTLECMD: return "MSG_SELECT_BATTLECMD";
                case SELECT_IDLECMD: return "MSG_SELECT_IDLECMD";
                case SELECT_EFFECTYN: return "MSG_SELECT_EFFECTYN";
                case SELECT_YESNO: return "MSG_SELECT_YESNO";
                case SELECT_OPTION: return "MSG_SELECT_OPTION";
                case SELECT_CARD: return "MSG_SELECT_CARD";
                case SELECT_CHAIN: return "MSG_SELECT_CHAIN";
                case SELECT_PLACE: return "MSG_SELECT_PLACE";
                case SELECT_POSITION: return "MSG_SELECT_POSITION";
                case SELECT_TRIBUTE: return "MSG_SELECT_TRIBUTE";
                case SORT_CHAIN: return "MSG_SORT_CHAIN";
                case SELECT_COUNTER: return "MSG_SELECT_COUNTER";
                case SELECT_SUM: return "MSG_SELECT_SUM";
                case SELECT_DISFIELD: return "MSG_SELECT_DISFIELD";
                case SORT_CARD: return "MSG_SORT_CARD";
                case SELECT_UNSELECT_CARD: return "MSG_SELECT_UNSELECT_CARD";
                case CONFIRM_DECKTOP: return "MSG_CONFIRM_DECKTOP";
                case CONFIRM_CARDS: return "MSG_CONFIRM_CARDS";
                case SHUFFLE_DECK: return "MSG_SHUFFLE_DECK";
                case SHUFFLE_HAND: return "MSG_SHUFFLE_HAND";
                case REFRESH_DECK: return "MSG_REFRESH_DECK";
                case SWAP_GRAVE_DECK: return "MSG_SWAP_GRAVE_DECK";
                case SHUFFLE_SET_CARD: return "MSG_SHUFFLE_SET_CARD";
                case REVERSE_DECK: return "MSG_REVERSE_DECK";
                case DECK_TOP: return "MSG_DECK_TOP";
                case SHUFFLE_EXTRA: return "MSG_SHUFFLE_EXTRA";
                case NEW_TURN: return "MSG_NEW_TURN";
                case NEW_PHASE: return "MSG_NEW_PHASE";
                case CONFIRM_EXTRATOP: return "MSG_CONFIRM_EXTRATOP";
                case MOVE: return "MSG_MOVE";
                case POS_CHANGE: return "MSG_POS_CHANGE";
                case SET: return "MSG_SET";
                case SWAP: return "MSG_SWAP";
                case FIELD_DISABLED: return "MSG_FIELD_DISABLED";
                case SUMMONING: return "MSG_SUMMONING";
                case SUMMONED: return "MSG_SUMMONED";
                case SPSUMMONING: return "MSG_SPSUMMONING";
                case SPSUMMONED: return "MSG_SPSUMMONED";
                case FLIPSUMMONING: return "MSG_FLIPSUMMONING";
                case FLIPSUMMONED: return "MSG_FLIPSUMMONED";
                case CHAINING: return "MSG_CHAINING";
                case CHAINED: return "MSG_CHAINED";
                case CHAIN_SOLVING: return "MSG_CHAIN_SOLVING";
                case CHAIN_SOLVED: return "MSG_CHAIN_SOLVED";
                case CHAIN_END: return "MSG_CHAIN_END";
                case CHAIN_NEGATED: return "MSG_CHAIN_NEGATED";
                case CHAIN_DISABLED: return "MSG_CHAIN_DISABLED";
                case CARD_SELECTED: return "MSG_CARD_SELECTED";
                case RANDOM_SELECTED: return "MSG_RANDOM_SELECTED";
                case BECOME_TARGET: return "MSG_BECOME_TARGET";
                case DRAW: return "MSG_DRAW";
                case DAMAGE: return "MSG_DAMAGE";
                case RECOVER: return "MSG_RECOVER";
                case EQUIP: return "MSG_EQUIP";
                case LPUPDATE: return "MSG_LPUPDATE";
                case UNEQUIP: return "MSG_UNEQUIP";
                case CARD_TARGET: return "MSG_CARD_TARGET";
                case CANCEL_TARGET: return "MSG_CANCEL_TARGET";
                case PAY_LPCOST: return "MSG_PAY_LPCOST";
                case ADD_COUNTER: return "MSG_ADD_COUNTER";
                case REMOVE_COUNTER: return "MSG_REMOVE_COUNTER";
                case ATTACK: return "MSG_ATTACK";
                case BATTLE: return "MSG_BATTLE";
                case ATTACK_DISABLED: return "MSG_ATTACK_DISABLED";
                case DAMAGE_STEP_START: return "MSG_DAMAGE_STEP_START";
                case DAMAGE_STEP_END: return "MSG_DAMAGE_STEP_END";
                case MISSED_EFFECT: return "MSG_MISSED_EFFECT";
                case BE_CHAIN_TARGET: return "MSG_BE_CHAIN_TARGET";
                case CREATE_RELATION: return "MSG_CREATE_RELATION";
                case RELEASE_RELATION: return "MSG_RELEASE_RELATION";
                case TOSS_COIN: return "MSG_TOSS_COIN";
                case TOSS_DICE: return "MSG_TOSS_DICE";
                case ROCK_PAPER_SCISSORS: return "MSG_ROCK_PAPER_SCISSORS";
                case HAND_RES: return "MSG_HAND_RES";
                case ANNOUNCE_RACE: return "MSG_ANNOUNCE_RACE";
                case ANNOUNCE_ATTRIB: return "MSG_ANNOUNCE_ATTRIB";
                case ANNOUNCE_CARD: return "MSG_ANNOUNCE_CARD";
                case ANNOUNCE_NUMBER: return "MSG_ANNOUNCE_NUMBER";
                case CARD_HINT: return "MSG_CARD_HINT";
                case TAG_SWAP: return "MSG_TAG_SWAP";
                case RELOAD_FIELD: return "MSG_RELOAD_FIELD";
                case AI_NAME: return "MSG_AI_NAME";
                case SHOW_HINT: return "MSG_SHOW_HINT";
                case PLAYER_HINT: return "MSG_PLAYER_HINT";
                case MATCH_KILL: return "MSG_MATCH_KILL";
                case CUSTOM_MSG: return "MSG_CUSTOM_MSG";
                case REMOVE_CARDS: return "MSG_REMOVE_CARDS";
                default: return "MSG_" + id;
            }
        }
    }

    /// <summary>
    /// EDOPro 스트리밍 리플레이(.yrpX) 파서 — 외부 라이브러리 없이 순수 C#.
    ///
    /// 포맷 (출처: <c>edo9300/edopro</c> gframe/replay.cpp / replay.h):
    /// <code>
    /// ReplayHeader { u32 id; u32 version; u32 flag; u32 timestamp; u32 datasize; u32 hash; u8 props[8] }  // 32B
    /// flag &amp; 0x200 → u64 header_version + u64 seed[4] 추가 (헤더 72B)
    /// 이름: SINGLE_MODE 면 2개 고정, 아니면 side 마다 NEWREPLAY 면 u32 개수 / TAG 면 2 / 그 외 1.
    ///       이름 1개 = 40바이트 UTF-16LE (널 앞까지)
    /// 파라미터: duel_flags (64BIT_DUELFLAG 면 u64, 아니면 u32)
    /// 스트림: 반복 { u8 message; u32 len; payload[len] }   ← len 이 남은 바이트보다 크면 종료
    /// </code>
    /// 압축(flag &amp; 0x1)은 **원시 LZMA1 스트림**(13바이트 .lzma 헤더 없음)이다.
    /// <code>
    /// 압축 데이터 = 헤더(32/72B) 이후 파일 끝까지, props = 헤더의 props[0..4],
    /// 미압축 크기 = header.datasize, 결과 버퍼 = 헤더 뒤 본문(이름/파라미터/스트림, 오프셋 0부터)
    /// </code>
    /// 해제는 SharpCompress 의 <see cref="LzmaStream"/>(관리 코드)만 사용한다 → 단일 파일 게시에 안전.
    /// </summary>
    public static class ReplayParser
    {
        // --- replay.h 의 REPLAY_* 플래그 ---
        public const uint REPLAY_COMPRESSED = 0x1;
        public const uint REPLAY_TAG = 0x2;
        public const uint REPLAY_DECODED = 0x4;
        public const uint REPLAY_SINGLE_MODE = 0x8;
        public const uint REPLAY_LUA64 = 0x10;
        public const uint REPLAY_NEWREPLAY = 0x20;
        public const uint REPLAY_HAND_TEST = 0x40;
        public const uint REPLAY_DIRECT_SEED = 0x80;
        public const uint REPLAY_64BIT_DUELFLAG = 0x100;
        public const uint REPLAY_EXTENDED_HEADER = 0x200;

        public const uint REPLAY_YRP1 = 0x31707279;   // 'yrp1'
        public const uint REPLAY_YRPX = 0x58707279;   // 'yrpX'

        public const int BaseHeaderSize = 32;
        public const int ExtendedHeaderSize = 72;
        public const int NameBytes = 40;

        /// <summary>헤더 안에서 LZMA props(5바이트)가 시작하는 위치</summary>
        public const int PropsOffset = 24;

        /// <summary>LZMA props 바이트 수 (EDOPro 는 props[8] 중 5바이트만 사용)</summary>
        public const int PropsSize = 5;

        /// <summary>해제 결과 상한 (손상된 datasize 로 인한 거대 할당 방지: 256MB)</summary>
        public const int MaxDecompressedBytes = 256 * 1024 * 1024;

        /// <summary>압축 해제 실패 메시지 (문구 고정)</summary>
        public const string CompressedMessage =
            "리플레이 압축을 해제하지 못했습니다 (파일 손상 또는 지원하지 않는 형식).";

        public const string OldFormatMessage =
            "구형 리플레이(.yrp / yrp1)는 지원하지 않습니다 — 최근 리플레이(.yrpX)를 선택해 주세요.";

        /// <summary>파일을 읽어 헤더/이름/패킷을 모두 파싱한다.</summary>
        public static ReplayFile Parse(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ReplayFormatException("리플레이 파일 경로가 비어 있습니다.");

            byte[] bytes;
            try
            {
                if (!File.Exists(path))
                    throw new ReplayFormatException("리플레이 파일을 찾지 못했습니다: " + path);
                bytes = File.ReadAllBytes(path);
            }
            catch (ReplayFormatException) { throw; }
            catch (Exception ex)
            {
                throw new ReplayFormatException("리플레이 파일을 읽지 못했습니다 — " + ex.Message);
            }

            var file = Parse(bytes);
            file.FilePath = path;
            return file;
        }

        /// <summary>메모리 버퍼 파싱 (테스트/CLI 용).</summary>
        public static ReplayFile Parse(byte[] bytes)
        {
            if (bytes == null || bytes.Length < BaseHeaderSize)
                throw new ReplayFormatException("리플레이 파일이 손상되었습니다 — 헤더(32바이트)가 없습니다.");

            var r = new Reader(bytes);
            var file = new ReplayFile
            {
                Id = r.U32(),
                Version = r.U32(),
                Flags = r.U32()
            };

            if (file.Id == REPLAY_YRP1)
                throw new ReplayFormatException(OldFormatMessage);
            if (file.Id != REPLAY_YRPX)
                throw new ReplayFormatException(
                    "리플레이 파일이 아닙니다 (알 수 없는 식별자 0x" + file.Id.ToString("X8") + ").");

            // timestamp / datasize / hash / props[8]
            r.Skip(4);                                  // timestamp
            var dataSize = r.U32();                     // 압축 전 본문 크기
            r.Skip(4);                                  // hash
            var props = r.Bytes(8);                     // props[8] 중 앞 5바이트가 LZMA props

            var headerSize = BaseHeaderSize;
            if ((file.Flags & REPLAY_EXTENDED_HEADER) != 0)
            {
                file.ExtendedHeader = true;
                if (bytes.Length < ExtendedHeaderSize)
                    throw new ReplayFormatException("리플레이 파일이 손상되었습니다 — 확장 헤더(72바이트)가 잘렸습니다.");
                r.Skip(40);                             // u64 header_version + u64 seed[4]
                headerSize = ExtendedHeaderSize;
            }

            if (file.Compressed)
            {
                var compSize = bytes.Length - headerSize;
                var body = Decompress(bytes, headerSize, compSize, dataSize, props);
                file.CompressedBytes = compSize;
                file.DecompressedBytes = body.Length;

                // 해제 버퍼는 헤더 뒤 본문만 담고 있다 → 이름/파라미터/스트림 모두 오프셋 0부터.
                var nr = new Reader(body);
                ParseNames(nr, file);
                ParseParams(nr, file);

                file.StreamOffset = nr.Position;
                ParseStream(nr, file);
                file.TrailingBytes = body.Length - nr.Position;
                return file;
            }

            ParseNames(r, file);
            ParseParams(r, file);

            file.StreamOffset = r.Position;
            ParseStream(r, file);
            file.TrailingBytes = bytes.Length - r.Position;
            return file;
        }

        /// <summary>
        /// 원시 LZMA1 스트림을 해제한다 (EDOPro <c>LzmaUncompress</c> 와 같은 입력 배치).
        /// </summary>
        /// <param name="src">파일 전체 바이트</param>
        /// <param name="offset">압축 데이터 시작(= 헤더 크기)</param>
        /// <param name="compSize">압축 데이터 길이(= 파일 끝까지)</param>
        /// <param name="outSize">해제 결과 길이(= 헤더의 datasize)</param>
        /// <param name="props">헤더 props[8] (앞 5바이트 사용)</param>
        private static byte[] Decompress(byte[] src, int offset, int compSize, uint outSize, byte[] props)
        {
            if (outSize == 0 || outSize > MaxDecompressedBytes)
                throw new ReplayFormatException(CompressedMessage);
            if (compSize <= 0 || offset < 0 || offset + compSize > src.Length)
                throw new ReplayFormatException(CompressedMessage);
            if (props == null || props.Length < PropsSize)
                throw new ReplayFormatException(CompressedMessage);

            var lzmaProps = new byte[PropsSize];
            Buffer.BlockCopy(props, 0, lzmaProps, 0, PropsSize);

            var size = (int)outSize;
            try
            {
                // LZMA SDK 의 클래식 Decoder — EDOPro 의 LzmaUncompress 와 동일하게
                // "outSize 만큼" 해제한다(스트림 끝 마커가 없어도 동작).
                using (var input = new MemoryStream(src, offset, compSize, false))
                using (var output = new MemoryStream(size))
                using (var decoder = new SharpCompress.Compressors.LZMA.Decoder())
                {
                    decoder.SetDecoderProperties(lzmaProps);
                    decoder.Code(input, output, compSize, size, null);
                    if (output.Length != size) throw new ReplayFormatException(CompressedMessage);
                    return output.ToArray();
                }
            }
            catch (ReplayFormatException) { throw; }
            catch (Exception)
            {
                throw new ReplayFormatException(CompressedMessage);
            }
        }

        /// <summary>
        /// 파일 헤더만 보고 압축 여부를 빠르게 판정한다 (파일 선택창 사전 안내용).
        /// 읽을 수 없으면 null.
        /// </summary>
        public static bool? IsCompressed(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                var head = new byte[BaseHeaderSize];
                using var fs = File.OpenRead(path);
                if (fs.Read(head, 0, head.Length) < head.Length) return null;
                var id = BitConverter.ToUInt32(head, 0);
                if (id == REPLAY_YRP1) return null;                  // 구형 → 다른 안내
                if (id != REPLAY_YRPX) return null;
                var flag = BitConverter.ToUInt32(head, 8);
                return (flag & REPLAY_COMPRESSED) != 0;
            }
            catch { return null; }
        }

        private static void ParseNames(Reader r, ReplayFile file)
        {
            var names = new List<string>();

            void ReadSide()
            {
                uint count;
                if ((file.Flags & REPLAY_NEWREPLAY) != 0) count = r.U32();
                else if ((file.Flags & REPLAY_TAG) != 0) count = 2;
                else count = 1;

                if (count > 16)
                    throw new ReplayFormatException("리플레이 파일이 손상되었습니다 — 플레이어 수가 비정상입니다 (" + count + ").");

                for (uint i = 0; i < count; i++)
                    names.Add(r.Utf16Fixed(NameBytes));
            }

            if ((file.Flags & REPLAY_SINGLE_MODE) != 0)
            {
                names.Add(r.Utf16Fixed(NameBytes));
                names.Add(r.Utf16Fixed(NameBytes));
            }
            else
            {
                ReadSide();   // side 0
                ReadSide();   // side 1
            }

            file.PlayerNames = names.ToArray();
        }

        private static void ParseParams(Reader r, ReplayFile file)
        {
            file.DuelFlags = (file.Flags & REPLAY_64BIT_DUELFLAG) != 0 ? r.U64() : r.U32();
        }

        private static void ParseStream(Reader r, ReplayFile file)
        {
            while (r.Remaining > 0)
            {
                // 남은 바이트가 헤더(1+4)보다 짧으면 스트림 끝으로 본다 (EDOPro 와 같은 관대한 처리)
                if (r.Remaining < 5) break;

                var message = r.U8();
                var len = r.U32();
                if (len > r.Remaining) break;   // 잘린 마지막 패킷 → 무시

                var data = r.Bytes((int)len);
                file.Packets.Add(new ReplayPacket { Message = message, Data = data });
                if (message == ReplayMsg.NEW_TURN) file.TurnCount++;
            }
        }

        /// <summary>바이트 커서 (경계 검사 포함).</summary>
        private sealed class Reader
        {
            private readonly byte[] _buf;
            private int _pos;

            public Reader(byte[] buf) { _buf = buf; }

            public int Position => _pos;
            public int Remaining => _buf.Length - _pos;

            public void Skip(int n)
            {
                if (n < 0 || _pos + n > _buf.Length)
                    throw new ReplayFormatException("리플레이 파일이 손상되었습니다 — 헤더/이름 구간이 잘렸습니다.");
                _pos += n;
            }

            public byte U8()
            {
                if (_pos + 1 > _buf.Length)
                    throw new ReplayFormatException("리플레이 파일이 손상되었습니다 — 데이터가 예상보다 짧습니다.");
                return _buf[_pos++];
            }

            public uint U32()
            {
                if (_pos + 4 > _buf.Length)
                    throw new ReplayFormatException("리플레이 파일이 손상되었습니다 — 데이터가 예상보다 짧습니다.");
                var v = (uint)(_buf[_pos] | (_buf[_pos + 1] << 8) | (_buf[_pos + 2] << 16) | (_buf[_pos + 3] << 24));
                _pos += 4;
                return v;
            }

            public ulong U64()
            {
                var lo = U32();
                var hi = U32();
                return ((ulong)hi << 32) | lo;
            }

            public byte[] Bytes(int n)
            {
                if (n < 0 || _pos + n > _buf.Length)
                    throw new ReplayFormatException("리플레이 파일이 손상되었습니다 — 패킷 데이터가 잘렸습니다.");
                var outBuf = new byte[n];
                Buffer.BlockCopy(_buf, _pos, outBuf, 0, n);
                _pos += n;
                return outBuf;
            }

            /// <summary>고정 길이 UTF-16LE 이름 (널 앞까지)</summary>
            public string Utf16Fixed(int byteCount)
            {
                var raw = Bytes(byteCount);
                var s = Encoding.Unicode.GetString(raw);
                var z = s.IndexOf('\0');
                if (z >= 0) s = s.Substring(0, z);
                return s.TrimEnd();
            }
        }
    }
}
