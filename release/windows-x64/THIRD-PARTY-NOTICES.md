# Third-party notices

RemoteDesk's optional Windows Media Foundation / D3D11 video path uses the
following MIT-licensed packages:

- Vortice.MediaFoundation, Vortice.Direct3D11, Vortice.DirectX, Vortice.DXGI,
  3.8.3 — Copyright (c) Amer Koleci and Contributors,
  <https://github.com/amerkoleci/Vortice.Windows>
- Vortice.Mathematics 2.1.0 — Copyright (c) Amer Koleci and Contributors,
  <https://github.com/amerkoleci/Vortice.Mathematics>
- SharpGen.Runtime and SharpGen.Runtime.COM 2.4.2-beta — Copyright (c)
  2010-2017 Alexandre Mutel, 2017-2023 Jeremy Koritzinsky, 2023-2024 Amer
  Koleci, <https://github.com/SharpGenTools/SharpGenTools>
- System.Text.Json, System.Text.Encodings.Web, and System.IO.Pipelines 9.0.1 —
  Copyright (c) .NET Foundation and Contributors,
  <https://github.com/dotnet/runtime>

RemoteDesk's Windows private-relay login and auto-provisioning paths use these
MIT-licensed packages:

- SSH.NET 2026.0.0 — SSH and SFTP client,
  <https://github.com/sshnet/SSH.NET>
- BouncyCastle.Cryptography 2.7.0 — cryptographic primitives used by SSH.NET,
  Copyright (c) 2000-2026 The Legion of the Bouncy Castle Inc.,
  <https://www.bouncycastle.org/>
- Microsoft.Extensions.Logging.Abstractions 8.0.3 — logging abstractions,
  Copyright (c) .NET Foundation and Contributors,
  <https://github.com/dotnet/runtime>

RemoteDesk's self-contained Linux packages additionally bundle components from
the Ubuntu build environment:

- CPython 3.12, distributed under the Python Software Foundation License
  Version 2, <https://docs.python.org/3/license.html>
- cryptography, distributed under either the Apache License 2.0 or the
  3-clause BSD license, <https://github.com/pyca/cryptography>
- cffi / `_cffi_backend`, distributed under the MIT license,
  <https://github.com/python-cffi/cffi>
- Paramiko, distributed under LGPL 2.1 or later,
  <https://github.com/paramiko/paramiko>
- PyNaCl and bcrypt, distributed under Apache License 2.0,
  <https://github.com/pyca/pynacl>, <https://github.com/pyca/bcrypt>
- six, distributed under the MIT license, <https://github.com/benjaminp/six>
- libsodium, distributed under the ISC license, <https://github.com/jedisct1/libsodium>

The Linux packaging script copies the exact distribution copyright and license
files for these bundled runtime packages into `third-party-licenses/` alongside
this notice.

RemoteDesk Android uses JSch 2.28.6 (mwiede fork) for one-time SSH server login,
under its BSD-style license: <https://github.com/mwiede/jsch>.
Copyright (c) 2002-2015 Atsuhiko Yamanaka, JCraft,Inc.
The full notice is included in the APK at
`assets/third-party-licenses/JSch-LICENSE.txt` and in the source tree at
`src/RemoteDesk.Android/app/src/main/assets/third-party-licenses/JSch-LICENSE.txt`.

The Windows package includes an optional companion installer for the Gyan
FFmpeg 8.1.2 release essentials build. The FFmpeg binary is not bundled with
RemoteDesk. The installer downloads the pinned upstream archive, verifies both
the archive and executable SHA-256 values, verifies the required `gfxcapture`
filter and Windows hardware H.264 encoders, and installs the upstream
`LICENSE` and `README.txt` beside the binary. Gyan's static builds are licensed
under GPLv3:

- FFmpeg, <https://ffmpeg.org/>
- Gyan FFmpeg Windows builds, <https://www.gyan.dev/ffmpeg/builds/>

The MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
