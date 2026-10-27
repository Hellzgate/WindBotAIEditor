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

            var args = new StringBuilder();
            args.Append("/nologo /target:library /platform:x86 /optimize+ ");
            args.Append($"/out:\"{outPath}\" ");
            args.Append($"/reference:\"{project.ExecutorBaseDll}\" ");
            if (File.Exists(project.YgoSharpOcgDll)) args.Append($"/reference:\"{project.YgoSharpOcgDll}\" ");
            if (File.Exists(project.YgoSharpNetDll)) args.Append($"/reference:\"{project.YgoSharpNetDll}\" ");
            args.Append("/reference:System.Runtime.Serialization.dll ");
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
            }
            catch (Exception ex)
            {
                result.Ok = false;
                result.Log = "컴파일 실행 실패: " + ex.Message;
            }

            return result;
        }
    }
}
