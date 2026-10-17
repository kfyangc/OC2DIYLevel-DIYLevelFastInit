# Third-party notices

The decoder subset in `FastInitPlugin.cs`, under
`DIYLevelFastInit.BundleCodecs.Compression`, is adapted from Igor Pavlov's
LZMA SDK 23.01 C# sources. Only the range decoder, bit decoders, output window,
LZMA constants and LZMA decoder are included. Encoder code is not included.
The adaptation changes namespaces and visibility, removes unused interfaces,
and uses `IOException` for invalid data and properties.

The LZMA SDK is in the public domain. Its author permits copying, modification,
compilation and distribution for any purpose, including commercial use.

Source: [LZMA SDK](https://www.7-zip.org/sdk.html),
[23.01 archive](https://www.7-zip.org/a/lzma2301.7z).
