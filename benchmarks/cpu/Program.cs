using System.Diagnostics;
using System.Net;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Plugins.UniversalForward;

namespace UniversalForward.CpuBenchmarks;

internal static class Program
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private const int Repetitions = 3;
    private static readonly byte[] RequestBody = "{\"model\":\"requested-bench\",\"reasoning\":{\"effort\":\"low\"}}"u8.ToArray();

    private static async Task Main(string[] args)
    {
        var variant = Argument(args, "--variant");
        var output = Argument(args, "--output");
        var maxClaude = int.TryParse(OptionalArgument(args, "--claude-max"), out var maximum) ? maximum : 10000;
        var cases = new List<CollectorInput>();
        foreach (var deltas in new[] { 2500, 5000, 10000 }.Where(value => value <= maxClaude)) cases.Add(ClaudeInput(deltas));
        foreach (var mebibytes in new[] { 1, 2, 4, 8 }) cases.Add(ResponsesInput(mebibytes * 1024 * 1024));
        var logInput = ResponsesEvent(512 * 1024);
        var results = new List<CaseResult>();
        foreach (var input in cases)
        {
            Console.WriteLine($"{variant}: warmup {input.Id}");
            Verify(await Collect(input), input.ExpectedSha256, input.Id);
            var samples = new List<Sample>();
            for (var iteration = 0; iteration < Repetitions; iteration++)
            {
                var sample = await MeasureCollector(input);
                samples.Add(sample);
                Console.WriteLine($"{variant}: {input.Id} sample {iteration + 1}: {sample.ElapsedMs:F3} ms, {sample.AllocatedBytes} allocated bytes");
            }
            results.Add(Result(input.Id, "collector", input.Bytes.Length, input.ChunkBytes, input.Deltas,
                input.ExpectedSha256, samples));
        }
        Console.WriteLine($"{variant}: warmup journal-sse-512KiB");
        _ = await MeasureLog(logInput, measured: false);
        var logSamples = new List<Sample>();
        LogEvidence? logEvidence = null;
        for (var iteration = 0; iteration < Repetitions; iteration++)
        {
            var measurement = await MeasureLog(logInput, measured: true);
            logSamples.Add(measurement.Sample);
            logEvidence = measurement.Evidence;
            Console.WriteLine($"{variant}: journal-sse-512KiB sample {iteration + 1}: {measurement.Sample.ElapsedMs:F3} ms, {measurement.Sample.AllocatedBytes} allocated bytes");
        }
        results.Add(Result("journal-sse-512KiB", "journal", logInput.Length, 512, null,
            Sha256(logInput), logSamples) with { Log = logEvidence });
        var plugin = typeof(ForwardStreamCollector).Assembly;
        var report = new RunResult(variant, DateTimeOffset.UtcNow, Environment.Version.ToString(),
            Environment.ProcessorCount, GCSettings(), Path.GetRelativePath(Path.GetDirectoryName(Path.GetFullPath(output))!, plugin.Location),
            plugin.GetName().Version!.ToString(), Sha256(await File.ReadAllBytesAsync(plugin.Location)),
            Repetitions, 1, results);
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(report, Json));
        Console.WriteLine($"Saved {output}");
    }

    private static string Argument(string[] args, string name) => OptionalArgument(args, name)
        ?? throw new ArgumentException($"Missing {name}");

    private static string? OptionalArgument(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static string GCSettings() => $"server={System.Runtime.GCSettings.IsServerGC};latency={System.Runtime.GCSettings.LatencyMode}";
    private static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static void Verify(byte[] actual, string expected, string id)
    {
        if (Sha256(actual) != expected) throw new InvalidOperationException($"Output hash mismatch in {id}");
    }

    private static CollectorInput ClaudeInput(int deltas)
    {
        const string start = "{\"type\":\"message_start\",\"message\":{\"id\":\"message-benchmark\",\"type\":\"message\",\"role\":\"assistant\",\"model\":\"claude-bench\",\"content\":[],\"usage\":{\"input_tokens\":1}}}";
        var deltaText = new string('x', 100);
        var delta = "data: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"" + deltaText + "\"}}\n\n";
        var input = new StringBuilder();
        input.Append("data: ").Append(start).Append("\n\n");
        input.Append("data: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}\n\n");
        for (var i = 0; i < deltas; i++) input.Append(delta);
        input.Append("data: {\"type\":\"content_block_stop\",\"index\":0}\n\n");
        input.Append("data: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"},\"usage\":{\"output_tokens\":1}}\n\n");
        input.Append("data: {\"type\":\"message_stop\"}\n\n");
        var expected = JsonNode.Parse(start)!["message"]!.DeepClone();
        expected["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = new string('x', 100 * deltas) });
        expected["usage"]!["output_tokens"] = 1;
        expected["stop_reason"] = "end_turn";
        return new($"claude-100char-{deltas}-deltas", "/v1/messages", Encoding.UTF8.GetBytes(input.ToString()), 16384,
            deltas, Sha256(JsonSerializer.SerializeToUtf8Bytes(expected)));
    }

    private static CollectorInput ResponsesInput(int bytes)
    {
        var input = ResponsesEvent(bytes);
        var payload = JsonNode.Parse(Encoding.UTF8.GetString(input, 6, input.Length - 8))!["response"]!;
        return new($"responses-single-line-{bytes / 1024 / 1024}MiB", "/v1/responses", input, 16384,
            null, Sha256(JsonSerializer.SerializeToUtf8Bytes(payload)));
    }

    private static byte[] ResponsesEvent(int targetBytes)
    {
        const string prefix = "data: {\"type\":\"response.completed\",\"response\":{\"id\":\"response-benchmark\",\"status\":\"completed\",\"model\":\"upstream-bench\",\"reasoning\":{\"effort\":\"high\"},\"output\":[{\"type\":\"message\",\"role\":\"assistant\",\"content\":[{\"type\":\"output_text\",\"text\":\"";
        const string suffix = "\"}]}],\"usage\":{\"input_tokens\":7,\"output_tokens\":9}}}\n\n";
        var textBytes = targetBytes - Encoding.UTF8.GetByteCount(prefix) - Encoding.UTF8.GetByteCount(suffix);
        return Encoding.UTF8.GetBytes(prefix + new string('x', textBytes) + suffix);
    }

    private static Task<byte[]> Collect(CollectorInput input) => ForwardStreamCollector.CollectAsync(
        Chunks(input.Bytes, input.ChunkBytes), input.Endpoint, CancellationToken.None);

    private static async IAsyncEnumerable<ReadOnlyMemory<byte>> Chunks(byte[] bytes, int size,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;
        for (var offset = 0; offset < bytes.Length; offset += size)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return bytes.AsMemory(offset, Math.Min(size, bytes.Length - offset));
        }
    }

    private static void ResetGc()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private static async Task<Sample> MeasureCollector(CollectorInput input)
    {
        ResetGc();
        using var process = Process.GetCurrentProcess();
        var beforeCpu = process.TotalProcessorTime;
        var allocated = GC.GetTotalAllocatedBytes(precise: true);
        var watch = Stopwatch.StartNew();
        var output = await Collect(input);
        watch.Stop();
        var allocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - allocated;
        var cpuMs = (process.TotalProcessorTime - beforeCpu).TotalMilliseconds;
        Verify(output, input.ExpectedSha256, input.Id);
        return new(watch.Elapsed.TotalMilliseconds, cpuMs, allocatedBytes, Sha256(output), output.Length);
    }

    private static async Task<(Sample Sample, LogEvidence Evidence)> MeasureLog(byte[] input, bool measured)
    {
        using var store = new RequestLogStore(queueCapacity: 4096);
        var capture = store.Begin("forward", "channel", "Benchmark", "requested-bench", "/v1/responses", "direct",
            "benchmark-trace", null, null, null) ?? throw new InvalidOperationException("Capture unavailable");
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://benchmark.invalid/v1/responses")
        { Content = new ByteArrayContent(RequestBody) };
        request.Content.Headers.ContentType = new("application/json");
        var attempt = capture.Sending(request, RequestBody);
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StreamContent(new ChunkedMemoryStream(input, 512)), RequestMessage = request };
        response.Content.Headers.ContentType = new("text/event-stream");
        capture.Received(attempt, response);
        await store.FlushAsync();
        var output = new byte[input.Length];
        var buffer = new byte[512];
        var stream = await response.Content.ReadAsStreamAsync();
        ResetGc();
        using var process = Process.GetCurrentProcess();
        var beforeCpu = process.TotalProcessorTime;
        var allocated = GC.GetTotalAllocatedBytes(precise: true);
        var watch = Stopwatch.StartNew();
        var offset = 0;
        while (true)
        {
            var count = await stream.ReadAsync(buffer.AsMemory());
            if (count == 0) break;
            buffer.AsSpan(0, count).CopyTo(output.AsSpan(offset));
            offset += count;
        }
        capture.Complete("completed", 200);
        await store.FlushAsync();
        watch.Stop();
        var allocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - allocated;
        var cpuMs = (process.TotalProcessorTime - beforeCpu).TotalMilliseconds;
        Verify(output, Sha256(input), "journal-sse-512KiB");
        if (offset != input.Length) throw new InvalidOperationException("Logged stream truncated downstream bytes");
        var detail = store.Detail(capture.Id) ?? throw new InvalidOperationException("Metadata missing");
        var saved = detail["parts"]!.AsArray().OfType<JsonObject>()
            .ToDictionary(part => part["name"]!.GetValue<string>(), part => part["savedBytes"]!.GetValue<long>());
        var responseBytes = saved.GetValueOrDefault("attempt-1-response");
        var requestBytes = saved.GetValueOrDefault("attempt-1-request");
        var metadataCorrect = detail["state"]!.GetValue<string>() == "completed"
            && detail["status"]!.GetValue<int>() == 200 && detail["upstreamStatus"]!.GetValue<int>() == 200
            && detail["trace"]!.GetValue<string>() == "benchmark-trace" && detail["firstByteMs"] is not null
            && detail["reportedModel"]!.GetValue<string>() == "upstream-bench"
            && detail["reportedReasoning"]!["reasoning.effort"]!.GetValue<string>() == "high"
            && detail["attempts"]![0]!["status"]!.GetValue<int>() == 200;
        if (!metadataCorrect) throw new InvalidOperationException("Journal metadata changed or went missing");
        var evidence = new LogEvidence(responseBytes, requestBytes, metadataCorrect,
            detail["reportedModel"]!.GetValue<string>(), detail["reportedReasoning"]!["reasoning.effort"]!.GetValue<string>(),
            detail["status"]!.GetValue<int>(), detail["tokenUsage"]?.DeepClone());
        return (new(watch.Elapsed.TotalMilliseconds, cpuMs, allocatedBytes, Sha256(output), output.Length), evidence);
    }

    private static CaseResult Result(string id, string category, int bytes, int chunkBytes, int? deltas,
        string expected, List<Sample> samples) => new(id, category, bytes, chunkBytes, deltas,
            Median(samples.Select(sample => sample.ElapsedMs)), Median(samples.Select(sample => sample.CpuMs)),
            (long)Median(samples.Select(sample => (double)sample.AllocatedBytes)), expected,
            samples.All(sample => sample.OutputSha256 == expected), samples, null);

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToArray();
        return sorted[sorted.Length / 2];
    }

    private sealed class ChunkedMemoryStream(byte[] bytes, int chunkSize) : Stream
    {
        private int _position;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => bytes.Length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            var count = Math.Min(Math.Min(buffer.Length, chunkSize), bytes.Length - _position);
            bytes.AsSpan(_position, count).CopyTo(buffer);
            _position += count;
            return count;
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Read(buffer.Span));
        }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed record CollectorInput(string Id, string Endpoint, byte[] Bytes, int ChunkBytes, int? Deltas, string ExpectedSha256);
    private sealed record Sample(double ElapsedMs, double CpuMs, long AllocatedBytes, string OutputSha256, int OutputBytes);
    private sealed record LogEvidence(long SavedResponseBodyBytes, long SavedRequestBodyBytes, bool MetadataCorrect,
        string ReportedModel, string ReportedEffort, int Status, JsonNode? TokenUsage);
    private sealed record CaseResult(string Id, string Category, int InputBytes, int ChunkBytes, int? Deltas,
        double MedianElapsedMs, double MedianCpuMs, long MedianAllocatedBytes, string ExpectedSha256,
        bool AllOutputsMatchExpected, List<Sample> Samples, LogEvidence? Log);
    private sealed record RunResult(string Variant, DateTimeOffset RunAtUtc, string RuntimeVersion, int ProcessorCount,
        string GcSettings, string PluginAssemblyPath, string PluginAssemblyVersion, string PluginAssemblySha256, int MeasuredRepetitions, int WarmupsPerCase,
        List<CaseResult> Cases);
}
