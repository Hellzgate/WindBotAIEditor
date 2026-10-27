using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace WindBotAIEditor.Models
{
    /// <summary>bots.json 의 한 항목. EDOPro 로비 "AI 리스트"에 그대로 노출된다.</summary>
    public class BotEntry
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("deck")]
        public string Deck { get; set; } = "";

        [JsonPropertyName("difficulty")]
        public int Difficulty { get; set; }

        [JsonPropertyName("masterRules")]
        public List<int> MasterRules { get; set; } = new List<int>();

        /// <summary>DataGrid 편집용 문자열 표현 (예: "4,5")</summary>
        [JsonIgnore]
        public string MasterRulesText
        {
            get => string.Join(", ", MasterRules);
            set
            {
                var list = new List<int>();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    foreach (var part in value.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
                        if (int.TryParse(part.Trim(), out var n)) list.Add(n);
                }
                MasterRules = list;
            }
        }

        [JsonIgnore]
        public string DeckFile => $"AI_{Deck}.ydk";

        public BotEntry Clone() => new BotEntry
        {
            Name = Name,
            Deck = Deck,
            Difficulty = Difficulty,
            MasterRules = MasterRules.ToList()
        };
    }
}
