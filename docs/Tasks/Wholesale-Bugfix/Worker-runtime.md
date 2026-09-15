# Worker runtime

The workers use the `v0.82.0-jk.1` gh-aw compiler and the matching immutable
`JKamsker/gh-aw` runtime commit. Compile through
`.github/scripts/compile_gh_aw.py`; it rejects a mismatched compiler.

The canary and fix worker explicitly select `gpt-6-astra`. Independent review
workers explicitly select `gpt-5.6-sol`. All use high reasoning effort. The
generated model settings are literals, so repository model variables cannot
silently change these choices. API-proxy model fallback is disabled; unavailable
models must fail the run rather than select a weaker model.

## Endpoint and accounting compatibility

The initial direct HTTP canary reached the configured Responses endpoint, but
the first gh-aw canary was rejected by AWF's older model-pricing catalogue.
The workers therefore pin AWF `v0.27.43`, the release that correctly forwards
`defaultAiCreditsPricing` from its configuration into the API proxy. The gh-aw
compiler resolves the release's container images to immutable digests.

The runtime patch supplies these **nominal accounting rates**, in dollars per
million tokens: input 25, output 150, cached input 25, and cache writes 25. These
are conservative campaign budget assumptions, **not verified provider prices**
or a forecast of the actual bill. Cached tokens receive no accounting discount.
One AWF AI credit represents one nominal cent under these assumptions.

The canary has a 500-credit accounting cap and 20 model-call cap. Production
workers have their own explicit finite caps in their workflow sources. Zero or
disabled AI-credit budgets are rejected by the runtime patch. Budget accounting
remains active for models absent from AWF's built-in pricing table. Caps govern
subsequent API calls; usage in a currently executing response can exceed a
threshold before the next call is rejected.

The wrapper preserves the pinned compiler's existing host-access and iptables
network topology using AWF's `--legacy-security` compatibility option. This is
required because the newer firewall defaults to a different network topology;
it retains the API proxy and its credential isolation.

The endpoint is read only from `CODEX_LB_BASE_URL` on the runner. Its host and
base path are configured at runtime, masked in logs, excluded from the agent's
environment, and redacted from uploaded text artifacts. No endpoint value belongs
in source, prompts, documentation, or commit messages.
