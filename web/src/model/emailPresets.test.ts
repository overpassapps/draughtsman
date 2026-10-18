import { describe, expect, it } from 'vitest';
import type { EmailFormInput } from './emailClient.ts';
import { applyPreset, EMAIL_PRESETS } from './emailPresets.ts';

const FORM: EmailFormInput = { from: 'Me <me@x.example>', replyTo: 'r@x.example', host: 'old.example.com', port: '25', tls: 'none', user: 'me', publicUrl: 'https://x.example', passwordAction: 'replace', password: 'typed' };

const preset = (id: string): (typeof EMAIL_PRESETS)[number] => {
  const found = EMAIL_PRESETS.find((p) => p.id === id);
  if (found === undefined) {
    throw new Error(`no preset ${id}`);
  }
  return found;
};

describe('email presets', () => {
  it('lists the providers the brief names, each on 587 with STARTTLS (465 implicit TLS is not supported) and with a gotcha', () => {
    expect(EMAIL_PRESETS.map((p) => p.name)).toEqual(['Microsoft 365', 'Google Workspace', 'Amazon SES', 'SendGrid', 'Postmark', 'Mailgun', 'My own mail server']);
    for (const p of EMAIL_PRESETS) {
      expect(p.port).toBe(587);
      expect(p.tls).toBe('starttls');
      expect(p.gotcha.length).toBeGreaterThan(40);
    }
    expect(preset('microsoft365').host).toBe('smtp.office365.com');
    expect(preset('google').host).toBe('smtp.gmail.com');
    expect(preset('ses').host).toMatch(/^email-smtp\.[a-z0-9-]+\.amazonaws\.com$/);
    expect(preset('sendgrid').host).toBe('smtp.sendgrid.net');
    expect(preset('sendgrid').user).toBe('apikey');
    expect(preset('postmark').host).toBe('smtp.postmarkapp.com');
    expect(preset('mailgun').host).toBe('smtp.mailgun.org');
  });

  it('names the one thing that trips each provider up', () => {
    expect(preset('microsoft365').gotcha).toMatch(/SMTP AUTH/);
    expect(preset('microsoft365').gotcha).toMatch(/app password|dedicated mailbox/);
    expect(preset('google').gotcha).toMatch(/app password/);
    expect(preset('ses').gotcha).toMatch(/verified/);
    expect(preset('ses').gotcha).toMatch(/region/);
    expect(preset('sendgrid').gotcha).toMatch(/verified|authenticated/);
    expect(preset('postmark').gotcha).toMatch(/sender signature|verified/);
    expect(preset('mailgun').gotcha).toMatch(/verify/);
  });

  it('fills the server fields and nothing about who the organisation is', () => {
    const next = applyPreset(FORM, preset('google'), {});

    expect(next).toMatchObject({ host: 'smtp.gmail.com', port: '587', tls: 'starttls' });
    expect(next.user).toBe('me');
    expect(next.from).toBe(FORM.from);
    expect(next.replyTo).toBe(FORM.replyTo);
    expect(next.publicUrl).toBe(FORM.publicUrl);
    expect(next.passwordAction).toBe('replace');
    expect(next.password).toBe('typed');
  });

  it('sets the user name only where the provider fixes it', () => {
    expect(applyPreset(FORM, preset('sendgrid'), {}).user).toBe('apikey');
    expect(applyPreset(FORM, preset('microsoft365'), {}).user).toBe('me');
  });

  it('leaves a locked field alone, and fills the unlocked ones', () => {
    const next = applyPreset(FORM, preset('sendgrid'), { host: 'set in yaml', user: 'set in yaml' });

    expect(next.host).toBe('old.example.com');
    expect(next.user).toBe('me');
    expect(next.port).toBe('587');
    expect(next.tls).toBe('starttls');
  });
});
