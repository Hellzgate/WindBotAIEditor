using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Text;

namespace WindBotAIEditor.Models
{
    /// <summary>테마(아키타입) 한 개. Code 0 = 전체(테마 무관)</summary>
    public class ThemeOption : ICodeOption
    {
        public int Code { get; set; }
        public string Name { get; set; } = "";
        public override string ToString() => Name;
    }
}

namespace WindBotAIEditor.Services
{
    using WindBotAIEditor.Models;

    /// <summary>
    /// EDOPro 의 strings.conf `!setname 0xNNNN 이름` 을 읽어 테마(아키타입) 목록을 만든다.
    /// 카드가 그 테마인지는 실행 시 ClientCard.Data.Setcode 로 판정한다.
    /// </summary>
    public sealed class ThemeCatalog
    {
        public static ThemeCatalog Instance { get; } = new ThemeCatalog();

        /// <summary>테마 목록 (바인딩이 잡고 있으므로 인스턴스를 유지하고 내용만 갱신)</summary>
        public ObservableCollection<ThemeOption> All { get; } = new ObservableCollection<ThemeOption>
        {
            new ThemeOption { Code = 0, Name = "전체 (테마 무관)" }
        };

        public string SourceFile { get; private set; } = "";

        public ThemeOption Find(int code)
        {
            foreach (var t in All) if (t.Code == code) return t;
            return All[0];
        }

        public void Load(string edoproRoot)
        {
            var map = new Dictionary<int, string>();

            // 영어 기본 → 한국어(정발명/구어체) 순으로 덮어쓴다
            foreach (var path in Candidates(edoproRoot))
            {
                try
                {
                    if (File.Exists(path)) Parse(path, map);
                }
                catch { }
            }

            var list = new List<ThemeOption>();
            foreach (var kv in map)
                if (kv.Key > 0 && !string.IsNullOrWhiteSpace(kv.Value))
                    list.Add(new ThemeOption { Code = kv.Key, Name = kv.Value.Trim() });

            list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCulture));

            All.Clear();
            All.Add(new ThemeOption { Code = 0, Name = "전체 (테마 무관)" });
            foreach (var t in list) All.Add(t);
            SourceFile = string.Join(" + ", Candidates(edoproRoot).FindAll(File.Exists));
        }

        private static List<string> Candidates(string root)
        {
            var list = new List<string>();
            if (string.IsNullOrWhiteSpace(root)) return list;
            void Add(string p) { if (!string.IsNullOrWhiteSpace(p)) list.Add(p); }

            Add(Path.Combine(root, "config", "strings.conf"));
            Add(Path.Combine(root, "config", "languages", "Korean", "strings.conf"));
            Add(Path.Combine(root, "config", "languages", "한국어", "strings.conf"));
            Add(Path.Combine(root, "repositories", "languages", "한국어", "strings.conf"));
            Add(Path.Combine(root, "repositories", "languages", "한국어-3", "strings.conf"));
            Add(Path.Combine(root, "languages", "Korean", "strings.conf"));
            return list;
        }

        private static void Parse(string path, Dictionary<int, string> map)
        {
            foreach (var line in File.ReadAllLines(path, Encoding.UTF8))
            {
                var s = line.Trim();
                if (!s.StartsWith("!setname", StringComparison.OrdinalIgnoreCase)) continue;
                var rest = s.Substring("!setname".Length).Trim();
                int sp = rest.IndexOf(' ');
                if (sp <= 0) continue;
                var codeText = rest.Substring(0, sp).Trim();
                var name = rest.Substring(sp + 1).Trim();
                if (name.Length == 0) continue;
                if (!codeText.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) continue;
                int code;
                if (!int.TryParse(codeText.Substring(2), System.Globalization.NumberStyles.HexNumber, null, out code)) continue;
                map[code] = name;
            }
        }
    }
}


