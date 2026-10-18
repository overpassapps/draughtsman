/**
 * The admin Themes page (M3; design/atlas/themes-admin.md): the organisation's themes, created from a base, edited
 * with live previews of the app chrome and a sample diagram, and one of them chosen as the default every signed-in
 * user receives. Import and export use the theme file format (docs/api.md "## Themes"), the same YAML the files
 * mechanism reads, so the page and the files are one format. The server owns validation and authorisation; a non-admin
 * is shown a message here only so they are not shown a page that cannot work.
 */
import { NotAllowed } from '../app/NotAllowed.tsx';
import { useEffect, useState, type ChangeEvent, type FormEvent } from 'react';
import type { AuthUser } from '../model/authClient.ts';
import {
  createTheme,
  deleteTheme,
  duplicateTheme,
  exportThemeUrl,
  fetchAdminThemes,
  fetchExtractOptions,
  importTheme,
  saveTheme,
  setDefaultTheme,
  ThemesError,
  type AdminThemes,
  type ThemeDraft,
  type ThemeProblem,
} from '../model/themesClient.ts';
import { fetchBranding } from '../model/brandingClient.ts';
import { diagramThemes, type DiagramThemeName } from '../generated/diagram-themes.ts';
import { applyDiagramOverride, chromeStyleVars, DIAGRAM_BASES, primarySet, type OrgTheme } from '../theme/orgTheme.ts';
import { fetchChromeThemeManifest, brandLogo, refreshChromeTheme, type ChromeThemeManifest } from '../theme/chromeTheme.ts';
import { ThemeEditor } from './themes/ThemeEditor.tsx';
import { FromWebsitePanel } from './themes/FromWebsitePanel.tsx';

interface Props {
  currentUser: AuthUser;
}

const MAX_FILE_BYTES = 64 * 1024;

/** A theme's look in one line: its own colours on its own surface, scoped the same way the preview scopes them. */
function ThemeSwatch({ theme }: { theme: OrgTheme }) {
  const set = primarySet(theme.mode);
  const canvas = applyDiagramOverride(theme.diagram).canvasBackground;
  return (
    <span className="theme-swatch" style={chromeStyleVars(theme.chrome, set)} aria-hidden="true" data-testid={`swatch-${theme.id}`}>
      <span className="theme-swatch-sample">Aa</span>
      <span className="theme-swatch-dot theme-swatch-brand" />
      <span className="theme-swatch-dot theme-swatch-accent" />
      <span className="theme-swatch-canvas" style={{ background: canvas }} />
    </span>
  );
}

function baseName(base: string): string {
  return (DIAGRAM_BASES as readonly string[]).includes(base) ? diagramThemes[base as DiagramThemeName].name : base;
}

function messageOf(cause: unknown, fallback: string): string {
  return cause instanceof ThemesError ? cause.message : fallback;
}

export function ThemesPage({ currentUser }: Props) {
  const [state, setState] = useState<AdminThemes>();
  const [selectedId, setSelectedId] = useState<string>();
  const [manifest, setManifest] = useState<ChromeThemeManifest>();
  const [hasUploadedLogo, setHasUploadedLogo] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string>();
  const [notice, setNotice] = useState<string>();
  const [problems, setProblems] = useState<ThemeProblem[]>([]);
  const [newName, setNewName] = useState('');
  const [newBase, setNewBase] = useState('overpass');
  const [confirmingDelete, setConfirmingDelete] = useState<string>();
  const [draft, setDraft] = useState<ThemeDraft>();
  const [allowUrl, setAllowUrl] = useState(false);

  const isAdmin = currentUser.role === 'Admin';

  useEffect(() => {
    if (!isAdmin) {
      return;
    }
    void fetchAdminThemes()
      .then(setState)
      .catch((cause: unknown) => { setError(messageOf(cause, 'Could not read the themes.')); });
    void fetchChromeThemeManifest().then(setManifest);
    void fetchExtractOptions().then((options) => { setAllowUrl(options.url); });
    void fetchBranding().then((branding) => { setHasUploadedLogo(branding.overridden.logo); }).catch(() => undefined);
  }, [isAdmin]);

  if (!isAdmin) {
    return <NotAllowed />;
  }

  const run = (work: () => Promise<void>, fallback: string): void => {
    if (busy) {
      return;
    }
    setBusy(true);
    setError(undefined);
    setNotice(undefined);
    setProblems([]);
    void work()
      .catch((cause: unknown) => {
        setError(messageOf(cause, fallback));
        if (cause instanceof ThemesError) {
          setProblems(cause.problems);
        }
      })
      .finally(() => { setBusy(false); });
  };

  /** Re-applies the instance stylesheet after anything that can change what every user receives. */
  const restyle = (): void => { void refreshChromeTheme().then(() => fetchChromeThemeManifest()).then(setManifest); };

  const applySet = (next: AdminThemes, message: string, restyleNow = false): void => {
    setState(next);
    setNotice(message);
    if (restyleNow) {
      restyle();
    }
  };

  const create = (event: FormEvent<HTMLFormElement>): void => {
    event.preventDefault();
    run(async () => {
      const created = await createTheme(newName.trim(), newBase === 'overpass' ? undefined : newBase);
      setNewName('');
      setSelectedId(created.id);
      setState(await fetchAdminThemes());
      setNotice(`Created ${created.name}.`);
    }, 'Could not create the theme.');
  };

  /**
   * Makes a theme of a draft read from a site, through the existing routes: create (a name on the Overpass base) then save the
   * edited draft over it. If the save is refused the created theme is removed again, so a refusal leaves nothing behind.
   */
  const createFromDraft = (theme: OrgTheme): void => {
    run(async () => {
      const created = await createTheme(theme.name, undefined);
      try {
        await saveTheme({ ...theme, id: created.id });
      } catch (cause) {
        await deleteTheme(created.id).catch(() => undefined);
        throw cause;
      }
      setDraft(undefined);
      setSelectedId(created.id);
      setState(await fetchAdminThemes());
      setNotice(`Created ${theme.name} from a website. Nothing else was saved.`);
    }, 'Could not create the theme.');
  };

  const save = (theme: OrgTheme): void => {
    run(async () => {
      const saved = await saveTheme(theme);
      const next = await fetchAdminThemes();
      applySet(next, `Saved ${saved.name}.`, saved.isDefault);
    }, 'Could not save the theme.');
  };

  const duplicate = (theme: OrgTheme): void => {
    run(async () => {
      const copy = await duplicateTheme(theme.id);
      setSelectedId(copy.id);
      setState(await fetchAdminThemes());
      setNotice(`Created ${copy.name}.`);
    }, 'Could not duplicate the theme.');
  };

  const remove = (theme: OrgTheme): void => {
    run(async () => {
      const next = await deleteTheme(theme.id);
      if (selectedId === theme.id) {
        setSelectedId(undefined);
      }
      applySet(next, `Deleted ${theme.name}.`);
    }, 'Could not delete the theme.');
  };

  const makeDefault = (theme: OrgTheme | null): void => {
    run(async () => {
      const next = await setDefaultTheme(theme?.id ?? null);
      applySet(next, theme === null ? 'Everyone now receives the Overpass default.' : `Everyone now receives ${theme.name}.`, true);
    }, 'Could not change the default theme.');
  };

  const importFile = (event: ChangeEvent<HTMLInputElement>): void => {
    const file = event.target.files?.[0];
    event.target.value = '';
    if (file === undefined) {
      return;
    }
    if (file.size > MAX_FILE_BYTES) {
      setError('A theme file is at most 64 KiB.');
      return;
    }
    run(async () => {
      const imported = await importTheme(await file.text());
      setSelectedId(imported.id);
      setState(await fetchAdminThemes());
      setNotice(`Imported ${imported.name}.`);
    }, 'Could not import the theme file.');
  };

  if (state === undefined) {
    return (
      <div className="admin-page admin-page-wide">
        <h1 className="admin-title">Themes</h1>
        {error !== undefined ? <p className="page-message" role="alert">Cannot read the themes: {error}</p> : <p className="page-message">Loading themes…</p>}
      </div>
    );
  }

  const selected = state.themes.find((theme) => theme.id === selectedId);
  const productName = manifest?.productName ?? 'Draughtsman';
  const logoFor = (set: 'dark' | 'light'): string | null => (manifest === undefined ? null : brandLogo({ ...manifest, mode: set }));
  const shippedIsDefault = state.defaultId === null;

  return (
    <div className="admin-page admin-page-wide">
      <h1 className="admin-title">Themes</h1>
      <p className="admin-intro">
        Your organisation&rsquo;s look: the app&rsquo;s colours and fonts, and the colours and fonts of its diagrams. The default theme is what every
        signed-in person receives. The logo, favicon and product name are set on the Branding page and stay the same across themes. Themes are
        stored in this instance&rsquo;s data folder, and export as a file you can keep or share with another instance.
      </p>
      {error !== undefined && <p className="admin-banner" role="alert">{error}</p>}
      {notice !== undefined && <p className="admin-notice" role="status">{notice}</p>}

      <h2 className="admin-subtitle">Your themes</h2>
      <ul className="theme-list" aria-label="Themes">
        <li className="theme-card" data-testid="theme-card-shipped">
          <div className="theme-card-main">
            <p className="theme-card-name">
              Overpass default
              {shippedIsDefault && <span className="branding-badge">Default</span>}
            </p>
            <p className="theme-card-meta">The shipped theme: navy and brand red. It cannot be edited; copy it to make your own.</p>
          </div>
          <div className="theme-card-actions">
            {!shippedIsDefault && <button type="button" disabled={busy} onClick={() => { makeDefault(null); }}>Use Overpass default</button>}
          </div>
        </li>
        {state.themes.map((theme) => (
          <li key={theme.id} className={theme.id === selectedId ? 'theme-card is-selected' : 'theme-card'} data-testid={`theme-card-${theme.id}`}>
            <ThemeSwatch theme={theme} />
            <div className="theme-card-main">
              <p className="theme-card-name">
                {theme.name}
                {theme.isDefault && <span className="branding-badge">Default</span>}
              </p>
              <p className="theme-card-meta">{theme.mode === 'system' ? 'Follows the system' : theme.mode === 'dark' ? 'Dark' : 'Light'} · diagrams start from {baseName(theme.diagram.base)}</p>
            </div>
            <div className="theme-card-actions">
              <button type="button" disabled={busy} onClick={() => { setDraft(undefined); setSelectedId(theme.id); setProblems([]); }} aria-label={`Edit ${theme.name}`}>Edit</button>
              {!theme.isDefault && <button type="button" disabled={busy} onClick={() => { makeDefault(theme); }} aria-label={`Make default: ${theme.name}`}>Make default</button>}
              <button type="button" disabled={busy} onClick={() => { duplicate(theme); }} aria-label={`Duplicate ${theme.name}`}>Duplicate</button>
              <a className="theme-link-button" href={exportThemeUrl(theme.id)} download={`${theme.id}.theme.yaml`} aria-label={`Export ${theme.name}`}>Export</a>
              {confirmingDelete !== theme.id && !theme.isDefault && (
                <button type="button" disabled={busy} onClick={() => { setConfirmingDelete(theme.id); }} aria-label={`Delete ${theme.name}`}>Delete</button>
              )}
              {confirmingDelete === theme.id && (
                <span role="group" aria-label={`Confirm delete ${theme.name}`}>
                  <button type="button" disabled={busy} onClick={() => { setConfirmingDelete(undefined); remove(theme); }}>Yes, delete {theme.name}</button>
                  <button type="button" onClick={() => { setConfirmingDelete(undefined); }}>Cancel</button>
                </span>
              )}
            </div>
          </li>
        ))}
      </ul>

      <div className="theme-create-row">
        <form className="admin-form" onSubmit={create} aria-label="New theme">
          <div className="branding-field">
            <label htmlFor="theme-new-name">New theme name</label>
            <input id="theme-new-name" type="text" value={newName} maxLength={60} onChange={(e) => { setNewName(e.target.value); }} />
          </div>
          <div className="branding-field">
            <label htmlFor="theme-new-base">Start from</label>
            <select id="theme-new-base" value={newBase} onChange={(e) => { setNewBase(e.target.value); }}>
              <option value="overpass">Overpass default</option>
              {state.themes.map((theme) => <option key={theme.id} value={theme.id}>Copy of {theme.name}</option>)}
            </select>
          </div>
          <button type="submit" disabled={busy || newName.trim() === ''}>Create theme</button>
        </form>
        <label className="theme-import">
          <span>Import a theme file…</span>
          <input type="file" accept=".yaml,.yml,text/yaml,application/x-yaml" aria-label="Import a theme file" disabled={busy} onChange={importFile} />
        </label>
      </div>

      <FromWebsitePanel
        allowUrl={allowUrl}
        draft={draft}
        onDraft={(next) => { setDraft(next); setProblems([]); }}
        productName={productName}
        logos={{ dark: logoFor('dark'), light: logoFor('light') }}
        hasUploadedLogo={hasUploadedLogo}
        busy={busy}
        serverProblems={problems}
        onCreate={createFromDraft}
      />

      {draft !== undefined ? null : selected !== undefined ? (
        <section aria-label="Theme editor">
          <h2 className="admin-subtitle">Edit {selected.name}</h2>
          <ThemeEditor
            key={`${selected.id}-${selected.updatedAt}`}
            theme={selected}
            productName={productName}
            logos={{ dark: logoFor('dark'), light: logoFor('light') }}
            hasUploadedLogo={hasUploadedLogo}
            busy={busy}
            serverProblems={problems}
            onSave={save}
          />
        </section>
      ) : (
        <p className="admin-intro">Choose Edit on a theme to change its colours and fonts, or create one above.</p>
      )}
    </div>
  );
}
