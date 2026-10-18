/**
 * What an Editor sees at an admin address (QA P17): a heading and a way out, not one grey line. The server already
 * refuses every admin call with 403 (the page never receives admin data), so this is only the manners: say what the page is,
 * why they cannot have it, and where to go instead. Rendered by every admin page for a non-admin; the shell's `<main>` is around it.
 */
import { navigate } from './router.ts';

export function NotAllowed() {
  return (
    <div className="page not-allowed">
      <h1 className="admin-title">Administrators only</h1>
      <p className="page-message" role="alert">You need to be an administrator to view this page.</p>
      <p>
        <a
          href="/"
          onClick={(event) => {
            event.preventDefault();
            navigate('/');
          }}
        >
          Back to documents
        </a>
      </p>
    </div>
  );
}
