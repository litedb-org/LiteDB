# Foreign primary / modern header journal fixtures

`ForeignPrimaryJournal_Tests` reuses pinned repository inputs without patching their
headers or generating a legacy file with the current engine:

| Input | SHA-256 | Provenance / purpose |
| --- | --- | --- |
| `v4.db` | `af6864cf7ba5a3f836b34ea736934e5d7a38e78071eeba30ef58c0c61f9b7348` | Existing v4 upgrade fixture, introduced by `487b9a2d6`, last changed by `663f7490f`. File-format version 7; signature at offset 25, version at 52. |
| `Issue_2494_EncryptedV4.db` | `5c864394a02ca5a4e2712597fd8bf6ab0fb807c6f39ba158a1a6c5d4c82badf1` | Existing encrypted v4 upgrade fixture, introduced by `48565c3e8`, password `pass123`. Its v7 header remains plaintext. |
| `LegacyWal_5_0_21.zip` | `0b89374671dc56cd28e15ca89a04390b622ab3bb689a174852f64b22bd68d603` | Released 5.0.21 plain/encrypted data files; [generator and provenance](LegacyWal_5_0_21.md). |

The v4 fixtures are inherited compatibility inputs, not freshly regenerated files;
their exact v4 writer patch version is not recorded. Their existing upgrade tests
verify complete collection readability. The new tests pair these unchanged bytes
with a valid modern checkpoint journal captured using the existing test helper.
This is deliberate restore/copy skew (`corrupt-input`), not a claim that an ordinary
supported crash produces legacy/foreign primary substitution.

Separate modern databases are created normally for creation-identity and encryption
mismatch controls. Both writable and read-only modes require a specific refusal code
and byte-identical data/WAL on two consecutive opens. Encrypted v4 with a password
refuses in the modern encryption stream layer (`NOT_ENCRYPTED`); without a password,
its recognizable v7 header must refuse journal replacement (`INVALID_DATABASE`).
The latter prevents overwriting the old encrypted database before it can be upgraded.
