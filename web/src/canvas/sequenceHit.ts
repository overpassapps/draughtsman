/**
 * Hit-testing on a sequence diagram's derived scene (model/sequenceScene.ts). The connector tool attaches to a
 * participant, not to a port: a press or release on its header, or on its lifeline within `radius` of the line, is on
 * that participant, and the y of the pointer (not any port) says where in time the message goes.
 */

import type { SequenceScene } from '../model/sequenceScene.ts';
import type { Id, Point } from '../model/types.ts';

/** The participant under a world point: its header, or its lifeline within `radius`; the nearest lifeline wins an overlap. */
export function participantAt(scene: SequenceScene, at: Point, radius: number): Id | undefined {
  let best: { id: Id; distance: number } | undefined;
  for (const header of scene.headers) {
    const onHeader = at.x >= header.x - radius && at.x <= header.x + header.w + radius && at.y >= header.y - radius && at.y <= header.y + header.h + radius;
    const lifeline = scene.lifelines.find((candidate) => candidate.id === header.id);
    const onLifeline = lifeline !== undefined && Math.abs(at.x - lifeline.x) <= radius && at.y >= lifeline.y0 - radius && at.y <= lifeline.y1 + radius;
    const distance = Math.abs(at.x - header.cx);
    if ((onHeader || onLifeline) && (best === undefined || distance < best.distance)) {
      best = { id: header.id, distance };
    }
  }
  return best?.id;
}
