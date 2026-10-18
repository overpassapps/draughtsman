## Summary

<!-- What does this change, and why? Link the issue it closes, if any. -->

## How it was tested

<!--
Run scripts/verify-local.sh before you ask for review: it runs the .NET build, tests and format check, then the web and
sidecar lint, build and tests. Paste the PASS/FAIL summary, or say what you could not run and why.
-->

- [ ] `scripts/verify-local.sh` passes
- [ ] New or changed behaviour has a test that fails without the change

## Checklist

- [ ] UI changes have a design atlas entry in `design/atlas/` (see `design/atlas/README.md`), and this description links it
- [ ] A change to the document model updates all four places listed in `AGENTS.md`, the schema example and the DSL golden file
- [ ] User-visible changes are in `CHANGELOG.md` under Unreleased
- [ ] Documentation is updated (British English)
- [ ] I have not added a dependency, or I have said which one and why

## Contributor Licence Agreement

- [ ] I have read the [CLA](https://github.com/overpassapps/draughtsman/blob/main/CLA.md) and I agree. (If I have already agreed on an earlier pull request, I say so here.)
