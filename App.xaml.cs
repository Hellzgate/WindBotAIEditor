using System;
using System.Linq;
using System.IO;
using System.Text.Json;
using System.Windows;
using WindBotAIEditor.Models;
using WindBotAIEditor.Services;

namespace WindBotAIEditor;

public partial class App : Application
{
    /// <summary>현재 열린 WindBot 폴더 (카드 DB / Lua 경로 계산용)</summary>
    public static string WindBotRoot { get; set; } = "";

    /// <summary>현재 열린 EDOPro 폴더 (script/ 경로 등)</summary>
    public static string EdoproRoot { get; set; } = "";
    protected override void OnStartup(StartupEventArgs e)
    {
        // 헤드리스 빌드: WindBotAIEditor.exe --build <profile.json> [<windbotFolder>]
        if (e.Args.Length >= 2 && e.Args[0] == "--build")
        {
            int code = RunHeadlessBuild(e.Args);
            Shutdown(code);
            return;
        }

        // 소스만 생성: WindBotAIEditor.exe --gen <profile.json> <out.cs>
        if (e.Args.Length >= 3 && e.Args[0] == "--gen")
        {
            int code = RunGenSource(e.Args);
            Shutdown(code);
            return;
        }

        // 배포용 ZIP: WindBotAIEditor.exe --export <봇이름> <out.zip> [<windbotFolder>]
        if (e.Args.Length >= 3 && e.Args[0] == "--export")
        {
            int code = RunExport(e.Args);
            Shutdown(code);
            return;
        }

        // 카드 DB 확인: WindBotAIEditor.exe --dbinfo <cards.cdb>
        if (e.Args.Length >= 2 && e.Args[0] == "--dbinfo")
        {
            int code = RunDbInfo(e.Args[1]);
            Shutdown(code);
            return;
        }

        // 후보 탐색 확인: WindBotAIEditor.exe --dbscan <windbotFolder>
        if (e.Args.Length >= 2 && e.Args[0] == "--dbscan")
        {
            int code = RunDbScan(e.Args[1]);
            Shutdown(code);
            return;
        }

        // 덱 저장/재읽기 왕복 검사: WindBotAIEditor.exe --decktest <windbotFolder>
        if (e.Args.Length >= 2 && e.Args[0] == "--decktest")
        {
            int code = RunDeckTest(e.Args[1]);
            Shutdown(code);
            return;
        }

        // 리플레이 파싱/추출 확인: WindBotAIEditor.exe --replay <파일.yrpX> [내쪽 0|1] [EDOPro폴더]
        if (e.Args.Length >= 2 && e.Args[0] == "--replay")
        {
            int code = RunReplay(e.Args);
            Shutdown(code);
            return;
        }

        // 루트 JSON 왕복 검사: WindBotAIEditor.exe --routetest <rules.json> [<out.json>]
        if (e.Args.Length >= 2 && e.Args[0] == "--routetest")
        {
            int code = RunRouteTest(e.Args);
            Shutdown(code);
            return;
        }

        // 화면을 띄우지 않고 XAML/⑤ 탭 자기검사: WindBotAIEditor.exe --uicheck [<rules.json>]
        if (e.Args.Length >= 1 && e.Args[0] == "--uicheck")
        {
            int code = RunUiCheck(e.Args);
            Shutdown(code);
            return;
        }
        base.OnStartup(e);
    }

    /// <summary>
    /// 창을 표시하지 않고 MainWindow 를 생성/레이아웃해 XAML 파싱·리소스·탭 분리(덱/⚙ AI 설정)·⑤ 탭 편집 동작을 검사한다.
    /// (사용자 화면을 건드리지 않는 검증용 CLI)
    /// 사용: --uicheck [&lt;rules.json&gt;]  (JSON 은 읽기 전용 — 화면 상태로만 반영)
    /// </summary>
    private static int RunUiCheck(string[] args)
    {
        try
        {
            var w = new MainWindow();
            var profilePath = args.Length >= 2 ? Path.GetFullPath(args[1]) : null;
            var loaded = profilePath != null && w.LoadProfileFile(profilePath);
            if (profilePath != null && !loaded)
                Console.WriteLine("profile=" + profilePath + " LOAD_FAILED");
            if (w.Content is FrameworkElement root)
            {
                root.Measure(new Size(1560, 1000));
                root.Arrange(new Rect(0, 0, 1560, 1000));
                root.UpdateLayout();
            }

            // B-7: 덱 / ⚙ AI 설정 탭 분리 + 가시성 + 최소 크기 레이아웃
            var problems = w.VerifyLayoutUi();
            foreach (var p in problems) Console.Error.WriteLine("UICHECK PROBLEM " + p);

            // ⑤ 전개 루트 동작 (XAML 템플릿/코드젠 포함)
            var total = problems.Count;
            problems.AddRange(w.VerifyRouteUi());
            for (var i = total; i < problems.Count; i++)
                Console.Error.WriteLine("UICHECK PROBLEM " + problems[i]);

            // B-15: 자유 플레이 마법/함정 세트
            total = problems.Count;
            problems.AddRange(w.VerifyFreePlaySetUi());
            for (var i = total; i < problems.Count; i++)
                Console.Error.WriteLine("UICHECK PROBLEM " + problems[i]);

            // B-16: 자유 플레이 엑스트라 덱 몬스터 릴리스 금지
            total = problems.Count;
            problems.AddRange(w.VerifyProtectExtraUi());
            for (var i = total; i < problems.Count; i++)
                Console.Error.WriteLine("UICHECK PROBLEM " + problems[i]);

            // B-17: 선택 정책 "발동 중인 카드(체인)"
            total = problems.Count;
            problems.AddRange(w.VerifyChainPickUi());
            for (var i = total; i < problems.Count; i++)
                Console.Error.WriteLine("UICHECK PROBLEM " + problems[i]);

            // B-18: 자유 플레이 제물 여력 가드
            total = problems.Count;
            problems.AddRange(w.VerifyTributeFodderUi());
            for (var i = total; i < problems.Count; i++)
                Console.Error.WriteLine("UICHECK PROBLEM " + problems[i]);

            // B-20: 조건 2종 (엑몹존 특정 카드 / 필드 레벨 N 몬스터 M장)
            total = problems.Count;
            problems.AddRange(w.VerifyB20Ui());
            for (var i = total; i < problems.Count; i++)
                Console.Error.WriteLine("UICHECK PROBLEM " + problems[i]);

            // B-21: 복합 조건 (테마·종족 엑시즈) + SelectOption 디버그 로그
            total = problems.Count;
            problems.AddRange(w.VerifyB21Ui());
            for (var i = total; i < problems.Count; i++)
                Console.Error.WriteLine("UICHECK PROBLEM " + problems[i]);
            Console.WriteLine(problems.Count == 0 ? "UICHECK OK" : "UICHECK FAIL " + problems.Count);
            Console.WriteLine("tabs=" + w.MainTabCount +
                              " ruleSubTabs=" + w.RuleSubTabCount +
                              " ruleSubTabsOk=" + (w.RuleSubTabsOk ? "OK" : "NG") +
                              " deckTab=" + (w.HasMainTab("덱") ? "OK" : "NG") +
                              " settingsTab=" + (w.HasMainTab("⚙ AI 설정") ? "OK" : "NG") +
                              " settingsCollapsed=" + (w.SettingsCollapsedByDefault ? "True" : "False") +
                              " controls=" + w.LayoutControlsFound + "/" + w.LayoutControlsChecked +
                              " minSize=" + w.MinWidth + "x" + w.MinHeight +
                              " routeSets=" + w.RouteSetCount(false) + "/" + w.RouteSetCount(true) +
                              " routeSteps=" + w.RouteStepCount(false) + "/" + w.RouteStepCount(true));
            Console.WriteLine("layout(min " + w.MinWidth + "x" + w.MinHeight + ") " + w.LayoutMetrics);
            Console.WriteLine("settings collapsedCardH=" + Math.Round(w.SettingsCollapsedHeight) +
                              " expandedCardH=" + Math.Round(w.SettingsExpandedHeight) +
                              " (1100x700) ruleTabsH=" + Math.Round(w.RuleTabsHeight));
            Console.WriteLine("routeListH=" + Math.Round(w.RouteListHeight) + " stepsH=" + Math.Round(w.StepsHeight) +
                              " (1100x700) / routeListH=" + Math.Round(w.RouteListHeightDefault) +
                              " stepsH=" + Math.Round(w.StepsHeightDefault) + " (1560x1000)");
            Console.WriteLine("trackBtn " + w.TrackButtonMetrics);
            Console.WriteLine("b14 " + w.B14Metrics);
            Console.WriteLine("b15 " + w.B15Metrics);
            Console.WriteLine("b16 " + w.B16Metrics);
            Console.WriteLine("b17 " + w.B17Metrics);
            Console.WriteLine("b18 " + w.B18Metrics);
            Console.WriteLine("b20 " + w.B20Metrics);
            Console.WriteLine("b21 " + w.B21Metrics);
            Console.WriteLine("profile=" + (profilePath == null ? "(없음)" : Path.GetFileName(profilePath) + (loaded ? " OK" : " FAIL")) +
                              " summary=\"" + w.RuleSummaryText + "\"");
            return problems.Count == 0 ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("UICHECK FAIL " + ex);
            return 2;
        }
    }

    /// <summary>
    /// 창을 띄우지 않고 리플레이(.yrpX)를 파싱해 ⑤ 전개 루트 추출 결과를 콘솔에 출력한다.
    /// 사용: --replay &lt;파일&gt; [내쪽 0|1] [EDOPro폴더]
    /// </summary>
    private static int RunReplay(string[] args)
    {
        try
        {
            var path = Path.GetFullPath(args[1]);
            var side = -1;
            if (args.Length >= 3 && int.TryParse(args[2], out var s) && (s == 0 || s == 1)) side = s;

            // 카드명 조회용 cdb (없어도 파싱은 계속)
            var edopro = args.Length >= 4 ? Path.GetFullPath(args[3]) : AppSettings.Load().EdoproPath;
            if (!string.IsNullOrWhiteSpace(edopro) && Directory.Exists(edopro))
            {
                var windbot = Path.Combine(edopro, "WindBot");
                if (!Directory.Exists(windbot)) windbot = edopro;
                var cands = CardDatabase.FindCandidates(windbot);
                if (cands.Count > 0) CardDatabase.Instance.OpenMany(cands);
            }

            ReplayFile replay;
            try { replay = ReplayParser.Parse(path); }
            catch (ReplayFormatException ex)
            {
                Console.Error.WriteLine("REPLAY FAIL " + ex.Message);
                return 2;
            }

            Console.WriteLine("FILE " + path);
            Console.WriteLine("flags=0x" + replay.Flags.ToString("X") + " extended=" + replay.ExtendedHeader +
                              " duelFlags=0x" + replay.DuelFlags.ToString("X") + " singleMode=" + replay.IsSingleMode);
            Console.WriteLine("compressed=" + replay.Compressed + " decompressed=" + replay.DecompressedBytes +
                              "B compressedSize=" + replay.CompressedBytes + "B");
            Console.WriteLine("players=" + replay.PlayerSummary);
            Console.WriteLine("packets=" + replay.PacketCount + " turns=" + replay.TurnCount +
                              " streamOffset=" + replay.StreamOffset + " total=" + new FileInfo(path).Length +
                              "B remaining=" + replay.TrailingBytes + "B");
            Console.WriteLine("cdb=" + CardDatabase.Instance.Paths.Count);

            // 메시지 종류 분포 (메시지 ID 표 검증용)
            var hist = new SortedDictionary<int, int>();
            foreach (var p in replay.Packets)
            {
                hist.TryGetValue(p.Message, out var c);
                hist[p.Message] = c + 1;
            }
            Console.WriteLine("histogram:");
            foreach (var kv in hist)
                Console.WriteLine("  " + kv.Key.ToString("X2") + " " + ReplayMsg.Name((byte)kv.Key).PadRight(24) + kv.Value);

            if (side < 0) side = ReplayRouteExtractor.GuessMySide(replay);
            for (var i = 0; i < replay.PlayerNames.Length; i++)
                Console.WriteLine("side " + i + " = " + replay.PlayerNames[i] + (i == side ? "   <= 내 쪽" : ""));

            var extraction = ReplayRouteExtractor.Extract(replay, side);
            Console.WriteLine("myTurns=" + extraction.MyTurnCount + " ignoredTurns(3턴째 이후)=" + extraction.IgnoredTurnCount);
            foreach (var a in extraction.Actions)
                Console.WriteLine("  " + a.Turn + "턴 [" + a.TrackLabel + "] " + a.Card + " " +
                                  (a.CardName.Length > 0 ? a.CardName : "(이름없음)") + " → " + a.Action +
                                  " (" + a.ActionLabel + ")" + (a.Note.Length > 0 ? "  # " + a.Note : ""));

            Console.WriteLine("SUMMARY " + extraction.Summary);

            // 단계 모델 → 코드 생성 왕복 확인 (규칙 JSON 없이도 검증 가능)
            var first = ReplayRouteExtractor.ToRouteSteps(extraction.OfTrack("first"));
            var second = ReplayRouteExtractor.ToRouteSteps(extraction.OfTrack("second"));
            var profile = new AiProfile { Engine = "ReplayProbe" };
            var setFirst = new RouteSet { Name = "리플레이", IsDefault = true };
            foreach (var st in first) setFirst.Steps.Add(st);
            profile.Routes.Add(setFirst);
            if (second.Count > 0)
            {
                var setSecond = new RouteSet { Name = "리플레이", IsDefault = true };
                foreach (var st in second) setSecond.Steps.Add(st);
                profile.RoutesSecond.Add(setSecond);
            }
            var src = RuleCodeGen.Generate(profile);
            Console.WriteLine("codegen=OK routes=" + profile.Routes.Count + " routesSecond=" + profile.RoutesSecond.Count +
                              " firstSteps=" + first.Count + " secondSteps=" + second.Count +
                              " bytes=" + src.Length +
                              " hasSelectFirst=" + src.Contains("SelectRouteFirst") +
                              " hasFirstStep0=" + src.Contains("RouteFirst0Step0_R1") +
                              " hasSecondStep0=" + src.Contains("RouteSecond0Step0_R1"));
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("ERROR " + ex.Message);
            return 3;
        }
    }

    /// <summary>
    /// 루트 스키마 왕복 검사 (화면 없음).
    /// 구형 단일 루트(route/routeSecond) JSON 을 읽어 routes/routesSecond 로 이관되는지,
    /// 저장 → 재읽기에서 벌 수가 유지되는지 콘솔로 확인한다.
    /// </summary>
    private static int RunRouteTest(string[] args)
    {
        try
        {
            var path = Path.GetFullPath(args[1]);
            var outPath = args.Length >= 3
                ? Path.GetFullPath(args[2])
                : Path.Combine(Path.GetTempPath(), "route-roundtrip.json");

            var p = JsonSerializer.Deserialize<AiProfile>(File.ReadAllText(path), WindBotProject.JsonOpts);
            if (p == null) { Console.Error.WriteLine("JSON 파싱 실패: " + path); return 2; }

            Console.WriteLine("FILE " + path);
            Console.WriteLine("before: legacyRoute=" + (p.Route == null ? "null" : p.Route.Count.ToString()) +
                              " legacyRouteSecond=" + (p.RouteSecond == null ? "null" : p.RouteSecond.Count.ToString()) +
                              " routes=" + p.Routes.Count + " routesSecond=" + p.RoutesSecond.Count +
                              " choices=" + (p.Choices == null ? 0 : p.Choices.Count) + " choiceConds=" + ChoiceCondTotal(p) +
                              " sameNameAliases=" + p.SameNameAliases);

            RuleMigration.Normalize(p);
            Console.WriteLine("after:  routes=" + ProfileSummary(p.Routes) + " routesSecond=" + ProfileSummary(p.RoutesSecond));
            Console.WriteLine("legacyCleared=" + (p.Route == null && p.RouteSecond == null));

            File.WriteAllText(outPath, JsonSerializer.Serialize(p, WindBotProject.JsonOpts), new System.Text.UTF8Encoding(false));
            var saved = File.ReadAllText(outPath);
            Console.WriteLine("saved=" + outPath + " bytes=" + saved.Length +
                              " hasLegacyKey=" + (saved.Contains("\"route\":") || saved.Contains("\"routeSecond\":")));

            var back = JsonSerializer.Deserialize<AiProfile>(File.ReadAllText(outPath), WindBotProject.JsonOpts);
            if (back == null) { Console.Error.WriteLine("재읽기 실패: " + outPath); return 3; }
            RuleMigration.Normalize(back);
            Console.WriteLine("reload: routes=" + ProfileSummary(back.Routes) + " routesSecond=" + ProfileSummary(back.RoutesSecond) +
                              " choices=" + (back.Choices == null ? 0 : back.Choices.Count) + " choiceConds=" + ChoiceCondTotal(back) +
                              " sameNameAliases=" + back.SameNameAliases + " hasKey=" + saved.Contains("\"sameNameAliases\":"));

            var ok = back.Routes.Count == p.Routes.Count
                     && back.RoutesSecond.Count == p.RoutesSecond.Count
                     && back.Routes.Sum(s => s.Steps.Count) == p.Routes.Sum(s => s.Steps.Count)
                     && back.RoutesSecond.Sum(s => s.Steps.Count) == p.RoutesSecond.Sum(s => s.Steps.Count)
                     && ChoiceCondTotal(back) == ChoiceCondTotal(p)
                     && back.SameNameAliases == p.SameNameAliases
                     && back.Routes.Zip(p.Routes).All(z => z.First.IsDefault == z.Second.IsDefault
                                                          && z.First.Conditions.Count == z.Second.Conditions.Count);

            Console.WriteLine(ok ? "ROUTETEST OK" : "ROUTETEST FAIL");
            return ok ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("ERROR " + ex.Message);
            return 4;
        }
    }

    private static string ProfileSummary(IEnumerable<RouteSet>? sets) =>
        "[" + string.Join(" | ", (sets ?? Enumerable.Empty<RouteSet>())
            .Select(s => (string.IsNullOrWhiteSpace(s.Name) ? "(무명)" : s.Name)
                         + (s.IsDefault ? "*" : "") + " c" + s.Conditions.Count + " s" + s.Steps.Count)) + "]";

    /// <summary>② 선택 규칙의 조건 총 개수 (JSON 왕복 검사용 — B-6)</summary>
    private static int ChoiceCondTotal(AiProfile? p) =>
        (p?.Choices ?? new List<ChoiceRule>()).Sum(c => (c == null || c.Conditions == null) ? 0 : c.Conditions.Count);

    private static int RunDeckTest(string windBotRoot)
    {
        try
        {
            var project = new WindBotProject(windBotRoot);
            const string name = "ZZEditorTest";
            project.WriteDeck(name, "#created by test\n#main\n11111111\n11111111\n\n#extra\n22222222\n\n!side\n33333333\n33333333\n");
            var back = project.ReadDeck(name);
            var ok1 = back.Contains("11111111") && back.Contains("22222222") && back.Contains("33333333");
            Console.WriteLine("write/read: " + (ok1 ? "PASS" : "FAIL") + "  (" + project.DeckPath(name) + ")");

            project.WriteDeck(name, back.Replace("11111111", "87654321"));
            var back2 = project.ReadDeck(name);
            var ok2 = back2.Contains("87654321") && !back2.Contains("11111111");
            Console.WriteLine("update: " + (ok2 ? "PASS" : "FAIL"));

            var listed = project.ListDecks().Contains("AI_" + name + ".ydk");
            Console.WriteLine("listed: " + (listed ? "PASS" : "FAIL") + "  count=" + project.ListDecks().Length);

            var counts = WindBotProject.CountDeck(back2);
            var ok3 = counts.main == 2 && counts.extra == 1 && counts.side == 2;
            Console.WriteLine($"counts: main={counts.main} extra={counts.extra} side={counts.side}  (기대 2/1/2) " + (ok3 ? "PASS" : "FAIL"));

            File.Delete(project.DeckPath(name));
            Console.WriteLine("cleanup: " + (File.Exists(project.DeckPath(name)) ? "FAIL" : "PASS"));
            return ok1 && ok2 && listed && ok3 ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("ERROR " + ex.Message);
            return 2;
        }
    }

    private static int RunDbScan(string windBotRoot)
    {
        var candidates = CardDatabase.FindCandidates(windBotRoot);
        Console.WriteLine("candidates=" + candidates.Count);
        foreach (var c in candidates) Console.WriteLine("  " + c);

        if (!CardDatabase.Instance.OpenMany(candidates))
        {
            Console.Error.WriteLine("DB 열기 실패");
            return 1;
        }
        Console.WriteLine("opened=" + CardDatabase.Instance.Paths.Count);
        var rows = CardDatabase.Instance.Search("푸른 눈", 5);
        foreach (var r in rows) Console.WriteLine($"  hit {r.Id} {r.Name} [{r.Source}]");
        Console.WriteLine("name(89631139)=" + CardDatabase.Instance.NameOf(89631139));
        Console.WriteLine("idOf(푸른 눈의 백룡)=" + CardDatabase.Instance.FindIdByName("푸른 눈의 백룡"));
        // B-12: 같은 이름 카드(대체 일러/재록) 그룹
        var ogre = CardDatabase.Instance.AliasesOf(59438930);
        Console.WriteLine("aliases(59438930)=" + string.Join(", ", ogre.Select(x => x.ToString()).ToArray()) +
                          "  name=" + CardDatabase.Instance.NameOf(59438930) +
                          "  groups=" + CardDatabase.Instance.AliasGroupCount);
        CardDatabase.Instance.Close();
        return 0;
    }

    private static int RunDbInfo(string path)
    {
        try
        {
            if (!CardDatabase.Instance.OpenMany(new[] { path }))
            {
                Console.Error.WriteLine("DB 열기 실패: " + path);
                return 1;
            }
            var rows = CardDatabase.Instance.Search("", 5);
            Console.WriteLine("DB: " + path);
            Console.WriteLine("rows(sample)=" + rows.Count);
            foreach (var r in rows)
                Console.WriteLine("  " + r.Id + "  " + r.Name + "  ATK " + r.Atk);
            var sample = CardDatabase.Instance.Info(89631139);
            if (sample != null)
                Console.WriteLine("89631139 -> " + sample.Name + " | " + (sample.Desc.Length > 60 ? sample.Desc.Substring(0, 60) + "..." : sample.Desc));
            CardDatabase.Instance.Close();
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("ERROR " + ex.Message);
            return 2;
        }
    }

    private static int RunGenSource(string[] args)
    {
        try
        {
            var profilePath = Path.GetFullPath(args[1]);
            var outPath = Path.GetFullPath(args[2]);
            var profile = JsonSerializer.Deserialize<AiProfile>(File.ReadAllText(profilePath), WindBotProject.JsonOpts);
            if (profile == null) { Console.Error.WriteLine("profile json 파싱 실패"); return 2; }
            RuleMigration.Normalize(profile);
            // B-12: --gen 도 별칭을 해석한다 (EDOPro 경로는 3번째 인자 → 없으면 저장된 설정)
            var cdb = EnsureCardDatabase(args.Length >= 4 ? Path.GetFullPath(args[3]) : null);
            var src = RuleCodeGen.Generate(profile);
            foreach (var w in RuleCodeGen.Warnings) Console.WriteLine("WARN " + w);
            Console.WriteLine("CDB " + cdb + " aliases=" + CardDatabase.Instance.AliasGroupCount);
            var dir = Path.GetDirectoryName(outPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(outPath, src, new System.Text.UTF8Encoding(false));
            Console.WriteLine("OK " + outPath);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("ERROR " + ex.Message);
            return 3;
        }
    }

    /// <summary>
    /// 헤드리스 코드젠이 같은 이름 카드 별칭(B-12)을 해석할 수 있도록 카드 DB 를 연다 (읽기 전용).
    /// 이미 열려 있으면 그대로 쓴다. 반환값 = 열린 cdb 수 (0 = 못 열었음 → 별칭은 자기 자신만).
    /// </summary>
    private static int EnsureCardDatabase(string? windBotRoot)
    {
        if (CardDatabase.Instance.IsOpen) return CardDatabase.Instance.Paths.Count;
        try
        {
            var root = windBotRoot;
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                var edopro = AppSettings.Load().EdoproPath;
                if (string.IsNullOrWhiteSpace(edopro) || !Directory.Exists(edopro)) return 0;
                root = Directory.Exists(Path.Combine(edopro, "WindBot")) ? Path.Combine(edopro, "WindBot") : edopro;
            }
            var candidates = CardDatabase.FindCandidates(root!);
            if (candidates.Count == 0) return 0;
            CardDatabase.Instance.OpenMany(candidates);
            return CardDatabase.Instance.Paths.Count;
        }
        catch { return 0; }
    }

    private static int RunExport(string[] args)
    {
        try
        {
            var botName = args[1];
            var outZip = Path.GetFullPath(args[2]);
            var root = args.Length >= 4 ? Path.GetFullPath(args[3]) : Path.GetFullPath(".");
            var project = new WindBotProject(root);
            var bots = project.LoadBots();
            var bot = bots.FirstOrDefault(b => string.Equals(b.Name, botName, StringComparison.OrdinalIgnoreCase))
                      ?? bots.FirstOrDefault(b => string.Equals(b.Deck, botName, StringComparison.OrdinalIgnoreCase));
            if (bot == null) { Console.Error.WriteLine("봇을 찾지 못했습니다: " + botName); return 2; }

            var res = BotPackageExporter.Export(project, bot, outZip);
            if (!res.Ok) { Console.Error.WriteLine("FAILED " + res.Error); return 1; }
            Console.WriteLine("OK " + res.ZipPath);
            foreach (var f in res.Files) Console.WriteLine("  " + f);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("ERROR " + ex.Message);
            return 3;
        }
    }

    private static int RunHeadlessBuild(string[] args)
    {
        try
        {
            var profilePath = Path.GetFullPath(args[1]);
            var profile = JsonSerializer.Deserialize<AiProfile>(File.ReadAllText(profilePath), WindBotProject.JsonOpts);
            RuleMigration.Normalize(profile);
            if (profile == null)
            {
                Console.Error.WriteLine("profile json 파싱 실패");
                return 2;
            }

            string root = args.Length >= 3
                ? Path.GetFullPath(args[2])
                : Path.GetDirectoryName(profilePath) ?? ".";
            var project = new WindBotProject(root);

            // B-12: 같은 이름 카드 별칭을 해석하려면 cdb 가 필요하다 (읽기 전용)
            var cdb = EnsureCardDatabase(project.Root);
            var source = RuleCodeGen.Generate(profile);
            foreach (var w in RuleCodeGen.Warnings) Console.WriteLine("WARN " + w);
            Console.WriteLine("CDB " + cdb + " aliases=" + CardDatabase.Instance.AliasGroupCount);
            var cls = RuleCodeGen.ClassName(profile.Engine);
            var res = PluginBuilder.BuildFromSource(project, cls, source);

            if (res.Ok)
            {
                Console.WriteLine("OK " + res.OutputPath);
                Console.WriteLine("SOURCE " + res.SourcePath);
                return 0;
            }
            Console.Error.WriteLine("FAILED");
            Console.Error.WriteLine(res.Log);
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("ERROR " + ex.Message);
            return 3;
        }
    }
}







