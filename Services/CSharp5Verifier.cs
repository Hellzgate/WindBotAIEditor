using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace WindBotAIEditor.Services
{
    /// <summary>
    /// 2026-11-02: 생성 코드를 **실제 csc v4.0(C#5)** 으로 컴파일해 검증한다.
    /// <para>배포 빌드(`PluginBuilder`)와 **같은 참조·같은 컴파일러**를 쓰므로 결과가 실빌드와 동일하다.</para>
    /// <para>용도: `rawBody`/`rawMembers` 편집 저장 전 검증 · 빌드 전 사전 검증 · `--check` CLI.</para>
    /// </summary>
    public static class CSharp5Verifier
    {
        public sealed class Diag
        {
            public int Line { get; set; }
            public int Column { get; set; }
            public string Code { get; set; } = "";
            public string Message { get; set; } = "";
            public override string ToString() =>
                (Line > 0 ? "(" + Line + "," + Column + ") " : "") + Code + ": " + Message;
        }

        public sealed class Result
        {
            public bool Ok { get; set; }
            public List<Diag> Items { get; } = new List<Diag>();
            public string Raw { get; set; } = "";
            public string Summary => Ok ? "C#5 OK" : (Items.Count + "건 오류");
        }

        /// <summary>
        /// 생성 소스를 컴파일 검증한다. 성공하면 Ok=true. DLL 은 임시 폴더에 만들고 지운다(실제 배포에는 쓰지 않는다).
        /// </summary>
        public static Result Verify(WindBotProject? project, string source, string className)
        {
            var r = new Result();
            if (project == null) { r.Items.Add(new Diag { Code = "VERIFIER", Message = "WindBot 폴더가 열려 있지 않습니다" }); return r; }
            if (!File.Exists(project.ExecutorBaseDll)) { r.Items.Add(new Diag { Code = "VERIFIER", Message = "ExecutorBase.dll 이 없습니다" }); return r; }

            var dir = Path.Combine(Path.GetTempPath(), "wbverify-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                Directory.CreateDirectory(dir);
                var srcPath = Path.Combine(dir, className + ".cs");
                var outPath = Path.Combine(dir, className + ".dll");
                File.WriteAllText(srcPath, source ?? "", new UTF8Encoding(false));

                var args = new StringBuilder();
                args.Append("/nologo /target:library /platform:x86 ");
                args.Append($"/out:\"{outPath}\" ");
                args.Append($"/reference:\"{project.ExecutorBaseDll}\" ");
                if (File.Exists(project.YgoSharpOcgDll)) args.Append($"/reference:\"{project.YgoSharpOcgDll}\" ");
                if (File.Exists(project.YgoSharpNetDll)) args.Append($"/reference:\"{project.YgoSharpNetDll}\" ");
                if (File.Exists(project.WindBotExe)) args.Append($"/reference:\"{project.WindBotExe}\" ");
                args.Append("/reference:System.Runtime.Serialization.dll ");
                args.Append($"\"{srcPath}\"");

                var psi = new ProcessStartInfo
                {
                    FileName = PluginBuilder.CscPath,
                    Arguments = args.ToString(),
                    WorkingDirectory = project.Root,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };

                using var p = Process.Start(psi);
                if (p == null) { r.Items.Add(new Diag { Code = "VERIFIER", Message = "csc 실행 실패" }); return r; }
                var stdout = p.StandardOutput.ReadToEnd();
                var stderr = p.StandardError.ReadToEnd();
                p.WaitForExit();
                var log = (stdout + "\n" + stderr).Trim();
                r.Raw = log;
                r.Ok = p.ExitCode == 0 && File.Exists(outPath);
                if (!r.Ok) Parse(log, srcPath, r.Items);
                if (!r.Ok && r.Items.Count == 0)
                    r.Items.Add(new Diag { Code = "VERIFIER", Message = string.IsNullOrWhiteSpace(log) ? "csc 실패(로그 없음)" : log.Split('\n')[0] });
            }
            catch (Exception ex)
            {
                r.Items.Add(new Diag { Code = "VERIFIER", Message = ex.GetType().Name + ": " + ex.Message });
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
            return r;
        }

        /// <summary>`파일(줄,열): error CSxxxx: 메시지` 형태를 파싱</summary>
        private static void Parse(string log, string srcPath, List<Diag> into)
        {
            foreach (var raw in (log ?? "").Replace("\r\n", "\n").Split('\n'))
            {
                var m = Regex.Match(raw, @"^(?<f>[^(]+)\((?<l>\d+),(?<c>\d+)\):\s*error\s+(?<code>CS\d+):\s*(?<msg>.+)$");
                if (!m.Success) continue;
                var file = m.Groups["f"].Value.Trim();
                if (!string.Equals(Path.GetFileName(file), Path.GetFileName(srcPath), StringComparison.OrdinalIgnoreCase)) continue;
                into.Add(new Diag
                {
                    Line = int.Parse(m.Groups["l"].Value),
                    Column = int.Parse(m.Groups["c"].Value),
                    Code = m.Groups["code"].Value,
                    Message = m.Groups["msg"].Value.Trim()
                });
                if (into.Count >= 40) break;
            }
        }

        /// <summary>2026-11-02: 안드로이드에서 문제가 되는 API 1건</summary>
        public sealed class Hit
        {
            public int Line { get; set; }
            public string Note { get; set; } = "";
            public string Excerpt { get; set; } = "";
            public override string ToString() =>
                (Line > 0 ? "L" + Line + " " : "") + Note + (Excerpt.Length > 0 ? "  ← " + Excerpt : "");
        }

        /// <summary>
        /// 2026-11-02: **Windows/Android 둘 다** 돌아가야 하므로, 안드로이드 트리밍 BCL 에 없는 API 를 찾는다.
        /// 결과는 경고가 아니라 **빌드 게이트**로 쓴다(AiProfile.AndroidSafeCheck).
        /// </summary>
        public static List<Hit> ScanForbiddenApis(string source)
        {
            var hits = new List<Hit>();
            if (string.IsNullOrEmpty(source)) return hits;

            // 주석은 제외 — 생성 코드 주석의 API 이름("없음: FileSystemInfo.Delete")을 오탐하지 않도록
            var raw = source.Replace("\r\n", "\n");
            var codeLines = raw.Split('\n');
            var code = new StringBuilder();
            var starts = new List<int>();
            var inBlock = false;
            foreach (var line in codeLines)
            {
                var l = line;
                if (inBlock)
                {
                    var e2 = l.IndexOf("*/", StringComparison.Ordinal);
                    if (e2 < 0) { l = ""; } else { l = l.Substring(e2 + 2); inBlock = false; }
                }
                var s2 = l.IndexOf("/*", StringComparison.Ordinal);
                if (s2 >= 0) { l = l.Substring(0, s2); inBlock = true; }
                var c2 = l.IndexOf("//", StringComparison.Ordinal);
                if (c2 >= 0) l = l.Substring(0, c2);
                starts.Add(code.Length);
                code.Append(l).Append('\n');
            }
            var text = code.ToString();

            var rules = new (string Pattern, string Note)[]
            {
                // 파일 IO — 안드로이드 트리밍 BCL 에 없음 (B-42 실측)
                (@"\bFile\.AppendAllText\s*\(", "File.AppendAllText 없음 → File.AppendText + StreamWriter"),
                (@"\bFile\.ReadAllText\s*\(", "File.ReadAllText 없음 → StreamReader"),
                (@"\bFile\.WriteAllText\s*\(", "File.WriteAllText 없음 → StreamWriter"),
                (@"\bFile\.ReadAllLines\s*\(", "File.ReadAllLines 없음"),
                (@"\bFile\.OpenWrite\s*\(", "File.OpenWrite 없음"),
                (@"\bPath\.GetTempPath\s*\(", "Path.GetTempPath 없음"),
                (@"\bPath\.GetRelativePath\s*\(", "Path.GetRelativePath 는 .NET Core 전용"),
                (@"\bFileSystemInfo\s*\.\s*Delete\s*\(", "FileSystemInfo.Delete 는 .NET 4.8 전용 → File.Delete"),
                (@"new\s+System\.IO\.FileInfo\s*\([^)]*\)\s*\.Delete\s*\(", "FileSystemInfo.Delete 는 .NET 4.8 전용 → File.Delete"),
                // LINQ — Any/OrderBy/FirstOrDefault 는 없음 (Where/ToList/Count/Contains/Select 는 있음)
                (@"\.Any\s*\(", "LINQ .Any() 없음 → .Count(...) > 0 또는 직접 루프"),
                (@"\.All\s*\(", "LINQ .All() 없음 → 직접 루프"),
                (@"\.OrderBy(Descending)?\s*\(", "LINQ .OrderBy() 없음 → List.Sort / 직접 비교"),
                (@"\.FirstOrDefault\s*\(", "LINQ .FirstOrDefault() 없음 → 직접 루프"),
                (@"\.First\s*\(", "LINQ .First() 없음 → 직접 루프"),
                (@"\.Select\s*\([^)]*\)\s*\.ToList\s*\(", "LINQ Select+ToList (동작은 하나 트리밍 위험)"),
                // 기타
                (@"\bArray\.Empty\s*<", "Array.Empty<T> 없음 → new T[0]"),
                (@"\bIReadOnly(List|Collection|Dictionary)\b", "IReadOnly* 없음"),
                (@"\bTask\.(Run|Delay|WaitAll)\s*\(", "Task 사용 불가(트리밍 + 스레드)"),
                (@"\bAppContext\.", "AppContext 없음"),
                (@"\basync\s+\w+\s+\w+\s*\(", "async 메서드 불가")
            };

            foreach (var (pattern, note) in rules)
            {
                foreach (Match m in Regex.Matches(text, pattern))
                {
                    var line = 1;
                    for (var i = 0; i < starts.Count; i++)
                    {
                        var next = i + 1 < starts.Count ? starts[i + 1] : text.Length;
                        if (m.Index >= starts[i] && m.Index < next) { line = i + 1; break; }
                    }
                    var excerpt = line - 1 < codeLines.Length ? codeLines[line - 1].Trim() : "";
                    if (excerpt.Length > 90) excerpt = excerpt.Substring(0, 90) + "…";
                    hits.Add(new Hit { Line = line, Note = note, Excerpt = excerpt });
                    if (hits.Count >= 40) return hits;
                }
            }
            return hits;
        }
    }
}
