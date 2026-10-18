using Draughtsman.Core.Models;

namespace Draughtsman.Core.Storage;

/// <summary>A document as stored together with its <see cref="DocumentVersionTag"/>: the tag a save of it must quote.</summary>
public sealed record StoredDocument(Document Document, string Version);
