/**
 * What each diagram palette entry is *for*, by the role it plays, for the Themes page (QA P19). The palettes name their
 * entries after their colours (`red`, `blueLight`, `charcoal`), and those names are the keys a saved organisation theme
 * stores its overrides under, so they cannot be renamed. A field labelled "Palette red" lies the moment the person makes it
 * teal; "Primary accent" does not. A palette this table does not know (a theme added later) gets a numbered, role-free
 * label rather than its key, so the lie cannot come back through the back door.
 */

const ROLES: Readonly<Record<string, Readonly<Record<string, string>>>> = {
  overpass: {
    red: 'Primary accent',
    redDeep: 'Strong accent',
    charcoal: 'Darkest neutral',
    graphite: 'Dark neutral',
    slate: 'Mid-dark neutral',
    ash: 'Light neutral',
    steel: 'Mid neutral',
    paper: 'Paper',
    ink: 'Ink',
    amber: 'Highlight',
  },
  neutral: {
    blue: 'Primary',
    blueLight: 'Primary tint',
    blueDeep: 'Strong primary',
    grey: 'Neutral',
    greyLight: 'Neutral tint',
    greyLine: 'Neutral line',
    white: 'Paper',
    ink: 'Ink',
    green: 'Positive',
    greenLight: 'Positive tint',
    yellow: 'Highlight',
  },
  mono: {
    black: 'Darkest',
    ink: 'Ink',
    dark: 'Dark',
    mid: 'Mid',
    light: 'Light',
    paper: 'Paper',
    tint: 'Tint',
  },
};

/** The label for the `index`th (from 0) palette entry `key` of the diagram theme `base`: a role, never the entry's colour name. */
export function paletteRoleLabel(base: string, key: string, index: number): string {
  return ROLES[base]?.[key] ?? `Palette colour ${String(index + 1)}`;
}
