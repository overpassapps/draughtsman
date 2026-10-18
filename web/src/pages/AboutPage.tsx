/**
 * About (design/atlas/help-about-status.md, PR-04): the version, build date, support contact and the
 * third-party licences, for every signed-in user. It reads `GET /api/about`, which returns the same body to an Editor
 * and an Administrator, so this page cannot show an Editor anything admin-only; an Administrator gets two extra links
 * to the pages that do carry detail.
 *
 * Two identity blocks, kept apart: the product's own (the Draughtsman lockup, always shown) and the organisation's
 * (its logo and product name, only when it has set either on the Branding page). The lockup is never drawn in the
 * organisation's block or in place of it.
 */
import { useEffect, useState } from 'react';
import { navigate } from '../app/router.ts';
import { fetchAbout, supportHref, type About } from '../model/aboutClient.ts';
import type { AuthUser } from '../model/authClient.ts';
import { brandLogo } from '../theme/chromeTheme.ts';
import { DefaultBrandLockup } from '../theme/DefaultBrand.tsx';
import { DEFAULT_PRODUCT_NAME, usesDefaultBrand } from '../theme/defaultBrand.ts';
import { useChromeBrand } from '../theme/useChromeBrand.ts';

interface Props {
  currentUser: AuthUser;
}

function formatBuildDate(iso: string): string {
  const parsed = new Date(`${iso}T00:00:00Z`);
  return Number.isNaN(parsed.getTime()) ? iso : parsed.toLocaleDateString('en-GB', { day: 'numeric', month: 'long', year: 'numeric', timeZone: 'UTC' });
}

export function AboutPage({ currentUser }: Props) {
  const brand = useChromeBrand();
  const [about, setAbout] = useState<About>();
  const [error, setError] = useState<string>();

  useEffect(() => {
    let cancelled = false;
    void fetchAbout()
      .then((next) => {
        if (!cancelled) {
          setAbout(next);
        }
      })
      .catch((cause: unknown) => {
        if (!cancelled) {
          setError(cause instanceof Error ? cause.message : 'Could not read the version details.');
        }
      });
    return () => {
      cancelled = true;
    };
  }, []);

  const logo = brand === undefined ? null : brandLogo(brand);
  const productName = brand?.productName ?? DEFAULT_PRODUCT_NAME;
  const organisationBranded = brand !== undefined && !usesDefaultBrand(brand);

  return (
    <div className="admin-page">
      <h1 className="admin-title">About {productName}</h1>
      {about === undefined ? (
        error !== undefined ? <p className="page-message" role="alert">{error}</p> : <p className="page-message">Loading…</p>
      ) : (
        <>
          <section className="about-identity" aria-labelledby="about-product-heading">
            <h2 id="about-product-heading" className="about-identity-title">The product</h2>
            <DefaultBrandLockup mode={brand?.mode ?? 'system'} className="about-lockup" />
          </section>
          {organisationBranded && (
            <section className="about-identity" aria-labelledby="about-organisation-heading">
              <h2 id="about-organisation-heading" className="about-identity-title">This instance’s branding</h2>
              {logo !== null && <img className="about-logo" src={logo} alt={`${productName} logo`} />}
              <p className="about-organisation-name">{productName}</p>
            </section>
          )}
          <dl className="about-fields">
            <dt>Version</dt>
            <dd>{about.version}</dd>
            <dt>Build date</dt>
            <dd>{formatBuildDate(about.buildDate)}</dd>
            <dt>Support</dt>
            <dd>
              {supportHref(about.supportContact) !== undefined ? <a href={supportHref(about.supportContact)}>{about.supportContact}</a> : about.supportContact}
            </dd>
            {about.docsUrl !== undefined && (
              <>
                <dt>Documentation</dt>
                <dd>
                  <a href={about.docsUrl} target="_blank" rel="noopener noreferrer">
                    {about.docsUrl.startsWith('/') ? 'The guides on this server' : about.docsUrl}
                    <span className="visually-hidden"> (opens in a new tab)</span>
                  </a>
                </dd>
              </>
            )}
            <dt>Third-party licences</dt>
            <dd>
              <a href={about.noticesUrl} target="_blank" rel="noopener noreferrer">
                Open the notices
                <span className="visually-hidden"> (opens in a new tab)</span>
              </a>
            </dd>
          </dl>
          {currentUser.role === 'Admin' && (
            <p className="ai-status">
              As an administrator you can also see the{' '}
              <a href="/admin/status" onClick={(e) => { e.preventDefault(); navigate('/admin/status'); }}>system status</a>.
            </p>
          )}
        </>
      )}
    </div>
  );
}
