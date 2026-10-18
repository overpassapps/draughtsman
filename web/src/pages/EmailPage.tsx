/**
 * The admin Email page (design/atlas/password-recovery.md, amendment 2026-10-01): outgoing email set up in the browser instead of only in
 * draughtsman.yaml. A form (mail server, port, encryption, user name, password, From, Reply-to and the public address), ready-made starting
 * points that fill it without storing anything, Save (validated and applied at once, no restart), and two test buttons that send one message to the
 * signed-in administrator: one for what is saved, one for the form as typed without saving it.
 *
 * Precedence: a value draughtsman.yaml (or the environment, for the password) sets wins and its field is read-only with the reason shown;
 * everything else is editable. The password is write-only: this page never receives one. It shows whether one is set and where it comes from,
 * and offers Keep, Replace and Clear; a typed password lives in a state slot only until it is sent. Limits stated on the page: implicit TLS
 * on port 465 is not supported, and the server connects to whatever mail server an administrator enters (so only administrators may).
 */
import { NotAllowed } from '../app/NotAllowed.tsx';
import { useCallback, useEffect, useState, type FormEvent, type ReactNode } from 'react';
import type { AuthUser } from '../model/authClient.ts';
import {
  EmailSettingsError,
  fetchEmailSettings,
  removeEmailSettings,
  saveEmailSettings,
  sendTestEmail,
  type EmailField,
  type EmailFormInput,
  type EmailSettings,
  type EmailTestResult,
  type PasswordAction,
} from '../model/emailClient.ts';
import { storedSecretNote } from '../model/aiSettingsClient.ts';
import { applyPreset, EMAIL_PRESETS } from '../model/emailPresets.ts';

interface Props {
  currentUser: AuthUser;
}

type Busy = 'save' | 'test-saved' | 'test-values' | 'remove';

function toForm(settings: EmailSettings): EmailFormInput {
  return {
    from: settings.from ?? '',
    replyTo: settings.replyTo ?? '',
    host: settings.host ?? '',
    port: String(settings.port),
    tls: settings.tls,
    user: settings.user ?? '',
    publicUrl: settings.publicUrl ?? '',
    passwordAction: 'keep',
    password: '',
  };
}

function sameFields(a: EmailFormInput, b: EmailFormInput): boolean {
  return (
    a.from.trim() === b.from.trim() &&
    a.replyTo.trim() === b.replyTo.trim() &&
    a.host.trim() === b.host.trim() &&
    a.port.trim() === b.port.trim() &&
    a.tls === b.tls &&
    a.user.trim() === b.user.trim() &&
    a.publicUrl.trim() === b.publicUrl.trim()
  );
}

interface TextFieldProps {
  id: string;
  label: string;
  value: string;
  onChange: (value: string) => void;
  locked?: string | undefined;
  hint?: ReactNode;
  error?: string | undefined;
  type?: 'text' | 'email' | 'url' | 'number';
  maxLength?: number;
  placeholder?: string;
  disabled: boolean;
}

/** One labelled input. A locked field stays in the tab order (read-only, not disabled) so its reason is announced with it. */
function TextField({ id, label, value, onChange, locked, hint, error, type = 'text', maxLength, placeholder, disabled }: TextFieldProps) {
  const describedBy = [hint !== undefined ? `${id}-hint` : undefined, locked !== undefined ? `${id}-locked` : undefined, error !== undefined ? `${id}-error` : undefined].filter((v) => v !== undefined).join(' ');
  return (
    <div className="email-field">
      <label htmlFor={id}>{label}</label>
      <input
        id={id}
        type={type}
        value={value}
        readOnly={locked !== undefined}
        aria-readonly={locked !== undefined ? true : undefined}
        aria-invalid={error !== undefined ? true : undefined}
        aria-describedby={describedBy === '' ? undefined : describedBy}
        disabled={disabled}
        maxLength={maxLength}
        placeholder={placeholder}
        autoComplete="off"
        spellCheck={false}
        min={type === 'number' ? 1 : undefined}
        max={type === 'number' ? 65535 : undefined}
        onChange={(e) => {
          onChange(e.target.value);
        }}
      />
      {hint !== undefined && (
        <span id={`${id}-hint`} className="email-hint">
          {hint}
        </span>
      )}
      {locked !== undefined && (
        <span id={`${id}-locked`} className="email-hint email-locked" data-testid={`${id}-locked`}>
          {locked}
        </span>
      )}
      {error !== undefined && (
        <span id={`${id}-error`} className="email-error" role="alert">
          {error}
        </span>
      )}
    </div>
  );
}

function PasswordSection({
  settings,
  action,
  password,
  disabled,
  error,
  onAction,
  onPassword,
}: {
  settings: EmailSettings;
  action: PasswordAction;
  password: string;
  disabled: boolean;
  error?: string | undefined;
  onAction: (action: PasswordAction) => void;
  onPassword: (value: string) => void;
}) {
  const locked = settings.locked.password;
  const saved = settings.passwordKind === 'saved' || settings.passwordUnreadable;
  return (
    <fieldset className="email-password" aria-describedby="email-password-state">
      <legend>Mail server password</legend>
      <p id="email-password-state" className="email-password-state" data-testid="email-password-state">
        {settings.passwordUnreadable
          ? `Password: the saved one could not be read. ${storedSecretNote(settings.keyRing, 'SMTP password')?.message ?? 'The server’s key ring changed. Enter it again.'}`
          : settings.passwordSet
            ? `Password: set (${settings.passwordSource ?? 'saved'}). It is never shown again.`
            : 'Password: not set.'}
      </p>
      {locked !== undefined ? (
        <p className="email-hint email-locked" data-testid="email-password-locked">
          {locked}
        </p>
      ) : (
        <>
          {saved && (
            <div className="email-radios" role="radiogroup" aria-label="What to do with the saved password">
              {(
                [
                  ['keep', 'Keep it'],
                  ['replace', 'Replace it with the one typed below'],
                  ['clear', 'Remove it'],
                ] as const
              ).map(([value, text]) => (
                <label key={value} className="email-radio">
                  <input
                    type="radio"
                    name="email-password-action"
                    value={value}
                    checked={action === value}
                    disabled={disabled}
                    onChange={() => {
                      onAction(value);
                    }}
                  />{' '}
                  {text}
                </label>
              ))}
            </div>
          )}
          <div className="email-field">
            <label htmlFor="email-password">{saved ? 'New password' : 'Password'}</label>
            <input
              id="email-password"
              type="password"
              value={password}
              autoComplete="new-password"
              spellCheck={false}
              maxLength={512}
              disabled={disabled}
              aria-invalid={error !== undefined ? true : undefined}
              aria-describedby={error !== undefined ? 'email-password-error email-password-hint' : 'email-password-hint'}
              onChange={(e) => {
                onPassword(e.target.value);
              }}
            />
            <span id="email-password-hint" className="email-hint">
              Write-only: once saved it is stored encrypted on this server and cannot be shown again. Leave it blank for a server that needs no sign-in.
            </span>
            {error !== undefined && (
              <span id="email-password-error" className="email-error" role="alert">
                {error}
              </span>
            )}
          </div>
        </>
      )}
    </fieldset>
  );
}

/** Whether the key ring that encrypts the saved password is protected at rest; says so when it is not (the same facts as the AI page). */
function KeyRingNote({ settings, replacing }: { settings: EmailSettings; replacing: boolean }) {
  const keyRing = settings.keyRing;
  const secret = storedSecretNote(keyRing, 'SMTP password');
  if (secret !== undefined && secret.state !== 'unreadable') {
    // Readable, but worth saying: an earlier version tied it to the install folder and this start moved it (or could not).
    return (
      <p className={secret.state === 'legacy' ? 'admin-banner' : 'admin-notice'} role="note" data-testid="email-stored-secret">
        {secret.message}
      </p>
    );
  }

  if (keyRing === undefined || (settings.passwordKind !== 'saved' && !replacing)) {
    return null;
  }
  if (keyRing.state !== 'unprotected' && keyRing.unreadableKeys === 0) {
    return null;
  }
  return (
    <p className="admin-banner" role="note" data-testid="email-keyring">
      The key ring that encrypts the saved password is not protected at rest, so anyone who can read this server’s data folder could decrypt it. Protect it
      (see “Protecting the key ring” in the README), or put the password in an environment variable or a password file instead.
    </p>
  );
}

export function EmailPage({ currentUser }: Props) {
  const [settings, setSettings] = useState<EmailSettings>();
  const [loadError, setLoadError] = useState<string>();
  const [form, setForm] = useState<EmailFormInput>();
  const [presetId, setPresetId] = useState('');
  const [busy, setBusy] = useState<Busy>();
  const [error, setError] = useState<string>();
  const [fieldErrors, setFieldErrors] = useState<Partial<Record<string, string>>>({});
  const [notice, setNotice] = useState<string>();
  const [result, setResult] = useState<EmailTestResult & { which: string }>();
  const [confirmingRemove, setConfirmingRemove] = useState(false);
  const isAdmin = currentUser.role === 'Admin';

  const show = useCallback((next: EmailSettings): void => {
    setSettings(next);
    setForm(toForm(next));
    setFieldErrors({});
  }, []);

  const load = useCallback((): void => {
    void fetchEmailSettings()
      .then((next) => {
        setLoadError(undefined);
        show(next);
      })
      .catch((cause: unknown) => {
        setLoadError(cause instanceof Error ? cause.message : 'Could not read the email settings.');
      });
  }, [show]);

  useEffect(() => {
    if (isAdmin) {
      load();
    }
  }, [isAdmin, load]);

  if (!isAdmin) {
    return <NotAllowed />;
  }

  if (settings === undefined || form === undefined) {
    return (
      <div className="admin-page">
        <h1 className="admin-title">Email</h1>
        {loadError !== undefined ? (
          <p className="page-message" role="alert">
            {loadError} <button type="button" onClick={load}>Retry</button>
          </p>
        ) : (
          <p className="page-message">Loading email settings…</p>
        )}
      </div>
    );
  }

  const locked = settings.locked;
  const lockedFile = settings.provider === 'file';
  const baseline = toForm(settings);
  const dirty = !sameFields(form, baseline) || form.passwordAction !== 'keep';
  const working = busy !== undefined;
  const preset = EMAIL_PRESETS.find((p) => p.id === presetId);

  const edit = (patch: Partial<EmailFormInput>): void => {
    setForm({ ...form, ...patch });
    setNotice(undefined);
    setResult(undefined);
  };

  const fail = (cause: unknown, fallback: string): void => {
    setBusy(undefined);
    if (cause instanceof EmailSettingsError) {
      setError(cause.message);
      setFieldErrors(cause.fieldErrors);
    } else {
      setError(fallback);
    }
  };

  const begin = (what: Busy): boolean => {
    if (busy !== undefined) {
      return false;
    }
    setBusy(what);
    setError(undefined);
    setFieldErrors({});
    setNotice(undefined);
    setResult(undefined);
    setConfirmingRemove(false);
    return true;
  };

  const save = (event: FormEvent<HTMLFormElement>): void => {
    event.preventDefault();
    if (!dirty || !begin('save')) {
      return;
    }
    void saveEmailSettings(form)
      .then((next) => {
        show(next);
        setNotice('Saved. The email settings apply now: no restart is needed.');
        setBusy(undefined);
      })
      .catch((cause: unknown) => {
        fail(cause, 'Could not save the email settings.');
      });
  };

  const test = (withValues: boolean): void => {
    if (!begin(withValues ? 'test-values' : 'test-saved')) {
      return;
    }
    void sendTestEmail(withValues ? form : undefined)
      .then((next) => {
        setResult({ ...next, which: withValues ? 'the values in the form (nothing was saved)' : 'the saved settings' });
        setBusy(undefined);
      })
      .catch((cause: unknown) => {
        fail(cause, 'Could not send the test email.');
      });
  };

  const remove = (): void => {
    if (!begin('remove')) {
      return;
    }
    void removeEmailSettings()
      .then((next) => {
        show(next);
        setNotice('The saved email settings and password were removed. Only draughtsman.yaml applies now.');
        setBusy(undefined);
      })
      .catch((cause: unknown) => {
        fail(cause, 'Could not remove the saved email settings.');
      });
  };

  const choosePreset = (id: string): void => {
    setPresetId(id);
    const chosen = EMAIL_PRESETS.find((p) => p.id === id);
    if (chosen !== undefined) {
      setForm(applyPreset(form, chosen, locked));
      setNotice(undefined);
      setResult(undefined);
    }
  };

  const fieldProps = (field: EmailField): { locked?: string | undefined; error?: string | undefined; disabled: boolean } => ({ locked: locked[field], error: fieldErrors[field], disabled: working });
  const formError = fieldErrors.form;

  return (
    <div className="admin-page email-page">
      <h1 className="admin-title">Email</h1>
      <p className="admin-intro">
        Outgoing email lets people reset their own passwords (“Forgot password?”). Enter your mail server below; changes apply at once, with no restart. Settings in{' '}
        <code>draughtsman.yaml</code> or the environment win and are shown read-only with the reason; everything else is yours to change here.
      </p>

      {settings.problems.length > 0 && (
        <div className="admin-banner email-problems" role="alert">
          <ul>
            {settings.problems.map((problem) => (
              <li key={problem}>{problem}</li>
            ))}
          </ul>
        </div>
      )}
      {error !== undefined && (
        <p className="admin-banner" role="alert" data-testid="email-error">
          {error}
        </p>
      )}
      {notice !== undefined && (
        <p className="admin-notice" role="status" data-testid="email-notice">
          {notice}
        </p>
      )}

      <p className="email-status" data-testid="email-status" data-configured={settings.configured}>
        {settings.configured
          ? settings.provider === 'file'
            ? `Email is set up to write messages to ${settings.outboxFolder ?? 'a folder'}; nothing is sent.`
            : `Email is set up. Messages go through ${settings.host ?? ''}:${String(settings.port)}.`
          : 'Email is not set up yet. “Forgot password?” is not offered on the sign-in page; people ask an administrator for a reset link (Admin, Users, Create reset link).'}
      </p>

      <form className="email-form" onSubmit={save} aria-label="Outgoing email settings" noValidate>
        <div className="email-field">
          <label htmlFor="email-preset">Start from</label>
          <select
            id="email-preset"
            value={presetId}
            disabled={working || lockedFile}
            aria-describedby="email-preset-hint"
            onChange={(e) => {
              choosePreset(e.target.value);
            }}
          >
            <option value="">Choose a provider…</option>
            {EMAIL_PRESETS.map((p) => (
              <option key={p.id} value={p.id}>
                {p.name}
              </option>
            ))}
          </select>
          <span id="email-preset-hint" className="email-hint">
            Fills in the server settings below. Nothing is saved until you press Save.
          </span>
          {preset !== undefined && (
            <p className="email-gotcha" role="note" data-testid="email-gotcha">
              <strong>{preset.name}:</strong> {preset.gotcha}
            </p>
          )}
        </div>

        <TextField
          id="email-host"
          label="Mail server"
          value={form.host}
          onChange={(host) => {
            edit({ host });
          }}
          placeholder="smtp.example.com"
          maxLength={253}
          hint="A host name such as smtp.example.com, not a web address. The port has its own box."
          {...fieldProps('host')}
        />
        <TextField
          id="email-port"
          label="Port"
          type="number"
          value={form.port}
          onChange={(port) => {
            edit({ port });
          }}
          hint="Usually 587."
          {...fieldProps('port')}
        />
        <div className="email-field">
          <label htmlFor="email-tls">Encryption</label>
          <select
            id="email-tls"
            value={form.tls}
            disabled={working || locked.tls !== undefined}
            aria-describedby={['email-tls-hint', locked.tls !== undefined ? 'email-tls-locked' : undefined].filter((v) => v !== undefined).join(' ')}
            onChange={(e) => {
              edit({ tls: e.target.value });
            }}
          >
            <option value="starttls">STARTTLS (recommended)</option>
            <option value="none">None</option>
          </select>
          <span id="email-tls-hint" className="email-hint">
            STARTTLS is required unless the server is on this machine or needs no user name. Port 465 (implicit TLS) is not supported: choose your provider’s port 587
            STARTTLS option instead.
          </span>
          {locked.tls !== undefined && (
            <span id="email-tls-locked" className="email-hint email-locked" data-testid="email-tls-locked">
              {locked.tls}
            </span>
          )}
          {fieldErrors.tls !== undefined && (
            <span className="email-error" role="alert">
              {fieldErrors.tls}
            </span>
          )}
        </div>
        <TextField
          id="email-user"
          label="User name"
          value={form.user}
          onChange={(user) => {
            edit({ user });
          }}
          maxLength={256}
          hint="Leave blank for a server that needs no sign-in."
          {...fieldProps('user')}
        />

        <PasswordSection
          settings={settings}
          action={form.passwordAction}
          password={form.password}
          disabled={working}
          error={fieldErrors.password}
          onAction={(passwordAction) => {
            edit({ passwordAction, password: passwordAction === 'replace' ? form.password : '' });
          }}
          onPassword={(password) => {
            edit({ password, passwordAction: password !== '' ? 'replace' : settings.passwordKind === 'saved' ? form.passwordAction : 'keep' });
          }}
        />
        <KeyRingNote settings={settings} replacing={form.passwordAction === 'replace'} />

        <TextField
          id="email-from"
          label="From address"
          value={form.from}
          onChange={(from) => {
            edit({ from });
          }}
          maxLength={320}
          placeholder="Draughtsman <noreply@example.com>"
          hint="name@example.com or Name <name@example.com>. Most providers only send from an address or domain you have verified with them."
          {...fieldProps('from')}
        />
        <TextField
          id="email-reply-to"
          label="Reply-to address (optional)"
          value={form.replyTo}
          onChange={(replyTo) => {
            edit({ replyTo });
          }}
          maxLength={320}
          hint="Where a reply should go, if not to the From address."
          {...fieldProps('replyTo')}
        />
        <TextField
          id="email-public-url"
          label="Public address"
          type="url"
          value={form.publicUrl}
          onChange={(publicUrl) => {
            edit({ publicUrl });
          }}
          maxLength={2048}
          placeholder="https://draughtsman.example.com"
          hint="The address people reach this instance at. Reset links in emails are built on it."
          {...fieldProps('publicUrl')}
        />
        {formError !== undefined && (
          <p className="email-error" role="alert">
            {formError}
          </p>
        )}

        <div className="email-actions">
          <button type="submit" className="email-primary" disabled={working || !dirty}>
            {busy === 'save' ? 'Saving…' : 'Save'}
          </button>
          {dirty && !working && <span className="email-hint">You have unsaved changes.</span>}
        </div>
      </form>

      <dl className="email-summary">
        <dt>Forgot password?</dt>
        <dd>
          {settings.selfServiceAvailable
            ? `On. A link is emailed and works for ${String(settings.emailedLinkMinutes)} minutes.`
            : settings.selfServiceEnabled
              ? 'Off: it needs working email and a public address.'
              : 'Off: auth.passwordReset.selfService is false.'}
        </dd>
        <dt>Administrator links</dt>
        <dd>Work for {String(settings.adminLinkHours)} hours. They need no email.</dd>
      </dl>

      <h2 className="admin-subtitle">Test</h2>
      <p className="admin-intro">
        Each test sends one message to your own address ({currentUser.email}), and only there. This server connects to whatever mail server you enter, which is why only
        administrators can change these settings. If a test fails you are told which kind of problem it was, never the mail server’s own reply.
      </p>
      <div className="email-actions">
        <button type="button" onClick={() => { test(false); }} disabled={working || !settings.configured}>
          {busy === 'test-saved' ? 'Sending…' : 'Test saved settings'}
        </button>
        <button type="button" onClick={() => { test(true); }} disabled={working || (form.host.trim() === '' && !lockedFile && locked.host === undefined)}>
          {busy === 'test-values' ? 'Sending…' : 'Test these values'}
        </button>
      </div>
      <p className="email-hint">“Test these values” sends through what is typed above without saving it, so you can check a password before you save it.</p>
      {result !== undefined &&
        (result.ok ? (
          <p className="admin-notice" role="status" data-testid="email-test-result">
            <strong>Sent</strong> using {result.which}. {result.message}
          </p>
        ) : (
          <p className="admin-banner" role="alert" data-testid="email-test-result">
            <strong>Failed</strong> using {result.which}. {result.message}
          </p>
        ))}

      <div className="branding-reset-row">
        {confirmingRemove ? (
          <div role="group" aria-label="Confirm removal">
            <p className="email-hint">
              This forgets everything saved on this page, including the saved password, and returns to <code>draughtsman.yaml</code> alone. If that file sets no email, email turns off
              and “Forgot password?” goes away.
            </p>
            <button type="button" onClick={remove} disabled={working}>
              Yes, remove the saved settings
            </button>{' '}
            <button type="button" onClick={() => { setConfirmingRemove(false); }}>
              Cancel
            </button>
          </div>
        ) : (
          <button type="button" disabled={working || !settings.hasSaved} onClick={() => { setConfirmingRemove(true); }}>
            Remove saved settings
          </button>
        )}
      </div>
    </div>
  );
}
