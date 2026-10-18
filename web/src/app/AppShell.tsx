/**
 * The first app shell (checklist: local sign-in): a thin bar carrying the instance brand, the signed-in user's name,
 * sign-out, and - for an Admin - one Admin menu holding the admin pages (folded with every other link into a Menu button under
 * COMPACT_NAV_MAX_WIDTH), wrapping whatever the router currently shows. This is
 * deliberately proportionate to this task: a header bar, not the left settings sidebar design/atlas/login-admin.md
 * describes for the full admin area (themes, storage), which stays out of scope here. The brand (product
 * name and logo) comes from the chrome theme, so an admin's branding change shows here.
 */
import { useEffect, useRef, useState, type ReactNode } from 'react';
import { CurrentUserContext } from './currentUser.ts';
import { guardedLeave, navigate, usePathname } from './router.ts';
import { ADMIN_LINKS } from './adminLinks.ts';
import { SkipLink } from './SkipLink.tsx';
import { useCompactNav } from './useCompactNav.ts';
import { logout, type AuthUser } from '../model/authClient.ts';
import { suppressSessionEndedNotice } from '../model/sessionEnded.ts';
import { brandHeaderLogo } from '../theme/chromeTheme.ts';
import { DefaultBrandMark } from '../theme/DefaultBrand.tsx';
import { usesDefaultBrand } from '../theme/defaultBrand.ts';
import { useChromeBrand } from '../theme/useChromeBrand.ts';

interface Props {
  user: AuthUser;
  onSignedOut: () => void;
  children: ReactNode;
}

function NavLink({ href, children, current }: { href: string; children: ReactNode; current?: boolean }) {
  return (
    <a
      href={href}
      aria-current={current === true ? 'page' : undefined}
      onClick={(event) => {
        event.preventDefault();
        navigate(href);
      }}
    >
      {children}
    </a>
  );
}

/**
 * The wide layout's Admin menu: one button that opens the eight admin pages, so the bar carries four things instead of
 * ten. A plain disclosure (a button with `aria-expanded` over a list of links), not an ARIA `menu`: the links are real
 * links, Tab moves through them, Escape closes and returns to the button, and leaving the menu any other way closes it.
 */
function AdminMenu({ path }: { path: string }) {
  // Open for one path only: a route change (a link in the menu, or Back) closes it without an effect.
  const [openAt, setOpenAt] = useState<string | undefined>(undefined);
  const open = openAt === path;
  const setOpen = (next: boolean | ((value: boolean) => boolean)): void => {
    const value = typeof next === 'function' ? next(open) : next;
    setOpenAt(value ? path : undefined);
  };
  const root = useRef<HTMLDivElement>(null);
  const button = useRef<HTMLButtonElement>(null);
  const active = ADMIN_LINKS.some((link) => link.href === path);

  useEffect(() => {
    if (!open) {
      return;
    }
    const onPointerDown = (event: PointerEvent): void => {
      if (root.current !== null && event.target instanceof Node && !root.current.contains(event.target)) {
        setOpenAt(undefined);
      }
    };
    document.addEventListener('pointerdown', onPointerDown);
    return () => {
      document.removeEventListener('pointerdown', onPointerDown);
    };
  }, [open]);

  return (
    <div
      className="shell-admin-menu"
      ref={root}
      onKeyDown={(event) => {
        if (event.key === 'Escape' && open) {
          event.stopPropagation();
          setOpen(false);
          button.current?.focus();
        }
      }}
      onBlur={(event) => {
        if (open && root.current !== null && !(event.relatedTarget instanceof Node && root.current.contains(event.relatedTarget))) {
          setOpen(false);
        }
      }}
    >
      <button
        type="button"
        ref={button}
        className="shell-admin-button"
        aria-expanded={open}
        aria-controls="shell-admin-links"
        data-active={active ? 'true' : undefined}
        onClick={() => {
          setOpen((value) => !value);
        }}
      >
        Admin
        <span aria-hidden="true" className="shell-caret" />
      </button>
      {open && (
        <ul id="shell-admin-links" className="shell-admin-links">
          {ADMIN_LINKS.map((link) => (
            <li key={link.href}>
              <NavLink href={link.href} current={link.href === path}>{link.label}</NavLink>
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}

export function AppShell({ user, onSignedOut, children }: Props) {
  const brand = useChromeBrand();
  const isAdmin = user.role === 'Admin';
  const path = usePathname();
  const compact = useCompactNav();
  // Open for one path and layout only: choosing a page, or growing the window out of the compact layout, closes it.
  const [menuOpenKey, setMenuOpenKey] = useState<string | undefined>(undefined);
  const menuKey = `${path}|${String(compact)}`;
  const menuOpen = menuOpenKey === menuKey;
  const setMenuOpen = (next: boolean | ((value: boolean) => boolean)): void => {
    const value = typeof next === 'function' ? next(menuOpen) : next;
    setMenuOpenKey(value ? menuKey : undefined);
  };
  const [busy, setBusy] = useState(false);
  const logo = brand === undefined ? null : brandHeaderLogo(brand);

  const signOut = (): void => {
    if (busy) {
      return;
    }
    // Signing out ends the session the open editor would save with, so unsaved edits get asked about first.
    guardedLeave(() => {
      setBusy(true);
      // A deliberate sign-out is not "you were signed out": a request the editor still had in flight must not leave a notice behind.
      suppressSessionEndedNotice();
      void logout()
        .catch(() => {
          // The session is going away either way; a failed logout call is not worth blocking the sign-out for.
        })
        .then(() => {
          setBusy(false);
          onSignedOut();
          navigate('/', { force: true });
        });
    });
  };

  const showLinks = !compact || menuOpen;
  const here = path.replace(/\/+$/, '') || '/';

  return (
    <div className="shell">
      <SkipLink />
      <header className="shell-bar">
        <NavLink href="/">
          <span className="shell-brand">
            {brand !== undefined && usesDefaultBrand(brand) ? (
              <DefaultBrandMark mode={brand.mode} className="shell-brand-logo" />
            ) : (
              logo !== null && <img className="shell-brand-logo" src={logo} alt="" />
            )}
            <span>{brand?.productName ?? 'Draughtsman'}</span>
          </span>
        </NavLink>
        {compact && (
          <button
            type="button"
            className="shell-menu-toggle"
            aria-expanded={menuOpen}
            aria-controls="shell-menu"
            onClick={() => {
              setMenuOpen((value) => !value);
            }}
          >
            Menu
          </button>
        )}
        {showLinks && (
          <div id="shell-menu" className={compact ? 'shell-menu shell-menu-compact' : 'shell-menu'}>
            <nav className="shell-nav" aria-label="Main">
              <NavLink href="/" current={here === '/'}>Documents</NavLink>
              {isAdmin && !compact && <AdminMenu path={here} />}
              {isAdmin && compact && (
                <>
                  <span className="shell-nav-group" id="shell-nav-admin">Admin</span>
                  {ADMIN_LINKS.map((link) => (
                    <NavLink key={link.href} href={link.href} current={link.href === here}>{link.label}</NavLink>
                  ))}
                </>
              )}
              <NavLink href="/admin/tokens" current={here === '/admin/tokens'}>Tokens</NavLink>
              <NavLink href="/about" current={here === '/about'}>About</NavLink>
            </nav>
            <div className="shell-user">
              <NavLink href="/account" current={here === '/account'}>
                <span className="shell-user-name">{user.displayName}</span>
              </NavLink>
              <button type="button" onClick={signOut} disabled={busy}>
                Sign out
              </button>
            </div>
          </div>
        )}
      </header>
      <CurrentUserContext.Provider value={user}>
        <main className="shell-content" id="main-content" tabIndex={-1}>{children}</main>
      </CurrentUserContext.Provider>
    </div>
  );
}
