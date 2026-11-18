# AirPlay mirroring interoperability

The managed key transform in this directory is adapted from [airplayreceiver](https://github.com/SteeBono/airplayreceiver/tree/806fd39ef263a2b38bdd7c8e636a9fd804a94c4e/AirPlay/Crypto), copyright 2020 Stefano Bono, MIT. Its algorithm and tables derive from the [PlayFair interoperability implementation](https://github.com/FDH2/UxPlay/tree/48f5ce47c9ef694afccfac907c9e8828329657fe/lib/playfair), distributed under GPLv3. The combined product follows the repository's GPLv3 license. Keep the MIT notice alongside these sources.

SonicRoute uses the transform for the AES key of the AirPlay screen mirroring session. The packed resource contains the original lookup tables in little-endian binary form, parsed once on demand. The C# adaptation replaces table slicing copies with spans and preserves the transform. The four v3 mirroring setup response records in `AirPlayMirrorHandshake.cs` come from the upstream receiver's `AirTunesListener.cs`.
