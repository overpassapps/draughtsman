/**
 * The admin AI provider page (M3; design/atlas/login-admin.md's Integrations row: provider, model, endpoint, and a
 * Test button). An admin picks the provider, endpoint and model, pastes an API key and tests the connection, all
 * applied without a restart; draughtsman.yaml stays the fallback and "Reset" returns to it.
 *
 * The key is write-only: this page never receives one. It shows whether a key is set and where the one in effect
 * comes from (the environment variable wins), and offers Replace and Clear. The typed key lives in a local state
 * slot only until it is sent, then is dropped. Ollama needs no key, and the page says so rather than asking for one.
 */
import { NotAllowed } from '../app/NotAllowed.tsx';
import { useCallback, useEffect, useState, type FormEvent } from 'react';
import {
  AiSettingsError,
  clearAiKey,
  fetchAiSettings,
  resetAiSettings,
  saveAiSettings,
  setAiKey,
  storedSecretNote,
  testAiConnection,
  type AiConnectionResult,
  type AiSettingsState,
  type KeyRingState,
} from '../model/aiSettingsClient.ts';
import type { AuthUser } from '../model/authClient.ts';

interface Props {
  currentUser: AuthUser;
}

const PROVIDER_LABELS: Record<string, string> = {
  ollama: 'Ollama (local)',
  openai: 'OpenAI or compatible',
  anthropic: 'Anthropic',
  'azure-openai': 'Azure OpenAI',
};

/** The key's state in a sentence, with the server's reason added while a key that exists is being held back from this endpoint. */
function keySummary(state: AiSettingsState): string {
  const base = keyState(state);
  return state.key.withheld === undefined ? base : `${base} ${state.key.withheld}`;
}

function keyState(state: AiSettingsState): string {
  const { key } = state;
  if (key.source === 'environment') {
    return key.storedSet
      ? `A key is set by the environment variable ${key.environmentVariable} and is in effect. The key stored here is not used while that variable is set.`
      : `A key is set by the environment variable ${key.environmentVariable} and is in effect. A key stored here would not be used while that variable is set.`;
  }
  if (key.source === 'stored') {
    return 'A key is set (stored encrypted on this server). It is never shown again.';
  }
  if (key.storedUnreadable) {
    // The server says which: a path an earlier version tied it to, a key ring that cannot be opened, or damage, and what to do about each.
    return storedSecretNote(state.keyRing, 'AI API key')?.message ?? 'A stored key could not be decrypted (the server’s key ring changed, or its protection is not available). Paste it again.';
  }
  return key.required
    ? `No key is set. Paste one below, or set the environment variable ${key.environmentVariable}.`
    : 'No key is needed for a local model, and nothing leaves your network.';
}

/**
 * Whether the key ring that encrypts the stored key is protected at rest. While it is not, anyone who can read the
 * server's data folder can decrypt the stored key, so this says so, and says what to set. A leftover pre-migration
 * backup holds the old plain-text ring and is called out for as long as it exists, protected or not.
 */
function KeyRingNotice({ keyRing }: { keyRing: KeyRingState }) {
  const warn = keyRing.state === 'unprotected' || keyRing.unreadableKeys > 0;
  const secret = storedSecretNote(keyRing, 'AI API key');
  return (
    <section className="ai-keyring" role="note" aria-label="Key ring protection" data-state={keyRing.state} data-reason={keyRing.reason}>
      <p className={warn ? 'admin-banner' : 'ai-key-summary'}>{keyRing.message}</p>
      {secret !== undefined && secret.state !== 'unreadable' && (
        <p className={secret.state === 'legacy' ? 'admin-banner' : 'admin-notice'} data-testid="ai-stored-secret">
          {secret.message}
        </p>
      )}
      {keyRing.backup !== undefined && (
        <p className="admin-notice">
          A backup of the previous, unprotected key ring is kept in <code>{keyRing.backup}</code> inside the data
          folder. Once Test connection shows the stored key still works, delete that folder: until then the stored key
          is as exposed as it was before.
        </p>
      )}
    </section>
  );
}

export function AiSettingsPage({ currentUser }: Props) {
  const [confirmingReset, setConfirmingReset] = useState(false);
  const [state, setState] = useState<AiSettingsState>();
  const [provider, setProvider] = useState('');
  const [model, setModel] = useState('');
  const [endpoint, setEndpoint] = useState('');
  const [apiVersion, setApiVersion] = useState('');
  const [apiKey, setApiKey] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string>();
  const [notice, setNotice] = useState<string>();
  const [test, setTest] = useState<AiConnectionResult>();

  const show = useCallback((next: AiSettingsState): void => {
    setState(next);
    setProvider(next.overrides.provider ?? '');
    setModel(next.overrides.model ?? '');
    setEndpoint(next.overrides.endpoint ?? '');
    setApiVersion(next.overrides.apiVersion ?? '');
  }, []);

  const fail = useCallback((cause: unknown, fallback: string): void => {
    setBusy(false);
    setError(cause instanceof AiSettingsError ? cause.message : fallback);
  }, []);

  const isAdmin = currentUser.role === 'Admin';
  useEffect(() => {
    if (!isAdmin) {
      return;
    }
    void fetchAiSettings()
      .then(show)
      .catch((cause: unknown) => { fail(cause, 'Could not read the AI settings.'); });
  }, [isAdmin, show, fail]);

  if (!isAdmin) {
    return <NotAllowed />;
  }

  /** Runs one call, then shows what it returned. Any earlier notice or test result is stale the moment something changes. */
  const run = (call: () => Promise<AiSettingsState>, message: string, failure: string): void => {
    if (busy) {
      return;
    }
    setBusy(true);
    setError(undefined);
    setNotice(undefined);
    setTest(undefined);
    void call()
      .then((next) => {
        show(next);
        setNotice(message);
        setBusy(false);
      })
      .catch((cause: unknown) => { fail(cause, failure); });
  };

  const saveSettings = (event: FormEvent<HTMLFormElement>): void => {
    event.preventDefault();
    run(() => saveAiSettings({ provider, model, endpoint, apiVersion }), 'AI settings saved.', 'Could not save the AI settings.');
  };

  const saveKey = (event: FormEvent<HTMLFormElement>): void => {
    event.preventDefault();
    if (apiKey.trim() === '') {
      return;
    }
    const typed = apiKey;
    setApiKey('');
    run(() => setAiKey(typed), 'API key saved.', 'Could not save the API key.');
  };

  const runTest = (): void => {
    if (busy) {
      return;
    }
    setBusy(true);
    setError(undefined);
    setNotice(undefined);
    setTest(undefined);
    void testAiConnection()
      .then((result) => {
        setTest(result);
        setBusy(false);
      })
      .catch((cause: unknown) => { fail(cause, 'Could not run the connection test.'); });
  };

  if (state === undefined) {
    return (
      <div className="admin-page">
        <h1 className="admin-title">AI settings</h1>
        {error !== undefined ? <p className="page-message" role="alert">Cannot read the AI settings: {error}</p> : <p className="page-message">Loading AI settings…</p>}
      </div>
    );
  }

  const anythingOverridden = Object.values(state.overrides).some((value) => value !== null) || state.key.storedSet;
  const shownProvider = provider !== '' ? provider : state.file.provider ?? '';
  // The server omits a null (DocumentJson.Options), so an unset file provider arrives as an absent key, not null or ''.
  const fileProviderName = (state.file.provider as string | null | undefined)?.trim() ?? '';
  const fileProvider = fileProviderName === '' ? 'none' : (PROVIDER_LABELS[fileProviderName] ?? fileProviderName);

  return (
    <div className="admin-page">
      <h1 className="admin-title">AI settings</h1>
      <p className="admin-intro">
        Bring your own AI: choose a provider, or a local model that keeps everything on your network. Changes apply
        at once, with no restart. Anything left blank falls back to <code>draughtsman.yaml</code>.
      </p>
      {error !== undefined && <p className="admin-banner" role="alert">{error}</p>}
      {notice !== undefined && <p className="admin-notice" role="status">{notice}</p>}
      <p className="ai-status" data-enabled={state.enabled}>
        {state.enabled
          ? `AI is on: ${state.effective.provider ?? ''} · ${state.effective.model ?? ''}.`
          : `AI is off${state.problem !== null ? `: ${state.problem}` : '.'}`}
      </p>

      <form className="admin-form ai-form" onSubmit={saveSettings}>
        <div className="branding-field">
          <label htmlFor="ai-provider">Provider</label>
          <select id="ai-provider" value={provider} disabled={busy} onChange={(e) => { setProvider(e.target.value); }}>
            <option value="">Use draughtsman.yaml ({fileProvider})</option>
            {state.providers.map((name) => (
              <option key={name} value={name}>{PROVIDER_LABELS[name] ?? name}</option>
            ))}
          </select>
        </div>
        <div className="branding-field">
          <label htmlFor="ai-model">{shownProvider === 'azure-openai' ? 'Deployment name' : 'Model'}</label>
          <input id="ai-model" type="text" value={model} maxLength={200} placeholder={state.effective.model ?? ''} disabled={busy} onChange={(e) => { setModel(e.target.value); }} />
        </div>
        <div className="branding-field">
          <label htmlFor="ai-endpoint">Endpoint</label>
          <input id="ai-endpoint" type="url" value={endpoint} placeholder={state.effective.endpoint ?? 'https://…'} disabled={busy} onChange={(e) => { setEndpoint(e.target.value); }} />
          <span className="branding-row-hint">A plain http or https URL. Never put a key in it.</span>
        </div>
        {shownProvider === 'azure-openai' && (
          <div className="branding-field">
            <label htmlFor="ai-api-version">API version</label>
            <input id="ai-api-version" type="text" value={apiVersion} maxLength={40} placeholder={state.effective.apiVersion ?? '2024-10-21'} disabled={busy} onChange={(e) => { setApiVersion(e.target.value); }} />
          </div>
        )}
        <button type="submit" disabled={busy}>Save settings</button>
      </form>

      <h2 className="admin-subtitle">API key</h2>
      <p className="ai-key-summary" data-source={state.key.source}>{keySummary(state)}</p>
      {state.keyRing !== undefined && <KeyRingNotice keyRing={state.keyRing} />}
      <form className="admin-form ai-form" onSubmit={saveKey}>
        <div className="branding-field">
          <label htmlFor="ai-api-key">{state.key.storedSet ? 'Replace API key' : 'API key'}</label>
          <input
            id="ai-api-key"
            type="password"
            value={apiKey}
            autoComplete="off"
            spellCheck={false}
            disabled={busy}
            onChange={(e) => { setApiKey(e.target.value); }}
          />
          <span className="branding-row-hint">Write-only: once saved it is stored encrypted and cannot be shown again.</span>
        </div>
        <button type="submit" disabled={busy || apiKey.trim() === ''}>{state.key.storedSet ? 'Replace key' : 'Save key'}</button>
        {state.key.storedSet && (
          <button type="button" disabled={busy} onClick={() => { run(clearAiKey, 'Stored API key cleared.', 'Could not clear the API key.'); }}>Clear key</button>
        )}
      </form>

      <h2 className="admin-subtitle">Test connection</h2>
      <div className="ai-test-row">
        <button type="button" disabled={busy || !state.enabled} onClick={runTest}>Test connection</button>
        {!state.enabled && <span className="branding-row-hint">Set a provider and model first.</span>}
      </div>
      {test !== undefined && (
        <p className={test.ok ? 'admin-notice' : 'admin-banner'} role={test.ok ? 'status' : 'alert'}>{test.message}</p>
      )}

      <div className="branding-reset-row">
        {confirmingReset ? (
          <div role="group" aria-label="Confirm reset">
            <p className="branding-row-hint">
              This returns every AI setting to draughtsman.yaml and permanently removes the stored API key. You would need to enter the key again.
            </p>
            <button type="button" disabled={busy} onClick={() => { setConfirmingReset(false); run(resetAiSettings, 'Reset to draughtsman.yaml. The stored key was forgotten.', 'Could not reset the AI settings.'); }}>
              Yes, reset and remove the key
            </button>
            <button type="button" onClick={() => { setConfirmingReset(false); }}>Cancel</button>
          </div>
        ) : (
          <button type="button" disabled={busy || !anythingOverridden} onClick={() => { setConfirmingReset(true); }}>
            Reset to draughtsman.yaml
          </button>
        )}
      </div>
    </div>
  );
}
