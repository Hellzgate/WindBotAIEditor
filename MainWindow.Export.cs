using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using WindBotAIEditor.Models;
using WindBotAIEditor.Services;

namespace WindBotAIEditor
{
    public partial class MainWindow
    {
        /// <summary>선택한 봇을 배포용 ZIP 으로 내보낸다 (DLL + 덱 + 대사 + bots.json 항목 + 설치 스크립트).</summary>
        private void OnExportBot(object sender, RoutedEventArgs e)
        {
            if (_project == null) { MessageBox.Show("EDOPro 폴더를 먼저 여세요."); return; }
            CommitGrids();
            if (GridBots.SelectedItem is not BotEntry bot || string.IsNullOrWhiteSpace(bot.Name))
            {
                MessageBox.Show("봇 목록에서 내보낼 항목을 선택하세요.", "배포용 ZIP");
                return;
            }
            if (string.IsNullOrWhiteSpace(bot.Deck))
            {
                MessageBox.Show("이 봇에는 덱(엔진) 이름이 없습니다.", "배포용 ZIP");
                return;
            }

            // 기본 저장 폴더: 내 문서\WindBotBots (없으면 만든다)
            var exportDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "WindBotBots");
            try { Directory.CreateDirectory(exportDir); } catch { }

            var sfd = new SaveFileDialog
            {
                Title = "배포용 ZIP 저장 (이 파일을 상대방에게 보내면 됩니다)",
                FileName = BotPackageExporter.SafeFileName(bot.Name) + "-bot.zip",
                Filter = "ZIP 파일|*.zip",
                InitialDirectory = Directory.Exists(exportDir)
                    ? exportDir
                    : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            };
            if (sfd.ShowDialog() != true) return;

            var res = BotPackageExporter.Export(_project, bot, sfd.FileName);
            if (!res.Ok)
            {
                MessageBox.Show("내보내기 실패: " + res.Error, "배포용 ZIP");
                return;
            }

            // DLL 은 '마지막으로 컴파일된 결과'를 그대로 담는다 → 규칙 '내용'이 바뀌었으면 경고
            var stale = "";
            try
            {
                var warn = BotPackageExporter.StaleWarning(_project, bot);
                if (warn.Length > 0)
                    stale = "⚠ " + warn + Environment.NewLine + Environment.NewLine;
            }
            catch { }

            Status("배포용 ZIP 생성: " + res.ZipPath);
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = "/select,\"" + res.ZipPath + "\"",
                    UseShellExecute = true
                });
            }
            catch { }
            MessageBox.Show(
                stale + "배포용 ZIP 을 만들었습니다." + Environment.NewLine + Environment.NewLine + res.ZipPath + Environment.NewLine + Environment.NewLine +
                "포함된 파일:" + Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", res.Files) + Environment.NewLine + Environment.NewLine +
                "받는 사람은 ZIP 을 풀고 ① 설치.exe 더블클릭 또는 ② install.ps1 실행 또는 ③ 설치방법.txt 대로 수동 설치하면 됩니다." + Environment.NewLine +
                "안드로이드 EDOPro 는 설치.exe/ps1 없이 설치방법.txt 의 [안드로이드] 절대로 복사하면 됩니다.",
                "배포용 ZIP");
        }
    }
}

