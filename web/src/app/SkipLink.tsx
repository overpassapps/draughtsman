/**
 * "Skip to main content" (QA S5): the first focusable thing on every page, so a keyboard user does not tab through the
 * header's controls before the content. It is a link to `#main-content` with a handler that also moves focus, because
 * the router is a pushState one and a hash jump alone leaves focus in the header in some browsers. The target is the
 * shell's one `<main id="main-content" tabindex="-1">` (AppShell), which holds every page including the editor; being
 * -1 it takes focus from this link but is not in the tab order.
 */
export function SkipLink() {
  return (
    <a
      className="skip-link"
      href="#main-content"
      onClick={(event) => {
        const main = document.querySelector<HTMLElement>('main');
        if (main === null) {
          return;
        }
        event.preventDefault();
        main.id = 'main-content';
        main.tabIndex = -1;
        // focus() brings the landmark into view as well.
        main.focus();
      }}
    >
      Skip to main content
    </a>
  );
}
