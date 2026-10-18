/**
 * Ready-made starting points for the Email form: pick one and the host, port, encryption (and a user name where the provider fixes one) are
 * filled in. Nothing is stored by picking one; the administrator still reviews, adds the password and saves. Each carries the one line an
 * administrator needs and usually does not know: what to verify, why a normal password is refused. Only 587 with STARTTLS is offered: the
 * framework mail client cannot do implicit TLS (port 465), so a provider that offers both is entered on its 587 option.
 */

import type { EmailField, EmailFormInput } from './emailClient.ts';

export interface EmailPreset {
  id: string;
  name: string;
  host: string;
  port: number;
  tls: 'starttls';
  /** Filled only when the provider fixes the user name (SendGrid's is the word `apikey`). */
  user?: string;
  /** One sentence: the thing that makes this provider fail for a newcomer. */
  gotcha: string;
}

export const EMAIL_PRESETS: readonly EmailPreset[] = [
  {
    id: 'microsoft365',
    name: 'Microsoft 365',
    host: 'smtp.office365.com',
    port: 587,
    tls: 'starttls',
    gotcha:
      'The mailbox you sign in with needs SMTP AUTH switched on (Microsoft turns it off by default), and many organisations block basic sign-in, so you may need an app password or a dedicated mailbox. The From address must be that mailbox or one it is allowed to send as.',
  },
  {
    id: 'google',
    name: 'Google Workspace',
    host: 'smtp.gmail.com',
    port: 587,
    tls: 'starttls',
    gotcha:
      'Use an app password (2-Step Verification must be on), not the account’s normal password: Google refuses basic sign-in otherwise. The From address must be that account or one of its aliases.',
  },
  {
    id: 'ses',
    name: 'Amazon SES',
    host: 'email-smtp.us-east-1.amazonaws.com',
    port: 587,
    tls: 'starttls',
    gotcha:
      'Change us-east-1 in the server name to your SES region. The user name and password are SMTP credentials made in the SES console (not your AWS access keys), and the From address or domain must be verified there; a new account can only send to verified addresses until AWS lifts its sandbox.',
  },
  {
    id: 'sendgrid',
    name: 'SendGrid',
    host: 'smtp.sendgrid.net',
    port: 587,
    tls: 'starttls',
    user: 'apikey',
    gotcha: 'The user name is the word apikey and the password is an API key with Mail Send permission. The From address needs a verified single sender or an authenticated domain.',
  },
  {
    id: 'postmark',
    name: 'Postmark',
    host: 'smtp.postmarkapp.com',
    port: 587,
    tls: 'starttls',
    gotcha: 'Use the server’s API token as both the user name and the password. The From address must be a confirmed sender signature or on a verified domain.',
  },
  {
    id: 'mailgun',
    name: 'Mailgun',
    host: 'smtp.mailgun.org',
    port: 587,
    tls: 'starttls',
    gotcha:
      'Use the SMTP credentials of a sending domain (a user name like postmaster@mg.example.com), and verify that domain. An EU account uses smtp.eu.mailgun.org instead.',
  },
  {
    id: 'own',
    name: 'My own mail server',
    host: '',
    port: 587,
    tls: 'starttls',
    gotcha:
      'Ask whoever runs it for the host name, a user name and a password. Use port 587 with STARTTLS. Encryption “None” is accepted only for a server on this machine, or one that needs no user name.',
  },
];

/**
 * The form with a preset's values filled in. A field `draughtsman.yaml` locks is left as it is, and the password, From address, Reply-to and
 * public address are never touched: a preset is about where the mail server is, not who the organisation is.
 */
export function applyPreset(form: EmailFormInput, preset: EmailPreset, locked: Partial<Record<EmailField, string>>): EmailFormInput {
  const next = { ...form };
  if (locked.host === undefined) {
    next.host = preset.host;
  }
  if (locked.port === undefined) {
    next.port = String(preset.port);
  }
  if (locked.tls === undefined) {
    next.tls = preset.tls;
  }
  if (preset.user !== undefined && locked.user === undefined) {
    next.user = preset.user;
  }
  return next;
}
