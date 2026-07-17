# M0LTE.Ardop

A managed, **ardopcf-compatible ARDOP 1 virtual TNC** for .NET, extracted from the
[pdn-soundmodem](https://github.com/packet-net/pdn-soundmodem) packet-radio modem. It is,
as far as we know, the only ARDOP TNC written in managed C#.

- The full **modem** — 4FSK / 4PSK / 8PSK / 16QAM modulator and demodulator, Reed-Solomon FEC.
- The full **ARQ engine** — connection setup, bandwidth negotiation, gearshift, teardown.
- A **byte-compatible ardopcf TCP host interface**, so Winlink hosts (Pat, Winlink Express,
  ARIM, gARIM, hamChat) connect unmodified on the usual command/data socket pair.

Validated bidirectionally against ardopcf itself (byte-for-byte host transcript; audio decodes
both ways; trial-identical noise-knee).

- **Targets** `net10.0`. Depends on [`M0LTE.Fec`](https://www.nuget.org/packages/M0LTE.Fec)
  and [`M0LTE.Radio.Audio`](https://www.nuget.org/packages/M0LTE.Radio.Audio).
- Public API is **locked by a build-time test**; the package follows
  [Semantic Versioning](https://semver.org/) (see [`docs/versioning.md`](docs/versioning.md)).

## Install

```sh
dotnet add package M0LTE.Ardop
```

## Run a TNC on an audio device

Implement the three [`M0LTE.Radio.Audio`](https://www.nuget.org/packages/M0LTE.Radio.Audio)
seams for your sound card / SDR (all at ARDOP's native **12 kHz**), then:

```csharp
using M0LTE.Ardop.Host;

IAudioInput  rx  = /* your 12 kHz capture  */;
IAudioOutput tx  = /* your 12 kHz playback */;
IPttControl  ptt = /* your keying          */;

await using ArdopHostServer server = ArdopHostServer.ForAudio(rx, tx, ptt, commandPort: 8515);
server.Start();
// Point Pat (or any ardopcf host) at localhost:8515 — data socket is 8516.
```

`ForAudio` pumps receive audio from `rx` into the demodulator, and each transmit burst keys
`ptt`, plays through `tx`, drains, and unkeys. For full control, construct
`new ArdopHostTnc(...)` yourself and bind its `Transmitter` / `ProcessReceive` seam directly.

## Licence & provenance

AGPL-3.0-or-later (see [`LICENSE`](LICENSE)). A port with provenance of the MIT-licensed
ardopcf (© Muething KN6KB / Wiseman G8BPQ / LaRue AI7YN); attributions in
[`PROVENANCE.md`](PROVENANCE.md). Not affiliated with the ARDOP authors.
