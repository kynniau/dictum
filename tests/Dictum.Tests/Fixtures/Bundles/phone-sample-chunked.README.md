# Synthetic chunked phone bundle conformance fixtures

Transfer code: `abandon ability able about above absent` (six BIP-39 list words; no checksum).

Test data only: fixed secrets must never be used for real recordings.

All hex ranges below are inclusive, increasing by one byte. Both bundles use these fixed values:

- Content key: `000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f`.
- PBKDF2 salt: `202122232425262728292a2b2c2d2e2f`; SHA-256, 600000 iterations, 32-byte output.
- Derived transfer key: `e308183fc23692c746e66fcb5b81ee5edff4085d7c612d7695ce14ef58eb14ed`.
- Transfer wrap nonce: `f0f1f2f3f4f5f6f7f8f9fafb`; AAD UTF-8 `transfer`. No owner wrap.
- Meeting id: `0123456789abcdef0123456789abcdef`; Source Phone = 3; channel Mixed = 2.
- Start and confirmation time: `2026-01-01T12:00:00+00:00`; duration `00:00:02`.
- Creator: Dictum / 1.5.1.0 / ios; transfer.kind omitted.
- PCM: the WAV fixture's same 32000 PCM16 little-endian samples, mono, 16000 Hz.
  Sample n = truncate-towards-zero(1000 * sin(2 * pi * 440 * n / 16000)), n = 0..31999.
  Two data records, 32000 PCM bytes (one second) each, with no WAV header in the records.
- DCTMAUD version 1 stream identity: 00..0f. Every byte-source call starts 0x10 above the previous call.
  Record 0 payload nonce / framing nonce: 10..1b / 20..2b.
  Record 1 payload nonce / framing nonce: 30..3b / 40..4b.
  Terminal (record 2) payload nonce / framing nonce: 50..5b / 60..6b.
  The writer chooses the payload nonce first; on disk each framing nonce/tag precedes the payload nonce/ciphertext/tag.
  Complete: two data records then a zero-length terminal record; IncompleteAudioChannels = [].
  Incomplete: the identical two data records, EOF with NO terminal record; IncompleteAudioChannels = [2] (System.Text.Json Mixed).
- Other encrypted entries: DCTMENC version 1, one data record and one zero-length terminal record.
  Header suffix / data nonce / terminal nonce (hex ranges):
  confirmations.json.enc: 70..7f / 80..8b / 90..9b;
  meta.json.enc: a0..af / b0..bb / c0..cb.
- ZIP entry order: audio-mixed.chunks.enc, confirmations.json.enc, meta.json.enc, manifest.json.
  All stored (method 0), DOS timestamp 2000-01-01 00:00:00, external attributes 0; no comments, directories or ZIP64.
  ZIP made-by version 20, creator OS DOS (0), required version 20, flags 0, no extra fields.
- No transcript, summaries, markers or video.

Full DCTMAUD header bytes (both bundles, hex):

`4443544d41554401000102030405060708090a0b0c0d0e0f`

Complete fixture, record 0 payload-operation AAD (hex):

`4443544d41554401000102030405060708090a0b0c0d0e0f0000000000000000007d000030313233343536373839616263646566303132333435363738396162636465662f617564696f2d6d697865642e6368756e6b732e656e63`

Complete fixture, record 0 framing-operation AAD (hex):

`4443544d41554401000102030405060708090a0b0c0d0e0f0000000000000000007d000030313233343536373839616263646566303132333435363738396162636465662f617564696f2d6d697865642e6368756e6b732e656e63`

These AAD byte strings MUST be byte-identical: header:24 || sequence:int64LE (0) || length:int32LE (32000, hex 007d0000) || UTF8(meeting-id + "/audio-mixed.chunks.enc").
The framing operation seals empty plaintext but its AAD length is the PCM length 32000, never zero for a data record.

Complete exact UTF-8 metadata plaintext (no BOM or trailing newline):

```json
{"Id":{"Value":"0123456789abcdef0123456789abcdef"},"Title":"Synthetic phone tone","StartedAt":"2026-01-01T12:00:00+00:00","Duration":"00:00:02","Source":3,"Agreement":{"AcknowledgedAt":"2026-01-01T12:00:00+00:00","How":"written","ScriptVersion":"synthetic-v1","Kind":1},"Markers":[],"Wraps":[],"HasVideo":false,"SummaryEngine":null,"SummaryTemplate":null,"IncompleteAudioChannels":[],"AudioIncomplete":false}
```

Incomplete exact UTF-8 metadata plaintext (no BOM or trailing newline):

```json
{"Id":{"Value":"0123456789abcdef0123456789abcdef"},"Title":"Synthetic phone tone","StartedAt":"2026-01-01T12:00:00+00:00","Duration":"00:00:02","Source":3,"Agreement":{"AcknowledgedAt":"2026-01-01T12:00:00+00:00","How":"written","ScriptVersion":"synthetic-v1","Kind":1},"Markers":[],"Wraps":[],"HasVideo":false,"SummaryEngine":null,"SummaryTemplate":null,"IncompleteAudioChannels":[2],"AudioIncomplete":true}
```

Exact UTF-8 confirmation plaintext (both bundles, no BOM or trailing newline):

```json
[{"Answer":"written","At":"2026-01-01T12:00:00+00:00","Kind":1,"ScriptVersion":"synthetic-v1","Script":"Synthetic fixture notice; no real participants.","ImporterIdentity":"Synthetic phone operator","Route":{"SpeechHost":null,"SummaryEngine":0,"SummaryHost":null,"KeepAudio":0,"SummaryModel":null}}]
```

`phone-sample-chunked.dictum` SHA-256 of uncompressed entry bytes (ciphertext for .enc entries):

- `audio-mixed.chunks.enc` (64204 bytes): `97ed36d4f3b59dd944431b873f63665a121804a18275bd556bbeb10d914e561f`
- `confirmations.json.enc` (387 bytes): `606664ca9bd6ee36b01d5b18b7b2c49e62081d26abb81721b8988641623c8139`
- `meta.json.enc` (496 bytes): `72abb354df4793ea02a6543a2df3b978cd07d099ecc87a595d7bc623170ea623`
- `manifest.json` (474 bytes): `55d69cac90ec969fe7e5ee8f5807c8aa06ff8906879461e4271cb4cf3fc2deed`

Whole bundle SHA-256: `431373203df2eb93c0594a8afb97dd92fb0bcc4735c584ffb169fbf549d99658`.

`phone-sample-chunked-incomplete.dictum` SHA-256 of uncompressed entry bytes (ciphertext for .enc entries):

- `audio-mixed.chunks.enc` (64144 bytes): `1fee485537e882c0604aa79c47841d19ec18f060e6b26a454601b5d118c1005c`
- `confirmations.json.enc` (387 bytes): `606664ca9bd6ee36b01d5b18b7b2c49e62081d26abb81721b8988641623c8139`
- `meta.json.enc` (496 bytes): `fb6dec6f1bc7709b156fa4868d42e807b28cdd655ec3a94a5d2f795d1956c9c1`
- `manifest.json` (474 bytes): `cf769f6ba08ef7f186f2a51a8049287edeb176e97e5429be158e2879af6e5222`

Whole bundle SHA-256: `b2ce9647adccc98448c1602ba666af77c36eb822d57c390618e28884d6fa1191`.

Regenerate intentionally from the worktree: set DICTUM_REGENERATE_PHONE_FIXTURE=1 and run the xUnit test
`MeetingBundleTests.The_chunked_fixture_generator_reproduces_the_committed_bytes`; unset it afterwards.
Normal runs only compare. The helper is PhoneBundleFixture.Generate(chunked: true, incomplete: ...); production RNG is never replaced.
