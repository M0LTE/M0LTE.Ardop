# Provenance

`M0LTE.Ardop` is a managed, ardopcf-compatible ARDOP 1 virtual TNC, extracted from the
[pdn-soundmodem](https://github.com/packet-net/pdn-soundmodem) packet-radio modem. It is a
**port with provenance** of the MIT-licensed ardopcf reference implementation, and is licensed
**AGPL-3.0-or-later** (the `M0LTE.*` family choice; MIT permits relicensing under AGPL, and the
upstream MIT notice is preserved below).

## Lineage

Ported from **ardopcf** v1.0.4.1.3 (git `a7c9228`), MIT, © 2014–2024 Rick Muething (KN6KB),
John Wiseman (G8BPQ), Peter LaRue (AI7YN), with per-file `file:line` citations in the source.
The ARDOP Specification Rev 2.0 (2017-11-27, public domain) is cited alongside, but the
operative wire format is the reference code's (e.g. the nonstandard CRC-16, the frame-type
parity quirk, the TX sample templates transcribed from `ardopSampleArrays.c`).

- **Modem / frame codec** (`ArdopModulator`, `ArdopDemodulator`, `ArdopFrameCodec`,
  `ArdopCrc`, `ArdopTxTemplates.g.cs`): behavioural ports of `Modulate.c` / `SoundInput.c`.
- **ARQ engine** (`Arq/*`): a behavioural port of `ARQ.c` (state machine, bandwidth
  negotiation, gearshift, teardown) plus the `ARDOPC.c` timer/poll scaffolding.
- **Host interface** (`Host/*`): a byte-compatible port of `HostInterface.c` /
  `TCPHostInterface.c` — the full command set with ardopcf's reply/fault formats and quirks.
- **Reed-Solomon** is via the [`M0LTE.Fec`](https://www.nuget.org/packages/M0LTE.Fec)
  dependency — the same GF(2⁸) field and generator as ardopcf's `lib/rockliff/rrs.c`
  (Simon Rockliff, permissive).

Validated bidirectionally against ardopcf itself (its TXFRAME audio decodes here
payload-exact; our audio decodes in its `--decodewav`); noise-knee sweeps measured
trial-identical decode counts at every swept (mode, SNR) point. The committed WAV fixtures in
the test project come from ardopcf's own output.

## Upstream licence (MIT)

```
Copyright (c) 2014-2024 Rick Muething, John Wiseman, Peter LaRue

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction ... (standard MIT terms; the full notice
ships with ardopcf). THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY
KIND.
```

## Dependencies

- **`M0LTE.Fec`** (AGPL-3.0-or-later) — Reed-Solomon.
- **`M0LTE.Radio.Audio`** (AGPL-3.0-or-later) — the `IAudioInput`/`IAudioOutput`/`IPttControl`
  seam that `ArdopHostServer.ForAudio` drives.
