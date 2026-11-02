using System;
using System.Text.Json;

namespace WindBotAIEditor.Services
{
    /// <summary>
    /// 2026-11-02: 규칙 JSON **형식 판별** — 분석 덤프(`decks-json/*Executor.json`)를 규칙으로 오인해
    /// 조용히 "0규칙"으로 열리는 사고를 막는다.
    /// </summary>
    public static class ProfileJsonGuard
    {
        /// <summary>에디터 규칙 JSON 이면 true. 아니면 error 에 이유를 담는다.</summary>
        public static bool IsEditorProfile(string? text, out string error)
        {
            error = "";
            if (string.IsNullOrWhiteSpace(text)) { error = "내용이 비어 있습니다"; return false; }
            try
            {
                using var doc = JsonDocument.Parse(text);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) { error = "JSON 객체가 아닙니다"; return false; }

                var isDump = root.TryGetProperty("behaviors", out _) || root.TryGetProperty("deckName", out _)
                             || root.TryGetProperty("signalCounts", out _);
                var isProfile = root.TryGetProperty("actions", out _) || root.TryGetProperty("choices", out _)
                                || root.TryGetProperty("forbids", out _) || root.TryGetProperty("engine", out _)
                                || root.TryGetProperty("freePlay", out _);

                if (isDump && !isProfile)
                {
                    error = "분석 덤프(decks-json/*Executor.json)입니다 — 에디터 규칙 JSON이 아닙니다. " +
                            "원본 실행기(.cs)를 --scaffold 로 변환한 뒤 불러오세요";
                    return false;
                }
                if (!isProfile)
                {
                    error = "에디터 규칙 키(actions/choices/forbids/engine)가 없습니다";
                    return false;
                }
                return true;
            }
            catch (JsonException)
            {
                error = "JSON 형식이 올바르지 않습니다";
                return false;
            }
        }
    }
}
