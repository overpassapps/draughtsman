using Draughtsman.Core.Models;

namespace Draughtsman.Sidecar;

/// <summary>
/// The derived geometry of a sequence diagram (sidecar method <c>sequence.scene</c>), exactly as the editor computes it
/// (web/src/model/sequenceScene.ts, bundled by the sidecar so there is one implementation): a header box per
/// participant, a lifeline, a polyline per message (a note or an interaction use is a message row whose <see cref="SequenceMessage.Note"/> is
/// its box), the activation bars, the X that ends each destroyed lifeline, the combined-fragment frames and the whole
/// picture's bounds, in canvas units. A sequence document stores none of this; every renderer asks for it.
/// </summary>
public sealed record SequenceScene(
    IReadOnlyList<SequenceHeader> Headers,
    IReadOnlyList<SequenceLifeline> Lifelines,
    IReadOnlyList<SequenceMessage> Messages,
    IReadOnlyList<SequenceBar> Bars,
    IReadOnlyList<SequenceDestroy> Destroys,
    IReadOnlyList<SequenceFragment> Fragments,
    SceneRect? Bounds);

public sealed record SequenceHeader(string Id, double X, double Y, double W, double H, double Cx);

public sealed record SequenceLifeline(string Id, double X, double Y0, double Y1);

/// <summary>
/// One row: a message with its polyline from the sender's end to the receiver's (four points for a self message's
/// bracket) and where its label plate is centred, if it has a label; or a note, whose <see cref="Note"/> is the box drawn
/// across the lifelines it covers (its polyline is the box's outline and its label is centred in it, with no plate).
/// </summary>
public sealed record SequenceMessage(string Id, IReadOnlyList<Point> Points, double Y, bool Self, double RowTop, double RowBottom, Point? LabelAt = null, double? LabelWidth = null, SequenceNote? Note = null);

/// <summary>The box a note or an interaction use draws across the lifelines it covers; <see cref="TagWidth"/> is present only on an interaction use, whose <c>ref</c> tab is that wide, at its top left.</summary>
public sealed record SequenceNote(double X, double Y, double W, double H, double? TagWidth = null);

public sealed record SequenceBar(string Participant, string Opener, int Depth, double X, double Y, double W, double H);

public sealed record SceneRect(double X, double Y, double W, double H);

/// <summary>The X that ends a destroyed participant's lifeline, centred on (X, Y).</summary>
public sealed record SequenceDestroy(string Participant, double X, double Y);

/// <summary>One operand of a frame: <see cref="Y"/> is the top of its guard row (the frame's top for the first, where a dashed divider is drawn for the others) and <see cref="GuardAt"/> where the guard text starts, left-aligned and vertically centred.</summary>
public sealed record SequenceFragmentSection(int Operand, string Guard, double Y, Point GuardAt);

/// <summary>
/// A combined fragment's frame: its box, the operator tab at its top left (<see cref="TagWidth"/> by <see cref="TagHeight"/>,
/// the operator centred in it) and one section per operand. Frames arrive outermost first, so an inner one is drawn over
/// its parent. <see cref="Empty"/> marks a frame no message is a member of.
/// </summary>
public sealed record SequenceFragment(string Id, string Operator, double X, double Y, double W, double H, int Depth, double TagWidth, double TagHeight, IReadOnlyList<SequenceFragmentSection> Sections, bool Empty);
