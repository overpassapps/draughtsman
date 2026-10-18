namespace Draughtsman.Mcp.Rendering;

/// <summary>
/// An image's bytes and content type, as <see cref="SvgRenderer"/> needs them to inline a node's asset as a data URI.
/// The resolver the caller supplies maps an image node's <c>props.assetId</c> to one of these; a null return (unknown
/// asset, no reader configured) leaves the node's placeholder frame in place.
/// </summary>
public sealed record AssetImage(string ContentType, byte[] Bytes);
