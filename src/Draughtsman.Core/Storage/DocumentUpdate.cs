namespace Draughtsman.Core.Storage;

/// <summary>
/// One entry in the append-only Yjs update log. The bytes are opaque to the store and the server; only the sidecar
/// (ADR 0002) decodes them. Seq is dense and starts at 1 per document.
/// </summary>
public sealed record DocumentUpdate(string DocumentId, long Seq, byte[] Update, DateTimeOffset CreatedAt, Actor Actor);
