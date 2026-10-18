// @vitest-environment jsdom
import { arrangeShortcut, clipboardCommand, groupShortcut, historyCommand, isCommandPalette, isDeleteCommand, isEditLabelKey, isShapePanelToggle, isTextField, nudgeDelta, selectionStep, toolShortcut, zoomShortcut } from './shortcuts.ts';

const key = (
  k: string,
  { altGraph = false, ...mods }: Partial<Record<'metaKey' | 'ctrlKey' | 'shiftKey' | 'altKey' | 'altGraph', boolean>> & { code?: string } = {},
) => ({
  key: k,
  code: '',
  metaKey: false,
  ctrlKey: false,
  shiftKey: false,
  altKey: false,
  getModifierState: (modifier: string) => modifier === 'AltGraph' && altGraph,
  ...mods,
});

describe('historyCommand', () => {
  it('maps Cmd+Z and Ctrl+Z to undo', () => {
    expect(historyCommand(key('z', { metaKey: true }))).toBe('undo');
    expect(historyCommand(key('z', { ctrlKey: true }))).toBe('undo');
  });

  it('maps Cmd+Shift+Z and Ctrl+Shift+Z to redo, whatever case the key reports', () => {
    expect(historyCommand(key('Z', { metaKey: true, shiftKey: true }))).toBe('redo');
    expect(historyCommand(key('z', { ctrlKey: true, shiftKey: true }))).toBe('redo');
  });

  it('ignores Z without a command modifier, with Alt, and other keys', () => {
    expect(historyCommand(key('z'))).toBeUndefined();
    expect(historyCommand(key('z', { shiftKey: true }))).toBeUndefined();
    expect(historyCommand(key('z', { metaKey: true, altKey: true }))).toBeUndefined();
    expect(historyCommand(key('y', { ctrlKey: true }))).toBeUndefined();
  });
});

describe('nudgeDelta', () => {
  it('moves 1px per arrow in its direction', () => {
    expect(nudgeDelta(key('ArrowLeft'))).toEqual({ x: -1, y: 0 });
    expect(nudgeDelta(key('ArrowRight'))).toEqual({ x: 1, y: 0 });
    expect(nudgeDelta(key('ArrowUp'))).toEqual({ x: 0, y: -1 });
    expect(nudgeDelta(key('ArrowDown'))).toEqual({ x: 0, y: 1 });
  });

  it('moves 10px with Shift', () => {
    expect(nudgeDelta(key('ArrowLeft', { shiftKey: true }))).toEqual({ x: -10, y: 0 });
    expect(nudgeDelta(key('ArrowDown', { shiftKey: true }))).toEqual({ x: 0, y: 10 });
  });

  it('leaves arrows with Cmd, Ctrl or Alt to the browser, and ignores every other key', () => {
    expect(nudgeDelta(key('ArrowLeft', { metaKey: true }))).toBeUndefined();
    expect(nudgeDelta(key('ArrowLeft', { ctrlKey: true }))).toBeUndefined();
    expect(nudgeDelta(key('ArrowLeft', { altKey: true }))).toBeUndefined();
    expect(nudgeDelta(key('a'))).toBeUndefined();
    expect(nudgeDelta(key('constructor'))).toBeUndefined();
  });
});

describe('isDeleteCommand', () => {
  it('is Delete or Backspace, Shift or not', () => {
    expect(isDeleteCommand(key('Delete'))).toBe(true);
    expect(isDeleteCommand(key('Backspace'))).toBe(true);
    expect(isDeleteCommand(key('Backspace', { shiftKey: true }))).toBe(true);
  });

  it('is not a command-modified delete or any other key', () => {
    expect(isDeleteCommand(key('Backspace', { metaKey: true }))).toBe(false);
    expect(isDeleteCommand(key('Delete', { ctrlKey: true }))).toBe(false);
    expect(isDeleteCommand(key('Delete', { altKey: true }))).toBe(false);
    expect(isDeleteCommand(key('d'))).toBe(false);
  });
});

describe('isTextField', () => {
  it('is true for inputs, text areas, selects and contenteditable elements and what they contain', () => {
    const editable = document.createElement('div');
    editable.setAttribute('contenteditable', 'true');
    const inside = document.createElement('span');
    editable.append(inside);
    document.body.append(editable);
    for (const target of [document.createElement('input'), document.createElement('textarea'), document.createElement('select'), editable, inside]) {
      expect(isTextField(target), target.tagName).toBe(true);
    }
    editable.remove();
  });

  it('is false for a plain element, a button, contenteditable="false", the window and no target', () => {
    const switchedOff = document.createElement('div');
    switchedOff.setAttribute('contenteditable', 'false');
    for (const target of [document.createElement('div'), document.createElement('button'), switchedOff, window, null]) {
      expect(isTextField(target)).toBe(false);
    }
  });
});

describe('toolShortcut', () => {
  it('maps a bare V to Select and a bare C to Connector, whatever case the key reports', () => {
    expect(toolShortcut(key('v'))).toBe('select');
    expect(toolShortcut(key('C'))).toBe('connector');
  });

  it('leaves V and C with any modifier to the browser (copy, paste) and ignores other letters', () => {
    expect(toolShortcut(key('c', { metaKey: true }))).toBeUndefined();
    expect(toolShortcut(key('c', { ctrlKey: true }))).toBeUndefined();
    expect(toolShortcut(key('v', { shiftKey: true }))).toBeUndefined();
    expect(toolShortcut(key('v', { altKey: true }))).toBeUndefined();
    expect(toolShortcut(key('x'))).toBeUndefined();
  });
});

describe('isShapePanelToggle', () => {
  it('is true for a bare S, whatever case the key reports, and for Cmd/Ctrl+Shift+S', () => {
    expect(isShapePanelToggle(key('s'))).toBe(true);
    expect(isShapePanelToggle(key('S'))).toBe(true);
    expect(isShapePanelToggle(key('s', { metaKey: true, shiftKey: true }))).toBe(true);
    expect(isShapePanelToggle(key('s', { ctrlKey: true, shiftKey: true }))).toBe(true);
  });

  it('is false for Cmd/Ctrl+S alone (save), Alt+S, and any other key', () => {
    expect(isShapePanelToggle(key('s', { metaKey: true }))).toBe(false);
    expect(isShapePanelToggle(key('s', { ctrlKey: true }))).toBe(false);
    expect(isShapePanelToggle(key('s', { altKey: true }))).toBe(false);
    expect(isShapePanelToggle(key('s', { metaKey: true, shiftKey: true, altKey: true }))).toBe(false);
    expect(isShapePanelToggle(key('t'))).toBe(false);
  });
});

describe('arrangeShortcut', () => {
  it('maps Cmd/Ctrl+] forward and Cmd/Ctrl+[ backward', () => {
    expect(arrangeShortcut(key(']', { metaKey: true }))).toBe('bringForward');
    expect(arrangeShortcut(key(']', { ctrlKey: true }))).toBe('bringForward');
    expect(arrangeShortcut(key('[', { metaKey: true }))).toBe('sendBackward');
    expect(arrangeShortcut(key('[', { ctrlKey: true }))).toBe('sendBackward');
  });

  it('maps the Alt chords to front and back, from the bracket key or its physical code', () => {
    expect(arrangeShortcut(key(']', { ctrlKey: true, altKey: true, code: 'BracketRight' }))).toBe('bringToFront');
    expect(arrangeShortcut(key('[', { ctrlKey: true, altKey: true, code: 'BracketLeft' }))).toBe('sendToBack');
    // macOS reports Option+] and Option+[ as typographic quote marks.
    expect(arrangeShortcut(key('‘', { metaKey: true, altKey: true, code: 'BracketRight' }))).toBe('bringToFront');
    expect(arrangeShortcut(key('“', { metaKey: true, altKey: true, code: 'BracketLeft' }))).toBe('sendToBack');
  });

  it('leaves AltGr to typing on the Ctrl chord, but keeps Cmd+Option arranging when Option reports AltGraph', () => {
    // German AltGr+8 types '[' and AltGr on the BracketRight key types '~'; both arrive as Ctrl+Alt.
    expect(arrangeShortcut(key('[', { ctrlKey: true, altKey: true, altGraph: true, code: 'Digit8' }))).toBeUndefined();
    expect(arrangeShortcut(key('~', { ctrlKey: true, altKey: true, altGraph: true, code: 'BracketRight' }))).toBeUndefined();
    expect(arrangeShortcut(key('[', { ctrlKey: true, altKey: true, code: 'Digit8' }))).toBe('sendToBack');
    expect(arrangeShortcut(key('‘', { metaKey: true, altKey: true, altGraph: true, code: 'BracketRight' }))).toBe('bringToFront');
  });

  it('leaves the Shift chords to the browser, which switches tabs on them', () => {
    expect(arrangeShortcut(key(']', { metaKey: true, shiftKey: true, code: 'BracketRight' }))).toBeUndefined();
    expect(arrangeShortcut(key('[', { ctrlKey: true, shiftKey: true, code: 'BracketLeft' }))).toBeUndefined();
    expect(arrangeShortcut(key('}', { metaKey: true, shiftKey: true, code: 'BracketRight' }))).toBeUndefined();
    expect(arrangeShortcut(key('{', { ctrlKey: true, shiftKey: true, code: 'BracketLeft' }))).toBeUndefined();
    expect(arrangeShortcut(key('’', { metaKey: true, altKey: true, shiftKey: true, code: 'BracketRight' }))).toBeUndefined();
  });

  it('ignores brackets without a command modifier, the zoom keys, and the bracket code without Alt', () => {
    expect(arrangeShortcut(key(']'))).toBeUndefined();
    expect(arrangeShortcut(key('[', { altKey: true, code: 'BracketLeft' }))).toBeUndefined();
    expect(arrangeShortcut(key('+', { metaKey: true }))).toBeUndefined();
    expect(arrangeShortcut(key('+', { metaKey: true, code: 'BracketRight' }))).toBeUndefined();
    expect(arrangeShortcut(key('-', { ctrlKey: true, shiftKey: true }))).toBeUndefined();
  });
});

describe('zoomShortcut', () => {
  it('maps the zoom cluster keys with Cmd or Ctrl', () => {
    expect(zoomShortcut(key('=', { metaKey: true }))).toBe('zoomIn');
    expect(zoomShortcut(key('+', { ctrlKey: true }))).toBe('zoomIn');
    expect(zoomShortcut(key('-', { metaKey: true }))).toBe('zoomOut');
    expect(zoomShortcut(key('_', { ctrlKey: true }))).toBe('zoomOut');
    expect(zoomShortcut(key('0', { metaKey: true }))).toBe('fit');
    expect(zoomShortcut(key('1', { ctrlKey: true }))).toBe('zoomTo100');
  });

  it('ignores them bare, with Shift or Alt, and other digits', () => {
    expect(zoomShortcut(key('='))).toBeUndefined();
    expect(zoomShortcut(key('=', { metaKey: true, shiftKey: true }))).toBeUndefined();
    expect(zoomShortcut(key('0', { ctrlKey: true, altKey: true }))).toBeUndefined();
    expect(zoomShortcut(key('2', { metaKey: true }))).toBeUndefined();
  });
});

describe('isEditLabelKey', () => {
  it('takes a bare Enter only', () => {
    expect(isEditLabelKey(key('Enter'))).toBe(true);
    expect(isEditLabelKey(key('Enter', { metaKey: true }))).toBe(false);
    expect(isEditLabelKey(key('Enter', { shiftKey: true }))).toBe(false);
    expect(isEditLabelKey(key('Enter', { altKey: true }))).toBe(false);
    expect(isEditLabelKey(key(' '))).toBe(false);
  });
});

describe('isCommandPalette', () => {
  it('takes Cmd+K and Ctrl+K in either case', () => {
    expect(isCommandPalette(key('k', { metaKey: true }))).toBe(true);
    expect(isCommandPalette(key('K', { ctrlKey: true }))).toBe(true);
  });

  it('leaves K bare, with Shift or with Alt', () => {
    expect(isCommandPalette(key('k'))).toBe(false);
    expect(isCommandPalette(key('k', { metaKey: true, shiftKey: true }))).toBe(false);
    expect(isCommandPalette(key('k', { ctrlKey: true, altKey: true }))).toBe(false);
  });
});

describe('groupShortcut', () => {
  it('maps Cmd/Ctrl+G to group and Cmd/Ctrl+Shift+G to ungroup, whatever case the key reports', () => {
    expect(groupShortcut(key('g', { metaKey: true }))).toBe('group');
    expect(groupShortcut(key('g', { ctrlKey: true }))).toBe('group');
    expect(groupShortcut(key('G', { metaKey: true, shiftKey: true }))).toBe('ungroup');
    expect(groupShortcut(key('g', { ctrlKey: true, shiftKey: true }))).toBe('ungroup');
  });

  it('ignores G without a command modifier, with Alt, and other keys', () => {
    expect(groupShortcut(key('g'))).toBeUndefined();
    expect(groupShortcut(key('G', { shiftKey: true }))).toBeUndefined();
    expect(groupShortcut(key('g', { ctrlKey: true, altKey: true }))).toBeUndefined();
    expect(groupShortcut(key('h', { metaKey: true }))).toBeUndefined();
  });
});

describe('clipboardCommand', () => {
  it('maps Cmd or Ctrl with C, X, V and D to copy, cut, paste and duplicate, whatever case the key reports', () => {
    for (const modifier of ['metaKey', 'ctrlKey'] as const) {
      expect(clipboardCommand(key('c', { [modifier]: true }))).toBe('copy');
      expect(clipboardCommand(key('X', { [modifier]: true }))).toBe('cut');
      expect(clipboardCommand(key('v', { [modifier]: true }))).toBe('paste');
      expect(clipboardCommand(key('d', { [modifier]: true }))).toBe('duplicate');
    }
  });

  it('leaves the letters alone without a command modifier, with Shift or Alt, and ignores other keys', () => {
    expect(clipboardCommand(key('c'))).toBeUndefined();
    expect(clipboardCommand(key('v', { metaKey: true, shiftKey: true }))).toBeUndefined();
    expect(clipboardCommand(key('d', { ctrlKey: true, altKey: true }))).toBeUndefined();
    expect(clipboardCommand(key('z', { metaKey: true }))).toBeUndefined();
  });
});

describe('selectionStep', () => {
  it('walks forward on Tab and back on Shift+Tab', () => {
    expect(selectionStep(key('Tab'))).toBe(1);
    expect(selectionStep(key('Tab', { shiftKey: true }))).toBe(-1);
  });

  it('leaves Tab with Cmd, Ctrl or Alt and every other key alone', () => {
    expect(selectionStep(key('Tab', { metaKey: true }))).toBeUndefined();
    expect(selectionStep(key('Tab', { ctrlKey: true }))).toBeUndefined();
    expect(selectionStep(key('Tab', { altKey: true }))).toBeUndefined();
    expect(selectionStep(key('ArrowRight'))).toBeUndefined();
  });
});
