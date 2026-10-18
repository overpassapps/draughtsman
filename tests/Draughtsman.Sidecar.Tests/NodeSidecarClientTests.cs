using System.Diagnostics;
using Draughtsman.Core.Enums;
using Draughtsman.Core.Models;
using Draughtsman.Core.Serialization;
using Microsoft.Extensions.Logging.Abstractions;

namespace Draughtsman.Sidecar.Tests;

/// <summary>Integration tests against the real bundle (sidecar/sidecar.js next to the test binary) and the Node on PATH.</summary>
public sealed class NodeSidecarClientTests : IAsyncLifetime
{
    private readonly NodeSidecarClient _client = new(new SidecarOptions(), NullLogger<NodeSidecarClient>.Instance);

    public Task InitializeAsync()
    {
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _client.DisposeAsync();
    }

    [Fact]
    public async Task Ping_starts_the_sidecar_and_reports_it_running()
    {
        SidecarHealth health = await _client.PingAsync(CancellationToken.None);

        Assert.True(health.Ok);
        Assert.Matches(@"^\d+\.\d+\.\d+", health.Version);
        Assert.StartsWith("v", health.Node, StringComparison.Ordinal);
        SidecarStatus status = _client.Status;
        Assert.Equal(SidecarState.Running, status.State);
        Assert.Equal(health.Version, status.Version);
        Assert.Equal(0, status.Restarts);
        Assert.NotNull(status.ProcessId);
        Assert.EndsWith(SidecarLocator.BundleFileName, status.ScriptPath, StringComparison.Ordinal);
    }

    private static Document Checkout() => DocumentJson.Deserialize(File.ReadAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "contract", "checkout-sequence.json")));

    [Fact]
    public async Task SequenceScene_returns_the_editors_derived_scene_as_typed_records()
    {
        SequenceScene scene = await _client.SequenceSceneAsync(Checkout(), CancellationToken.None);

        // The numbers are worked out by hand in web/src/model/sequenceScene.test.ts.
        Assert.Equal(["p_user", "p_web", "p_api", "p_pay"], scene.Headers.Select(h => h.Id));
        Assert.Equal([80.0, 220.0, 360.0, 500.0], scene.Headers.Select(h => h.Cx));
        Assert.Equal(new SceneRect(0, 0, 580, 378), scene.Bounds);
        Assert.Equal(new[] { 108.0, 144, 180, 216, 252, 288, 326 }, scene.Messages.Select(m => m.Y));
        SequenceMessage self = Assert.Single(scene.Messages, m => m.Self);
        Assert.Equal("m_7", self.Id);
        Assert.Equal([new Point(360, 326), new Point(390, 326), new Point(390, 342), new Point(360, 342)], self.Points);
        Assert.Equal(new Point(150, 94), Assert.Single(scene.Messages, m => m.Id == "m_1").LabelAt);
        Assert.Equal([new SequenceBar("p_api", "m_2", 0, 355, 144, 10, 144), new SequenceBar("p_pay", "m_3", 0, 495, 180, 10, 36)], scene.Bars);
        Assert.Equal(new SequenceLifeline("p_api", 360, 60, 378), Assert.Single(scene.Lifelines, l => l.Id == "p_api"));
    }

    [Fact]
    public async Task SequenceScene_carries_the_frames_notes_and_destroys_of_a_milestone_two_document()
    {
        Document document = DocumentJson.Deserialize(File.ReadAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "contract", "checkout-fragments.json")));

        SequenceScene scene = await _client.SequenceSceneAsync(document, CancellationToken.None);

        // The numbers are worked out by hand in web/src/model/sequenceFragments.test.ts; the same literals are pinned here.
        SequenceFragment frame = Assert.Single(scene.Fragments);
        Assert.Equal(("f_1", "alt", 200.0, 198.0, 315.0, 160.0, 0, 31.0, 18.0, false), (frame.Id, frame.Operator, frame.X, frame.Y, frame.W, frame.H, frame.Depth, frame.TagWidth, frame.TagHeight, frame.Empty));
        Assert.Equal(
            [new SequenceFragmentSection(0, "[card ok]", 198, new Point(239, 207)), new SequenceFragmentSection(1, "[declined]", 292, new Point(208, 301))],
            frame.Sections);
        SequenceMessage note = Assert.Single(scene.Messages, m => m.Note is not null);
        Assert.Equal(("n_1", new SequenceNote(210, 166, 160, 28), new Point(290, 180), 162.0, 198.0), (note.Id, note.Note, note.LabelAt, note.RowTop, note.RowBottom));
        Assert.Equal([new SequenceDestroy("p_mail", 640, 506)], scene.Destroys);
        Assert.Equal(new SequenceHeader("p_mail", 580, 408, 120, 40, 640), Assert.Single(scene.Headers, h => h.Id == "p_mail"));
        Assert.Equal(new SceneRect(0, 0, 720, 544), scene.Bounds);
        // A message without a label (or a note-free one) leaves the optional parts absent and still deserialises.
        Assert.Null(Assert.Single(scene.Messages, m => m.Id == "m_1").Note);
    }

    [Fact]
    public async Task SequenceScene_refuses_a_document_of_another_type_as_a_client_fault()
    {
        SidecarRpcException ex = await Assert.ThrowsAsync<SidecarRpcException>(() => _client.SequenceSceneAsync(Example.ReadDocument(), CancellationToken.None));

        Assert.True(ex.IsClientFault);
        Assert.Contains("'sequence'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Layout_hands_a_sequence_document_back_untouched_without_starting_the_sidecar()
    {
        Document checkout = Checkout();

        LayoutResult result = await _client.LayoutAsync(checkout, cancellationToken: CancellationToken.None);

        Assert.Empty(result.Warnings);
        Assert.Same(checkout, result.Document);
        Assert.Equal(SidecarState.Starting, _client.Status.State);
    }

    [Fact]
    public async Task Route_runs_the_editors_router_and_answers_only_for_the_edges_it_routes()
    {
        Document example = Example.ReadDocument();
        Document orthogonal = example with
        {
            Edges = example.Edges.Select((e, i) => e with { Routing = new Routing(i == 0 ? RoutingMode.Straight : RoutingMode.Orthogonal) }).ToList(),
        };

        IReadOnlyDictionary<string, EdgeRoute> routes = await _client.RouteAsync(orthogonal, CancellationToken.None);

        // e_1 is straight and so is not routed; the others come back as axis-aligned polylines.
        Assert.Equal(orthogonal.Edges.Skip(1).Select(e => e.Id).Order(), routes.Keys.Order());
        foreach (EdgeRoute route in routes.Values)
        {
            Assert.Equal("routed", route.Status);
            Assert.True(route.Points.Count >= 2);
            for (int i = 1; i < route.Points.Count; i++)
            {
                Assert.True(route.Points[i].X == route.Points[i - 1].X || route.Points[i].Y == route.Points[i - 1].Y, "a route segment is not axis-aligned");
            }
        }
    }

    [Fact]
    public async Task Route_runs_from_the_row_of_a_column_port_to_the_row_it_names()
    {
        // The same two tables and rows as web/src/canvas/columnConnectors.test.ts: row centres are 42, 60 and 78 below a table's top.
        Node Table(string id, double x, double y, params string[] columns) =>
            new(id, "entity", id, "custom:er/entity") { Geometry = new Geometry(x, y, 270, 92), Props = new Dictionary<string, System.Text.Json.JsonElement> { ["columns"] = System.Text.Json.JsonSerializer.SerializeToElement(columns) } };
        Document Doc(string? fromPort) => Example.ReadDocument() with
        {
            Nodes = [Table("t_order", 0, 0, "PK Id int not null", "FK CustomerId int not null"), Table("t_customer", 500, 200, "PK Id int not null", "Name text null")],
            Edges = [new Edge("e_fk", "association", new Endpoint("t_order") { Port = fromPort }, new Endpoint("t_customer") { Port = "col:Id" }) { Routing = new Routing(RoutingMode.Orthogonal) }],
        };

        EdgeRoute onRow = (await _client.RouteAsync(Doc("col:CustomerId"), CancellationToken.None))["e_fk"];
        EdgeRoute floating = (await _client.RouteAsync(Doc("col:Nope"), CancellationToken.None))["e_fk"];

        Assert.Equal("routed", onRow.Status);
        Assert.Equal(new Point(270, 60), onRow.Points[0]);
        Assert.Equal(new Point(500, 242), onRow.Points[^1]);
        // A column the table lacks falls back to the table box: the end is not on that row (and the other end still is).
        Assert.NotEqual(60, floating.Points[0].Y);
        Assert.Equal(new Point(500, 242), floating.Points[^1]);
    }

    [Fact]
    public async Task Layout_fills_geometry_for_the_example_without_geometry()
    {
        LayoutResult result = await _client.LayoutAsync(Example.WithoutGeometry(), cancellationToken: CancellationToken.None);

        Assert.Empty(result.Warnings);
        Example.AssertWellFormedLayout(result.Document);
        Assert.Equal(["n_web", "c_vpc", "n_api", "n_db", "n_cache"], result.Document.Nodes.Select(n => n.Id));
        Node api = result.Document.Nodes.Single(n => n.Id == "n_api");
        Assert.Equal("c_vpc", api.Parent);
        Assert.Equal("platform", api.Props!["owner"].GetString());
        Assert.Equal(["in", "db", "cache"], api.Ports!.Select(p => p.Id));
        Assert.Equal("read/write", result.Document.Edges.Single(e => e.Id == "e_2").Label);
        Assert.NotNull(result.Document.Meta.AiProvenance);
    }

    [Fact]
    public async Task Layout_of_a_document_record_keeps_sizes_and_honours_options()
    {
        Document example = Example.ReadDocument();

        LayoutResult down = await _client.LayoutAsync(example, cancellationToken: CancellationToken.None);
        LayoutResult right = await _client.LayoutAsync(example, new LayoutOptions { Algorithm = LayoutAlgorithm.Layered, Direction = LayoutDirection.Right }, CancellationToken.None);

        Example.AssertWellFormedLayout(down.Document);
        Example.AssertWellFormedLayout(right.Document);
        Geometry web = Example.LaidOut(down.Document.Nodes.Single(n => n.Id == "n_web"));
        Assert.Equal(320, web.W);
        Assert.Equal(80, web.H);
        Geometry webRight = Example.LaidOut(right.Document.Nodes.Single(n => n.Id == "n_web"));
        Geometry apiRight = Example.LaidOut(right.Document.Nodes.Single(n => n.Id == "n_api"));
        Assert.True(webRight.X + webRight.W <= apiRight.X, "left-to-right layout puts the API to the right of the web app");
    }

    [Fact]
    public async Task Yjs_round_trip_is_byte_stable_against_the_C_sharp_serialiser()
    {
        Document original = Example.ReadDocument();

        byte[] state = await _client.YjsFromJsonAsync(original, CancellationToken.None);
        Document materialised = await _client.YjsToJsonAsync(state, cancellationToken: CancellationToken.None);

        Assert.NotEmpty(state);
        Assert.Equal(DocumentJson.Serialize(original), DocumentJson.Serialize(materialised));
    }

    [Fact]
    public async Task ApplyUpdates_from_nothing_reproduces_the_state()
    {
        Document original = Example.ReadDocument();
        byte[] state = await _client.YjsFromJsonAsync(original, CancellationToken.None);

        YjsState applied = await _client.ApplyUpdatesAsync(null, [state], CancellationToken.None);
        Document fromUpdates = await _client.YjsToJsonAsync(null, [applied.State], CancellationToken.None);

        Assert.NotEmpty(applied.StateVector);
        Assert.Equal(DocumentJson.Serialize(original), DocumentJson.Serialize(fromUpdates));
        await Assert.ThrowsAsync<ArgumentException>(() => _client.YjsToJsonAsync(null, [], CancellationToken.None));
    }

    [Fact]
    public async Task A_state_that_holds_no_document_is_reported_as_an_rpc_error_not_a_crash()
    {
        byte[] emptyYjsState = [0, 0];

        SidecarRpcException ex = await Assert.ThrowsAsync<SidecarRpcException>(() => _client.YjsToJsonAsync(emptyYjsState, cancellationToken: CancellationToken.None));

        Assert.Equal(SidecarRpcException.InvalidDocumentCode, ex.Code);
        Assert.True(ex.IsClientFault);
        Assert.Equal(SidecarState.Running, _client.Status.State);
        Assert.True((await _client.PingAsync(CancellationToken.None)).Ok);
    }

    [Fact]
    public async Task Concurrent_requests_are_multiplexed_over_one_process()
    {
        Document input = Example.WithoutGeometry();
        Task<LayoutResult>[] layouts = Enumerable.Range(0, 8).Select(_ => _client.LayoutAsync(input, cancellationToken: CancellationToken.None)).ToArray();
        Task<SidecarHealth> ping = _client.PingAsync(CancellationToken.None);

        LayoutResult[] results = await Task.WhenAll(layouts);
        SidecarHealth health = await ping;

        Assert.All(results, r => Example.AssertWellFormedLayout(r.Document));
        Assert.Equal(results[0].Document.Nodes.Select(n => n.Geometry), results[7].Document.Nodes.Select(n => n.Geometry));
        Assert.Equal(health.Pid, _client.Status.ProcessId);
    }

    [Fact]
    public async Task A_crashed_sidecar_is_restarted_with_backoff()
    {
        await _client.PingAsync(CancellationToken.None);
        int pid = _client.Status.ProcessId!.Value;

        Process.GetProcessById(pid).Kill();
        await WaitUntilAsync(() => _client.Status.State != SidecarState.Running);

        Assert.Equal(SidecarState.Unavailable, _client.Status.State);
        Assert.Contains("exited", _client.Status.Error, StringComparison.Ordinal);

        await WaitUntilAsync(() => _client.Status.State == SidecarState.Running);
        SidecarHealth health = await _client.PingAsync(CancellationToken.None);

        Assert.True(health.Ok);
        Assert.NotEqual(pid, health.Pid);
        Assert.Equal(SidecarState.Running, _client.Status.State);
        Assert.Equal(1, _client.Status.Restarts);
    }

    [Fact]
    public async Task A_timed_out_request_stops_the_process_and_the_next_request_recovers()
    {
        var options = new SidecarOptions { LayoutTimeout = TimeSpan.FromMilliseconds(1) };
        await using var client = new NodeSidecarClient(options, NullLogger<NodeSidecarClient>.Instance);

        await Assert.ThrowsAsync<SidecarTimeoutException>(() => client.LayoutAsync(Example.LargeChain(400), cancellationToken: CancellationToken.None));
        await WaitUntilAsync(() => client.Status.State != SidecarState.Running);
        await WaitUntilAsync(() => client.Status.State == SidecarState.Running);

        SidecarHealth health = await client.PingAsync(CancellationToken.None);

        Assert.True(health.Ok);
        Assert.Equal(1, client.Status.Restarts);
    }

    [Fact]
    public async Task Requests_during_the_backoff_fail_fast_instead_of_queueing()
    {
        string missing = Path.Combine(AppContext.BaseDirectory, "no-such-node");
        var options = new SidecarOptions { NodePath = missing, InitialRestartDelay = TimeSpan.FromSeconds(120), MaxRestartDelay = TimeSpan.FromSeconds(120) };
        await using var client = new NodeSidecarClient(options, NullLogger<NodeSidecarClient>.Instance);
        await Assert.ThrowsAsync<SidecarUnavailableException>(() => client.PingAsync(CancellationToken.None));

        var stopwatch = Stopwatch.StartNew();
        Task<SidecarHealth> first = client.PingAsync(CancellationToken.None);
        Task<SidecarHealth> second = client.PingAsync(CancellationToken.None);
        SidecarUnavailableException firstError = await Assert.ThrowsAsync<SidecarUnavailableException>(() => first);
        SidecarUnavailableException secondError = await Assert.ThrowsAsync<SidecarUnavailableException>(() => second);
        stopwatch.Stop();

        // Waiting out the 120 s backoff is the failure; an answer within a minute, however busy the host, is fail-fast.
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(60), $"two requests during a 120 s backoff took {stopwatch.Elapsed}");
        Assert.Contains("no-such-node", firstError.Message, StringComparison.Ordinal);
        Assert.Contains("no-such-node", secondError.Message, StringComparison.Ordinal);
        Assert.Equal(SidecarState.Unavailable, client.Status.State);
    }

    [Fact]
    public async Task A_process_that_never_answers_ping_is_recorded_once_with_the_ping_failure()
    {
        string exitsAtOnce = Path.Combine(Path.GetTempPath(), "draughtsman-tests", Guid.NewGuid().ToString("N") + ".js");
        Directory.CreateDirectory(Path.GetDirectoryName(exitsAtOnce)!);
        await File.WriteAllTextAsync(exitsAtOnce, "process.exit(3);", CancellationToken.None);
        try
        {
            var options = new SidecarOptions { ScriptPath = exitsAtOnce, InitialRestartDelay = TimeSpan.FromMilliseconds(200), MaxRestartDelay = TimeSpan.FromSeconds(30) };
            await using var client = new NodeSidecarClient(options, NullLogger<NodeSidecarClient>.Instance);

            SidecarUnavailableException ex = await Assert.ThrowsAsync<SidecarUnavailableException>(() => client.PingAsync(CancellationToken.None));
            await Task.Delay(300);

            Assert.Contains("did not answer ping", ex.Message, StringComparison.Ordinal);
            Assert.Equal(ex.Message, client.Status.Error);
            Assert.Equal(SidecarState.Unavailable, client.Status.State);
            Assert.Equal(0, client.Status.Restarts);
        }
        finally
        {
            File.Delete(exitsAtOnce);
        }
    }

    [Fact]
    public async Task A_cancelled_request_leaves_the_process_and_its_stream_usable()
    {
        await _client.PingAsync(CancellationToken.None);
        int pid = _client.Status.ProcessId!.Value;
        Document large = Example.LargeChain(400);
        List<CancellationTokenSource> sources = Enumerable.Range(0, 4).Select(i => new CancellationTokenSource(TimeSpan.FromMilliseconds(i))).ToList();
        try
        {
            List<Task<LayoutResult>> attempts = sources.Select(s => _client.LayoutAsync(large, cancellationToken: s.Token)).ToList();
            attempts.Add(_client.LayoutAsync(large, cancellationToken: new CancellationToken(canceled: true)));

            foreach (Task<LayoutResult> attempt in attempts)
            {
                try
                {
                    await attempt;
                }
                catch (OperationCanceledException)
                {
                    // Either outcome is fine; what matters is the process afterwards.
                }
            }

            SidecarHealth health = await _client.PingAsync(CancellationToken.None);

            Assert.Equal(pid, health.Pid);
            Assert.Equal(SidecarState.Running, _client.Status.State);
            Assert.Equal(0, _client.Status.Restarts);
        }
        finally
        {
            sources.ForEach(s => s.Dispose());
        }
    }

    [Fact]
    public async Task A_missing_node_executable_is_reported_not_thrown_from_Process_Start()
    {
        string missing = Path.Combine(AppContext.BaseDirectory, "no-such-node");
        await using var client = new NodeSidecarClient(new SidecarOptions { NodePath = missing }, NullLogger<NodeSidecarClient>.Instance);

        SidecarUnavailableException ex = await Assert.ThrowsAsync<SidecarUnavailableException>(() => client.PingAsync(CancellationToken.None));

        Assert.Contains("no-such-node", ex.Message, StringComparison.Ordinal);
        Assert.Equal(SidecarState.Unavailable, client.Status.State);
        Assert.Equal(ex.Message, client.Status.Error);
    }

    [Fact]
    public async Task A_missing_bundle_is_reported_with_the_places_it_looked()
    {
        string emptyBase = Path.Combine(Path.GetTempPath(), "draughtsman-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(emptyBase);
        try
        {
            await using var client = new NodeSidecarClient(new SidecarOptions { ScriptPath = Path.Combine(emptyBase, "nope.js") }, NullLogger<NodeSidecarClient>.Instance);

            SidecarUnavailableException ex = await Assert.ThrowsAsync<SidecarUnavailableException>(() => client.PingAsync(CancellationToken.None));

            Assert.Contains("nope.js", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(emptyBase, recursive: true);
        }
    }

    [Fact]
    public void Locator_finds_the_bundle_next_to_the_binary_and_node_on_path()
    {
        string script = SidecarLocator.ResolveScript(string.Empty, AppContext.BaseDirectory);
        string node = SidecarLocator.ResolveNode(string.Empty, AppContext.BaseDirectory);

        Assert.Equal(Path.Combine(AppContext.BaseDirectory, SidecarLocator.BundleFolder, SidecarLocator.BundleFileName), script);
        Assert.True(File.Exists(node), node);
        Assert.True(Path.IsPathRooted(node));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60); // a hang guard: the loop returns the moment the condition holds
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "condition was not met within 60 s");
            await Task.Delay(20);
        }
    }
}
