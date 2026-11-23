using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using CommandTray.Sync;

namespace CommandTray.Tests;

public sealed class WorkspaceSyncJournalTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PullPreservesEditsSavedWhileWaitingForTheRelay(bool overlap)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var directory = Directory.CreateTempSubdirectory("commandtray-live-sync-");
        try
        {
            var baseline = new JsonObject
            {
                ["spaces"] = new JsonArray(new JsonObject
                {
                    ["id"] = "work",
                    ["name"] = "Work",
                    ["items"] = new JsonArray()
                })
            };
            var remote = (JsonObject)baseline.DeepClone();
            remote["spaces"]![0]!["name"] = "Relay rename";
            var local = (JsonObject)baseline.DeepClone();
            var workspaceLock = new object();
            var journal = new WorkspaceSyncJournal(Path.Combine(directory.FullName, "sync.json"), workspaceLock, () => local);
            journal.SetPairingBaseline(5, baseline);
            using var handler = new PausedRelay(remote);
            using var http = new HttpClient(handler);
            var client = new RelayClient(new Uri("http://localhost"), http: http);
            var pull = journal.SynchronizeAsync(client, (JsonObject)local.DeepClone(), 5, push: false,
                (document, _) => local = document, timeout.Token);
            await handler.Started.Task.WaitAsync(timeout.Token);
            lock (workspaceLock)
            {
                if (overlap) local["spaces"]![0]!["name"] = "Local rename";
                else local["spaces"]![0]!["items"]!.AsArray().Add(new JsonObject { ["id"] = "note", ["kind"] = "note" });
            }
            handler.Release.SetResult();
            var result = await pull;

            if (overlap)
            {
                Assert.Equal("Local rename", (string?)local["spaces"]![0]!["name"]);
                var conflict = Assert.IsType<WorkspaceSyncConflict>(journal.ReadConflict());
                Assert.True(JsonNode.DeepEquals(local, conflict.Local));
                Assert.True(JsonNode.DeepEquals(remote, conflict.Remote));
            }
            else
            {
                Assert.Null(result.Conflict);
                Assert.Equal(6, result.Revision);
                Assert.Equal("Relay rename", (string?)local["spaces"]![0]!["name"]);
                Assert.Equal("note", (string?)local["spaces"]![0]!["items"]![0]!["id"]);
            }
        }
        finally { directory.Delete(recursive: true); }
    }

    private sealed class PausedRelay(JsonObject document) : HttpMessageHandler
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("/v1/workspace/changes?after=5", request.RequestUri?.PathAndQuery);
            Started.SetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new { revision = 6, document })
            };
        }
    }
}
