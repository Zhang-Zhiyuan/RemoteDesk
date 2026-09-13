# NVIDIA Image Scaling

Unmodified SDK 1.0.3 source from NVIDIA's repository, pinned to
`35e13ba316c98eeecf16f37eae70ce88019911f6`:
https://github.com/NVIDIAGameWorks/NVIDIAImageScaling/tree/35e13ba316c98eeecf16f37eae70ce88019911f6

`D3D11NisScaler` embeds these files, ports the SDR configuration calculation,
and loads the original FP32 coefficient tables. No runtime download.
The optional viewer path uses RGB input, mild sharpening, and 1–2x scaling.
The HLSL adapter converts signed block starts and unsigned pixel/origin offsets
to float separately before addition. This fixes top/left sampling wraparound
on DX11 (covered by saturated RGB corner and odd-size hardware tests); the
vendored upstream files remain unchanged.
NV12 is deliberately not enabled: the upstream YUV conversion uses fixed
BT.601 coefficients and must not replace the viewer's existing color conversion
without stream color-metadata handling and validation.

The MIT license is in `licence.txt` and the distributed third-party notice.
