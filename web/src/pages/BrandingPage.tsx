/**
 * The admin Instance/Branding page (M3; design/atlas/login-admin.md's Instance row: "product name, logo upload").
 * It edits the product name and uploads a logo, an optional light-on-dark logo and a favicon. Uploads are validated
 * by content on the server (never by the file name) and stored under the data folder; the change lands without a
 * restart. The chrome theme file remains the fallback, so every slot has a "Remove" that returns it to the theme's
 * own value and a "Reset all" at the bottom.
 */
import { NotAllowed } from '../app/NotAllowed.tsx';
import { useCallback, useEffect, useState, type ChangeEvent, type FormEvent } from 'react';
import {
  BrandingError,
  deleteBrandingAsset,
  fetchBranding,
  resetBranding,
  setProductName,
  uploadBrandingAsset,
  type BrandingSlot,
  type BrandingState,
} from '../model/brandingClient.ts';
import type { AuthUser } from '../model/authClient.ts';
import { refreshChromeTheme } from '../theme/chromeTheme.ts';

interface Props {
  currentUser: AuthUser;
}

const SLOTS: { slot: BrandingSlot; label: string; hint: string; accept: string }[] = [
  { slot: 'logo', label: 'Logo', hint: 'PNG or SVG. Shown on the instance\'s light surfaces.', accept: '.png,.svg,image/png,image/svg+xml' },
  { slot: 'logo-light', label: 'Light-on-dark logo', hint: 'Optional. PNG or SVG, shown on a dark surface; falls back to the logo.', accept: '.png,.svg,image/png,image/svg+xml' },
  { slot: 'favicon', label: 'Favicon', hint: 'PNG, SVG or ICO, up to 256×256.', accept: '.png,.svg,.ico,image/png,image/svg+xml,image/x-icon' },
];

export function BrandingPage({ currentUser }: Props) {
  const [state, setState] = useState<BrandingState>();
  const [name, setName] = useState('');
  const [revision, setRevision] = useState(0);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string>();
  const [notice, setNotice] = useState<string>();
  const [confirmingReset, setConfirmingReset] = useState(false);
  // The slot whose Remove is waiting for a yes: removing an upload deletes the stored file, so it asks first, as the Reset buttons do.
  const [confirmingRemove, setConfirmingRemove] = useState<BrandingSlot>();

  const apply = useCallback((next: BrandingState, message: string): void => {
    setState(next);
    setName(next.productName);
    setRevision((value) => value + 1);
    setNotice(message);
    setBusy(false);
    void refreshChromeTheme();
  }, []);

  const fail = useCallback((cause: unknown, fallback: string): void => {
    setBusy(false);
    setError(cause instanceof BrandingError ? cause.message : fallback);
  }, []);

  useEffect(() => {
    void fetchBranding()
      .then((next) => {
        setState(next);
        setName(next.productName);
      })
      .catch((cause: unknown) => { fail(cause, 'Could not read the branding settings.'); });
  }, [fail]);

  if (currentUser.role !== 'Admin') {
    return <NotAllowed />;
  }

  const saveName = (event: FormEvent<HTMLFormElement>): void => {
    event.preventDefault();
    if (busy) {
      return;
    }
    setBusy(true);
    setError(undefined);
    setNotice(undefined);
    void setProductName(name).then((next) => { apply(next, name.trim() === '' ? 'Product name reset.' : 'Product name saved.'); }).catch((cause: unknown) => { fail(cause, 'Could not save the product name.'); });
  };

  const upload = (slot: BrandingSlot, event: ChangeEvent<HTMLInputElement>): void => {
    const file = event.target.files?.[0];
    event.target.value = '';
    if (file === undefined || busy) {
      return;
    }
    setBusy(true);
    setError(undefined);
    setNotice(undefined);
    void uploadBrandingAsset(slot, file).then((next) => { apply(next, 'Image uploaded.'); }).catch((cause: unknown) => { fail(cause, 'Could not upload the image.'); });
  };

  const remove = (slot: BrandingSlot): void => {
    if (busy) {
      return;
    }
    setBusy(true);
    setError(undefined);
    setNotice(undefined);
    void deleteBrandingAsset(slot).then((next) => { apply(next, 'Image reset to the theme default.'); }).catch((cause: unknown) => { fail(cause, 'Could not remove the image.'); });
  };

  const reset = (): void => {
    if (busy) {
      return;
    }
    setBusy(true);
    setError(undefined);
    setNotice(undefined);
    void resetBranding().then((next) => { apply(next, 'All branding reset to the theme default.'); }).catch((cause: unknown) => { fail(cause, 'Could not reset the branding.'); });
  };

  if (state === undefined) {
    return (
      <div className="admin-page">
        <h1 className="admin-title">Branding</h1>
        {error !== undefined ? <p className="page-message" role="alert">Cannot read the branding settings: {error}</p> : <p className="page-message">Loading branding…</p>}
      </div>
    );
  }

  const anyOverridden = state.overridden.productName || state.overridden.logo || state.overridden.logoLight || state.overridden.favicon;

  return (
    <div className="admin-page">
      <h1 className="admin-title">Branding</h1>
      <p className="admin-intro">
        The instance&rsquo;s name and marks, shown on the sign-in page and the app shell. Uploaded files are stored in
        this instance&rsquo;s data folder and applied without a restart. Removing an upload (or the product name) returns
        that part to the chrome theme&rsquo;s value: {state.themeProductName}.
      </p>
      {error !== undefined && <p className="admin-banner" role="alert">{error}</p>}
      {notice !== undefined && <p className="admin-notice" role="status">{notice}</p>}

      <form className="admin-form branding-name-form" onSubmit={saveName}>
        <div className="branding-field">
          <label htmlFor="branding-product-name">Product name</label>
          <input
            id="branding-product-name"
            type="text"
            value={name}
            maxLength={80}
            disabled={busy}
            onChange={(e) => { setName(e.target.value); }}
          />
        </div>
        <button type="submit" disabled={busy}>Save name</button>
        {state.overridden.productName && <span className="branding-badge">Overridden</span>}
      </form>

      <h2 className="admin-subtitle">Logo and favicon</h2>
      <ul className="branding-list">
        {SLOTS.map(({ slot, label, hint, accept }) => {
          const overridden = state.overridden[slot === 'logo-light' ? 'logoLight' : slot];
          const url = slot === 'logo' ? state.logo : slot === 'logo-light' ? state.logoLight : state.favicon;
          return (
            <li key={slot} className="branding-row">
              <div className="branding-preview" aria-hidden="true">
                {url !== null ? <img src={`${url}?v=${String(revision)}`} alt="" /> : <span className="branding-preview-empty">Theme default</span>}
              </div>
              <div className="branding-row-body">
                <p className="branding-row-label">
                  {label}
                  {overridden && <span className="branding-badge">Overridden</span>}
                </p>
                <p className="branding-row-hint">{hint}</p>
                <div className="branding-row-actions">
                  <label className="branding-upload">
                    <span>{overridden ? 'Replace…' : 'Upload…'}</span>
                    <input type="file" accept={accept} aria-label={`${overridden ? 'Replace' : 'Upload'} ${label}`} disabled={busy} onChange={(e) => { upload(slot, e); }} />
                  </label>
                  {overridden && confirmingRemove !== slot && (
                    <button type="button" onClick={() => { setConfirmingRemove(slot); }} disabled={busy} aria-label={`Remove ${label}`}>Remove</button>
                  )}
                  {overridden && confirmingRemove === slot && (
                    <span role="group" aria-label={`Confirm remove ${label}`}>
                      <span className="branding-row-hint">This deletes the uploaded file and returns to the theme&rsquo;s image.</span>
                      <button type="button" disabled={busy} onClick={() => { setConfirmingRemove(undefined); remove(slot); }}>Yes, remove {label}</button>
                      <button type="button" onClick={() => { setConfirmingRemove(undefined); }}>Cancel</button>
                    </span>
                  )}
                </div>
              </div>
            </li>
          );
        })}
      </ul>

      <div className="branding-reset-row">
        {confirmingReset ? (
          <div role="group" aria-label="Confirm reset">
            <p className="branding-row-hint">
              This removes the uploaded logo, light-on-dark logo and favicon, and the product name, returning all of them to the theme&rsquo;s values.
            </p>
            <button type="button" disabled={busy} onClick={() => { setConfirmingReset(false); reset(); }}>Yes, reset all branding</button>
            <button type="button" onClick={() => { setConfirmingReset(false); }}>Cancel</button>
          </div>
        ) : (
          <button type="button" onClick={() => { setConfirmingReset(true); }} disabled={busy || !anyOverridden}>Reset all branding</button>
        )}
      </div>
    </div>
  );
}
