# Damaged modern primary loses surviving foreign creation identity during journal recovery

Pins PR #3137 commit `7c206728b423dc3f0b8bac8ba3d6f7941a11d718`. No released package has been verified for this exact pre-merge defect.

The public-API proof constructs the affected file pair, checks a matching recovery control, and checks both files across two writable and two read-only opens. The damaged-primary case captures a real checkpoint journal then changes only creation time, invalidating the primary CRC. The prepared-suffix case uses the embedded released 5.0.21 primary and constructs CRC-bound conversion records with an identifiable A5 confirmation-slot page.

Exit 0 requires the exact known-bad mutation/admission; exit 1 requires INVALID_DATABASE refusal and byte-identical files; other outcomes exit 2. Broader plain/encrypted, partial-tail and authority-path coverage remains in the registered xUnit guards.
