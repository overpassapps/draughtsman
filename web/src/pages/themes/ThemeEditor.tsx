/**
 * The editor half of the Themes page: one theme's name, mode, chrome colours, fonts and diagram override as a local
 * draft, with both live previews and inline WCAG AA contrast warnings. Nothing here saves on its own; the draft goes
 * up through `onSave` when the admin presses Save, and the server re-validates every value (this page's checks only
 * spare a round trip, they are not the guard). Contrast is warn-only by design: the same checks the token compiler
 * prints (`theme/themeContrast.ts`) appear beside the colour that causes them and never block a save.
 */
import { paletteRoleLabel } from '../../theme/paletteRoles.ts';
import { useMemo, useState, type FormEvent } from 'react';
import { diagramThemes, type DiagramThemeName } from '../../generated/diagram-themes.ts';
import {
  applyDiagramOverride,
  CHROME_KEYS,
  CHROME_LABELS,
  DIAGRAM_BASES,
  isHexColour,
  isSafeFont,
  primarySet,
  type ChromeKey,
  type OrgTheme,
  type ThemeMode,
} from '../../theme/orgTheme.ts';
import { contrastRatio } from '../../theme/contrast.ts';
import { CHROME_CONTRAST_PAIRS, chromeContrastIssues, diagramContrastIssues, type ContrastIssue } from '../../theme/themeContrast.ts';
import type { ThemeProblem } from '../../model/themesClient.ts';
import { ChromePreview, DiagramPreview } from './ThemePreview.tsx';

interface Props {
  theme: OrgTheme;
  productName: string;
  /** The logo for a dark and a light surface, from the instance branding. */
  logos: { dark: string | null; light: string | null };
  /** True when the admin has uploaded a logo for light surfaces (Branding page); used to warn about a light theme without one. */
  hasUploadedLogo: boolean;
  busy: boolean;
  /** Problems the server reported for the last save, keyed by the field names docs/api.md lists. */
  serverProblems: ThemeProblem[];
  onSave: (theme: OrgTheme) => void;
  /** A draft nothing has stored yet (From a website): Save is offered straight away, as "Create theme from draft". */
  isNew?: boolean;
}

function expand(hex: string): string {
  const value = hex.trim();
  return /^#[0-9a-f]{3}$/i.test(value) ? `#${value[1] ?? ''}${value[1] ?? ''}${value[2] ?? ''}${value[2] ?? ''}${value[3] ?? ''}${value[3] ?? ''}` : value;
}

interface ColourFieldProps {
  id: string;
  label: string;
  value: string;
  onChange: (value: string) => void;
  warnings?: ContrastIssue[];
  error?: string | undefined;
  onReset?: (() => void) | undefined;
  placeholder?: string | undefined;
}

function ColourField({ id, label, value, onChange, warnings = [], error, onReset, placeholder }: ColourFieldProps) {
  const valid = isHexColour(value);
  return (
    <div className="theme-field">
      <label htmlFor={`${id}-hex`}>{label}</label>
      <div className="theme-colour-inputs">
        <input
          type="color"
          aria-label={`${label}, colour picker`}
          value={valid ? expand(value).toLowerCase() : '#000000'}
          onChange={(e) => { onChange(e.target.value); }}
        />
        <input
          id={`${id}-hex`}
          type="text"
          className="theme-hex"
          value={value}
          maxLength={7}
          spellCheck={false}
          placeholder={placeholder}
          aria-invalid={!valid && value !== ''}
          onChange={(e) => { onChange(e.target.value); }}
        />
        {onReset !== undefined && <button type="button" className="theme-link-button" onClick={onReset}>Reset</button>}
      </div>
      {error !== undefined && <p className="theme-field-error" role="alert">{error}</p>}
      {warnings.map((issue) => (
        <p key={`${issue.subject}-${issue.message}`} className="theme-warning" role="status">
          Contrast: {issue.message}.
        </p>
      ))}
    </div>
  );
}

export function ThemeEditor({ theme, productName, logos, hasUploadedLogo, busy, serverProblems, onSave, isNew = false }: Props) {
  const [draft, setDraft] = useState<OrgTheme>(theme);
  const [set, setSet] = useState<'dark' | 'light'>(primarySet(theme.mode));

  const dirty = JSON.stringify(draft) !== JSON.stringify(theme);
  const base = diagramThemes[(DIAGRAM_BASES as readonly string[]).includes(draft.diagram.base) ? (draft.diagram.base as DiagramThemeName) : 'overpass'];
  const derived = useMemo(() => applyDiagramOverride(draft.diagram, draft.name), [draft.diagram, draft.name]);
  const chromeIssues = useMemo(() => chromeContrastIssues(draft.chrome[set]), [draft.chrome, set]);
  const otherSet = set === 'dark' ? 'light' : 'dark';
  const otherIssues = useMemo(() => chromeContrastIssues(draft.chrome[otherSet]), [draft.chrome, otherSet]);
  const diagramIssues = useMemo(() => diagramContrastIssues(derived), [derived]);

  // What the page can tell is wrong before the server does. The server re-checks all of it.
  const errors = new Map<string, string>();
  if (draft.name.trim() === '') {
    errors.set('name', 'Give the theme a name.');
  }
  for (const s of ['dark', 'light'] as const) {
    for (const key of CHROME_KEYS) {
      if (!isHexColour(draft.chrome[s][key])) {
        errors.set(`${s}.${key}`, 'Use a hex colour such as #e04832.');
      }
    }
  }
  if (!Number.isFinite(draft.chrome.radius) || draft.chrome.radius < 0 || draft.chrome.radius > 24) {
    errors.set('radius', 'The corner radius is between 0 and 24.');
  }
  if (!isSafeFont(draft.chrome.fonts.ui)) {
    errors.set('fontUi', 'Family names separated by commas, such as Inter, system-ui, sans-serif.');
  }
  if (!isSafeFont(draft.chrome.fonts.mono)) {
    errors.set('fontMono', 'Family names separated by commas, such as JetBrains Mono, monospace.');
  }
  for (const [field, value] of [['canvasBackground', draft.diagram.canvasBackground], ['exportBackground', draft.diagram.exportBackground], ['gridColor', draft.diagram.gridColor]] as const) {
    if (value !== null && !isHexColour(value)) {
      errors.set(`diagram.${field}`, 'Use a hex colour such as #151e33.');
    }
  }
  for (const [name, value] of Object.entries(draft.diagram.palette)) {
    if (!isHexColour(value)) {
      errors.set(`diagram.palette.${name}`, 'Use a hex colour such as #e04832.');
    }
  }
  for (const [field, value] of [['fontLabel', draft.diagram.fonts.label], ['fontMono', draft.diagram.fonts.mono]] as const) {
    if (value !== null && !isSafeFont(value)) {
      errors.set(`diagram.${field}`, 'Family names separated by commas.');
    }
  }
  const invalid = errors.size > 0;

  const setColour = (s: 'dark' | 'light', key: ChromeKey, value: string): void => {
    setDraft((d) => ({ ...d, chrome: { ...d.chrome, [s]: { ...d.chrome[s], [key]: value } } }));
  };
  const setDiagram = (patch: Partial<OrgTheme['diagram']>): void => {
    setDraft((d) => ({ ...d, diagram: { ...d.diagram, ...patch } }));
  };
  const setPalette = (name: string, value: string | null): void => {
    setDraft((d) => {
      const kept = Object.entries(d.diagram.palette).filter(([key]) => key !== name);
      const palette = Object.fromEntries(value === null ? kept : [...kept, [name, value]]);
      return { ...d, diagram: { ...d.diagram, palette } };
    });
  };

  const submit = (event: FormEvent<HTMLFormElement>): void => {
    event.preventDefault();
    if (!busy && !invalid) {
      onSave({ ...draft, name: draft.name.trim() });
    }
  };

  const logo = set === 'dark' ? logos.dark : logos.light;
  const effective = (value: string | null, fallback: string): string => value ?? fallback;
  const lightWithoutLogo = draft.mode !== 'dark' && !hasUploadedLogo;

  return (
    <form className="theme-editor" onSubmit={submit} aria-label={`Edit theme ${theme.name}`}>
      <div className="theme-editor-head">
        <div className="theme-field">
          <label htmlFor="theme-name">Theme name</label>
          <input id="theme-name" type="text" value={draft.name} maxLength={60} onChange={(e) => { setDraft({ ...draft, name: e.target.value }); }} aria-invalid={errors.get('name') !== undefined} />
          {errors.get('name') !== undefined && <p className="theme-field-error" role="alert">{errors.get('name')}</p>}
        </div>
        <div className="theme-field">
          <label htmlFor="theme-mode">Mode</label>
          <select id="theme-mode" value={draft.mode} onChange={(e) => { const mode = e.target.value as ThemeMode; setDraft({ ...draft, mode }); setSet(primarySet(mode)); }}>
            <option value="dark">Dark</option>
            <option value="light">Light</option>
            <option value="system">Follow the system</option>
          </select>
        </div>
        <div className="theme-editor-buttons">
          <button type="submit" className="theme-primary" disabled={busy || (!dirty && !isNew) || invalid}>{isNew ? 'Create theme from draft' : 'Save theme'}</button>
          <button type="button" disabled={busy || !dirty} onClick={() => { setDraft(theme); setSet(primarySet(theme.mode)); }}>Revert</button>
        </div>
      </div>
      {(dirty || isNew) && <p className="theme-dirty" role="status">{isNew ? 'Not saved yet. The previews show the draft; nothing is stored until you create the theme.' : 'Unsaved changes. The previews show the draft.'}</p>}
      {serverProblems.length > 0 && (
        <div className="admin-banner" role="alert">
          <p>The server refused this theme:</p>
          <ul>
            {serverProblems.map((p) => <li key={`${p.field}-${p.message}`}>{p.message}</li>)}
          </ul>
        </div>
      )}
      {invalid && (
        <p className="theme-field-error" role="alert">
          Fix the highlighted fields to save{CHROME_KEYS.some((key) => errors.get(`${otherSet}.${key}`) !== undefined) ? `; the ${otherSet} colour set has an invalid colour` : ''}.
        </p>
      )}
      {lightWithoutLogo && (
        <p className="theme-warning" role="status">
          This theme can show a light surface, and no logo has been uploaded for one on the Branding page, so the instance&rsquo;s current logo may not read on it.
        </p>
      )}

      <div className="theme-editor-grid">
        <div className="theme-editor-form">
          <fieldset>
            <legend>Application colours</legend>
            <div className="theme-set-toggle" role="radiogroup" aria-label="Colour set to edit">
              {(['dark', 'light'] as const).map((s) => (
                <label key={s} className={set === s ? 'theme-set is-active' : 'theme-set'}>
                  <input type="radio" name="theme-set" value={s} checked={set === s} onChange={() => { setSet(s); }} />
                  {s === 'dark' ? 'Dark set' : 'Light set'}
                </label>
              ))}
            </div>
            {CHROME_KEYS.map((key) => (
              <ColourField
                key={`${set}-${key}`}
                id={`theme-${set}-${key}`}
                label={`${CHROME_LABELS[key]} (${set})`}
                value={draft.chrome[set][key]}
                onChange={(value) => { setColour(set, key, value); }}
                warnings={chromeIssues.filter((i) => i.subject.split('/').includes(key))}
                error={errors.get(`${set}.${key}`)}
              />
            ))}
            <table className="theme-contrast-table" aria-label={`Contrast report, ${set} colours`}>
              <thead>
                <tr><th scope="col">Pair</th><th scope="col">Ratio</th><th scope="col">Needs</th><th scope="col">Result</th></tr>
              </thead>
              <tbody>
                {CHROME_CONTRAST_PAIRS.map(([fg, bg, minimum]) => {
                  const ratio = contrastRatio(draft.chrome[set][fg], draft.chrome[set][bg]);
                  return (
                    <tr key={`${fg}/${bg}`}>
                      <td>{CHROME_LABELS[fg]} on {CHROME_LABELS[bg].toLowerCase()}</td>
                      <td>{ratio === undefined ? '–' : `${ratio.toFixed(2)}:1`}</td>
                      <td>{String(minimum)}:1</td>
                      <td>{ratio === undefined ? 'Not checked' : ratio >= minimum ? 'Pass' : 'Warning'}</td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
            {otherIssues.length > 0 && (
              <p className="theme-warning" role="status">
                The {otherSet} set has {String(otherIssues.length)} contrast {otherIssues.length === 1 ? 'warning' : 'warnings'}: {otherIssues.map((i) => i.message).join('; ')}.
              </p>
            )}
            <div className="theme-field">
              <label htmlFor="theme-radius">Corner radius (px)</label>
              <input
                id="theme-radius"
                type="number"
                min={0}
                max={24}
                step={1}
                value={Number.isFinite(draft.chrome.radius) ? draft.chrome.radius : ''}
                onChange={(e) => { setDraft({ ...draft, chrome: { ...draft.chrome, radius: e.target.value === '' ? Number.NaN : Number(e.target.value) } }); }}
              />
              {errors.get('radius') !== undefined && <p className="theme-field-error" role="alert">{errors.get('radius')}</p>}
            </div>
            <div className="theme-field">
              <label htmlFor="theme-font-ui">Interface font</label>
              <input id="theme-font-ui" type="text" value={draft.chrome.fonts.ui} onChange={(e) => { setDraft({ ...draft, chrome: { ...draft.chrome, fonts: { ...draft.chrome.fonts, ui: e.target.value } } }); }} aria-invalid={errors.get('fontUi') !== undefined} />
              {errors.get('fontUi') !== undefined && <p className="theme-field-error" role="alert">{errors.get('fontUi')}</p>}
            </div>
            <div className="theme-field">
              <label htmlFor="theme-font-mono">Monospace font</label>
              <input id="theme-font-mono" type="text" value={draft.chrome.fonts.mono} onChange={(e) => { setDraft({ ...draft, chrome: { ...draft.chrome, fonts: { ...draft.chrome.fonts, mono: e.target.value } } }); }} aria-invalid={errors.get('fontMono') !== undefined} />
              {errors.get('fontMono') !== undefined && <p className="theme-field-error" role="alert">{errors.get('fontMono')}</p>}
            </div>
            <p className="theme-hint">Fonts are named, not uploaded: a family shows only where it is installed or the app already loads it.</p>
          </fieldset>

          <fieldset>
            <legend>Diagram theme</legend>
            <div className="theme-field">
              <label htmlFor="theme-diagram-base">Start from</label>
              <select id="theme-diagram-base" value={draft.diagram.base} onChange={(e) => { setDiagram({ base: e.target.value, palette: {} }); }}>
                {DIAGRAM_BASES.map((name) => <option key={name} value={name}>{diagramThemes[name].name}</option>)}
              </select>
              <p className="theme-hint">Changing the starting theme clears the palette edits below.</p>
            </div>
            <h3 className="theme-subheading">Palette</h3>
            <p className="theme-hint">Changing a palette colour changes it everywhere this theme&rsquo;s shapes and connectors use it.</p>
            {Object.entries(base.palette).map(([name, original], index) => (
              <ColourField
                key={`${draft.diagram.base}-${name}`}
                id={`theme-palette-${name}`}
                label={paletteRoleLabel(draft.diagram.base, name, index)}
                value={draft.diagram.palette[name] ?? original}
                onChange={(value) => { setPalette(name, value); }}
                error={errors.get(`diagram.palette.${name}`)}
                onReset={draft.diagram.palette[name] !== undefined ? () => { setPalette(name, null); } : undefined}
              />
            ))}
            <ColourField
              id="theme-canvas"
              label="Canvas background"
              value={effective(draft.diagram.canvasBackground, derived.canvasBackground)}
              onChange={(value) => { setDiagram({ canvasBackground: value }); }}
              error={errors.get('diagram.canvasBackground')}
              warnings={diagramIssues.filter((i) => i.subject === 'canvas')}
              onReset={draft.diagram.canvasBackground !== null ? () => { setDiagram({ canvasBackground: null }); } : undefined}
            />
            <ColourField
              id="theme-export"
              label="Export paper"
              value={effective(draft.diagram.exportBackground, derived.exportBackground)}
              onChange={(value) => { setDiagram({ exportBackground: value }); }}
              error={errors.get('diagram.exportBackground')}
              warnings={diagramIssues.filter((i) => i.subject === 'export')}
              onReset={draft.diagram.exportBackground !== null ? () => { setDiagram({ exportBackground: null }); } : undefined}
            />
            <ColourField
              id="theme-grid"
              label="Grid"
              value={effective(draft.diagram.gridColor, derived.grid.color)}
              onChange={(value) => { setDiagram({ gridColor: value }); }}
              error={errors.get('diagram.gridColor')}
              onReset={draft.diagram.gridColor !== null ? () => { setDiagram({ gridColor: null }); } : undefined}
            />
            <div className="theme-field">
              <label htmlFor="theme-diagram-font-label">Diagram label font</label>
              <input id="theme-diagram-font-label" type="text" value={draft.diagram.fonts.label ?? ''} placeholder={base.fonts.label} onChange={(e) => { setDiagram({ fonts: { ...draft.diagram.fonts, label: e.target.value === '' ? null : e.target.value } }); }} aria-invalid={errors.get('diagram.fontLabel') !== undefined} />
              {errors.get('diagram.fontLabel') !== undefined && <p className="theme-field-error" role="alert">{errors.get('diagram.fontLabel')}</p>}
            </div>
            <div className="theme-field">
              <label htmlFor="theme-diagram-font-mono">Diagram monospace font</label>
              <input id="theme-diagram-font-mono" type="text" value={draft.diagram.fonts.mono ?? ''} placeholder={base.fonts.mono} onChange={(e) => { setDiagram({ fonts: { ...draft.diagram.fonts, mono: e.target.value === '' ? null : e.target.value } }); }} aria-invalid={errors.get('diagram.fontMono') !== undefined} />
              {errors.get('diagram.fontMono') !== undefined && <p className="theme-field-error" role="alert">{errors.get('diagram.fontMono')}</p>}
            </div>
            <h3 className="theme-subheading">Diagram contrast</h3>
            {diagramIssues.length === 0 ? (
              <p className="theme-hint" role="status">No contrast warnings for this diagram theme.</p>
            ) : (
              <div role="status">
                <p className="theme-warning">{String(diagramIssues.length)} contrast {diagramIssues.length === 1 ? 'warning' : 'warnings'} (you can still save):</p>
                <ul className="theme-warning-list">
                  {diagramIssues.slice(0, 8).map((i) => <li key={i.message}>{i.message}</li>)}
                  {diagramIssues.length > 8 && <li>and {String(diagramIssues.length - 8)} more.</li>}
                </ul>
              </div>
            )}
          </fieldset>
        </div>

        <div className="theme-editor-previews">
          <h3 className="theme-subheading">App preview ({set} colours)</h3>
          <ChromePreview chrome={draft.chrome} set={set} productName={productName} logo={logo} />
          <h3 className="theme-subheading">Diagram on the editor canvas</h3>
          <DiagramPreview diagram={draft.diagram} name={draft.name} surface="canvas" idPrefix="theme-canvas-" />
          <h3 className="theme-subheading">Diagram on export paper</h3>
          <DiagramPreview diagram={draft.diagram} name={draft.name} surface="export" idPrefix="theme-export-" />
        </div>
      </div>
    </form>
  );
}
