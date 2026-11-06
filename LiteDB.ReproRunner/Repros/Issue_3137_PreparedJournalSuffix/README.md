# Prepared legacy journal admits a foreign confirmation suffix

Pins PR #3137 commit `7c206728b423dc3f0b8bac8ba3d6f7941a11d718`.
No released package has been verified for this exact pre-merge defect.

The public-API proof uses the embedded released 5.0.21 primary and constructs
CRC-bound conversion records with an identifiable A5 confirmation-slot page.
A matching recovery control must recover before the foreign suffix is appended.
The test then checks two writable and two read-only opens.

The known-bad engine accepts the suffix without an exception: writable opens
replace the primary and lose/truncate WAL evidence; read-only opens accept it
without changing bytes (exit 0). The candidate must refuse with INVALID_DATABASE
and preserve both files on every attempt (exit 1). Other outcomes exit 2.
Broader plain/encrypted and partial-tail coverage remains in the registered
xUnit guards.

The earlier historical oracle expected a post-repair exception, which did not
match the retained real known-bad run. F-3137h corrects that expectation to require
acceptance plus primary mutation and WAL loss, without relaxing the candidate's
preserving-refusal checks. Its shared source also serves the separate torn-primary
recovery proof; each scenario retains its own oracle and pinned revision.
