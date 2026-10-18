import type { RouteBatchMessage, RouteNode, RouteRequest, RouteResult } from './routingProtocol.ts';
import { RoutingClient } from './routingWorker.ts';

/** A port that records what the client posts, standing in for the Worker. */
function fakePort(): { posted: RouteBatchMessage[]; terminated: () => boolean; port: { post: (message: RouteBatchMessage) => void; terminate: () => void } } {
  const posted: RouteBatchMessage[] = [];
  let terminated = false;
  return {
    posted,
    terminated: () => terminated,
    port: {
      post: (message) => {
        posted.push(message);
      },
      terminate: () => {
        terminated = true;
      },
    },
  };
}

const nodes: RouteNode[] = [
  { id: 'left', bounds: { x: 0, y: 0, w: 100, h: 50 } },
  { id: 'right', bounds: { x: 300, y: 0, w: 100, h: 50 } },
];

function request(edgeId: string): RouteRequest {
  return {
    edgeId,
    start: { point: { x: 100, y: 25 }, side: 'right', nodeId: 'left' },
    end: { point: { x: 300, y: 25 }, side: 'left', nodeId: 'right' },
  };
}

/** Every node bounds rect anywhere in a posted message, however it is nested. */
function countRects(value: unknown): number {
  if (Array.isArray(value)) {
    return value.reduce((sum: number, entry) => sum + countRects(entry), 0);
  }
  if (value === null || typeof value !== 'object') {
    return 0;
  }
  const isRect = ['x', 'y', 'w', 'h'].every((key) => key in value);
  return (isRect ? 1 : 0) + Object.values(value).reduce((sum: number, entry) => sum + countRects(entry), 0);
}

function result(edgeId: string, marker: number): RouteResult {
  return { edgeId, route: { status: 'routed', points: [{ x: marker, y: 0 }] } };
}

describe('RoutingClient', () => {
  it('posts each call as a numbered batch and resolves it with the worker reply', async () => {
    const { posted, port } = fakePort();
    const client = new RoutingClient(port);
    const pending = client.route(nodes, [request('a')]);
    expect(posted).toHaveLength(1);
    const batch = posted[0]?.batch ?? -1;
    expect(posted[0]?.requests.map((entry) => entry.edgeId)).toEqual(['a']);
    client.receive({ type: 'routed', batch, results: [result('a', 1)] });
    await expect(pending).resolves.toEqual([result('a', 1)]);
  });

  it('posts the node set once per batch, however many edges the batch routes', () => {
    const { posted, port } = fakePort();
    const client = new RoutingClient(port);
    void client.route(nodes, [request('a')]);
    void client.route(nodes, Array.from({ length: 50 }, (_, index) => request(`e${String(index)}`)));
    expect(posted.map((message) => message.requests.length)).toEqual([1, 50]);
    expect(posted.map(countRects)).toEqual([nodes.length, nodes.length]);
  });

  it('leaves out of a stale batch the edges a newer call superseded, and delivers them from the newer one', async () => {
    const { posted, port } = fakePort();
    const client = new RoutingClient(port);
    const older = client.route(nodes, [request('a'), request('b')]);
    const newer = client.route(nodes, [request('b')]);
    const [olderBatch, newerBatch] = posted.map((message) => message.batch);
    expect(olderBatch).not.toBe(newerBatch);
    client.receive({ type: 'routed', batch: olderBatch ?? -1, results: [result('a', 1), result('b', 1)] });
    client.receive({ type: 'routed', batch: newerBatch ?? -1, results: [result('b', 2)] });
    await expect(older).resolves.toEqual([result('a', 1)]);
    await expect(newer).resolves.toEqual([result('b', 2)]);
  });

  it('rejects a batch the worker reports as failed, without disturbing the others', async () => {
    const { posted, port } = fakePort();
    const client = new RoutingClient(port);
    const failing = client.route(nodes, [request('a')]);
    const fine = client.route(nodes, [request('b')]);
    client.receive({ type: 'failed', batch: posted[0]?.batch ?? -1, message: 'grid index out of range' });
    client.receive({ type: 'routed', batch: posted[1]?.batch ?? -1, results: [result('b', 1)] });
    await expect(failing).rejects.toThrow('grid index out of range');
    await expect(fine).resolves.toEqual([result('b', 1)]);
  });

  it('ignores a reply for a batch it is not waiting on', async () => {
    const { posted, port } = fakePort();
    const client = new RoutingClient(port);
    const pending = client.route(nodes, [request('a')]);
    const batch = posted[0]?.batch ?? -1;
    client.receive({ type: 'routed', batch: batch + 100, results: [result('a', 9)] });
    client.receive({ type: 'routed', batch, results: [result('a', 1)] });
    await expect(pending).resolves.toEqual([result('a', 1)]);
  });

  it('rejects everything waiting when the worker errors', async () => {
    const { port } = fakePort();
    const client = new RoutingClient(port);
    const pending = client.route(nodes, [request('a')]);
    client.fail('script failed to load');
    await expect(pending).rejects.toThrow('script failed to load');
  });

  it('stops a failed worker and rejects later batches without posting them', async () => {
    const { posted, port, terminated } = fakePort();
    const client = new RoutingClient(port);
    client.fail('script failed to load');
    expect(terminated()).toBe(true);
    await expect(client.route(nodes, [request('a')])).rejects.toThrow('script failed to load');
    expect(posted).toHaveLength(0);
  });

  it('terminates the worker on dispose, rejects what was waiting and refuses new batches', async () => {
    const { posted, port, terminated } = fakePort();
    const client = new RoutingClient(port);
    const pending = client.route(nodes, [request('a')]);
    expect(terminated()).toBe(false);
    client.dispose();
    expect(terminated()).toBe(true);
    await expect(pending).rejects.toThrow('disposed');
    await expect(client.route(nodes, [request('b')])).rejects.toThrow('disposed');
    expect(posted).toHaveLength(1);
  });
});
