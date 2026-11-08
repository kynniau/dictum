# DictumBundle

Swift package that writes Dictum's encrypted `.dictum` meeting bundle on iPhone, byte-for-byte compatible
with the desktop format in `docs/MEETINGS-CONTRACTS.md` section 14.A and 14.A.1.

- Audio is sealed as it is captured into the chunked `audio-mixed.chunks.enc` (DCTMAUD) form, one-second
  records, each synced to disk. No plaintext audio is written.
- `confirmations.json.enc` and `meta.json.enc` use the DCTMENC single-file framing.
- The bundle is a STORED-only ZIP with `manifest.json`; the six-word transfer code derives the transfer key
  (PBKDF2-HMAC-SHA256, 600000 iterations) that wraps the content key.
- `RecordingSession.recover` packages a session cut short (app killed) as an incomplete snapshot.

Apple frameworks only (CryptoKit, CommonCrypto, Foundation). iOS 17, macOS 14.

## Tests

```
swift test
```

The conformance tests rebuild the synthetic fixtures in `tests/Dictum.Tests/Fixtures/Bundles/` and check
every entry hash and the whole-bundle hash from `phone-sample-chunked.README.md`. The byte comparison
against the committed fixture files is skipped when the package is used outside the Dictum repository.

## Licence

MIT. Copyright (c) Kynni Pty Ltd.

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated
documentation files (the "Software"), to deal in the Software without restriction, including without
limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the
Software, and to permit persons to whom the Software is furnished to do so, subject to the following
conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial
portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED
TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL
THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF
CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
DEALINGS IN THE SOFTWARE.

Word list: `Sources/DictumBundle/Resources/transfer-words.txt` is the BIP-39 English word list from
trezor/python-mnemonic, MIT, Copyright (c) 2013-2016 Pavol Rusnak; licence text in `transfer-words.LICENSE`.
