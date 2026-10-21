Unsquish 0.5.1 - component licences

LibJpeg.NET 1.5.324 (Bit Miracle) - BSD-3-Clause - LibJpeg.NET-LICENSE.txt
libjxl 0.12.0 (JPEG XL Project Authors) - BSD-3-Clause with patent grant - libjxl-LICENSE.txt
  The DLLs jxl, jxl_cms, jxl_threads, brotlicommon, brotlidec and brotlienc are embedded in the exe
  and extracted to a temporary folder at start-up.
XbrzSharp 1.1.0 (Ho Tzin Mein), a port of xBRZ 1.8 (Zenju) - GPL-3.0-only - GPL-3.0.txt
SixLabors.ImageSharp 3.1 (Six Labors) - Six Labors Split License (free for open-source and small-revenue use) - ImageSharp-LICENSE.txt
.NET 10 runtime and Windows Forms (Microsoft) - MIT - dotnet-MIT-LICENSE.txt

Because of xBRZ the combined executable is GPL-3.0. The tool's own source is this repository.
Build: dotnet publish -c Release -p:PublishTrimmed=true -p:TrimMode=partial
(the six libjxl DLLs must sit in a "native" folder beside the csproj; they come from jxl-x64-windows.7z of the libjxl v0.12.0 release).
