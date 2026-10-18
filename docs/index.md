# Draughtsman documentation

Pick by what you are doing. Everything here describes version 0.1.0 as it is on `main`; where a feature is not built, the
page says so.

## Install and run it

| I want to | Read |
| --- | --- |
| Choose an install and see what is in a release archive | [`install.md`](install.md) |
| Install on a server with **no internet access**: archives and image tarballs, PDF, upgrades, what leaves your network, and how to prove it | [`air-gapped.md`](air-gapped.md) |
| Run it in Docker | [`install-docker.md`](install-docker.md) |
| Run it on Linux with systemd | [`install-linux.md`](install-linux.md) |
| Run it on a Mac with launchd | [`install-macos.md`](install-macos.md) |
| Run it as a Windows service | [`install-windows.md`](install-windows.md) |
| Put TLS in front of it (Caddy, nginx, IIS), set the proxy, host and HTTPS settings, firewall and file permissions | [`deployment.md`](deployment.md) |
| See every setting in `draughtsman.yaml`, annotated, at its default | [`draughtsman.example.yaml`](draughtsman.example.yaml) |

## Look after it

| I want to | Read |
| --- | --- |
| Do first run, add people, recover a forgotten password, make API tokens, set themes and branding, configure AI, read the security log | [`admin-guide.md`](admin-guide.md) |
| Set up outgoing email (SMTP) for password-reset emails | [`deployment.md`](deployment.md#outgoing-email) |
| Back up, restore, upgrade, go back to an older version, export everything, use Postgres | [`upgrade-backup.md`](upgrade-backup.md) |
| Fix something that is not working | [`troubleshooting.md`](troubleshooting.md) |
| Turn on PDF export | [`pdf-export.md`](pdf-export.md) |

## Security and data

| I want to | Read |
| --- | --- |
| Report a vulnerability, and see the security model in brief | [`../SECURITY.md`](../SECURITY.md) |
| Know what is stored and what leaves the machine | [`data-flow.md`](data-flow.md) |
| Prove it makes no outbound connection, and get the software bill of materials (CycloneDX, in every release) | [`air-gapped.md`](air-gapped.md) |
| See where the AI endpoint may point, and what is left | [`ai-endpoint-policy.md`](ai-endpoint-policy.md) |

## Use the editor

| I want to | Read |
| --- | --- |
| Edit a diagram as text beside the canvas (DSL, Mermaid), and know what Apply does when someone else has changed it; use the Documents page's grid of thumbnails or its table | [`editor.md`](editor.md) |

## Use it from a program or an agent

| I want to | Read |
| --- | --- |
| Drive it from an AI agent (the shipped skill) | [`../SKILL.md`](../SKILL.md) |
| Call the HTTP API or the MCP tools directly | [`api.md`](api.md) |
| Write the text form | [`spec/dsl.md`](spec/dsl.md) |
| Keep diagrams as `.dsl` files in your own git repository, and show what changed in a pull request in plain sentences (`draughtsman diff` and `check`, GitHub Actions and GitLab CI recipes) | [`git-sync.md`](git-sync.md) |
| Bring in an existing draw.io (diagrams.net) diagram | [`drawio-import.md`](drawio-import.md) |
| Move from Lucidchart, Visio or draw.io: import a `.vsdx` (and a Lucidchart export to Visio), what comes across and what is lost | [`moving-from-lucidchart-visio-drawio.md`](moving-from-lucidchart-visio-drawio.md) |

## About the diagram types

[Sequence diagrams](sequence-diagrams.md), [ER tables](er-entities.md), [UML classes](uml-class-compartments.md),
[BPMN](bpmn.md), [basic shapes](basic-shapes.md), [fly-through](flythrough.md).

## What changed

[`../CHANGELOG.md`](../CHANGELOG.md).

## For people who build it

The build specification and its amendments are [`spec/build-spec-v0.1.md`](spec/build-spec-v0.1.md), the decisions are in
[`adr/`](adr/). The repository's [`README.md`](../README.md) and [`CONTRIBUTING.md`](../CONTRIBUTING.md) cover building and
testing, [`status.md`](status.md) is the long-form account of what is built and what is not, and
[`testing-installs.md`](testing-installs.md) the install matrix: one scripted first journey run against every supported install layout.
