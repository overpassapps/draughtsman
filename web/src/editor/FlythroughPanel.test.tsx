// @vitest-environment jsdom
import { act, fireEvent, render, screen, within } from '@testing-library/react';
import { useRef, useState } from 'react';
import { MAX_KEYFRAMES } from '../model/flythrough.ts';
import { must } from '../model/sequenceFixture.ts';
import type { Flythrough, Keyframe, ViewRect } from '../model/types.ts';
import { FlythroughPanel } from './FlythroughPanel.tsx';

const view = (x: number): ViewRect => ({ x, y: 0, w: 400, h: 250 });
const frame = (id: string, x: number, extra: Partial<Keyframe> = {}): Keyframe => ({ id, view: view(x), ...extra });
const tourOf = (...keyframes: Keyframe[]): Flythrough => ({ keyframes });

interface Harness {
  edits: { current: number };
  shown: ViewRect[];
  presented: number[];
  tour: () => Flythrough | undefined;
}

/** The panel over a real state, so an edit the panel asks for is applied before the next thing the test does. */
function mount(initial: Flythrough | undefined, options: { captured?: ViewRect | undefined; shots?: { view: ViewRect; caption?: string }[]; mayGenerate?: boolean } = {}): Harness {
  const harness: Harness = { edits: { current: 0 }, shown: [], presented: [], tour: () => undefined };
  function Host() {
    const [tour, setTour] = useState(initial);
    const latest = useRef(tour);
    latest.current = tour;
    harness.tour = () => latest.current;
    return (
      <FlythroughPanel
        flythrough={tour}
        capture={() => ('captured' in options ? options.captured : view(123))}
        onShow={(v) => harness.shown.push(v)}
        canGenerate={options.mayGenerate ?? (options.shots?.length ?? 0) > 0}
        generate={() => options.shots ?? []}
        onEdit={(edit) => {
          const before = latest.current;
          const after = edit(before);
          if (JSON.stringify(after) === JSON.stringify(before)) {
            return false;
          }
          harness.edits.current += 1;
          latest.current = after;
          setTour(after);
          return true;
        }}
        onPresent={(index) => harness.presented.push(index)}
      />
    );
  }
  render(<Host />);
  return harness;
}

const captions = (): string[] => screen.queryAllByRole('listitem').map((row) => must(row.querySelector('.tour-row-caption')).textContent);
const row = (n: number): HTMLElement => must(screen.getAllByRole('listitem')[n - 1]);
const select = (n: number): void => {
  fireEvent.click(must(row(n).querySelector('.tour-row-main')));
};

describe('the Tour panel', () => {
  it('starts empty: a hint, Add keyframe, Generate and Present both disabled, no list', () => {
    mount(undefined);

    expect(screen.getByText('No keyframes yet.')).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Add keyframe' }).hasAttribute('disabled')).toBe(false);
    expect(screen.getByRole('button', { name: 'Generate from containers' }).hasAttribute('disabled')).toBe(true);
    expect(screen.getByRole('button', { name: 'Present' }).hasAttribute('disabled')).toBe(true);
    expect(screen.queryByRole('list', { name: 'Keyframes' })).toBeNull();
    expect(screen.queryByLabelText('Loop back to the first keyframe')).toBeNull();
  });

  it('Add keyframe captures the canvas’s view as one edit, selects the new row and says so', () => {
    const tour = mount(undefined, { captured: view(77) });

    fireEvent.click(screen.getByRole('button', { name: 'Add keyframe' }));

    expect(tour.edits.current).toBe(1);
    expect(tour.tour()).toEqual(tourOf(frame('k_1', 77)));
    expect(captions()).toEqual(['Keyframe 1']);
    expect(row(1).getAttribute('data-selected')).toBe('true');
    expect(screen.getByRole('status').textContent).toBe('Added keyframe 1 from the current view.');
    expect(screen.getByLabelText('Caption')).toBeTruthy();
  });

  it('adds after the selected keyframe, and at the end when none is selected', () => {
    const tour = mount(tourOf(frame('k_1', 0), frame('k_2', 10), frame('k_3', 20)));
    select(1);

    fireEvent.click(screen.getByRole('button', { name: 'Add keyframe' }));
    expect(tour.tour()?.keyframes.map((k) => k.id)).toEqual(['k_1', 'k_4', 'k_2', 'k_3']);
    // Selected now, so the next one goes after it, not at the end.
    fireEvent.click(screen.getByRole('button', { name: 'Add keyframe' }));
    expect(tour.tour()?.keyframes.map((k) => k.id)).toEqual(['k_1', 'k_4', 'k_5', 'k_2', 'k_3']);
  });

  it('adds at the end when nothing is selected', () => {
    const tour = mount(tourOf(frame('k_1', 0), frame('k_2', 10)));

    fireEvent.click(screen.getByRole('button', { name: 'Add keyframe' }));

    expect(tour.tour()?.keyframes.map((k) => k.id)).toEqual(['k_1', 'k_2', 'k_3']);
  });

  it('says so, and adds nothing, when the canvas has no size to capture', () => {
    const tour = mount(undefined, { captured: undefined });

    fireEvent.click(screen.getByRole('button', { name: 'Add keyframe' }));

    expect(tour.edits.current).toBe(0);
    expect(tour.tour()).toBeUndefined();
    expect(screen.getByRole('status').textContent).toMatch(/no size yet/);
  });

  // Five hundred rows is thousands of elements: find the button by its text, not by an accessible-name scan of every one.
  it('says so, and changes nothing, when generation finds nothing after all', () => {
    const tour = mount(tourOf(frame('k_1', 0)), { mayGenerate: true, shots: [] });

    fireEvent.click(screen.getByRole('button', { name: 'Regenerate from containers' }));

    expect(tour.edits.current).toBe(0);
    expect(captions()).toEqual(['Keyframe 1']);
    expect(screen.getByRole('status').textContent).toMatch(/Nothing to generate/);
  });

  it('refuses to add past the schema’s cap: the button is disabled at the cap and not one short of it', { timeout: 30_000 }, () => {
    const full = tourOf(...Array.from({ length: MAX_KEYFRAMES }, (_, n) => frame(`k_${String(n + 1)}`, n)));
    mount(full);
    expect(screen.getByText('Add keyframe').hasAttribute('disabled')).toBe(true);
    cleanup();

    mount(tourOf(...Array.from({ length: MAX_KEYFRAMES - 1 }, (_, n) => frame(`k_${String(n + 1)}`, n))));
    expect(screen.getByText('Add keyframe').hasAttribute('disabled')).toBe(false);
  });

  it('lists each keyframe by caption, or by its number when it has none', () => {
    mount(tourOf(frame('k_1', 0, { caption: 'Overview' }), frame('k_2', 10), frame('k_3', 20, { caption: '   ' })));

    expect(captions()).toEqual(['Overview', 'Keyframe 2', 'Keyframe 3']);
  });

  it('selecting a row cuts the canvas camera to that keyframe and opens its fields; selecting another moves them', () => {
    const tour = mount(tourOf(frame('k_1', 0), frame('k_2', 50)));

    select(2);
    expect(tour.shown).toEqual([view(50)]);
    expect(within(row(2)).getByLabelText('Caption')).toBeTruthy();
    expect(within(row(1)).queryByLabelText('Caption')).toBeNull();
    select(1);
    expect(tour.shown).toEqual([view(50), view(0)]);
    expect(within(row(1)).getByLabelText('Caption')).toBeTruthy();
    expect(within(row(2)).queryByLabelText('Caption')).toBeNull();
  });

  it('Present starts on the selected keyframe, or the first when none is selected', () => {
    const tour = mount(tourOf(frame('k_1', 0), frame('k_2', 50), frame('k_3', 90)));

    fireEvent.click(screen.getByRole('button', { name: 'Present' }));
    select(3);
    fireEvent.click(screen.getByRole('button', { name: 'Present' }));

    expect(tour.presented).toEqual([0, 2]);
  });
});

describe('reordering', () => {
  const three = (): Flythrough => tourOf(frame('k_1', 0, { caption: 'A' }), frame('k_2', 10, { caption: 'B' }), frame('k_3', 20, { caption: 'C' }));

  it('Move up and Move down reorder one step each as one edit, and the first and last cannot go further', () => {
    const tour = mount(three());

    fireEvent.click(within(row(3)).getByRole('button', { name: 'Move up' }));
    expect(captions()).toEqual(['A', 'C', 'B']);
    fireEvent.click(within(row(1)).getByRole('button', { name: 'Move down' }));
    expect(captions()).toEqual(['C', 'A', 'B']);
    expect(tour.edits.current).toBe(2);
    expect(within(row(1)).getByRole('button', { name: 'Move up' }).hasAttribute('disabled')).toBe(true);
    expect(within(row(3)).getByRole('button', { name: 'Move down' }).hasAttribute('disabled')).toBe(true);
    expect(within(row(2)).getByRole('button', { name: 'Move up' }).hasAttribute('disabled')).toBe(false);
  });

  it('Alt+ArrowDown and Alt+ArrowUp on a focused row reorder it, and plain arrows only move focus', () => {
    const tour = mount(three());
    const main = (n: number): HTMLElement => must(row(n).querySelector<HTMLElement>('.tour-row-main'));

    fireEvent.keyDown(main(1), { key: 'ArrowDown' });
    expect(globalThis.document.activeElement).toBe(main(2));
    expect(tour.edits.current).toBe(0);
    fireEvent.keyDown(main(2), { key: 'ArrowDown', altKey: true });
    expect(captions()).toEqual(['A', 'C', 'B']);
    fireEvent.keyDown(main(2), { key: 'ArrowUp', altKey: true });
    expect(captions()).toEqual(['C', 'A', 'B']);
    expect(tour.edits.current).toBe(2);
  });

  it('Duplicate puts a copy after the row, selected, with the same settings and a new id', () => {
    const tour = mount(tourOf(frame('k_1', 0, { caption: 'A', holdMs: 400 }), frame('k_2', 10)));

    fireEvent.click(within(row(1)).getByRole('button', { name: 'Duplicate' }));

    expect(captions()).toEqual(['A', 'A', 'Keyframe 3']);
    expect(tour.tour()?.keyframes[1]).toEqual({ id: 'k_3', view: view(0), caption: 'A', holdMs: 400 });
    expect(row(2).getAttribute('data-selected')).toBe('true');
  });

  it('Delete removes the row and selects its neighbour; deleting the last keyframe leaves no tour at all', () => {
    const tour = mount(tourOf(frame('k_1', 0, { caption: 'A' }), frame('k_2', 10, { caption: 'B' })));

    fireEvent.click(within(row(1)).getByRole('button', { name: 'Delete' }));
    expect(captions()).toEqual(['B']);
    expect(screen.getByRole('status').textContent).toBe('Deleted A.');
    fireEvent.click(within(row(1)).getByRole('button', { name: 'Delete' }));

    expect(tour.tour()).toBeUndefined();
    expect(screen.getByText('No keyframes yet.')).toBeTruthy();
    expect(screen.getByRole('status').textContent).toBe('Deleted the last keyframe, so there is no tour.');
  });
});

describe('a keyframe’s fields', () => {
  const open = (keyframes: Keyframe[], loop = false): Harness => {
    const tour = mount({ keyframes, ...(loop ? { loop: true } : {}) });
    select(keyframes.length > 1 ? 2 : 1);
    return tour;
  };
  const field = (name: string): HTMLInputElement => screen.getByLabelText(name);

  it('commits a caption on blur as one edit, not per keystroke, and Enter commits too', () => {
    const tour = open([frame('k_1', 0), frame('k_2', 10)]);
    const caption = screen.getByLabelText('Caption');

    for (const text of ['T', 'Th', 'The A', 'The API']) {
      fireEvent.change(caption, { target: { value: text } });
    }
    expect(tour.edits.current).toBe(0);
    fireEvent.blur(caption);
    expect(tour.edits.current).toBe(1);
    expect(tour.tour()?.keyframes[1]?.caption).toBe('The API');

    // Enter blurs the focused field, which commits it.
    const again = screen.getByLabelText('Caption');
    again.focus();
    fireEvent.change(again, { target: { value: 'Second' } });
    fireEvent.keyDown(again, { key: 'Enter' });
    expect(tour.tour()?.keyframes[1]?.caption).toBe('Second');
  });

  it('Shift+Enter is a line break and does not commit; Escape discards what was typed', () => {
    const tour = open([frame('k_1', 0), frame('k_2', 10, { caption: 'Keep' })]);
    const caption = screen.getByLabelText('Caption');

    fireEvent.change(caption, { target: { value: 'Keep\nmore' } });
    const newline = fireEvent.keyDown(caption, { key: 'Enter', shiftKey: true });
    expect(newline).toBe(true);
    expect(tour.edits.current).toBe(0);
    fireEvent.keyDown(caption, { key: 'Escape' });
    fireEvent.blur(caption);

    expect(tour.edits.current).toBe(0);
    expect(tour.tour()?.keyframes[1]?.caption).toBe('Keep');
  });

  it('clearing a caption removes it, and an untouched one writes nothing', () => {
    const tour = open([frame('k_1', 0), frame('k_2', 10, { caption: 'Gone' })]);

    fireEvent.blur(screen.getByLabelText('Caption'));
    expect(tour.edits.current).toBe(0);
    fireEvent.change(screen.getByLabelText('Caption'), { target: { value: '' } });
    fireEvent.blur(screen.getByLabelText('Caption'));

    expect(tour.tour()?.keyframes[1]).toEqual(frame('k_2', 10));
  });

  it('takes Arrive and Hold in seconds and stores milliseconds; empty goes back to the default', () => {
    const tour = open([frame('k_1', 0), frame('k_2', 10, { durationMs: 900 })]);

    expect(field('Arrive (s)').value).toBe('0.9');
    expect(field('Arrive (s)').placeholder).toBe('1.5');
    expect(field('Hold (s)').placeholder).toBe('2.5');
    fireEvent.change(field('Arrive (s)'), { target: { value: '2.5' } });
    fireEvent.blur(field('Arrive (s)'));
    fireEvent.change(field('Hold (s)'), { target: { value: '0' } });
    fireEvent.blur(field('Hold (s)'));
    expect(tour.tour()?.keyframes[1]).toEqual(frame('k_2', 10, { durationMs: 2500, holdMs: 0 }));

    fireEvent.change(field('Arrive (s)'), { target: { value: '' } });
    fireEvent.blur(field('Arrive (s)'));
    expect(tour.tour()?.keyframes[1]).toEqual(frame('k_2', 10, { holdMs: 0 }));
  });

  it('refuses text that is not a time and puts the stored value back, rather than clearing the setting', () => {
    const tour = open([frame('k_1', 0), frame('k_2', 10, { holdMs: 1000 })]);

    for (const text of ['soon', '-3', '1..2', '12s']) {
      fireEvent.change(field('Hold (s)'), { target: { value: text } });
      fireEvent.blur(field('Hold (s)'));
      expect(field('Hold (s)').value, text).toBe('1');
    }

    expect(tour.edits.current).toBe(0);
    expect(tour.tour()?.keyframes[1]?.holdMs).toBe(1000);
  });

  it('clamps a time that is too large, takes a decimal comma, and does not write a value that has not changed', () => {
    const tour = open([frame('k_1', 0), frame('k_2', 10, { holdMs: 1000 })]);

    fireEvent.change(field('Hold (s)'), { target: { value: '1' } });
    fireEvent.blur(field('Hold (s)'));
    expect(tour.edits.current).toBe(0);
    fireEvent.change(field('Hold (s)'), { target: { value: '1,5' } });
    fireEvent.blur(field('Hold (s)'));
    expect(tour.tour()?.keyframes[1]?.holdMs).toBe(1500);
    fireEvent.change(field('Hold (s)'), { target: { value: '99999' } });
    fireEvent.blur(field('Hold (s)'));

    expect(tour.tour()?.keyframes[1]?.holdMs).toBe(600_000);
  });

  it('every keyframe\'s travel time is labelled Arrive, and the first one\'s only applies while the tour loops, as it says', () => {
    open([frame('k_1', 0), frame('k_2', 10)]);
    select(1);
    expect(field('Arrive (s)').disabled).toBe(true);
    expect(screen.getByText('Used only when the tour loops: the trip back from the last keyframe.')).toBeTruthy();
    expect(screen.queryByText('Return (s)')).toBeNull();
    cleanup();

    open([frame('k_1', 0), frame('k_2', 10)], true);
    select(1);
    expect(field('Arrive (s)').disabled).toBe(false);
  });

  it('a Cut has no travel time to set', () => {
    open([frame('k_1', 0), frame('k_2', 10, { easing: 'cut' })]);

    expect(field('Arrive (s)').disabled).toBe(true);
  });

  it('easing is chosen from the five, Smooth being the default that is stored as nothing', () => {
    const tour = open([frame('k_1', 0), frame('k_2', 10)]);
    const easing = screen.getByLabelText<HTMLSelectElement>('Easing');

    expect([...easing.options].map((option) => option.text)).toEqual(['Smooth', 'Ease in', 'Ease out', 'Even', 'Cut']);
    expect(easing.value).toBe('easeInOut');
    fireEvent.change(easing, { target: { value: 'cut' } });
    expect(tour.tour()?.keyframes[1]?.easing).toBe('cut');
    fireEvent.change(screen.getByLabelText('Easing'), { target: { value: 'easeInOut' } });
    expect(tour.tour()?.keyframes[1]).toEqual(frame('k_2', 10));
  });

  it('Use current view replaces only the view', () => {
    const tour = mount(tourOf(frame('k_1', 0), frame('k_2', 10, { caption: 'Keep me' })), { captured: view(999) });
    select(2);

    fireEvent.click(screen.getByRole('button', { name: 'Use current view' }));

    expect(tour.tour()?.keyframes[1]).toEqual(frame('k_2', 999, { caption: 'Keep me' }));
    expect(screen.getByRole('status').textContent).toBe('Updated the keyframe to the current view.');
  });
});

describe('looping and generating', () => {
  it('Loop shows only for two keyframes or more, and is a checkbox that writes and clears the member', () => {
    const tour = mount(tourOf(frame('k_1', 0)));
    expect(screen.queryByLabelText('Loop back to the first keyframe')).toBeNull();
    cleanup();

    const two = mount(tourOf(frame('k_1', 0), frame('k_2', 10)));
    const loop = screen.getByLabelText<HTMLInputElement>('Loop back to the first keyframe');
    expect(loop.checked).toBe(false);
    fireEvent.click(loop);
    expect(two.tour()?.loop).toBe(true);
    expect(screen.getByLabelText<HTMLInputElement>('Loop back to the first keyframe').checked).toBe(true);
    fireEvent.click(screen.getByLabelText('Loop back to the first keyframe'));
    const latest = two.tour();
    expect(latest !== undefined && 'loop' in latest).toBe(false);
    expect(tour.edits.current).toBe(0);
  });

  it('Generate builds the tour from the shots with their captions, selecting nothing, in one edit', () => {
    const tour = mount(undefined, { shots: [{ view: view(0), caption: 'Overview' }, { view: view(50), caption: 'VPC' }] });

    fireEvent.click(screen.getByRole('button', { name: 'Generate from containers' }));

    expect(tour.edits.current).toBe(1);
    expect(captions()).toEqual(['Overview', 'VPC']);
    expect(screen.getByRole('status').textContent).toBe('Generated 2 keyframes. Undo brings back what was there.');
    expect(screen.queryByLabelText('Caption')).toBeNull();
  });

  it('with a tour already there the same button regenerates it, keeping the loop setting', () => {
    const tour = mount({ keyframes: [frame('k_1', 0, { caption: 'Mine' }), frame('k_2', 10)], loop: true }, { shots: [{ view: view(5), caption: 'Fresh' }, { view: view(6) }] });

    fireEvent.click(screen.getByRole('button', { name: 'Regenerate from containers' }));

    expect(captions()).toEqual(['Fresh', 'Keyframe 2']);
    expect(tour.tour()?.loop).toBe(true);
    expect(screen.getByRole('status').textContent).toMatch(/^Replaced the tour with 2 keyframes/);
  });
});

// Testing Library's cleanup, named here because a test above mounts twice.
function cleanup(): void {
  act(() => {
    document.body.innerHTML = '';
  });
}
