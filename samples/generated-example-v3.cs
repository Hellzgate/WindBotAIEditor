// WindBot AI Editor — rule-generated executor (schema v2)
// engine : TestRules3Executor
// note   : AnnounceCard / SelectPlace / SelectPosition 검증
using System;
using System.Collections.Generic;
using WindBot.Game;
using WindBot.Game.AI;
using YGOSharp.OCGWrapper.Enums;

namespace WindBot.Game.AI.Decks
{
    public class TestRules3Executor : Executor
    {
        public TestRules3Executor(GameAI ai, Duel duel) : base(ai, duel)
        {
            AddExecutor(ExecutorType.Summon, () => Enemy.GetMonsterCount() >= 0);
        }

        public override IList<ClientCard> OnSelectCard(IList<ClientCard> cards, int min, int max, long hint, bool cancelable)
        {
            return Pick(cards, max, 1, 0);
        }

        public override int OnAnnounceCard(IList<int> avail)
        {
            for (int i = 0; i < avail.Count; i++)
                if (avail[i] == 89631139) return avail[i];
            return avail.Count > 0 ? avail[0] : 0;
        }

        public override int OnSelectPlace(long cardId, int player, CardLocation location, int available)
        {
            for (int i = 0; i < 7; i++)
                if ((available & (1 << i)) != 0) return i;
            return 0;
        }

        public override CardPosition OnSelectPosition(int cardId, IList<CardPosition> positions)
        {
            for (int i = 0; i < positions.Count; i++)
                if (positions[i] == CardPosition.FaceUpDefence || positions[i] == CardPosition.FaceDownDefence) return positions[i];
            return positions.Count > 0 ? positions[0] : CardPosition.FaceUpDefence;
        }

        // policy: 0=목록순 1=공격력↓ 2=공격력↑ 3=수비력↓ 4=레벨↑ 5=특정카드우선
        private static IList<ClientCard> Pick(IList<ClientCard> cards, int max, int policy, int code)
        {
            List<ClientCard> pool = new List<ClientCard>(cards);
            if (policy == 1) pool.Sort(delegate(ClientCard a, ClientCard b) { return b.Attack.CompareTo(a.Attack); });
            else if (policy == 2) pool.Sort(delegate(ClientCard a, ClientCard b) { return a.Attack.CompareTo(b.Attack); });
            else if (policy == 3) pool.Sort(delegate(ClientCard a, ClientCard b) { return b.Defense.CompareTo(a.Defense); });
            else if (policy == 4) pool.Sort(delegate(ClientCard a, ClientCard b) { return a.Level.CompareTo(b.Level); });
            List<ClientCard> picked = new List<ClientCard>();
            if (code > 0)
            {
                for (int i = 0; i < pool.Count && picked.Count < max; i++)
                    if (pool[i].IsCode(code)) picked.Add(pool[i]);
            }
            for (int i = 0; i < pool.Count && picked.Count < max; i++)
                if (!picked.Contains(pool[i])) picked.Add(pool[i]);
            return picked;
        }
    }
}
