using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using Silksong_Rando_Logic_Manager.Authentication;

namespace Silksong_Rando_Logic_Manager.Services;

public static partial class V3CatalogueHttp
{
    private static readonly JsonSerializerOptions SceneJson = new(JsonSerializerDefaults.Web)
    {
        RespectRequiredConstructorParameters = true,
        MaxDepth = 16,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow
    };
    internal sealed class ScenePayloadTooLargeException : Exception;

    private static void MapScene(RouteGroupBuilder api)
    {
        var scene = api.MapGroup("/scene-import").RequireAuthorization(AccessPolicies.Author);
        scene.MapPost("/preview", async (HttpContext context, SceneImportService service) =>
        {
            var request = await ReadSceneAsync<ScenePreviewRequest>(context);
            if (request.SceneName is null || request.IdentityKeys is null || request.IdentityKeys.Any(k => k is null) || request.DestinationKeys is null || request.DestinationKeys.Any(k => k is null) ||
                request.IdentityKeys.Distinct(StringComparer.Ordinal).Count() != request.IdentityKeys.Count ||
                request.DestinationKeys.Distinct(StringComparer.Ordinal).Count() != request.DestinationKeys.Count) throw new SceneTransportException();
            await WriteAsync(context, 200, await service.PreviewV3Async(request, context.RequestAborted));
        });
        scene.MapPost("/apply", async (HttpContext context, SceneImportService service) =>
        {
            var request = await ReadSceneAsync<SceneApplyRequest>(context);
            var receipt = await service.ApplyV3Async(request);
            await WriteCacheResultAsync(context, 200, receipt);
        });
        scene.MapPost("/inverse/prepare", async (HttpContext context, TransitionInverseSetupService service) =>
        {
            var request = await ReadSceneAsync<SceneInversePrepare>(context);
            if (request.SourceId == Guid.Empty) throw new SceneTransportException();
            await WriteAsync(context, 200, await service.PrepareSceneAsync(request.SourceId, context.RequestAborted));
        }).WithMetadata(new InverseSceneOperation());
        scene.MapPost("/inverse/confirm", async (HttpContext context, TransitionInverseSetupService service) =>
        {
            var request = await ReadSceneAsync<SceneInverseConfirm>(context);
            if (request.SourceId == Guid.Empty || string.IsNullOrEmpty(request.Baseline)) throw new SceneTransportException();
            var result = await service.ConfirmSceneAsync(request);
            await WriteCacheResultAsync(context, 200, result);
        }).WithMetadata(new InverseSceneOperation());
    }

    internal sealed record InverseSceneOperation;

    private static async Task<T> ReadSceneAsync<T>(HttpContext context)
    {
        if (!context.Request.HasJsonContentType()) throw new BadHttpRequestException("Expected JSON.", 415);
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } feature)
            feature.MaxRequestBodySize = null; // The counting stream below, including chunked bytes, is authoritative.
        if (context.Request.ContentLength > SceneDumpParser.MaximumBytes) throw new ScenePayloadTooLargeException();
        using var stream = new SceneBudgetStream(context.Request.Body);
        T? result = default;
        var malformed = false;
        try { result = await JsonSerializer.DeserializeAsync<T>(stream, SceneJson, context.RequestAborted); }
        catch (JsonException) { malformed = true; }
        // Also drain on malformed input. An admitted prefix/trailing body cannot evade the byte ceiling.
        var buffer = new byte[8192];
        while (await stream.ReadAsync(buffer, context.RequestAborted) != 0) { }
        if (malformed || result is null) throw new SceneTransportException();
        return result;
    }

    private sealed class SceneBudgetStream(Stream inner) : Stream
    {
        private long consumed;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => consumed; set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            var remaining = SceneDumpParser.MaximumBytes - consumed;
            var read = await inner.ReadAsync(buffer[..(int)Math.Min(buffer.Length, remaining + 1)], token);
            consumed += read;
            if (consumed > SceneDumpParser.MaximumBytes) throw new ScenePayloadTooLargeException();
            return read;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token) => ReadAsync(buffer.AsMemory(offset, count), token).AsTask();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
