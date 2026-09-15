# Worker runtime

The workers use the `v0.82.0-jk.1` gh-aw compiler and the matching immutable
`JKamsker/gh-aw` runtime commit. Compile through
`.github/scripts/compile_gh_aw.py`; it rejects a mismatched compiler.

The canary and fix worker explicitly select `gpt-6-astra`. Independent review
workers explicitly select `gpt-5.6-sol`. All use high reasoning effort. The
generated model settings are literals, so repository model variables cannot
silently change these choices. API-proxy model fallback is disabled; unavailable
models must fail the run rather than select a weaker model.

Each workflow runs one agent. The generated Codex command explicitly disables
`features.multi_agent` and `features.multi_agent_v2` so child
agents cannot inherit a different default model. Worker prompts also prohibit
delegation or launching additional model processes. The controller continues to
dispatch three independent review workflows with their explicit model settings.

## Codex version and verified reasoning

Workers pin npm `@openai/codex@0.154.0`, as requested by the user. The generated
command explicitly supplies `-c model_reasoning_effort=high`. Model and effort
are separate settings; the model name must remain `gpt-6-astra` or `gpt-5.6-sol`.
See the [official configuration reference](https://learn.chatgpt.com/docs/config-file/config-reference).

Before inference, `probe_gh_aw_reasoning.py` checks the installed binary version
and captures one harmless outgoing request for each model using a local HTTP
stub. It requires both requests to contain `reasoning.effort: high`. This check
uses no credentials or model inference and publishes only version, model,
effort, and request count. Both models passed with the actual 0.154.0 binary;
no reasoning-support override is needed.

The proof resides in the runner's control directory, mounted read-only in the
worker. Collection binds its digest into each fix/review artifact and checks
actual API usage for the requested model. GitHub's step summary displays the
verified model names and high reasoning explicitly.

The old 0.142.4 binary did not recognize these aliases. Its fallback model
metadata could suppress reasoning in requests despite high effort in TOML.
The canary using that runtime was blocked. An earlier `agents.enabled=false`
setting also aborted startup because that binary parsed it as an agent role.
Runtime settings must be verified against the installed binary, not inferred
from a different source checkout or the displayed model label.

## Artifact-only worker outputs

The pinned compiler automatically enables issue creation when no non-builtin
safe output is configured, including with an empty or noop-only configuration.
Each worker therefore declares an inert `record-completion` script returning
`{ success: true }`. This prevents the compiler from inserting issue creation.
Failure-issue reporting, missing-tool/data issue creation, incomplete-task issue
creation, and noop issue reporting are explicitly disabled. Worker evidence is
uploaded by deterministic post-steps rather than published as issues or comments.

The compile wrapper checks the generated workflows and rejects GitHub write
permissions, publication handlers, or safe-output tools other than `noop` and
`record_completion`. Run `.github/scripts/test_gh_aw_readonly.py` to verify all
three compiled workers and the rejection cases. Controller permissions remain
separate from these artifact-only workers.

Workers explicitly allow `github-actions[bot]` through gh-aw's activation check
so the deterministic controller can dispatch them using its workflow token.
Other actors still require the default `admin`, `maintainer`, or `write` role.
The bot exception is an exact allowlist entry, not a disabled membership gate.

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
