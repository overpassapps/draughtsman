/**
 * "Where do I find the setup code?" for the first-run page (PR-02; design/atlas/empty-loading-error-states.md, "First
 * run"). The code is printed to the server's log when it starts (a Windows service instead keeps it in a file only
 * Administrators can read, which `draughtsman first-run-code` prints), and where to look depends on how Draughtsman was
 * started, so every way is listed and the one the server recognised is opened and marked. The code itself is never shown
 * here: it is the gate against anyone on the network, so it only ever reaches the person who can read the log. Closed
 * until asked for (QA P12): open, it pushed Create administrator below the fold of an 860 px window; the server's guess at
 * how it was started still marks the matching way, and `open` is only set for the one the person is looking for.
 */
import type { FirstRunHints, HostKind } from '../model/authClient.ts';

interface Way {
  host: HostKind;
  title: string;
  /** What to do; `dataFolder` is the real path when the server told us, else a placeholder. */
  how: (dataFolder: string) => string;
}

const PLACEHOLDER_FOLDER = '<data folder>';

const SETUP_CODE_WAYS: readonly Way[] = [
  { host: 'console', title: 'Running in a terminal', how: () => 'Look in the window that started Draughtsman, for the boxed line "First-run setup code".' },
  { host: 'container', title: 'Docker', how: () => 'Run docker logs <container name>, or docker compose logs <service name>, and look for "First-run setup code".' },
  { host: 'systemd', title: 'Linux service (systemd)', how: () => 'Run journalctl -u draughtsman (use the unit name you installed it under) and look for "First-run setup code".' },
  { host: 'launchd', title: 'macOS service', how: (folder) => `Open ${folder}/logs/draughtsman.log and look for "First-run setup code".` },
  {
    host: 'windowsService',
    title: 'Windows service',
    how: (folder) => `The code is not in any log, because every user on the machine can read the Event Viewer. It is in ${folder}\\setup-code.txt, readable by Administrators only: open an elevated (Administrator) PowerShell and run draughtsman first-run-code --DRAUGHTSMAN_DATA "${folder}". The Application event log entry from draughtsman names that file.`,
  },
];

export function SetupCodeHelp({ hints }: { hints: FirstRunHints | undefined }) {
  const folder = hints?.dataFolder ?? PLACEHOLDER_FOLDER;
  return (
    <details className="auth-help">
      <summary>Where do I find it?</summary>
      <p>Each time Draughtsman starts with no administrator it makes a code, and a restart makes a new one. Most installs print it to the server&rsquo;s log; a Windows service keeps it in a file that only Administrators can read. Where to look depends on how it was started:</p>
      <ul>
        {SETUP_CODE_WAYS.map((way) => (
          <li key={way.host} data-detected={hints?.host === way.host ? 'true' : undefined}>
            <strong>{way.title}.</strong> {way.how(folder)}
            {hints?.host === way.host && <em> This server looks like this.</em>}
          </li>
        ))}
      </ul>
    </details>
  );
}
