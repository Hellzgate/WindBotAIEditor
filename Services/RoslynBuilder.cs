using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;

namespace WindBotAIEditor.Services
{
    /// <summary>
    /// 2026-11-02 (B-70): **Roslyn 컴파일 대체 경로**.
    /// <para>기본 빌드는 .NET Framework 4.0 `csc.exe`(C# 5) 를 쓴다. 그런데 신버전 실행기를 포팅하면
    /// `?.` · 문자열 보간 · 식 본문 멤버 같은 **C# 6+ 문법**이 들어와 csc v4.0 이 거부한다.</para>
    /// <para>그럴 때만 Roslyn(C# 7.3) 으로 **같은 참조·같은 출력(x86 DLL)** 을 만든다.
    /// 런타임 API 는 그대로 .NET Framework 4.x 이므로 WindBot/Android Mono 에서 동작한다.</para>
    /// </summary>
    public static class RoslynBuilder
    {
        /// <summary>Roslyn 으로 컴파일한다(성공 시 true). refs 에는 mscorlib·System 등 전체 참조를 넘긴다.</summary>
        public static bool TryBuild(string[] sourceFiles, IEnumerable<string> refs, string outPath, string assemblyName, out string log)
        {
            var messages = new StringBuilder();
            try
            {
                var parseOptions = new CSharpParseOptions(LanguageVersion.Latest, DocumentationMode.None);
                var trees = new List<SyntaxTree>();
                foreach (var file in sourceFiles)
                {
                    if (!File.Exists(file)) { log = "소스 없음: " + file; return false; }
                    trees.Add(CSharpSyntaxTree.ParseText(File.ReadAllText(file), parseOptions, file, Encoding.UTF8));
                }

                var meta = new List<MetadataReference>();
                foreach (var r in refs)
                {
                    if (string.IsNullOrWhiteSpace(r) || !File.Exists(r)) continue;
                    meta.Add(MetadataReference.CreateFromFile(r));
                }

                var options = new CSharpCompilationOptions(
                    OutputKind.DynamicallyLinkedLibrary,
                    optimizationLevel: OptimizationLevel.Release,
                    platform: Platform.X86,
                    allowUnsafe: false,
                    deterministic: false);

                var comp = CSharpCompilation.Create(
                    string.IsNullOrWhiteSpace(assemblyName) ? Path.GetFileNameWithoutExtension(outPath) : assemblyName, trees, meta, options);

                using (var fs = new FileStream(outPath, FileMode.Create, FileAccess.Write))
                {
                    EmitResult res = comp.Emit(fs);
                    foreach (var d in res.Diagnostics)
                    {
                        if (d.Severity == DiagnosticSeverity.Error)
                            messages.AppendLine(Format(d));
                    }
                    if (!res.Success)
                    {
                        log = messages.Length > 0 ? messages.ToString() : "Roslyn 컴파일 실패(진단 없음)";
                        return false;
                    }
                }
                log = messages.Length > 0 ? messages.ToString() : "OK (Roslyn C# 7.3)";
                return true;
            }
            catch (Exception ex)
            {
                log = "Roslyn 예외: " + ex.GetType().Name + ": " + ex.Message;
                return false;
            }
        }

        private static string Format(Diagnostic d)
        {
            try
            {
                var span = d.Location.GetLineSpan();
                return "(" + (span.StartLinePosition.Line + 1) + "," + (span.StartLinePosition.Character + 1) + ") "
                       + d.Id + ": " + d.GetMessage(System.Globalization.CultureInfo.CurrentCulture);
            }
            catch { return d.Id + ": " + d.GetMessage(); }
        }

        /// <summary>.NET Framework 기준 어셈블리 경로들(있으면).</summary>
        public static List<string> FrameworkRefs()
        {
            var list = new List<string>();
            try
            {
                var dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                    @"Microsoft.NET\Framework\v4.0.30319");
                foreach (var name in new[] { "mscorlib.dll", "System.dll", "System.Core.dll", "System.Xml.dll",
                                             "System.Runtime.Serialization.dll", "System.Drawing.dll" })
                {
                    var p = Path.Combine(dir, name);
                    if (File.Exists(p)) list.Add(p);
                }
            }
            catch { }
            return list;
        }
    }
}
