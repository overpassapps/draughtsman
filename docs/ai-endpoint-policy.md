# AI endpoint policy (AU-11): what is restricted, and what is left

Status: **decided and implemented.** The decision was option A, restrict by provider. This page says what shipped, what an
operator has to do about it, and the risks that remain. It was first written as the two options and the question; the options
are kept at the end so the reasoning is not lost.

## The exposure it closes

An administrator can set the AI provider, model, endpoint and API key at `/admin/ai` (`PUT /api/admin/ai`,
`PUT /api/admin/ai/key`, `POST /api/admin/ai/test`; see `docs/api.md`, "AI settings"). Those routes are cookie-session and
admin only: an API token, even one minted by an admin, cannot reach them. Before this policy the endpoint was taken as given, so
someone holding an admin session (a stolen cookie, a hijacked browser, a rogue admin) could:

1. **Exfiltrate the key.** Point the endpoint at a host they control and press Test connection (or use any AI feature): the
   organisation's provider key, stored or from the environment, arrived in their logs.
2. **Probe the internal network (SSRF).** Point the endpoint at `http://169.254.169.254/` or an internal address and read
   reachable or unreachable and the status class.

## The rules, as shipped

| Rule | Where it is enforced |
| --- | --- |
| A keyed provider (`openai`, `anthropic`, `azure-openai`) may use only its published host (`api.openai.com`, `api.anthropic.com`, `*.openai.azure.com`) unless the operator lists the host under `ai.allowedEndpointHosts` in `draughtsman.yaml`. The list is in the file only; the admin page cannot add to it. | When an endpoint is read from `draughtsman.yaml` (a refused one leaves AI off, with the reason in the log and on the admin page), when the admin page saves one (`400`), and again on every connection. |
| `ollama` may use loopback and private addresses. Anything else (a public address) needs listing too. | The same three places. A name is judged by what it resolves to, when it is connected to. |
| Link-local and cloud-metadata addresses are refused for every provider, **even when listed**: as a literal address, as the address a name resolves to, and in every spelling that embeds one (decimal, hex and octal IPv4 URLs, `::ffff:169.254.169.254`, `::169.254.169.254`, NAT64 `64:ff9b::`, 6to4 `2002:`, `fd00:ec2::254`, bracketed hosts). Also refused: `0.0.0.0/8`, multicast and reserved ranges, `100.100.100.200` (Alibaba metadata), `168.63.129.16` (Azure wire server) and `192.0.0.0/24`. | The literal forms when the endpoint is saved or read; every form, including a name's answers, at connect time. |
| `http://` with a key is refused unless the host is loopback (`127.0.0.0/8`, `::1`, `localhost`, `*.localhost`). | A keyed provider's endpoint is refused when saved or read. For Ollama, whose key is optional, a key is never stored for, and never sent to, a plain-http host that is not this machine (`400` on `PUT /key`; the page says why a key is held back). A name that claims to be loopback must resolve to loopback. |
| Redirects are not followed. | The providers' HTTP client (`AllowAutoRedirect = false`). A redirect is reported as an answer ("redirect, not followed"), never as a second request. |
| Changing the host (or port) of the endpoint clears the stored key; it has to be entered again. | `AiSettingsService.SetOverrides` and `Reset`, whether the change is an endpoint, a provider or a reset to the file. |
| The environment key is sent only to the host in `draughtsman.yaml` or, when the file names none, the provider's own default. | `AiSettingsService.ChooseKey`, on every request. |
| Each change of host is logged with the acting admin; each refused endpoint is too. | The security event log: `ai.endpoint_host_changed` and `ai.endpoint_refused` (docs/api.md, "Security events"). |

What an address *is* (public, private, loopback, blocked, in every IPv4 and IPv6 spelling) is decided by one small class,
`src/Draughtsman.Server/Hardening/OutboundAddressGuard.cs`, the server's shared outbound-address guard, with its own tests
(`OutboundAddressGuardTests`); `AiEndpointPolicy` only says what each provider may do with that answer. Any other feature that
connects out should use the guard rather than keep a copy of the ranges.

Plain-English notes on the less obvious ones:

- **Ollama and public hosts.** Ollama has no key by default, but a proxied install can send a bearer token. If any public host were
  allowed for it, the key-exfiltration hole would be back through `ollama`, so a public Ollama host has to be listed like any other.
- **"Host" means host and port.** `proxy.example.com:443` and `proxy.example.com:8443` are different destinations as far as the
  stored key and the environment key are concerned.
- **A key entered before any endpoint existed.** It stays only if the endpoint then chosen is the provider's own default (the
  ordinary order of things: paste the key, pick OpenAI). Any other host, which includes every Azure OpenAI resource (it has no
  default), forgets it. Without that rule, a key held while there was no endpoint could be carried to a host the admin named afterwards.
- **The environment key and Azure.** Every `*.openai.azure.com` host passes the endpoint rule, and anyone can register one, so the
  environment key is sent to the Azure host the file names and to no other.
- **Unlisted means refused, not warned.** A `draughtsman.yaml` endpoint that fails the policy after an upgrade leaves AI off until
  the host is listed; nothing reaches it.

## What an operator does

Nothing, if AI uses OpenAI, Anthropic, Azure OpenAI at an `*.openai.azure.com` host, or Ollama on this machine or a private address.

Anyone else lists the host, once, in `draughtsman.yaml` (the annotated `docs/draughtsman.example.yaml` has the line):

```yaml
ai:
  provider: openai
  model: gpt-4o-mini
  endpoint: https://llm.example.com/v1     # a corporate gateway or any OpenAI-compatible server
  allowedEndpointHosts:
    - llm.example.com                      # or "*.example.com" for every subdomain (not example.com itself)
```

An entry is a host name, an IP address or `*.suffix`. A bare `*`, a URL, a port or a path is not an entry and matches nothing (the
server logs how many entries were unusable, never their text). The file is read at start, so a change needs a restart. Then re-enter
the API key on the admin page if the host changed: the stored key does not follow a host change.

Upgrading from a build without this policy: an `ai.endpoint` in `draughtsman.yaml`, or an override stored by the admin page, that
the policy now refuses leaves AI off, with the reason on the admin page (`problem`) and in the log. List the host and restart. The
stored key is untouched until the host changes.

## How DNS is handled (and rebinding)

A host name cannot be judged by its spelling, so the policy is enforced where the connection is made
(`src/Draughtsman.Server/Ai/AiConnectGuard.cs`, the HTTP client's connect step):

1. The name is resolved there and **every** answer is judged. A name that also answers with one blocked address is refused whole.
2. The connection is made to one of the addresses that was judged, **by address**, never by name again. There is no second lookup
   between the check and the connect, so the classic rebinding window (answer a harmless address to the check, the metadata address to
   the connect) does not exist.
3. Every new connection comes through the same step, so a name that later answers differently is judged again.
4. TLS is unchanged: the certificate is still checked against the host name, not the address.

When the endpoint is saved, only what can be judged without DNS is: the allow-list, the scheme, and a literal address. A name that
resolves somewhere blocked is saved and then refused at the first connection, with the reason in Test connection's answer. No lookup is
made while a setting is saved, so saving cannot be used to make the server query an attacker's name server.

## Risks that remain

- **A proxy.** When the system proxy carries the request (`HTTP_PROXY` or `HTTPS_PROXY`, not bypassed), the proxy resolves the
  target itself and the server cannot pin the address it connects to. The target's name is still resolved and judged first, so a
  name that points at a blocked address is refused with a proxy configured too, but the proxy's own lookup is a second lookup, and
  DNS rebinding between the two is not closed. A keyed provider is limited to its published or listed hosts anyway, and the proxy
  is the operator's own. A deployment that needs the guarantee leaves the proxy variables unset for this server.
- **Test connection still probes private networks, for Ollama.** Ollama must be able to reach loopback and private addresses, so an
  administrator (or a stolen admin session) can still aim Test connection at an internal host and port and read reachable or not and
  the status class. What is closed: link-local and metadata addresses, public hosts, and any key leaving. If that is not acceptable
  for an instance, leave Ollama unconfigured or run the server where those addresses are not reachable.
- **A listed host is trusted with the key.** The policy limits where the key can go to the published hosts and what an operator lists.
  An operator who lists `*.example.com` has trusted every subdomain of it, including one an attacker can register.
- **The file is the root of trust.** Anyone who can edit `draughtsman.yaml` or the environment can do all of this and more; the
  policy is against the admin page, not against the host.
- **Provider error text.** A refused or failed connection's message is built from the outcome, but the providers' own error
  messages (which the AI features, not Test connection, may return to an editor) name the endpoint's address. They never carry a key.
- **IPv6 coverage is by rule, not by exhaustive list.** The embedded-IPv4 forms above are judged as the IPv4 address they carry.
  Teredo (`2001::/32`) is not unwrapped; reaching a metadata address through it would need a Teredo relay on the server's network.

## What is not changed

The AI "update" mode, which sends the whole current diagram as DSL to the configured provider, is exactly as it was, with its
disclosure (`docs/data-flow.md`). This policy decides where the endpoint may point, not what is sent to it.

## The options, for the record

**Option A: restrict by provider** was chosen and is what the rest of this page describes.

**Option B: keep "any host", and say so** would have accepted that an administrator is trusted with the key and the server's
network reach, made that visible in the admin guide and `SECURITY.md`, and shipped only the cheap mitigations (log host changes, no
redirects, no `http://` with a key except loopback, block link-local and metadata). Its cost was that an admin session compromise
leaks the provider key. Its cheap mitigations are all part of option A as shipped.

Related decision in the audit (Q7): whether the AI "update" mode is per-instance opt-in or stays as it is with its disclosure. It
stays as it is.
