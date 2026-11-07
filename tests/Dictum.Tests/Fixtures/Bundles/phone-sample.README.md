# Synthetic phone bundle conformance fixture

Transfer code: `abandon ability able about above absent` (six BIP-39 list words; no checksum).

Test data only: fixed secrets must never be used for real recordings.

All hex ranges below are inclusive, increasing by one byte.

- Content key: `000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f`.
- PBKDF2 salt: `202122232425262728292a2b2c2d2e2f`; SHA-256, 600000 iterations, 32-byte output.
- Derived transfer key: `e308183fc23692c746e66fcb5b81ee5edff4085d7c612d7695ce14ef58eb14ed`.
- Transfer wrap nonce: `f0f1f2f3f4f5f6f7f8f9fafb`; AAD UTF-8 `transfer`. No owner wrap.
- Meeting id: `0123456789abcdef0123456789abcdef`; Source Phone = 3; channel Mixed = 2.
- Start and confirmation time: `2026-01-01T12:00:00+00:00`; duration `00:00:02`.
- Creator: Dictum / 1.5.1.0 / ios; transfer.kind omitted.
- WAV: 32000 PCM16 little-endian samples, mono, 16000 Hz, 64044 bytes.
  Sample n = truncate-towards-zero(1000 * sin(2 * pi * 440 * n / 16000)), n = 0..31999.
  Canonical 44-byte RIFF header, PCM format 1, byte rate 32000, block alignment 2.
- Each encrypted entry: DCTMENC version 1, one data record and one zero-length terminal record.
  Header suffix / data nonce / terminal nonce (hex ranges):
  audio-mixed.wav.enc: 40..4f / 50..5b / 60..6b;
  confirmations.json.enc: 70..7f / 80..8b / 90..9b;
  meta.json.enc: a0..af / b0..bb / c0..cb.
- ZIP entry order: audio-mixed.wav.enc, confirmations.json.enc, meta.json.enc, manifest.json.
  All stored (method 0), DOS timestamp 2000-01-01 00:00:00, external attributes 0; no comments, directories or ZIP64.
  ZIP made-by version 20, creator OS DOS (0), required version 20, flags 0, no extra fields.
- No transcript, summaries, markers, video or incomplete channels.

Exact UTF-8 metadata plaintext (no BOM or trailing newline):

```json
{"Id":{"Value":"0123456789abcdef0123456789abcdef"},"Title":"Synthetic phone tone","StartedAt":"2026-01-01T12:00:00+00:00","Duration":"00:00:02","Source":3,"Agreement":{"AcknowledgedAt":"2026-01-01T12:00:00+00:00","How":"written","ScriptVersion":"synthetic-v1","Kind":1},"Markers":[],"Wraps":[],"HasVideo":false,"SummaryEngine":null,"SummaryTemplate":null,"IncompleteAudioChannels":[],"AudioIncomplete":false}
```

Exact UTF-8 confirmation plaintext (no BOM or trailing newline):

```json
[{"Answer":"written","At":"2026-01-01T12:00:00+00:00","Kind":1,"ScriptVersion":"synthetic-v1","Script":"Synthetic fixture notice; no real participants.","ImporterIdentity":"Synthetic phone operator","Route":{"SpeechHost":null,"SummaryEngine":0,"SummaryHost":null,"KeepAudio":0,"SummaryModel":null}}]
```

SHA-256 of uncompressed entry bytes (ciphertext for .enc entries):

- `audio-mixed.wav.enc` (64132 bytes): `01ec8232a51b50432187d9ac614b23ace51f99564bc5c1b83d28752e08d8050b`
- `confirmations.json.enc` (387 bytes): `606664ca9bd6ee36b01d5b18b7b2c49e62081d26abb81721b8988641623c8139`
- `meta.json.enc` (496 bytes): `72abb354df4793ea02a6543a2df3b978cd07d099ecc87a595d7bc623170ea623`
- `manifest.json` (471 bytes): `7c9e9929111c44ff4056218719475f962abb5c63985e603289ae6d86d2246a07`

Whole bundle SHA-256: `84f869a2a749be72d7dba779f0cf5e1742d738739912a214fa5990ef9515ce42`.

Regenerate intentionally from the worktree: set DICTUM_REGENERATE_PHONE_FIXTURE=1 and run the xUnit test
`MeetingBundleTests.The_fixture_generator_reproduces_the_committed_bytes`; unset it afterwards.
Normal runs only compare. The helper is PhoneBundleFixture.Generate; production RNG is never replaced.
