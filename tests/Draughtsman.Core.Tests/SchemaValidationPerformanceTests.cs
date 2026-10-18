using System.Diagnostics;
using Draughtsman.Core.Serialization;
using Draughtsman.Tests.Shared;
using Xunit.Abstractions;

namespace Draughtsman.Core.Tests;

/// <summary>
/// The document REST API validates every body it is sent, so the schema validator sits on the path of every save. These measure it on the two
/// shapes that matter (the 2,000-node benchmark document, and a stream of small documents like the ones the editor and agents write) and report the
/// numbers. The benchmark document fails only on a gross regression, never on the absolute time (AGENTS.md "Load-robust tests"); the stream counts the
/// bytes allocated and times nothing, so a busy machine fails neither and a validator that got ten times slower (or grew with its input) still does. See docs/adr/0004-document-schema-validator.md for the numbers before and after the
/// schema library was replaced.
/// </summary>
public sealed class SchemaValidationPerformanceTests(ITestOutputHelper output)
{
    private const int StreamLength = 10_000;

    [Fact]
    public void Validating_the_2000_node_benchmark_document_stays_fast()
    {
        string json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "contract", "perf-2000n-2500e.json"));
        Assert.Empty(DocumentSchema.Validate(json));
        for (int i = 0; i < 3; i++)
        {
            DocumentSchema.Validate(json);
        }

        double[] runs = Measure(15, () => DocumentSchema.Validate(json));
        output.WriteLine($"2,000 nodes / 2,500 edges ({json.Length / 1024} KiB): min {runs[0]:F1} ms, median {runs[runs.Length / 2]:F1} ms over {runs.Length} runs");

        Assert.True(runs[runs.Length / 2] < 150 * LoadScale.Now(), $"validating the benchmark document took a median of {runs[runs.Length / 2]:F1} ms");
    }

    [Fact]
    public void Validating_a_stream_of_ten_thousand_small_documents_stays_fast()
    {
        // Counted, never timed: this test failed on the Windows runner at a median of 8.1 s against a limit of 2.5 s times the load scale (best pass 3.4 s;
        // the Core and Server test projects run side by side on two vCPUs) although nothing had regressed, and flaked on a loaded Mac before that. The bytes
        // a validation allocates are the same on macOS, Linux and Windows (AGENTS.md "Load-robust tests") and do not depend on the load, so:
        //  - the bytes per document have a ceiling well above what one small document costs, and a floor that shows the validator really ran;
        //  - four times the documents allocate about four times the bytes (4.0 times measured), which a per-call cost that grows with the calls so far
        //    (a cache or a list that is never trimmed, state kept between documents) cannot do.
        // No wall-clock assertion remains. What a count of bytes cannot see is a regression that only burns CPU; the benchmark document's ceiling above
        // (scaled by LoadScale) is where that shows.
        string template = ContractFiles.ReadExample();
        string[] stream = [.. Enumerable.Range(0, StreamLength).Select(i => template.Replace("doc_order_service", "doc_" + i, StringComparison.Ordinal))];
        for (int i = 0; i < 200; i++)
        {
            DocumentSchema.Validate(stream[i]);
        }

        const int Quarter = StreamLength / 4;
        long quarter = AllocatedBy(stream.AsSpan(0, Quarter));
        long whole = AllocatedBy(stream);
        double perDocument = (double)whole / StreamLength;
        output.WriteLine($"{StreamLength:N0} documents of {template.Length / 1024.0:F1} KiB: {perDocument:N0} bytes each ({whole:N0} in all; {Quarter:N0} documents took {quarter:N0}, so 4x the documents took {(double)whole / quarter:F2}x the bytes)");

        Assert.True(perDocument < 20.0 * template.Length, $"validating one {template.Length:N0}-character document allocated {perDocument:N0} bytes on average");
        Assert.True(perDocument > template.Length / 10.0, $"validating one {template.Length:N0}-character document allocated only {perDocument:N0} bytes on average: the documents were not validated");
        Assert.InRange((double)whole / quarter, 3.0, 5.0);
    }

    /// <summary>The bytes this thread allocates validating each document (every one valid) and nothing else.</summary>
    private static long AllocatedBy(ReadOnlySpan<string> documents)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        foreach (string document in documents)
        {
            if (DocumentSchema.Validate(document).Count != 0)
            {
                throw new InvalidOperationException("The stream's documents are valid.");
            }
        }

        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    /// <summary>Wall-clock milliseconds of each of <paramref name="count"/> runs, fastest first.</summary>
    private static double[] Measure(int count, Action action)
    {
        var times = new double[count];
        for (int i = 0; i < count; i++)
        {
            var timer = Stopwatch.StartNew();
            action();
            times[i] = timer.Elapsed.TotalMilliseconds;
        }

        Array.Sort(times);
        return times;
    }
}
