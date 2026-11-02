using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace WindBotAIEditor.Services
{
    /// <summary>
    /// Executor 파생 클래스를 생성하고 .NET Framework csc 로 컴파일해 Executors/*.dll 을 만든다.
    /// WindBot 은 시작 시 Executors/*.dll 을 Assembly.LoadFrom + GetTypes + Activator.CreateInstance 로 로드한다.
    /// </summary>
    public static class PluginBuilder
    {
        /// <summary>메타데이터에서 확인된 ExecutorType 값 (WindBot.Game.AI.ExecutorType)</summary>
        public static readonly string[] ExecutorTypes =
        {
            "Summon", "SpSummon", "Repos", "MonsterSet", "SpellSet",
            "Activate", "SummonOrSet", "GoToBattlePhase", "GoToMainPhase2", "GoToEndPhase"
        };

        /// <summary>.NET Framework 4.0 csc — WindBot(4.0) 대상 어셈블리를 만든다.</summary>
        public static string CscPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            @"Microsoft.NET\Framework\v4.0.30319\csc.exe");

        public class Registration
        {
            public string Type { get; set; } = "Summon";   // ExecutorType
            public int Code { get; set; }                  // 카드 ID
            public string Comment { get; set; } = "";
            public override string ToString() => $"{Type} / {Code}";
        }

        public class BuildRequest
        {
            public string EngineName { get; set; } = "MyDeck";
            public string Description { get; set; } = "";
            public List<Registration> Registrations { get; set; } = new List<Registration>();
            public string ExtraCode { get; set; } = "";
        }

        public static bool IsValidIdentifier(string name) =>
            !string.IsNullOrWhiteSpace(name) && Regex.IsMatch(name, @"^[A-Za-z_][A-Za-z0-9_]*$");

        public static string ClassName(string engineName) =>
            engineName.EndsWith("Executor", StringComparison.OrdinalIgnoreCase)
                ? engineName
                : engineName + "Executor";

        public static string GenerateSource(BuildRequest req)
        {
            var cls = ClassName(req.EngineName);
            var sb = new StringBuilder();
            sb.AppendLine("// WindBot AI Editor — generated executor");
            sb.AppendLine("// engine : " + cls);
            if (!string.IsNullOrWhiteSpace(req.Description))
                sb.AppendLine("// note   : " + req.Description.Replace("\n", " ").Replace("\r", " "));
            sb.AppendLine("using System;");
            sb.AppendLine("using System.Collections.Generic;");
            sb.AppendLine("using WindBot.Game;");
            sb.AppendLine("using WindBot.Game.AI;");
            sb.AppendLine();
            sb.AppendLine("namespace WindBot.Game.AI.Decks");
            sb.AppendLine("{");
            sb.AppendLine("    public class " + cls + " : Executor");
            sb.AppendLine("    {");
            sb.AppendLine("        public " + cls + "(GameAI ai, Duel duel) : base(ai, duel)");
            sb.AppendLine("        {");
            foreach (var r in req.Registrations)
            {
                if (!string.IsNullOrWhiteSpace(r.Comment))
                    sb.AppendLine("            // " + r.Comment.Replace("\r", " ").Replace("\n", " "));
                sb.AppendLine($"            AddExecutor(ExecutorType.{r.Type}, {r.Code});");
            }
            sb.AppendLine("        }");
            if (!string.IsNullOrWhiteSpace(req.ExtraCode))
            {
                sb.AppendLine();
                foreach (var line in req.ExtraCode.Replace("\r\n", "\n").Split('\n'))
                    sb.AppendLine("        " + line);
            }
            sb.AppendLine("    }");
            sb.AppendLine("}");
            return sb.ToString();
        }

        public class BuildResult
        {
            public bool Ok { get; set; }
            public string Log { get; set; } = "";
            public string OutputPath { get; set; } = "";
            public string SourcePath { get; set; } = "";
        }

        public static BuildResult Build(WindBotProject project, BuildRequest req)
        {
            return BuildFromSource(project, ClassName(req.EngineName), GenerateSource(req));
        }

        /// <summary>
        /// B-70 (2026-11-02): 신버전 호환 베이스 DLL 을 WindBot 폴더에 준비한다.
        /// <para>앱 옆 `compat\WindBotCompat.dll` 을 원본으로, WindBot 폴더에 없거나 내용이 다르면 복사한다.</para>
        /// </summary>
        /// <summary>csc 오류가 C# 6+ 문법 때문인지 판정한다.</summary>
        private static bool LooksLikeCSharp6(string log)
        {
            if (string.IsNullOrEmpty(log)) return false;
            foreach (var code in new[] { "CS1056", "CS1519", "CS1520", "CS1002", "CS1026", "CS1525", "CS0116", "CS1031" })
                if (log.IndexOf(code, StringComparison.Ordinal) >= 0) return true;
            return false;
        }

        public static string? EnsureCompatDll(WindBotProject project, out string note)
        {
            note = "";
            try
            {
                var target = Path.Combine(Path.GetDirectoryName(project.ExecutorBaseDll) ?? project.Root, "WindBotCompat.dll");
                var source = Path.Combine(AppContext.BaseDirectory, "compat", "WindBotCompat.dll");
                if (!File.Exists(source))
                {
                    note = "앱 폴더에 compat\\WindBotCompat.dll 이 없습니다: " + source;
                    return File.Exists(target) ? target : null;
                }
                var need = true;
                if (File.Exists(target))
                {
                    try { need = new FileInfo(target).Length != new FileInfo(source).Length; } catch { need = true; }
                }
                if (need)
                {
                    File.Copy(source, target, true);
                    note = "복사됨: " + target;
                }
                else note = "이미 최신";
                return target;
            }
            catch (Exception ex)
            {
                note = ex.Message;
                return null;
            }
        }

        /// <summary>이미 생성된 C# 소스를 Executors/*.dll 로 컴파일한다.</summary>
        public static BuildResult BuildFromSource(WindBotProject project, string className, string sourceText)
        {
            var result = new BuildResult();
            var cls = className;

            if (!IsValidIdentifier(cls))
                return new BuildResult { Ok = false, Log = "엔진 이름이 C# 식별자로 유효하지 않습니다: " + cls };

            if (!File.Exists(CscPath))
                return new BuildResult { Ok = false, Log = "csc.exe 를 찾을 수 없습니다: " + CscPath };

            if (!File.Exists(project.ExecutorBaseDll))
                return new BuildResult { Ok = false, Log = "ExecutorBase.dll 이 없습니다. WindBot 폴더를 확인하세요." };

            project.EnsureExecutorsDir();

            var workDir = Path.Combine(project.ExecutorsDir, "_src");
            Directory.CreateDirectory(workDir);

            var srcPath = Path.Combine(workDir, cls + ".cs");
            File.WriteAllText(srcPath, sourceText, new UTF8Encoding(true));

            var outPath = Path.Combine(project.ExecutorsDir, cls + ".dll");

            if (File.Exists(outPath))
            {
                var backup = outPath + ".bak_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");
                try { File.Move(outPath, backup); } catch { /* ignore */ }
            }

            var isCompatBuild = sourceText != null && sourceText.Contains("CompatDefaultExecutor");
            var args = new StringBuilder();
            args.Append("/nologo /target:library /platform:x86 /optimize+ ");
            args.Append($"/out:\"{outPath}\" ");
            args.Append($"/reference:\"{project.ExecutorBaseDll}\" ");
            // B-70: 호환 모드에서는 YGOSharp.OCGWrapper.dll 을 참조하지 않는다
            //   (ExecutorBase.dll 이 같은 형식(NamedCard 등)을 갖고 있어 CS0433 이 난다)
            var compatMode = isCompatBuild;
            if (!compatMode && File.Exists(project.YgoSharpOcgDll)) args.Append($"/reference:\"{project.YgoSharpOcgDll}\" ");
            if (File.Exists(project.YgoSharpNetDll)) args.Append($"/reference:\"{project.YgoSharpNetDll}\" ");
            args.Append("/reference:System.Runtime.Serialization.dll ");
            // B-70 (2026-11-02): 신버전 호환 베이스 — CompatDefaultExecutor 를 쓰는 소스면 참조를 추가한다
            if (sourceText != null && sourceText.Contains("CompatDefaultExecutor"))
            {
                var compatDll = EnsureCompatDll(project, out var compatNote);
                if (compatDll == null)
                    return new BuildResult { Ok = false, Log = "WindBotCompat.dll 을 준비하지 못했습니다: " + compatNote };
                args.Append($"/reference:\"{compatDll}\" ");
            }
            args.Append($"\"{srcPath}\"");

            var psi = new ProcessStartInfo
            {
                FileName = CscPath,
                Arguments = args.ToString(),
                WorkingDirectory = project.Root,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            try
            {
                using var p = Process.Start(psi)!;
                var stdout = p.StandardOutput.ReadToEnd();
                var stderr = p.StandardError.ReadToEnd();
                p.WaitForExit();

                result.Log = (stdout + "\n" + stderr).Trim();
                result.Ok = p.ExitCode == 0 && File.Exists(outPath);
                result.OutputPath = outPath;
                result.SourcePath = srcPath;
                if (!result.Ok && string.IsNullOrWhiteSpace(result.Log))
                    result.Log = "csc 종료 코드 " + p.ExitCode;

                // B-70 (2026-11-02): csc v4.0(C#5) 이 C#6+ 문법 때문에 실패하면 **Roslyn(C# 7.3)** 으로 다시 시도한다.
                //   (신버전 실행기 포팅 코드에 `?.` · 문자열 보간 · 식 본문 멤버가 흔하다)
                // 호환 모드는 csc(C#5) 오버로드 해석 차이(예: Enumerable.Sum 모호)도 있으므로 **항상** Roslyn 으로 재시도한다
                if (!result.Ok && (isCompatBuild || LooksLikeCSharp6(result.Log)))
                {
                    var isCompat = isCompatBuild;
                    var rosRefs = new List<string>(RoslynBuilder.FrameworkRefs());
                    rosRefs.Add(project.ExecutorBaseDll);
                    // 호환 모드에서는 YGOSharp.* 를 참조하지 않는다 — ExecutorBase.dll 이 같은 형식을 갖고 있어
                    // `YGOSharp.OCGWrapper.NamedCard` 가 두 어셈블리에 존재(CS0433)하게 된다.
                    if (!isCompat && File.Exists(project.YgoSharpOcgDll)) rosRefs.Add(project.YgoSharpOcgDll);
                    // Network 는 NamedCard 중복이 없어 항상 참조한다(실행기가 YGOSharp.Network 를 쓸 수 있다)
                    if (File.Exists(project.YgoSharpNetDll)) rosRefs.Add(project.YgoSharpNetDll);
                    if (isCompat)
                    {
                        var cd = EnsureCompatDll(project, out _);
                        if (cd != null) rosRefs.Add(cd);
                    }
                    var stagedPath = outPath + ".roslyn.tmp.dll";
                    string rosLog;
                    if (RoslynBuilder.TryBuild(new[] { srcPath }, rosRefs, stagedPath, cls, out rosLog))
                    {
                        try
                        {
                            if (File.Exists(outPath)) File.Delete(outPath);
                            File.Move(stagedPath, outPath);
                            result.Ok = true;
                            result.Log = result.Log + Environment.NewLine + "— csc(C#5) 실패 → Roslyn(C# 7.3) 로 빌드했습니다 —" + Environment.NewLine + rosLog;
                        }
                        catch (Exception exMove)
                        {
                            result.Log += Environment.NewLine + "Roslyn 결과 이동 실패: " + exMove.Message;
                        }
                    }
                    else
                    {
                        try { if (File.Exists(stagedPath)) File.Delete(stagedPath); } catch { }
                        result.Log = result.Log + Environment.NewLine + "— Roslyn 재시도 실패 —" + Environment.NewLine + rosLog;
                    }
                }

                // (2026-10-29) 빌드 스탬프 — 내보내기에서 'DLL 이 규칙보다 오래됐는지'를 **내용(해시)** 으로 판정한다
                //   (파일 수정시각만 보면 규칙을 저장만 해도 오탐이 난다)
                if (result.Ok)
                {
                    try
                    {
                        var rulesPath = Path.Combine(workDir, cls + ".rules.json");
                        var rulesSha = File.Exists(rulesPath) ? Sha256File(rulesPath) : "";
                        var srcSha = Sha256Text(sourceText);
                        var stampPath = Path.Combine(workDir, cls + ".build.json");
                        var json = "{" + Environment.NewLine +
                                   "  \"builtAt\": \"" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "\"," + Environment.NewLine +
                                   "  \"rulesSha256\": \"" + rulesSha + "\"," + Environment.NewLine +
                                   "  \"srcSha256\": \"" + srcSha + "\"" + Environment.NewLine +
                                   "}" + Environment.NewLine;
                        File.WriteAllText(stampPath, json, new UTF8Encoding(false));
                    }
                    catch { /* 스탬프 실패는 빌드 결과에 영향을 주지 않는다 */ }
                }
            }
            catch (Exception ex)
            {
                result.Ok = false;
                result.Log = "컴파일 실행 실패: " + ex.Message;
            }

            return result;
        }

        /// <summary>파일 SHA-256 (빌드 스탬프/오래됨 판정용).</summary>
        internal static string Sha256File(string path)
        {
            using var fs = File.OpenRead(path);
            using var sha = System.Security.Cryptography.SHA256.Create();
            return Convert.ToHexString(sha.ComputeHash(fs));
        }

        /// <summary>문자열 SHA-256 (소스 동일성 판정용).</summary>
        internal static string Sha256Text(string text)
        {
            using var sha = System.Security.Cryptography.SHA256.Create();
            return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(text ?? "")));
        }
    }
}


