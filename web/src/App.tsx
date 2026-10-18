import { useCallback, useEffect, useState } from 'react';
import { AppShell } from './app/AppShell.tsx';
import { matchRoute, navigate, usePathname, type Route } from './app/router.ts';
import { useAuthStatus } from './app/useAuthStatus.ts';
import type { AuthStatus, AuthUser } from './model/authClient.ts';
import { AccountPage } from './pages/AccountPage.tsx';
import { AboutPage } from './pages/AboutPage.tsx';
import { AiSettingsPage } from './pages/AiSettingsPage.tsx';
import { BrandingPage } from './pages/BrandingPage.tsx';
import { DocumentPage } from './pages/DocumentPage.tsx';
import { EmailPage } from './pages/EmailPage.tsx';
import { ExamplePage } from './pages/ExamplePage.tsx';
import { FirstRunPage } from './pages/FirstRunPage.tsx';
import { HomePage } from './pages/HomePage.tsx';
import { LoginPage } from './pages/LoginPage.tsx';
import { ResetPasswordPage } from './pages/ResetPasswordPage.tsx';
import { SecurityLogPage } from './pages/SecurityLogPage.tsx';
import { StatusPage } from './pages/StatusPage.tsx';
import { ConnectionCheckPage } from './pages/ConnectionCheckPage.tsx';
import { ThemesPage } from './pages/ThemesPage.tsx';
import { TokensPage } from './pages/TokensPage.tsx';
import { TryPage } from './pages/TryPage.tsx';
import { UsersPage } from './pages/UsersPage.tsx';

/** The routed content once a user is signed in; `login` only reaches here mid-redirect (see App below). */
function renderRoute(route: Route, user: AuthUser) {
  switch (route.page) {
    case 'home':
    case 'login':
      return <HomePage />;
    case 'example':
      return <ExamplePage />;
    case 'try':
      return <TryPage />;
    case 'document':
      return <DocumentPage id={route.id} />;
    case 'account':
      return <AccountPage />;
    case 'adminSecurity':
      return <SecurityLogPage currentUser={user} />;
    case 'adminUsers':
      return <UsersPage currentUser={user} />;
    case 'adminTokens':
      return <TokensPage />;
    case 'adminBranding':
      return <BrandingPage currentUser={user} />;
    case 'adminThemes':
      return <ThemesPage currentUser={user} />;
    case 'adminAi':
      return <AiSettingsPage currentUser={user} />;
    case 'adminEmail':
      return <EmailPage currentUser={user} />;
    case 'adminStatus':
      return <StatusPage currentUser={user} />;
    case 'adminConnection':
      return <ConnectionCheckPage currentUser={user} />;
    case 'about':
      return <AboutPage currentUser={user} />;
    case 'resetPassword':
      return <HomePage />;
    case 'notFound':
      return (
        <div className="page">
          <h1>Page not found</h1>
          <p>There is nothing at {route.path}.</p>
        </div>
      );
  }
}

/**
 * The app-level auth gate (local sign-in): checks `GET /api/auth/status` once before rendering anything else, and
 * shows first-run, sign-in, or the app shell around the normal routed content accordingly. A signed-in user who
 * navigates to `/login` is bounced back to `/` rather than shown a page that no longer applies to them.
 */
export function App() {
  const auth = useAuthStatus();
  const route = matchRoute(usePathname());
  const signedIn = auth.kind === 'ok' && !auth.status.firstRunRequired && auth.status.user !== null;
  // A first run the server accepted whose session the browser did not keep (forceHttps over plain http): the
  // administrator exists, so the gate moves on to the sign-in page, which has to say why it is there.
  const [firstRunKeptNoSession, setFirstRunKeptNoSession] = useState(false);
  const { refresh } = auth;
  const afterFirstRun = useCallback(async (): Promise<AuthStatus | undefined> => {
    const status = await refresh();
    setFirstRunKeptNoSession(status !== undefined && !status.firstRunRequired && status.user === null);
    return status;
  }, [refresh]);
  const afterSignOut = useCallback((): void => {
    setFirstRunKeptNoSession(false);
    void refresh();
  }, [refresh]);

  useEffect(() => {
    if (signedIn && route.page === 'login') {
      navigate('/');
    }
  }, [signedIn, route.page]);

  // The page a reset link opens needs no session and no status: it works whether the viewer is signed out (the usual case) or signed in as
  // someone else, and a link that was clicked must not wait on, or be hidden by, the sign-in gate.
  if (route.page === 'resetPassword') {
    return (
      <ResetPasswordPage
        selfService={auth.kind === 'ok' && auth.status.passwordReset.selfService}
        onFinished={() => {
          void refresh();
          navigate('/');
        }}
      />
    );
  }

  if (auth.kind === 'loading') {
    return <main className="page"><p className="page-message">Loading…</p></main>;
  }
  if (auth.kind === 'error') {
    return <main className="page"><p className="page-message" role="alert">Cannot reach Draughtsman: {auth.message}</p></main>;
  }
  if (auth.status.firstRunRequired) {
    return <FirstRunPage onSignedIn={afterFirstRun} hints={auth.status.firstRun} />;
  }
  if (auth.status.user === null) {
    return <LoginPage onSignedIn={refresh} passwordReset={auth.status.passwordReset} sessionNotKept={firstRunKeptNoSession} />;
  }

  return (
    <AppShell user={auth.status.user} onSignedOut={afterSignOut}>
      {renderRoute(route, auth.status.user)}
    </AppShell>
  );
}
