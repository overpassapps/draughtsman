import orderService from '../../../docs/examples/order-service.json';
import { copy } from '../model/clipboard.ts';
import { parseDocument } from '../model/parse.ts';
import { createClipboardStore, parseClipboardText, type TextClipboard } from './systemClipboard.ts';

const document = parseDocument(orderService);
// n_api and n_db with e_2 between them; e_1 and e_3 each leave an endpoint behind and are not in the payload.
const payload = copy(document, ['n_api', 'n_db']);

/** A system clipboard shared the way two browser tabs share the OS one; `refuse` makes reads or writes throw. */
function fakeSystem(refuse: { read?: boolean; write?: boolean } = {}) {
  const state = { text: '' };
  const clipboard: TextClipboard = {
    readText: vi.fn(() => (refuse.read ? Promise.reject(new DOMException('Denied', 'NotAllowedError')) : Promise.resolve(state.text))),
    writeText: vi.fn((text: string) => {
      if (refuse.write) {
        return Promise.reject(new DOMException('Denied', 'NotAllowedError'));
      }
      state.text = text;
      return Promise.resolve();
    }),
  };
  return { state, clipboard };
}

describe('clipboard store', () => {
  it('carries a copy to another tab through the system clipboard, with nothing in that tab’s memory', async () => {
    const system = fakeSystem();
    const copyingTab = createClipboardStore(() => system.clipboard);
    const pastingTab = createClipboardStore(() => system.clipboard);
    await copyingTab.write(payload);
    // What went on the system clipboard is plain JSON text any app can hold.
    expect(JSON.parse(system.state.text)).toMatchObject({ format: 'draughtsman/selection', version: 1 });
    const read = await pastingTab.read();
    expect(read?.nodes.map((n) => n.id)).toEqual(['n_api', 'n_db']);
    expect(read?.edges.map((e) => e.id)).toEqual(['e_2']);
  });

  it('pastes nothing when the system clipboard holds anything else, even with a copy in memory', async () => {
    const system = fakeSystem();
    const store = createClipboardStore(() => system.clipboard);
    await store.write(payload);
    // The user copied some text elsewhere after copying in the editor.
    for (const text of ['hello', '', '{"format":"other","version":1,"nodes":[],"edges":[]}', '[1,2]', 'null']) {
      system.state.text = text;
      expect(await store.read(), text).toBeUndefined();
    }
    system.state.text = JSON.stringify(payload);
    expect(await store.read()).toBeDefined();
  });

  it('falls back to this tab’s last copy when reading the system clipboard is refused', async () => {
    const system = fakeSystem({ read: true });
    const store = createClipboardStore(() => system.clipboard);
    expect(await store.read()).toBeUndefined();
    await store.write(payload);
    expect((await store.read())?.nodes.map((n) => n.id)).toEqual(['n_api', 'n_db']);
  });

  it('pastes the text a paste event carried without asking the system clipboard, and nothing when it is not a selection', async () => {
    const system = fakeSystem();
    const store = createClipboardStore(() => system.clipboard);
    await store.write(payload);
    expect((await store.read(JSON.stringify(payload)))?.nodes.map((n) => n.id)).toEqual(['n_api', 'n_db']);
    expect(await store.read('copied elsewhere since')).toBeUndefined();
    expect(system.clipboard.readText).not.toHaveBeenCalled();
  });

  it('writes the DataTransfer of a copy event, not the Clipboard API, and keeps a copy in memory', async () => {
    const system = fakeSystem({ read: true });
    const store = createClipboardStore(() => system.clipboard);
    const data = new Map<string, string>();
    const transfer = { setData: (type: string, text: string) => data.set(type, text) } as unknown as DataTransfer;
    // Written before write returns: a copy event's DataTransfer is only writable while the event is dispatched.
    void store.write(payload, transfer);
    expect(parseClipboardText(data.get('text/plain') ?? '')?.nodes.map((n) => n.id)).toEqual(['n_api', 'n_db']);
    expect(system.clipboard.writeText).not.toHaveBeenCalled();
    expect((await store.read())?.nodes.map((n) => n.id)).toEqual(['n_api', 'n_db']);
  });

  it('keeps the copy in memory when writing the system clipboard is refused, and when there is no Clipboard API', async () => {
    const refused = fakeSystem({ write: true, read: true });
    const store = createClipboardStore(() => refused.clipboard);
    await expect(store.write(payload)).resolves.toBeUndefined();
    expect(refused.state.text).toBe('');
    expect((await store.read())?.edges.map((e) => e.id)).toEqual(['e_2']);
    // Text on the clipboard is the user's last copy even when this tab's write never got there.
    expect(await store.read('older text')).toBeUndefined();
    const readable = fakeSystem({ write: true });
    const readableStore = createClipboardStore(() => readable.clipboard);
    await readableStore.write(payload);
    readable.state.text = 'copied elsewhere';
    expect(await readableStore.read()).toBeUndefined();

    const bare = createClipboardStore(() => undefined);
    await bare.write(payload);
    expect((await bare.read())?.nodes.map((n) => n.id)).toEqual(['n_api', 'n_db']);
  });
});

describe('parseClipboardText', () => {
  it('rejects a payload with the right header whose elements the schema refuses', () => {
    const good = JSON.parse(JSON.stringify(payload)) as { nodes: Record<string, unknown>[]; edges: Record<string, unknown>[] };
    expect(parseClipboardText(JSON.stringify(good))?.source).toBe(document.id);
    const noShape = structuredClone(good);
    delete noShape.nodes[0]?.shape;
    const badId = structuredClone(good);
    if (badId.nodes[0]) {
      badId.nodes[0].id = '1 not an id';
    }
    const notArrays = { ...good, nodes: {} };
    const wrongVersion = { ...good, version: 2 };
    const badSource = { ...good, source: 7 };
    for (const bad of [noShape, badId, notArrays, wrongVersion, badSource]) {
      expect(parseClipboardText(JSON.stringify(bad))).toBeUndefined();
    }
  });
});
