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

## Monitoring the channel

`ArdopHostTnc` raises a pair of events for anything that shows the operator what the radio
is doing. Neither is part of the host protocol: the command and data sockets carry only
what this station's own session sent and received.

```csharp
tnc.FrameDecoded    += f => Console.WriteLine($"heard {f.Name} from {f.Caller}");
tnc.FrameTransmitted += f => Console.WriteLine($"sent  {f.Name} to {f.Target}");
```

- `FrameDecoded` is every frame the demodulator recovers, good or bad, including frames
  belonging to other stations' sessions.
- `FrameTransmitted` is every frame this station sends: ARQ frames, FEC frames and ID
  frames, named with the same spelling the station at the other end will list. The
  two-tone test raises nothing, being tones rather than a frame. It is raised once the
  burst has been played and while PTT is still up, so a frame announced is one that
  reached the transmitter, and a handler that blocks holds PTT up.

Without the second one a host has no way to say what its own bursts were: `Transmitter` is
handed modulated audio, not a frame.

## Channel busy

ardopcf's busy detector is **not** ported: it is a spectral peak-to-baseline test on the
receive audio (`BusyDetect.c`), and this package has no audio device of its own. What is
ported is everything ardopcf drives *from* the busy state, over a seam the host
application fills in:

```csharp
tnc.Config.ChannelBusy = () => myModem.ChannelIsOccupied;   // default: null
```

**The default is "never busy".** Leave `ChannelBusy` unset and nothing below happens,
whatever `BUSYDET` and `BUSYBLOCK` are set to, so existing behaviour is unchanged. Wire it
and:

- `BUSY TRUE` / `BUSY FALSE` go to the host on transitions, with ardopcf's hysteresis
  applied to your raw reading: three consecutive samples (about 300 ms) to assert, and a
  5 s hold after the last busy reading before it releases. Only a raw "is the channel
  occupied now" answer is wanted here; the timing is ours.
- `BUSYBLOCK TRUE` refuses an inbound `ConReq` with `ConRejBusy` when the channel was
  already in use by someone other than the caller (ardopcf's rule, `ARQ.c:1199`), and
  blocks an outgoing `ARQCALL` with `FAULT Blocked by Busy`. The outgoing half is an
  addition: ardopcf calls regardless and leaves the decision to the host it told
  `BUSY TRUE`.
- The post-session and 10-minute ID frames wait for a clear channel, as in ardopcf.
- `BUSYDET 0` disables detection exactly as ardopcf does. `BUSYDET 1`-`10` are thresholds
  on a spectral ratio that does not exist here, so they cannot be honoured through a
  boolean; the value is kept, reported back, and published on the config for the seam's
  owner to act on.

**The busy state gates session initiation only.** It is sampled solely in protocol state
DISC, exactly as in ardopcf, so it can never delay a transmission inside a session: an IRS
has to ACK inside the ISS's repeat window, and a busy check that stalled a burst would
break the link rather than protect the channel. See
`A_busy_channel_must_never_disturb_an_in_flight_session` in the test suite.

## Licence & provenance

AGPL-3.0-or-later (see [`LICENSE`](LICENSE)). A port with provenance of the MIT-licensed
ardopcf (© Muething KN6KB / Wiseman G8BPQ / LaRue AI7YN); attributions in
[`PROVENANCE.md`](PROVENANCE.md). Not affiliated with the ARDOP authors.
