/**
 * "From a website" on the admin Themes page (design/atlas/themes-admin.md, amendment 2026-10-05): turns a site's colours into
 * a draft theme. Paste CSS (or a page's HTML) always works and needs no network; a web address box exists only when the server
 * reports that the operator enabled it. The result is a draft held in the parent, shown in the existing editor with its
 * previews and contrast table and a plain list of how each colour was chosen; nothing is saved until the admin creates the
 * theme from it. The server does the reading and owns every limit: this component sends text or an address and shows the answer.
 */
import { useState } from 'react';
import { extractTheme, ThemesError, type ThemeDraft, type ThemeProblem } from '../../model/themesClient.ts';
import type { OrgTheme } from '../../theme/orgTheme.ts';
import { ThemeEditor } from './ThemeEditor.tsx';

interface Props {
  /** The server reports the operator turned on fetching a page by address. */
  allowUrl: boolean;
  draft: ThemeDraft | undefined;
  onDraft: (draft: ThemeDraft | undefined) => void;
  productName: string;
  logos: { dark: string | null; light: string | null };
  hasUploadedLogo: boolean;
  busy: boolean;
  serverProblems: ThemeProblem[];
  onCreate: (theme: OrgTheme) => void;
}

export function FromWebsitePanel({ allowUrl, draft, onDraft, productName, logos, hasUploadedLogo, busy, serverProblems, onCreate }: Props) {
  const [css, setCss] = useState('');
  const [address, setAddress] = useState('');
  const [reading, setReading] = useState(false);
  const [error, setError] = useState<string>();

  const read = (input: { css: string } | { url: string }): void => {
    if (reading) {
      return;
    }
    setReading(true);
    setError(undefined);
    onDraft(undefined);
    void extractTheme(input)
      .then(onDraft)
      .catch((cause: unknown) => { setError(cause instanceof ThemesError ? cause.message : 'Could not read colours from that.'); })
      .finally(() => { setReading(false); });
  };

  return (
    <section className="theme-from-website" aria-label="From a website">
      <h2 className="admin-subtitle">From a website</h2>
      <p className="admin-intro">
        Make a draft theme from a site&rsquo;s colours. Paste its CSS (or the page&rsquo;s HTML) below{allowUrl ? ', or give a web address' : ''}. You
        review and change the draft before anything is saved, and nothing is stored from the site.
      </p>

      <div className="branding-field">
        <label htmlFor="theme-from-css">Paste CSS or a page&rsquo;s HTML</label>
        <textarea id="theme-from-css" rows={6} spellCheck={false} value={css} onChange={(e) => { setCss(e.target.value); }} />
      </div>
      <button type="button" disabled={reading || busy || css.trim() === ''} onClick={() => { read({ css }); }}>Read colours from this CSS</button>

      {allowUrl ? (
        <div className="theme-from-url">
          <div className="branding-field">
            <label htmlFor="theme-from-url">Web address</label>
            <input id="theme-from-url" type="text" inputMode="url" placeholder="The page’s address, starting with https" value={address} onChange={(e) => { setAddress(e.target.value); }} />
          </div>
          <p className="theme-hint">
            The server will fetch this page and up to 8 of its stylesheets, over https, and keep none of it. Only you can start this, and it is
            recorded in the security log.
          </p>
          <button type="button" disabled={reading || busy || address.trim() === ''} onClick={() => { read({ url: address.trim() }); }}>Fetch and read colours</button>
        </div>
      ) : (
        <p className="theme-hint">Reading a page by its address is switched off on this server. Pasting the CSS works the same way.</p>
      )}

      {reading && <p className="admin-notice" role="status">Reading colours…</p>}
      {error !== undefined && <p className="admin-banner" role="alert">{error}</p>}

      {draft !== undefined && (
        <div className="theme-draft" role="region" aria-label="Draft theme">
          <h3 className="theme-subheading">Draft from {draft.source === 'url' ? 'a web address' : 'pasted CSS'}</h3>
          <h4 className="theme-subheading">How this was chosen</h4>
          <ul className="theme-reasons" aria-label="How this was chosen">
            {draft.reasons.map((reason) => <li key={reason}>{reason}</li>)}
          </ul>
          <h4 className="theme-subheading">Adjusted for contrast</h4>
          {draft.adjustments.length === 0 ? (
            <p className="theme-hint">No colour needed adjusting: every contrast check passes as found.</p>
          ) : (
            <ul className="theme-reasons" aria-label="Adjusted for contrast">
              {draft.adjustments.map((adjustment) => <li key={adjustment}>{adjustment}</li>)}
            </ul>
          )}
          <ThemeEditor
            theme={draft.draft}
            productName={productName}
            logos={logos}
            hasUploadedLogo={hasUploadedLogo}
            busy={busy}
            serverProblems={serverProblems}
            onSave={onCreate}
            isNew
          />
          <button type="button" onClick={() => { onDraft(undefined); }}>Discard this draft</button>
        </div>
      )}
    </section>
  );
}
