/**
 * Keeps unsaved edits from being thrown away by leaving the editor (design/atlas/editor-safety.md). While `dirty`:
 *
 * - closing, reloading or leaving the tab gets the browser's own confirmation (`beforeunload`; a page cannot word it);
 * - any in-app navigation - the top bar links, the browser's back and forward, and the File menu's New, Import and
 *   Back - is held (`setLeaveBlocker`, app/router.ts) and `pending` says so, for the caller to show the choice:
 *   `save` stores the document through the editor's ETag-guarded save and lets the navigation continue only when that
 *   succeeded, `discard` lets it continue and drops the edits, `cancel` stays.
 *
 * With autosave on (`saveFirst`), leaving while the document is merely not yet saved is not worth a question: the save
 * is made first and the navigation goes ahead when it stored everything; only a save that fails or is refused shows
 * the question, with the reason. Without it a save is always the user's choice. If the document becomes clean while
 * the question is open (a save that was already running finished, or undo went back to the saved state) the
 * navigation just continues.
 */

import { useCallback, useEffect, useRef, useState } from 'react';
import { setLeaveBlocker } from '../app/router.ts';

export interface LeaveGuard {
  /** True while a navigation is held and waiting for the user's choice. */
  pending: boolean;
  /** True while a save started from the question is running. */
  saving: boolean;
  save: () => void;
  discard: () => void;
  cancel: () => void;
}

/**
 * `save` resolves true only when the whole document is stored (DocumentEditor.flush). `saveFirst` says a navigation
 * should try that before asking anything.
 */
export function useLeaveGuard(dirty: boolean, save: () => Promise<boolean>, saveFirst = false): LeaveGuard {
  // The navigation being held. In a ref as well as state so a second request replaces the first without a render between.
  const held = useRef<(() => void) | undefined>(undefined);
  const [pending, setPending] = useState(false);
  const [saving, setSaving] = useState(false);
  // Read when a navigation is held, not when the blocker is installed, so a status change does not reinstall the blocker.
  const saveFirstRef = useRef(saveFirst);
  const saveRef = useRef(save);
  useEffect(() => {
    saveFirstRef.current = saveFirst;
    saveRef.current = save;
  }, [saveFirst, save]);

  const release = useCallback((proceed: boolean) => {
    const next = held.current;
    held.current = undefined;
    setPending(false);
    if (proceed) {
      next?.();
    }
  }, []);

  useEffect(() => {
    if (!dirty) {
      // Clean again: anything held goes ahead, and nothing needs blocking.
      if (held.current !== undefined) {
        release(true);
      }
      return;
    }
    const onBeforeUnload = (event: BeforeUnloadEvent): void => {
      // preventDefault alone raises the browser's prompt in current Chrome, Firefox and Safari (its text is not ours to set).
      event.preventDefault();
    };
    window.addEventListener('beforeunload', onBeforeUnload);
    setLeaveBlocker((proceed) => {
      held.current = proceed;
      if (saveFirstRef.current) {
        void saveRef.current().then(
          (stored) => {
            if (stored) {
              release(true);
            } else if (held.current === proceed) {
              setPending(true);
            }
          },
          () => {
            if (held.current === proceed) {
              setPending(true);
            }
          },
        );
        return;
      }
      setPending(true);
    });
    return () => {
      window.removeEventListener('beforeunload', onBeforeUnload);
      setLeaveBlocker(undefined);
    };
  }, [dirty, release]);

  // Unmounting (the navigation went ahead) drops whatever was held.
  useEffect(
    () => () => {
      held.current = undefined;
    },
    [],
  );

  const saveAndLeave = useCallback(() => {
    setSaving(true);
    void save().then(
      (stored) => {
        setSaving(false);
        // A refused or failed save leaves the question open: the editor's own status carries the reason.
        if (stored) {
          release(true);
        }
      },
      () => {
        setSaving(false);
      },
    );
  }, [save, release]);

  return {
    pending,
    saving,
    save: saveAndLeave,
    discard: useCallback(() => {
      release(true);
    }, [release]),
    cancel: useCallback(() => {
      release(false);
    }, [release]),
  };
}
