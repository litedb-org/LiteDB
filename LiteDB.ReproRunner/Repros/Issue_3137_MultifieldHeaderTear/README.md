# Multifield header tear refuses valid redo

A public caller-stream capture retains a real modern checkpoint journal.
The positive control recovers complete committed rows. The repro then clears
version bits to 8, both modern format markers and all four header CRC bytes.
The real PR commit `49adf618d067fbeecc1c107ec510214617ba50c5` refuses
both writable and read-only retries with identical files (exit 0).
The candidate recovers complete payloads, indexed lookup and the WAL-only
collection twice; read-only files remain identical (exit 1).
Unexpected behavior is inconclusive (exit 2). No hooks or engine internals.

Permanent guards also cover encrypted files, version 14 and 380 multifield/raw
ciphertext states. This is synthetic header damage, not hardware power loss.
