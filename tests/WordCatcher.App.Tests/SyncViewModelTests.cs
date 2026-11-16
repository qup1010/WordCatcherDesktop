using System.Reflection;
using WordCatcher.App.ViewModels;
using WordCatcher.Core.Interfaces;
using WordCatcher.Core.Models;

namespace WordCatcher.App.Tests;

public class SyncViewModelTests
{
    private class WebDavProxy : DispatchProxy
    {
        public WebDavConflict? Conflict { get; set; }
        public string LastError { get; set; } = string.Empty;
        public WebDavFailureKind FailureKind { get; set; } = WebDavFailureKind.Conflict;

        protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name switch
        {
            "get_Conflict" => Conflict,
            "get_IsSyncing" => false,
            "get_LastError" => LastError,
            "get_LastSyncedAtUtc" => null,
            "get_HasPendingChanges" => false,
            "get_HasVerifiedSnapshot" => false,
            "get_FailureKind" => FailureKind,
            "add_StatusChanged" or "remove_StatusChanged" or
            "add_SyncCompleted" or "remove_SyncCompleted" or
            "add_SyncFailed" or "remove_SyncFailed" => null,
            _ => throw new NotSupportedException(method.Name)
        };
    }

    private static SyncViewModel Create(WebDavConflict? conflict)
    {
        var repo = DispatchProxy.Create<IWordRepository, InteractionTests.Proxy>();
        var queue = DispatchProxy.Create<IAnkiSyncQueue, InteractionTests.Proxy>();
        var settings = DispatchProxy.Create<ISettingsService, InteractionTests.Proxy>();
        var webDav = DispatchProxy.Create<IWebDavSyncService, WebDavProxy>();
        ((WebDavProxy)(object)webDav).Conflict = conflict;
        return new SyncViewModel(repo, queue, settings, webDav);
    }

    [Fact]
    public void BuildConflictDiffSeparatesChangedAndUnchangedFields()
    {
        const string localJson = """
        {
          "card": {
            "selection": "ephemeral",
            "sentence": "Fame is ephemeral.",
            "pageTitle": "Example Article",
            "entry": { "definition": "短暂的", "reading": "ɪˈfemərəl", "partOfSpeech": "adj." }
          },
          "edits": { "definition": "转瞬即逝的", "note": "本地补充笔记" }
        }
        """;
        const string remoteJson = """
        {
          "deletedAt": "2026-11-15T00:00:00Z",
          "card": {
            "selection": "ephemeral",
            "sentence": "Fame is ephemeral.",
            "pageTitle": "Example Article",
            "entry": { "definition": "短暂的", "reading": "ɪˈfemərəl", "partOfSpeech": "adj." }
          },
          "edits": { "definition": "短暂的", "note": "" }
        }
        """;

        var vm = Create(new WebDavConflict("rec-1", localJson, remoteJson));

        Assert.True(vm.HasWebDavConflict);
        Assert.True(vm.HasConflictChangedFields);
        Assert.True(vm.HasConflictUnchangedFields);
        Assert.Contains(vm.ConflictChangedFields, f => f.Label == "状态" && f.RemoteDisplay.Contains("已删除"));
        Assert.Contains(vm.ConflictChangedFields, f => f.Label == "释义" && f.LocalDisplay == "转瞬即逝的" && f.RemoteDisplay == "短暂的");
        Assert.Contains(vm.ConflictChangedFields, f => f.Label == "备注" && f.LocalDisplay == "本地补充笔记" && f.RemoteDisplay == "（空）");
        Assert.DoesNotContain(vm.ConflictChangedFields, f => f.Label == "表达");
        Assert.Contains(vm.ConflictUnchangedFields, f => f.Label == "表达" && f.LocalDisplay == "ephemeral");
        Assert.DoesNotContain(vm.ConflictUnchangedFields, f => f.Label == "来源链接");
        Assert.Contains("3 项差异已高亮", vm.ConflictSummaryText);
        Assert.Contains("本地补充笔记", vm.LocalConflictText);
        Assert.Contains("已删除", vm.RemoteConflictText);
    }

    [Fact]
    public void BuildConflictDiffDetectsExpressionUsageNoteAndOffsetDifferences()
    {
        const string localJson = """
        {
          "card": {
            "selection": "ephemeral",
            "sentence": "Fame is ephemeral.",
            "sentenceOffset": 8,
            "entry": { "expression": "be ephemeral", "usageNote": "正式书面语", "keyExplanation": "原句解析" }
          }
        }
        """;
        const string remoteJson = """
        {
          "card": {
            "selection": "ephemeral",
            "sentence": "Fame is ephemeral.",
            "sentenceOffset": 8,
            "entry": { "expression": "ephemeral", "usageNote": "常见搭配", "keyExplanation": "原句解析" }
          }
        }
        """;

        var vm = Create(new WebDavConflict("rec-2", localJson, remoteJson));

        Assert.Contains(vm.ConflictChangedFields, f => f.Label == "重点表达" && f.LocalDisplay == "be ephemeral");
        Assert.Contains(vm.ConflictChangedFields, f => f.Label == "用法提示" && f.RemoteDisplay == "常见搭配");
        Assert.Contains(vm.ConflictUnchangedFields, f => f.Label == "语境解析" && f.LocalDisplay == "原句解析");
        Assert.Contains("2 项差异已高亮", vm.ConflictSummaryText);
    }

    [Fact]
    public void InvalidConflictJsonFallsBackSafelyWithoutThrowing()
    {
        var vm = Create(new WebDavConflict("rec-bad", "{not-json", "{also-bad"));

        Assert.Empty(vm.ConflictChangedFields);
        Assert.Empty(vm.ConflictUnchangedFields);
        Assert.False(vm.HasConflictChangedFields);
        Assert.False(vm.HasConflictUnchangedFields);
    }
}
