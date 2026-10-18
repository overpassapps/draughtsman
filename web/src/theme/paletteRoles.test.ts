import { describe, expect, it } from 'vitest';
import { diagramThemeNames, diagramThemes } from '../generated/diagram-themes.ts';
import { paletteRoleLabel } from './paletteRoles.ts';

// Words that name a colour; none may appear in a label, or it lies once the person changes that colour.
const COLOUR_WORDS = /\b(red|blue|green|yellow|amber|grey|gray|charcoal|graphite|slate|ash|steel|black|white|teal|navy|orange|purple|pink)\b/i;

describe('paletteRoleLabel', () => {
  it('gives every entry of every shipped diagram palette a role, not a colour name', () => {
    for (const name of diagramThemeNames) {
      const keys = Object.keys(diagramThemes[name].palette);
      expect(keys.length).toBeGreaterThan(0);
      const labels = keys.map((key, index) => paletteRoleLabel(name, key, index));
      for (const label of labels) {
        expect(label, `${name}: ${label}`).not.toMatch(COLOUR_WORDS);
        expect(label).not.toMatch(/^Palette \w+$/);
        expect(label.startsWith('Palette colour')).toBe(false);
      }
      // Two entries never read alike, or the person cannot tell which field is which.
      expect(new Set(labels).size).toBe(labels.length);
    }
  });

  it('numbers an entry it has no role for instead of naming its key', () => {
    expect(paletteRoleLabel('overpass', 'turquoise', 3)).toBe('Palette colour 4');
    expect(paletteRoleLabel('a-theme-added-later', 'red', 0)).toBe('Palette colour 1');
    expect(paletteRoleLabel('a-theme-added-later', 'red', 0)).not.toMatch(COLOUR_WORDS);
  });
});
